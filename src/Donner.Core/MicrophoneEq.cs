using System.Numerics;

namespace Donner.Core;

public enum EqFilter { Bell,HighPass,LowPass,LowShelf,HighShelf,Notch }
public sealed record EqBand(EqFilter Type,double Frequency,double Gain,double Q,bool Enabled=true);
public sealed record MicrophoneEqSettings(bool Enabled,double Preamp,EqBand[] Bands)
{
    public static MicrophoneEqSettings Flat()=>new(false,0,new double[]{80,200,600,1500,4000,10000}.Select(f=>new EqBand(EqFilter.Bell,f,0,1)).ToArray());
    public void Validate()
    {
        if(!double.IsFinite(Preamp)||Preamp is <-24 or >12||Bands is null||Bands.Length!=6)throw new InvalidDataException("EQ输出增益范围为 -24 至 +12 dB，必须包含六个频段。");
        foreach(var band in Bands)if(band is null||!Enum.IsDefined(band.Type)||!double.IsFinite(band.Frequency)||band.Frequency is <20 or >20000||!double.IsFinite(band.Gain)||band.Gain is <-24 or >24||!double.IsFinite(band.Q)||band.Q is <.1 or >20)throw new InvalidDataException("EQ频率范围为20–20000 Hz，增益为±24 dB，Q为0.1–20。");
    }
}
public readonly record struct EqCoefficients(double B0,double B1,double B2,double A1,double A2)
{
    public static readonly EqCoefficients Identity=new(1,0,0,0,0);
    public static EqCoefficients Create(EqBand band,int sampleRate)
    {
        if(sampleRate is <8000 or >384000)throw new InvalidDataException("EQ采样率无效。");
        if(!band.Enabled)return Identity;
        double w=2*Math.PI*Math.Min(band.Frequency,sampleRate*.45)/sampleRate,c=Math.Cos(w),a=Math.Sin(w)/(2*band.Q),A=Math.Pow(10,band.Gain/40),root=2*Math.Sqrt(A)*a;
        double b0,b1,b2,a0,a1,a2;
        switch(band.Type)
        {
            case EqFilter.Bell:b0=1+a*A;b1=-2*c;b2=1-a*A;a0=1+a/A;a1=-2*c;a2=1-a/A;break;
            case EqFilter.HighPass:b0=(1+c)/2;b1=-(1+c);b2=b0;a0=1+a;a1=-2*c;a2=1-a;break;
            case EqFilter.LowPass:b0=(1-c)/2;b1=1-c;b2=b0;a0=1+a;a1=-2*c;a2=1-a;break;
            case EqFilter.Notch:b0=1;b1=-2*c;b2=1;a0=1+a;a1=-2*c;a2=1-a;break;
            case EqFilter.LowShelf:b0=A*((A+1)-(A-1)*c+root);b1=2*A*((A-1)-(A+1)*c);b2=A*((A+1)-(A-1)*c-root);a0=(A+1)+(A-1)*c+root;a1=-2*((A-1)+(A+1)*c);a2=(A+1)+(A-1)*c-root;break;
            case EqFilter.HighShelf:b0=A*((A+1)+(A-1)*c+root);b1=-2*A*((A-1)+(A+1)*c);b2=A*((A+1)+(A-1)*c-root);a0=(A+1)-(A-1)*c+root;a1=2*((A-1)-(A+1)*c);a2=(A+1)-(A-1)*c-root;break;
            default:throw new InvalidDataException("EQ滤波器类型无效。");
        }
        return new(b0/a0,b1/a0,b2/a0,a1/a0,a2/a0);
    }
    public double Decibels(double frequency,int sampleRate)
    {
        var z=Complex.FromPolarCoordinates(1,-2*Math.PI*frequency/sampleRate);
        double magnitude=((B0+B1*z+B2*z*z)/(1+A1*z+A2*z*z)).Magnitude;
        return 20*Math.Log10(Math.Max(magnitude,1e-12));
    }
}

// Immutable publications are built on the UI thread. The audio path only reads references.
public sealed class MicrophoneEqBank
{
    public int SampleRate {get;}
    internal readonly EqCoefficients[] Filters=new EqCoefficients[18];
    internal readonly bool[] Enabled=new bool[3];
    internal readonly double[] Gains=new double[3];
    public MicrophoneEqBank(MicrophoneEqSettings[] settings,int sampleRate)
    {
        if(settings.Length!=3||sampleRate is <8000 or >384000)throw new InvalidDataException("EQ输入数或采样率无效。");
        SampleRate=sampleRate;
        for(int i=0;i<3;i++)
        {
            settings[i].Validate();Enabled[i]=settings[i].Enabled;Gains[i]=Math.Pow(10,settings[i].Preamp/20);
            for(int j=0;j<6;j++)Filters[i*6+j]=EqCoefficients.Create(settings[i].Bands[j],sampleRate);
        }
    }
}
public sealed class MicrophoneEqProcessor
{
    private MicrophoneEqBank? published,active;
    private readonly MicrophoneEqBank?[] previous=new MicrophoneEqBank?[3];
    private readonly double[] states=new double[72],oldStates=new double[72];
    private readonly int[] remaining=new int[6];
    private int fadeLength,transitionFrames,blockFrames;
    private bool validRate;
    public void Publish(MicrophoneEqBank bank)=>Volatile.Write(ref published,bank);
    public void BeginBlock(int sampleRate)
    {
        transitionFrames=Math.Max(0,transitionFrames-blockFrames);blockFrames=0;
        var incoming=Volatile.Read(ref published);validRate=incoming?.SampleRate==sampleRate;
        if(!validRate)return;
        if(ReferenceEquals(active,incoming))return;
        if(transitionFrames>0&&active?.SampleRate==sampleRate)return;
        fadeLength=Math.Max(1,sampleRate/100);bool changed=false;
        for(int strip=0;strip<3;strip++)
        {
            bool same=active?.SampleRate==sampleRate&&active.Enabled[strip]==incoming!.Enabled[strip]&&active.Gains[strip]==incoming.Gains[strip];
            if(same)for(int band=0;band<6;band++)if(active!.Filters[strip*6+band]!=incoming!.Filters[strip*6+band]){same=false;break;}
            if(same)continue;
            changed=true;previous[strip]=active?.SampleRate==sampleRate?active:null;
            for(int channel=strip*2;channel<strip*2+2;channel++)
            {
                int offset=channel*12;Array.Copy(states,offset,oldStates,offset,12);Array.Clear(states,offset,12);remaining[channel]=fadeLength;
            }
        }
        active=incoming;if(changed)transitionFrames=fadeLength;
    }
    public void ProcessChannel(int channel,ReadOnlySpan<float> input,Span<float> output)
    {
        blockFrames=Math.Max(blockFrames,input.Length);
        if(output.Length<input.Length)throw new ArgumentException("Output too short");
        if(channel is <0 or >=6||!validRate||active is null){input.CopyTo(output);return;}
        int strip=channel/2;
        for(int i=0;i<input.Length;i++)
        {
            double dry=float.IsFinite(input[i])?input[i]:0;
            double wet=Run(active,states,strip,channel,dry);
            if(remaining[channel]>0)
            {
                var oldBank=previous[strip];double old=oldBank is null?dry:Run(oldBank,oldStates,strip,channel,dry);
                double mix=1-(double)remaining[channel]/fadeLength;wet=old+(wet-old)*mix;remaining[channel]--;
            }
            output[i]=double.IsFinite(wet)&&Math.Abs(wet)<float.MaxValue?(float)wet:0;
        }
    }
    private static double Run(MicrophoneEqBank bank,double[] memory,int strip,int channel,double sample)
    {
        if(!bank.Enabled[strip])return sample;
        sample*=bank.Gains[strip];
        for(int band=0;band<6;band++)
        {
            var c=bank.Filters[strip*6+band];int offset=channel*12+band*2;
            double result=c.B0*sample+memory[offset];
            memory[offset]=c.B1*sample-c.A1*result+memory[offset+1];memory[offset+1]=c.B2*sample-c.A2*result;
            if(Math.Abs(memory[offset])<1e-30)memory[offset]=0;
            if(Math.Abs(memory[offset+1])<1e-30)memory[offset+1]=0;
            sample=result;
        }
        return sample;
    }
}

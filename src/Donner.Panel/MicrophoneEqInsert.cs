using System.Runtime.InteropServices;
using System.Text;
using Donner.Core;

namespace Donner.Panel;

internal sealed class MicrophoneEqInsert:IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Callback(IntPtr user,int command,IntPtr data,int extra);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Register(int mode,Callback callback,IntPtr user,IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Empty();
    private readonly Register register;
    private readonly Empty start,stop,unregister;
    private readonly Callback callback;
    private readonly MicrophoneEqProcessor processor=new();
    private readonly float[] ring=new float[4096];
    private readonly MicrophoneEqSettings[] settings=[MicrophoneEqSettings.Flat(),MicrophoneEqSettings.Flat(),MicrophoneEqSettings.Flat()];
    private bool registered,disposed;
    private int sampleRate=48000,preparedRate=48000,restart,preview,position;
    private long frames,lastFrames,lastActive,nextRestart,releaseAt,releaseDeadline;
    internal bool HasAudio=>registered&&Frames>0&&Environment.TickCount64-lastActive<1200;
    private int callbacks,callbackFault,inputChannels,outputChannels;
    internal int InputChannels=>Volatile.Read(ref inputChannels);
    internal int OutputChannels=>Volatile.Read(ref outputChannels);
    internal string Status {get;private set;}="旁通 · 未启用麦克风 EQ";
    internal bool Registered=>registered;
    internal int SampleRate=>Volatile.Read(ref sampleRate);
    internal long Frames=>Interlocked.Read(ref frames);
    internal int CallbackCount=>Volatile.Read(ref callbacks);
    internal int CallbackFault=>Volatile.Read(ref callbackFault);
    internal int Preview {get=>Volatile.Read(ref preview);set=>Volatile.Write(ref preview,Math.Clamp(value,0,2));}
    internal MicrophoneEqInsert(IntPtr library)
    {
        T Load<T>(string name) where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library,"VBVMR_AudioCallback"+name));
        register=Load<Register>("Register");start=Load<Empty>("Start");stop=Load<Empty>("Stop");unregister=Load<Empty>("Unregister");callback=Process;
        processor.Publish(new(settings,48000));
        // JIT the same DSP paths before entering the time-critical callback.
        processor.BeginBlock(48000);var warm=new float[512];for(int i=0;i<6;i++)processor.ProcessChannel(i,warm,warm);
    }
    internal void Configure(int strip,MicrophoneEqSettings value)
    {
        value.Validate();var old=settings[strip];settings[strip]=value with{Bands=(EqBand[])value.Bands.Clone()};Publish();
        try{if(settings.Any(x=>x.Enabled)&&!registered)Start();else if(!settings.Any(x=>x.Enabled)&&registered){releaseAt=Frames+SampleRate/20;releaseDeadline=Environment.TickCount64+500;}}
        catch{settings[strip]=old;Publish();throw;}
        Status=settings.Any(x=>x.Enabled)?"麦克风 EQ 已启用":"旁通 · 麦克风原声";
    }
    private void Publish(){preparedRate=SampleRate;processor.Publish(new(settings,preparedRate));}
    private void Start()
    {
        if(disposed)throw new ObjectDisposedException(nameof(MicrophoneEqInsert));
        var name=Marshal.AllocHGlobal(64);
        try
        {
            Marshal.Copy(new byte[64],0,name,64);var label=Encoding.ASCII.GetBytes("Donner Microphone EQ");Marshal.Copy(label,0,name,label.Length);
            int result=register(1,callback,IntPtr.Zero,name);
            if(result!=0)throw new IOException(result==1?"麦克风输入效果接口已被其他程序占用，未接管。":$"无法注册麦克风 EQ（{result}）。");
            registered=true;
            result=start();if(result!=0){int release=unregister();if(release is 0 or 1)registered=false;throw new IOException($"无法启动麦克风 EQ（{result}）。");}
        }
        finally{Marshal.FreeHGlobal(name);}
        Interlocked.Exchange(ref callbackFault,0);
    }
    internal void Maintain()
    {
        if(disposed)return;
        if(preparedRate!=SampleRate)Publish();
        if(registered&&Volatile.Read(ref restart)!=0&&Environment.TickCount64>=nextRestart)
        {
            nextRestart=Environment.TickCount64+1000;int result=stop();if(result==0)result=start();if(result==0)Interlocked.Exchange(ref restart,0);
            Status=result==0?"麦克风 EQ 已重新连接":$"麦克风 EQ 等待重连（{result}）";
        }
        if(registered&&!settings.Any(x=>x.Enabled)&&(Frames>=releaseAt||Environment.TickCount64>=releaseDeadline)){Release();Status="旁通 · 麦克风原声";}
        if(registered&&Frames!=lastFrames){lastFrames=Frames;lastActive=Environment.TickCount64;if(settings.Any(x=>x.Enabled))Status="麦克风 EQ 已启用";}
        else if(registered&&!HasAudio)Status="等待 Banana 音频缓冲，恢复后将自动重连。";
        if(CallbackFault!=0)Status="麦克风 EQ 回调异常，请关闭后重新启用。";
    }
    internal void CopySpectrum(Span<float> target)
    {
        int end=Volatile.Read(ref position);
        for(int i=0;i<target.Length;i++)target[i]=ring[(end-target.Length+i)&4095];
    }
    private int Process(IntPtr user,int command,IntPtr data,int extra)
    {
        try{return ProcessBuffer(command,data);}
        catch(Exception){Interlocked.Exchange(ref callbackFault,1);return 0;}
    }
    private unsafe int ProcessBuffer(int command,IntPtr data)
    {
        if(command==1&&data!=IntPtr.Zero){Volatile.Write(ref sampleRate,Marshal.ReadInt32(data));return 0;}
        if(command is 2 or 3){Interlocked.Exchange(ref restart,1);return 0;}
        if(command!=10||data==IntPtr.Zero)return 0;
        // Windows long is 32 bits; the SDK's 128 read/write pointers are 64 bits in this x64 app.
        int* header=(int*)data;int sr=header[0],count=header[1],inputs=header[2],outputs=header[3];
        if(sr is <8000 or >384000||count is <1 or >16384||inputs is <0 or >128||outputs is <0 or >128){Interlocked.Exchange(ref callbackFault,1);return 0;}
        Volatile.Write(ref sampleRate,sr);Volatile.Write(ref inputChannels,inputs);Volatile.Write(ref outputChannels,outputs);processor.BeginBlock(sr);
        float** reads=(float**)((byte*)data+16);float** writes=reads+128;
        for(int channel=0;channel<outputs;channel++)
        {
            if(writes[channel]==null)continue;
            var output=new Span<float>(writes[channel],count);
            if(channel>=inputs||reads[channel]==null){output.Clear();continue;}
            new ReadOnlySpan<float>(reads[channel],count).CopyTo(output);
        }
        int first=Volatile.Read(ref preview)*2;
        if(first+1<inputs&&reads[first]!=null&&reads[first+1]!=null)
        {
            float* left=reads[first];float* right=reads[first+1];double energyL=0,energyR=0;
            for(int i=0;i<count;i++){energyL+=left[i]*left[i];energyR+=right[i]*right[i];}
            float* source=energyR>energyL?right:left;int end=position;
            for(int i=0;i<count;i++)ring[(end+i)&4095]=float.IsFinite(source[i])?source[i]:0;
            Volatile.Write(ref position,(end+count)&4095);
        }
        // All outputs already contain the dry path; only the first six hardware channels may change.
        for(int channel=0;channel<Math.Min(6,Math.Min(inputs,outputs));channel++)
            if(reads[channel]!=null&&writes[channel]!=null)processor.ProcessChannel(channel,new ReadOnlySpan<float>(reads[channel],count),new Span<float>(writes[channel],count));
        Interlocked.Add(ref frames,count);Interlocked.Increment(ref callbacks);return 0;
    }
    private void Release()
    {
        if(registered)
        {
            stop();int result=unregister();
            if(result is not (0 or 1))throw new IOException("麦克风 EQ 注销失败，保留接口以避免音频回调失效。");
            registered=false;
        }
    }
    public void Dispose(){if(disposed)return;Release();disposed=true;GC.KeepAlive(callback);}
}

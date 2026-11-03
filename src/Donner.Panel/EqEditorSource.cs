using Donner.Core;
namespace Donner.Panel;
internal interface IEqEditorSource
{
    bool IsOutput {get;}
    string[] Targets {get;}
    int SampleRate {get;}
    string Status {get;}
    bool HasAudio {get;}
    void Maintain();
    void Monitor(bool active,int target){}
    MicrophoneEqSettings Read(int target,int channel);
    void Apply(int target,int channel,bool linked,MicrophoneEqSettings settings);
    void Spectrum(int target,Span<float> values);
    void Reset(int target,int channel,bool linked)=>Apply(target,channel,linked,MicrophoneEqSettings.Flat() with{Enabled=Read(target,channel).Enabled});
}
internal sealed class OutputEqSource(Func<string,float> read,Action<string,float> write,Func<int> rate):IEqEditorSource
{
    private readonly Dictionary<(int,int),(long Updated,MicrophoneEqSettings Settings)> cache=[];
    private static MicrophoneEqSettings Copy(MicrophoneEqSettings value)=>value with{Bands=(EqBand[])value.Bands.Clone()};
    public bool IsOutput=>true;
    public string[] Targets=>Enumerable.Range(0,5).Select(MixerPanel.BusName).ToArray();
    public int SampleRate=>rate();
    public string Status=>"输出总线 EQ";
    public bool HasAudio=>false;
    public void Maintain(){}
    public void Spectrum(int target,Span<float> values)=>values.Clear();
    private static readonly EqFilter[] nativeTypes=[EqFilter.Bell,EqFilter.Notch,EqFilter.BandPass,EqFilter.LowPass,EqFilter.HighPass,EqFilter.HighShelf,EqFilter.LowShelf];
    public MicrophoneEqSettings Read(int target,int channel)
    {
        if(cache.TryGetValue((target,channel),out var cached)&&Environment.TickCount64-cached.Updated<450)return Copy(cached.Settings);
        var bands=new EqBand[6];
        for(int cell=0;cell<6;cell++)
        {
            string key=$"Bus[{target}].EQ.channel[{channel}].cell[{cell}].";
            int type=(int)read(key+"type");if(type<0||type>=nativeTypes.Length)throw new IOException("Banana 返回未知滤波器类型。");
            bands[cell]=new(nativeTypes[type],read(key+"f"),read(key+"gain"),read(key+"q"),read(key+"on")>.5f);
        }
        var result=new MicrophoneEqSettings(read($"Bus[{target}].EQ.on")>.5f,0,bands);cache[(target,channel)]=(Environment.TickCount64,result);return Copy(result);
    }
    public void Reset(int target,int channel,bool linked)
    {
        foreach(int side in linked?new[]{0,1}:new[]{channel})Apply(target,side,false,MicrophoneEqSettings.Flat() with{Enabled=Read(target,side).Enabled});
    }
    public void Apply(int target,int channel,bool linked,MicrophoneEqSettings settings)
    {
        var old=Read(target,channel);var changes=new List<(string,float)>();
        if(old.Enabled!=settings.Enabled)changes.Add(($"Bus[{target}].EQ.on",settings.Enabled?1:0));
        for(int cell=0;cell<6;cell++)
        {
            var b=settings.Bands[cell];var previous=old.Bands[cell];
            foreach(var pair in new[]{("on",b.Enabled?1f:0f,previous.Enabled?1f:0f),("type",(float)Array.IndexOf(nativeTypes,b.Type),(float)Array.IndexOf(nativeTypes,previous.Type)),("f",(float)b.Frequency,(float)previous.Frequency),("gain",(float)b.Gain,(float)previous.Gain),("q",(float)b.Q,(float)previous.Q)})
            {
                if(Math.Abs(pair.Item2-pair.Item3)<.00001)continue;
                foreach(var key in MixerRules.EqKeys(target,cell,pair.Item1,linked,channel))changes.Add((key,pair.Item2));
            }
        }
        foreach(var pair in changes)MixerRules.Validate(pair.Item1,pair.Item2);
        foreach(var pair in changes)write(pair.Item1,pair.Item2);
        cache[(target,channel)]=(Environment.TickCount64,Copy(settings));if(linked)cache.Remove((target,1-channel));
    }
}

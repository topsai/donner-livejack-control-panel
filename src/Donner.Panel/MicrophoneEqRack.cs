using System.Text.Json;
using Donner.Core;

namespace Donner.Panel;

internal sealed class MicrophoneEqRack:IDisposable,IEqEditorSource
{
    private readonly VoicemeeterClient? client;
    private readonly string? path;
    private readonly MicrophoneEqSettings[] settings=[MicrophoneEqSettings.Flat(),MicrophoneEqSettings.Flat(),MicrophoneEqSettings.Flat()];
    private MicrophoneEqInsert? insert;
    private string? error,monitorError;
    private long nextMonitorAttempt;
    private readonly System.Windows.Threading.DispatcherTimer saveTimer=new(){Interval=TimeSpan.FromMilliseconds(300)};
    private bool dirty;
    internal event Action<int>? Changed;
    internal bool IsEnabled(int index)=>settings[index].Enabled;
    internal MicrophoneEqSettings Get(int index)=>settings[index] with{Bands=(EqBand[])settings[index].Bands.Clone()};
    internal int SampleRate=>insert?.SampleRate??48000;
    internal bool Running=>insert?.Registered==true;
    internal bool HasAudio=>insert?.HasAudio==true;
    internal long Frames=>insert?.Frames??0;
    internal string Status=>error??monitorError??insert?.Status??"旁通 · 未启用麦克风 EQ";
    internal MicrophoneEqRack(VoicemeeterClient? client,string? path)
    {
        this.client=client;this.path=path;saveTimer.Tick+=(_,_)=>Save();
        if(path is null||!File.Exists(path))return;
        try
        {
            var saved=JsonSerializer.Deserialize<MicrophoneEqSettings[]>(File.ReadAllText(path));
            if(saved is null||saved.Length!=3)throw new InvalidDataException("EQ配置必须包含三个输入。");
            foreach(var item in saved){if(item is null)throw new InvalidDataException("EQ配置为空。");item.Validate();}
            for(int i=0;i<3;i++)settings[i]=saved[i] with{Enabled=false};
        }
        catch(Exception e) when(e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException){error="EQ配置无法读取，已恢复平直并旁通。";}
    }
    internal void Set(int index,MicrophoneEqSettings value)
    {
        try{SetCore(index,value);}
        catch(Exception e) when(e is IOException or InvalidDataException){error=e.Message;throw;}
    }
    private void SetCore(int index,MicrophoneEqSettings value)
    {
        value.Validate();
        if(value.Enabled&&client is null)throw new IOException("当前为只读预览，未连接可用的麦克风处理接口。");
        if(client is not null)
        {
            if(!client.Connected&&value.Enabled)throw new IOException("Banana 未连接，未启用麦克风 EQ。");
            if(value.Enabled||insert is not null)
            {
                insert??=client.InputEq;
                insert.Configure(index,value);
            }
        }
        settings[index]=value with{Bands=(EqBand[])value.Bands.Clone()};error=null;Changed?.Invoke(index);
        dirty=path is not null;saveTimer.Stop();if(dirty)saveTimer.Start();
    }
    private void Save()
    {
        saveTimer.Stop();if(!dirty||path is null)return;
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temporary=path+".tmp";
                File.WriteAllText(temporary,JsonSerializer.Serialize(settings.Select(x=>x with{Enabled=false}),new JsonSerializerOptions{WriteIndented=true}));
                File.Move(temporary,path,true);dirty=false;
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException){error="EQ已应用，但无法保存设置："+e.Message;}
        }
    }
    internal void Maintain()
    {
        try{insert?.Maintain();}catch(Exception e) when(e is IOException or InvalidDataException){error=e.Message;}
    }
    internal void Spectrum(int index,Span<float> target)
    {
        if(insert is null){target.Clear();return;}insert.Preview=index;insert.CopySpectrum(target);
    }
    public void Monitor(bool active,int target)
    {
        if(client is null)return;
        if(active&&(!client.Connected||Environment.TickCount64<nextMonitorAttempt))return;
        try
        {
            if(active)insert??=client.InputEq;
            insert?.Monitor(active,target);monitorError=null;
        }
        catch(Exception e) when(e is IOException or InvalidDataException){monitorError=e.Message;nextMonitorAttempt=Environment.TickCount64+2000;}
    }
    public bool IsOutput=>false;
    public string[] Targets=>["输入 1","输入 2","输入 3"];
    int IEqEditorSource.SampleRate=>SampleRate;
    string IEqEditorSource.Status=>Status;
    bool IEqEditorSource.HasAudio=>HasAudio;
    void IEqEditorSource.Maintain()=>Maintain();
    MicrophoneEqSettings IEqEditorSource.Read(int target,int channel)=>Get(target);
    void IEqEditorSource.Apply(int target,int channel,bool linked,MicrophoneEqSettings value)=>Set(target,value);
    void IEqEditorSource.Spectrum(int target,Span<float> values)=>Spectrum(target,values);
    public void Dispose(){Monitor(false,0);Save();saveTimer.Stop();}
}

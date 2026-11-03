using System.Text.Json;
using Donner.Core;

namespace Donner.Panel;

internal sealed class MicrophoneEqRack:IDisposable
{
    private readonly VoicemeeterClient? client;
    private readonly string? path;
    private readonly MicrophoneEqSettings[] settings=[MicrophoneEqSettings.Flat(),MicrophoneEqSettings.Flat(),MicrophoneEqSettings.Flat()];
    private MicrophoneEqWindow? editor;
    private MicrophoneEqInsert? insert;
    private string? error;
    private readonly System.Windows.Threading.DispatcherTimer saveTimer=new(){Interval=TimeSpan.FromMilliseconds(300)};
    private bool dirty;
    internal MicrophoneEqSettings Get(int index)=>settings[index] with{Bands=(EqBand[])settings[index].Bands.Clone()};
    internal int SampleRate=>insert?.SampleRate??48000;
    internal bool Running=>insert?.Registered==true;
    internal bool HasAudio=>insert?.HasAudio==true;
    internal long Frames=>insert?.Frames??0;
    internal string Status=>error??insert?.Status??"旁通 · 未启用麦克风 EQ";
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
        settings[index]=value with{Bands=(EqBand[])value.Bands.Clone()};error=null;
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
    internal void Show(System.Windows.Window? owner,int index)
    {
        if(editor is not null){editor.SelectInput(index);if(editor.WindowState==System.Windows.WindowState.Minimized)editor.WindowState=System.Windows.WindowState.Normal;editor.Activate();return;}
        editor=new(this);editor.SelectInput(index);if(owner is not null)editor.Owner=owner;
        editor.Closed+=(_,_)=>editor=null;editor.Show();
    }
    internal MicrophoneEqWindow? Editor=>editor;
    public void Dispose(){editor?.Close();editor=null;Save();saveTimer.Stop();}
}

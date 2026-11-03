using System.Text.Json;
namespace Donner.Core;

public sealed record WindowsRateBinding(string Id,string Name,bool Capture,uint Rate);
public sealed record MixerBinding(bool Input,int Index,int Type,string Name,string Id="");
public sealed record EqEditorState(int Target=0,int Band=0,int Channel=0,bool Linked=true);
public sealed record MixerViewState(int Tab=0,EqEditorState? Input=null,EqEditorState? Output=null,bool RatesExpanded=false);
public sealed record MixerConfiguration
{
    [System.Text.Json.Serialization.JsonRequired]
    public int Version {get;init;}=1;
    [System.Text.Json.Serialization.JsonRequired]
    public bool Enabled {get;init;}=true;
    [System.Text.Json.Serialization.JsonRequired]
    public Dictionary<string,float> Parameters {get;init;}=[];
    [System.Text.Json.Serialization.JsonRequired]
    public MixerBinding[] Devices {get;init;}=[];
    [System.Text.Json.Serialization.JsonRequired]
    public MicrophoneEqSettings[] InputEq {get;init;}=[MicrophoneEqSettings.Flat(),MicrophoneEqSettings.Flat(),MicrophoneEqSettings.Flat()];
    [System.Text.Json.Serialization.JsonRequired]
    public MixerViewState View {get;init;}=new();
    public WindowsRateBinding[] WindowsRates {get;init;}=[];
    public void Validate()
    {
        if(Version!=1||Parameters is null||Parameters.Count>1600||Devices is null||Devices.Length>6||InputEq is null||InputEq.Length!=3||View is null)throw new InvalidDataException("调音台配置格式不受支持。");
        var allowed=MixerRules.PersistentParameters.ToHashSet(StringComparer.Ordinal);
        foreach(var p in Parameters){if(!allowed.Contains(p.Key))throw new InvalidDataException("配置包含非持久参数。");MixerRules.Validate(p.Key,p.Value);}
        if(Devices.Any(x=>x is null||x.Index is <0 or >2||x.Type is not (1 or 3 or 4 or 5)||x.Input&&x.Type==5||!x.Input&&x.Type==5&&x.Index!=0||x.Name is null||x.Name.Length>512||x.Id is null||x.Id.Length>1024)||Devices.Select(x=>(x.Input,x.Index)).Distinct().Count()!=Devices.Length)throw new InvalidDataException("调音台设备绑定无效。");
        foreach(var eq in InputEq){if(eq is null)throw new InvalidDataException("输入EQ为空。");eq.Validate();}
        if(View.Tab is <0 or >1)throw new InvalidDataException("调音台页面无效。");
        if(WindowsRates is null||WindowsRates.Length>16||WindowsRates.Any(x=>x is null||string.IsNullOrWhiteSpace(x.Id)||x.Id.Length>256||x.Name is null||x.Name.Length>512||!new uint[]{44100,48000,88200,96000,176400,192000}.Contains(x.Rate))||WindowsRates.Select(x=>x.Id).Distinct().Count()!=WindowsRates.Length)throw new InvalidDataException("Windows采样率配置无效。");
        CheckView(View.Input,3,10);CheckView(View.Output,5,6);
    }
    private static void CheckView(EqEditorState? state,int count,int bands)
    {if(state is not null&&(state.Target<0||state.Target>=count||(state.Band<0||state.Band>=bands)||state.Channel is <0 or >1))throw new InvalidDataException("EQ编辑器状态无效。");}
}
public static class ConfigurationFiles
{
    public static T? Load<T>(string path,Action<T> validate) where T:class
    {
        Exception? failure=null;
        foreach(string candidate in new[]{path,path+".bak"})
        {
            if(!File.Exists(candidate))continue;
            try
            {
                if(new FileInfo(candidate).Length>524288)throw new InvalidDataException("配置文件过大。");
                var result=JsonSerializer.Deserialize<T>(File.ReadAllText(candidate))??throw new InvalidDataException("配置文件为空。");validate(result);return result;
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException){failure=e;}
        }
        if(failure is not null)throw new InvalidDataException("配置及备份无法读取，保留原文件："+failure.Message,failure);
        return null;
    }
    public static void Save<T>(string path,T value,Action<T> validate) where T:class
    {
        validate(value);Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,value,new JsonSerializerOptions{WriteIndented=true});file.Flush(true);}
            if(File.Exists(path))
            {
                bool valid=false;try{var old=JsonSerializer.Deserialize<T>(File.ReadAllText(path));if(old is not null){validate(old);valid=true;}}catch(Exception e) when(e is IOException or JsonException or InvalidDataException){}
                if(valid)File.Replace(temporary,path,path+".bak",true);else{File.Copy(path,path+".corrupt."+Guid.NewGuid().ToString("N"));File.Move(temporary,path,true);}
            }
            else File.Move(temporary,path);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}

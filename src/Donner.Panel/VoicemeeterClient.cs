using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Donner.Core;
using Microsoft.Win32;

namespace Donner.Panel;

internal sealed record MixerDevice(int Type,string Name,string Id="")
{
    public string Driver=>Type switch {1=>"mme",3=>"wdm",4=>"ks",5=>"asio",_=>throw new InvalidDataException("设备驱动类型无效。")};
    public override string ToString()=>Name.Length==0?"未选择":$"{Driver.ToUpperInvariant()} · {(MixerEndpointNames.TryCustomDisplay(Name,Id)??MixerNames.Display(Name))}";
}
internal interface IMixerClient:IDisposable
{
    bool Connected {get;}
    string Status {get;}
    bool Refresh();
    void Start(bool show=false);
    float Get(string key);
    void Set(string key,float value);
    string DeviceName(bool input,int index);
    MixerDevice[] Devices(bool input);
    void SetDevice(bool input,int index,MixerDevice device);
    float Level(bool input,int index);
    float ChannelLevel(bool input,int index,int channel)=>Level(input,index);
}

internal sealed class VoicemeeterClient:IMixerClient
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Empty();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Number(int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadInt(out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall,CharSet=CharSet.Ansi)] private delegate int ReadFloat(string key,out float value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall,CharSet=CharSet.Ansi)] private delegate int WriteFloat(string key,float value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadLevel(int type,int channel,out float value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall,CharSet=CharSet.Unicode)] private delegate int ReadString([MarshalAs(UnmanagedType.LPStr)] string key,StringBuilder value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall,CharSet=CharSet.Unicode)] private delegate int WriteString([MarshalAs(UnmanagedType.LPStr)] string key,string value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall,CharSet=CharSet.Unicode)] private delegate int Describe(int index,out int type,StringBuilder name,StringBuilder id);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr LoadLibraryExW(string path,IntPtr reserved,uint flags);
    private IntPtr library;
    private MicrophoneEqInsert? inputEq;
    private static readonly List<VoicemeeterClient> retainedForNativeCallbacks=[];
    internal MicrophoneEqInsert InputEq=>inputEq??=new(library);
    private bool loggedIn;
    private bool? pendingWindow;
    private T Function<T>(string name) where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library,"VBVMR_"+name));
    public bool Connected {get;private set;}
    public string Status {get;private set;}="尚未连接 Banana";
    public static string? InstalledDirectory()
    {
        var directories=new List<string>();
        foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})
        {
            using var key=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view).OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB:Voicemeeter {17359A74-1236-5467}");
            if(key?.GetValue("UninstallString") is string command)
            {
                var candidate=command.Trim().Trim('"');var exe=candidate.IndexOf(".exe",StringComparison.OrdinalIgnoreCase);
                if(exe>=0)directories.Add(Path.GetDirectoryName(candidate[..(exe+4)].Trim('"'))??"");
            }
        }
        directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"VB","Voicemeeter"));
        directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"VB","Voicemeeter"));
        return directories.FirstOrDefault(d=>File.Exists(Path.Combine(d,"VoicemeeterRemote64.dll"))&&
            (File.Exists(Path.Combine(d,"voicemeeterpro.exe"))||File.Exists(Path.Combine(d,"voicemeeterpro64.exe"))||File.Exists(Path.Combine(d,"voicemeeterpro_x64.exe"))));
    }
    public VoicemeeterClient():this(Path.Combine(InstalledDirectory()??throw new IOException("未安装 Voicemeeter Banana。"),"VoicemeeterRemote64.dll")){}
    internal VoicemeeterClient(string path)
    {
        library=LoadLibraryExW(Path.GetFullPath(path),IntPtr.Zero,0x1100);
        if(library==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"无法加载 Banana 控制接口。");
        try{int result=Function<Empty>("Login")();if(result<0)throw new IOException($"Banana 登录失败（{result}）。");loggedIn=true;}
        catch{NativeLibrary.Free(library);library=IntPtr.Zero;throw;}
    }
    public bool Refresh()
    {
        int dirty=Function<Empty>("IsParametersDirty")();
        int code=Function<ReadInt>("GetVoicemeeterType")(out var type);
        Connected=dirty>=0&&code==0&&type==2;
        Status=Connected?"已连接 Voicemeeter Banana":type is 1 or 3&&code==0&&dirty>=0?"当前运行的是其他 Voicemeeter 版本，请切换到 Banana。":"Banana 未运行或正在启动；如刚安装，请先重启 Windows。";
        if(Connected&&pendingWindow is {} show){Check(Function<WriteFloat>("SetParameterFloat")("Command.Show",show?1:0),"显示调音台界面");pendingWindow=null;}
        return Connected;
    }
    public void Start(bool show=false)
    {
        if(Refresh()) {Check(Function<WriteFloat>("SetParameterFloat")("Command.Show",show?1:0),"切换 Banana 界面");return;}
        if(Status.Contains("其他"))throw new IOException(Status);
        var directory=InstalledDirectory()??throw new IOException("未安装 Banana。");
        var exe=new[]{"voicemeeterpro_x64.exe","voicemeeterpro64.exe","voicemeeterpro.exe"}.Select(x=>Path.Combine(directory,x)).FirstOrDefault(File.Exists)??throw new IOException("找不到 Banana 程序。");
        pendingWindow=show;
        var running=Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
        bool alreadyRunning=running.Length>0;foreach(var item in running)item.Dispose();if(alreadyRunning)return;
        using var process=Process.Start(new ProcessStartInfo(exe){UseShellExecute=true,WindowStyle=show?ProcessWindowStyle.Normal:ProcessWindowStyle.Hidden});
    }
    private void Require(){if(!Connected)throw new IOException("Banana 尚未连接，请稍后重试。");}
    private static void Check(int code,string operation){if(code!=0)throw new IOException($"{operation}失败（Banana 返回 {code}）。");}
    public float Get(string key){Require();Check(Function<ReadFloat>("GetParameterFloat")(key,out var value),"读取参数");if(!float.IsFinite(value))throw new IOException("Banana 返回无效数值。");return value;}
    public void Set(string key,float value){Require();MixerRules.Validate(key,value);Check(Function<WriteFloat>("SetParameterFloat")(key,value),"设置参数");}
    public string DeviceName(bool input,int index)
    {Require();var value=new StringBuilder(512);Check(Function<ReadString>("GetParameterStringW")($"{(input?"Strip":"Bus")}[{index}].device.name",value),"读取设备");return value.ToString();}
    public MixerDevice[] Devices(bool input)
    {
        var prefix=input?"Input":"Output";int count=Function<Empty>(prefix+"_GetDeviceNumber")();
        var devices=new List<MixerDevice>{new(1,"")};
        for(int i=0;i<Math.Clamp(count,0,512);i++)
        {
            var name=new StringBuilder(512);var id=new StringBuilder(512);
            if(Function<Describe>(prefix+"_GetDeviceDescW")(i,out int type,name,id)==0&&type is 1 or 3 or 4 or 5)devices.Add(new(type,name.ToString(),id.ToString()));
        }
        return devices.Distinct().ToArray();
    }
    public void SetDevice(bool input,int index,MixerDevice device)
    {
        Require();if(index is <0 or >2||device.Type==5&&(input||index!=0))throw new InvalidDataException("该位置不支持 ASIO 设备。");
        if(!Devices(input).Contains(device))throw new IOException("所选音频设备已移除，请刷新设备列表。");
        Check(Function<WriteString>("SetParameterStringW")($"{(input?"Strip":"Bus")}[{index}].device.{device.Driver}",device.Name),"选择音频设备");
    }
    public float ChannelLevel(bool input,int index,int channel)
    {
        Require();
        if(index is <0 or >4||channel<0||channel>=(input&&index<3?2:8))throw new InvalidDataException("电平声道无效。");
        int native=input?MixerRules.InputLevelIndex(index)+channel:MixerMeterRouting.OutputChannel(index,channel);
        Check(Function<ReadLevel>("GetLevel")(input?2:3,native,out var value),"读取声道电平");
        return float.IsFinite(value)?Math.Clamp(value,0,1):throw new IOException("电平返回无效数值。");
    }
    public float Level(bool input,int index)
    {
        Require();var first=input?MixerRules.InputLevelIndex(index):index*8;
        float peak=0;int channels=input&&index<3?2:8;
        for(int i=0;i<channels;i++)if(Function<ReadLevel>("GetLevel")(input?2:3,first+i,out var value)==0&&float.IsFinite(value))peak=Math.Max(peak,value);
        return Math.Clamp(peak,0,1);
    }
    public void Dispose()
    {
        try{inputEq?.Dispose();inputEq=null;}
        catch(IOException){Connected=false;retainedForNativeCallbacks.Add(this);return;}
        Connected=false;if(library==IntPtr.Zero)return;
        try{if(loggedIn)Function<Empty>("Logout")();}finally{loggedIn=false;NativeLibrary.Free(library);library=IntPtr.Zero;}
    }
}

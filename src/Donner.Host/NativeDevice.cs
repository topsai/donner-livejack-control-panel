using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Donner.Core;
using Microsoft.Win32;

namespace Donner.Host;

public sealed class NativeDevice : IDisposable
{
    private const string ExpectedHash="0A173773D4F5E77E69BB7863158533D3110F78B50972A56E29E1CABA679B5753";
    private IntPtr library;
    private uint token;
    private uint count;
    private string identity="";
    private string version="";
    public string ApiPath {get;private set;}="";
    private DeviceSnapshot snapshot=new();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint NoArg();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Open(uint index,out uint handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Token(uint handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ReadUInt(uint handle,out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint WriteUInt(uint handle,uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ReadChannel(uint handle,uint direction,uint channel,out ushort value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint WriteChannel(uint handle,uint direction,uint channel,ushort value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ChannelBuffer(uint handle,uint direction,uint channel,IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint BufferCall(uint handle,IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint StringCall(uint handle,IntPtr buffer,uint characters);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Rates(uint handle,uint capacity,
        [Out,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=1)] uint[] rates,out uint length);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint SetBuffer(uint index,uint rate,uint samples,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr LoadLibraryExW(string path,IntPtr reserved,uint flags);
    private T Api<T>(string name) where T:Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library,"TUSBAUDIO_"+name));
    private static void Check(uint status,string operation)
    {
        if(status==0) return;
        var description=status switch {0xee000030=>"设备不支持此操作",0xee000048=>"设备句柄已失效",
            0xee000033=>"设备未打开或已断开",0xee000070=>"请先重新检测设备",0xee001002=>"当前设备正在使用，无法切换",
            0xee000074=>"返回缓冲不足",_=>"驱动拒绝请求"};
        throw new IOException($"{operation}：{description}（0x{status:X8}）。");
    }
    private static byte[] Buffer(int size,Func<IntPtr,uint> call,string name)
    {
        var pointer=Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size],0,pointer,size); Check(call(pointer),name);
            var bytes=new byte[size];Marshal.Copy(pointer,bytes,0,size);return bytes;
        }
        finally {Marshal.FreeHGlobal(pointer);}
    }
    public DeviceSnapshot Detect(string? path)
    {
        Disconnect();
        if(library!=IntPtr.Zero) {NativeLibrary.Free(library);library=IntPtr.Zero;}
        ApiPath=string.IsNullOrWhiteSpace(path)?FindInstalledApi()??"":Path.GetFullPath(path);
        version="";
        if(ApiPath.Length==0) return snapshot=new() {Status="未找到 Donner 驱动，请安装原厂驱动后重新检测。"};
        if(!File.Exists(ApiPath)) throw new FileNotFoundException("找不到 API DLL。",ApiPath);
        using(var file=File.OpenRead(ApiPath))
            if(Convert.ToHexString(SHA256.HashData(file))!=ExpectedHash)
                throw new InvalidDataException("当前 Donner 驱动版本不匹配，请安装兼容的原厂驱动。其他版本需重新核实接口。");
        var ini=Path.Combine(Path.GetDirectoryName(ApiPath)!,"custom.ini");
        if(!File.Exists(ini) || !File.ReadAllText(ini).Contains("8EBCE255-AA2D-4CD5-A142-5C8683F6905D",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("DLL 同目录缺少匹配 Livejack 的 custom.ini。");
        library=LoadLibraryExW(ApiPath,IntPtr.Zero,0x1100);
        if(library==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"无法加载 x64 控制 API。");
        var abi=Api<NoArg>("GetApiVersion")();
        if(abi!=0x50007) throw new InvalidDataException("API 版本不是已核实的 5.7。");
        version="5.7 / DLL "+FileVersionInfo.GetVersionInfo(ApiPath).FileVersion;
        Check(Api<NoArg>("EnumerateDevices")(),"枚举设备");
        count=Api<NoArg>("GetDeviceCount")();
        if(count==0) return snapshot=new() {Status="驱动 API 已加载，未连接 Donner Livejack。",ApiPath=ApiPath,ApiVersion=version};
        Check(Api<Open>("OpenDeviceByIndex")(0,out token),"打开设备");
        var id=Buffer(2048,p=>Api<StringCall>("GetDeviceInstanceIdString")(token,p,1024),"读取设备标识");
        identity=System.Text.Encoding.Unicode.GetString(id).Split('\0')[0];
        if(!identity.Contains("VID_152A&PID_885C",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("当前枚举的设备不是已验证的 Livejack 型号。");
        return Read();
    }
    public DeviceSnapshot Read()
    {
        if(token==0) return snapshot;
        if(DeviceTopology.Count()!=count)throw new IOException("设备连接列表已改变，请重新检测后再操作。");
        Check(Api<ReadUInt>("GetCurrentSampleRate")(token,out var rate),"读取当前采样率");
        var diagnostics=new Dictionary<string,string> { ["设备标识"]=identity,["API 版本"]=version,["API 路径"]=ApiPath,["枚举设备数量"]=count.ToString() };
        var rates=new uint[32]; uint length=0;
        var status=Api<Rates>("GetSupportedSampleRates")(token,32,rates,out length);
        if(status!=0 || length>32) {diagnostics["采样率能力查询"]=$"0x{status:X8} / count={length}";rates=[];}
        else rates=rates.Take((int)length).Where(r=>r>0&&r<=1000000).Distinct().ToArray();
        var channels=new List<ChannelState>();
        foreach(var direction in new uint[]{1,0}) foreach(var channel in new uint[]{1,2})
        {
            string name=direction==1?$"输入 {channel}":channel==1?"输出左":"输出右";
            try
            {
                var bytes=Buffer(16,p=>Api<ChannelBuffer>("GetVolumeMuteInfo")(token,direction,channel,p),name+"能力");
                var info=NativeLayouts.VolumeInfo(bytes);
                short? volume=null;bool? mute=null;
                if(info.Volume) {status=Api<ReadChannel>("GetVolume")(token,direction,channel,out var value);if(status==0)volume=unchecked((short)value);else diagnostics[name+"音量"]=$"0x{status:X8}";}
                if(info.Mute) {var result=NativeOutput.ReadInt32(p=>Api<ChannelBuffer>("GetMute")(token,direction,channel,p));if(result.Status==0)mute=result.Value!=0;else diagnostics[name+"静音"]=$"0x{result.Status:X8}";}
                channels.Add(new(direction,channel,name,info.Volume,info.Mute,info.Minimum,info.Maximum,info.Step,volume,mute));
            }
            catch(IOException e) {diagnostics[name+"能力"]=e.Message;channels.Add(new(direction,channel,name,false,false,0,0,0,null,null));}
        }
        uint? bufferSize=null;uint bufferRate=0;uint[] sizes=[];
        uint? inputLatency=null,outputLatency=null,latencyRate=null;
        if(count==1)
        {
            try
            {
                var bytes=Buffer(196,p=>Api<BufferCall>("GetASIOInstanceInfo")(0,p),"查询 ASIO");
                var info=NativeLayouts.AsioInfo(bytes);bufferSize=info.Preferred;bufferRate=info.Rate;sizes=info.Sizes;
                diagnostics["输入延迟（samples）"]=info.InputLatency.ToString();diagnostics["输出延迟（samples）"]=info.OutputLatency.ToString();
                inputLatency=info.InputLatency;outputLatency=info.OutputLatency;latencyRate=info.Rate>0?info.Rate:null;
            }
            catch(IOException e) {diagnostics["ASIO 查询"]=e.Message;}
        }
        else diagnostics["ASIO 设置限制"]="多个设备时禁用：原厂 API 隐式操作设备 0。";
        var model=DeviceTopology.Model(identity);
        return snapshot=new() {Connected=true,Status=count>1?"已连接（当前为第一个设备）":"已连接 Donner Livejack",DeviceId=identity,DeviceCount=count,Model=model,Name=model??"Donner Livejack",
            InputLatencySamples=inputLatency,OutputLatencySamples=outputLatency,LatencySampleRate=latencyRate,
            ApiPath=ApiPath,ApiVersion=version,SampleRate=rate,SampleRates=rates,Channels=channels.ToArray(),BufferSize=bufferSize,BufferRate=bufferRate,BufferSizes=sizes,Diagnostics=diagnostics};
    }
    public HostReply Apply(string id,Setting[] settings)
    {
        if(settings.Length>32) throw new InvalidDataException("单次设置数量过多。");
        var current=Read();foreach(var setting in settings)ControlPolicy.Validate(current,setting);
        var results=new List<OperationResult>();
        var ordered=settings.OrderBy(s=>s.Kind=="rate"?0:s.Kind=="buffer"?1:2).ToArray();
        foreach(var setting in ordered)
        {
            var kind=setting.Kind switch {"volume"=>"音量","mute"=>"静音","rate"=>"采样率","buffer"=>"缓冲",_=>setting.Kind};
            var operation=setting.Kind is "volume" or "mute"?$"{(setting.Direction==1?"输入":"输出")} {setting.Channel} {kind} → {setting.Value}":$"{kind} → {setting.Value}";
            try
            {
                ControlPolicy.Validate(current,setting);
                uint status=setting.Kind switch
                {
                    "rate"=>Api<WriteUInt>("SetSampleRate")(token,(uint)setting.Value),
                    "buffer"=>Api<SetBuffer>("SetASIOBufferPreferredSize")(0,current.BufferRate,(uint)setting.Value,0),
                    "volume"=>Api<WriteChannel>("SetVolume")(token,setting.Direction,setting.Channel,unchecked((ushort)setting.Value)),
                    "mute"=>Api<WriteChannel>("SetMute")(token,setting.Direction,setting.Channel,(ushort)setting.Value),
                    _=>throw new InvalidDataException("未知设置。")
                };
                Check(status,operation);current=Read();
                var channel=current.Channels.FirstOrDefault(c=>c.Direction==setting.Direction&&c.Number==setting.Channel);
                bool verified=setting.Kind switch {"rate"=>current.SampleRate==setting.Value,"buffer"=>current.BufferSize==setting.Value,
                    "volume"=>channel?.Volume==setting.Value,"mute"=>channel?.Muted==(setting.Value==1),_=>false};
                results.Add(new(operation,verified,verified?"写入并回读确认":"已发送，但回读值不一致或暂不可用"));
            }
            catch(Exception e) when(e is IOException or InvalidDataException)
            {
                results.Add(new(operation,false,e.Message));
                try {current=Read();} catch(IOException) {Disconnect();current=new() {Status="设备断开，请重新检测",ApiPath=ApiPath,ApiVersion=version};break;}
            }
        }
        foreach(var setting in ordered.Skip(results.Count)) results.Add(new($"{setting.Kind} {setting.Direction}/{setting.Channel}",false,"设备断开，未执行"));
        return new(id,results.All(r=>r.Success),results.All(r=>r.Success)?"设置已应用并回读确认":"部分或全部设置失败，请查看操作结果",current,results.ToArray());
    }
    public void Disconnect()
    {
        var previous=token;token=0;identity="";
        if(previous!=0 && library!=IntPtr.Zero) _=Api<Token>("CloseDevice")(previous);
        snapshot=new() {Status="未连接设备",ApiPath=ApiPath,ApiVersion=version};
    }
    public void Dispose() {Disconnect();if(library!=IntPtr.Zero){NativeLibrary.Free(library);library=IntPtr.Zero;}}
    private static string? FindInstalledApi()
    {
        var folders=new List<string>();
        foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})
        {
            using var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view);
            using var uninstall=machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Software_Donner_Donner_UsbAudio_Driver_Setup");
            if(uninstall?.GetValue("InstallLocation") is string dir) folders.Add(dir);
            using var cls=machine.OpenSubKey(@"SOFTWARE\Classes\CLSID\{5FC1A528-B0CB-4ED9-AD02-50502054ED59}\InprocServer32");
            if(cls?.GetValue(null) is string file && Path.GetDirectoryName(file.Trim('"')) is string folder) folders.Add(folder);
        }
        return folders.SelectMany(folder=>new[]{Path.Combine(folder,"DonnerUsbAudioapi_x64.dll"),Path.Combine(folder,"tusbaudioapi.dll"),Path.Combine(folder,"x64","DonnerUsbAudioapi_x64.dll")}).FirstOrDefault(File.Exists);
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Donner.Panel;

internal static class AudioEndpointFormats
{
    internal static readonly uint[] Rates=[44100,48000,88200,96000,176400,192000];
    private static readonly PropertyKey FormatKey=new(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"),0);
    internal sealed record Endpoint(string Id,string Name,bool Capture,uint DefaultRate,uint EngineRate,int Bits,int Channels);
    internal sealed record Request(string EndpointId,uint SampleRate);
    private static string Storage=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DonnerControlPanel");

    internal static bool ValidRequestPath(string path)
    {
        var full=Path.GetFullPath(path);var file=Path.GetFileName(full);
        return string.Equals(Path.GetDirectoryName(full),Path.GetFullPath(Storage),StringComparison.OrdinalIgnoreCase)
            &&file.StartsWith("audio-format-",StringComparison.Ordinal)&&file.EndsWith(".json",StringComparison.Ordinal)
            &&Guid.TryParseExact(file[13..^5],"N",out _);
    }
    internal static bool ValidReportPath(string requestPath,string reportPath)=>ValidRequestPath(requestPath)
        &&string.Equals(Path.GetFullPath(reportPath),Path.GetFullPath(requestPath)+".txt",StringComparison.OrdinalIgnoreCase);
    private static Request ReadRequest(string path)
    {
        if(!ValidRequestPath(path)||!HelperReports.PlainPath(path))throw new InvalidDataException("格式请求路径无效。");
        var info=new FileInfo(path);
        if(info.Length>4096)throw new InvalidDataException("格式请求过大。");
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length>4096)throw new InvalidDataException("格式请求过大。");
        using var json=JsonDocument.Parse(input,new JsonDocumentOptions{MaxDepth=4});
        if(json.RootElement.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("格式请求无效。");
        string? id=null;uint? rate=null;var fields=new HashSet<string>(StringComparer.Ordinal);
        foreach(var property in json.RootElement.EnumerateObject())
        {
            if(!fields.Add(property.Name))throw new InvalidDataException("格式请求包含重复字段。");
            switch(property.Name)
            {
                case "EndpointId" when property.Value.ValueKind==JsonValueKind.String:id=property.Value.GetString();break;
                case "SampleRate" when property.Value.ValueKind==JsonValueKind.Number&&property.Value.TryGetUInt32(out var value):rate=value;break;
                default:throw new InvalidDataException("格式请求包含未知或无效字段。");
            }
        }
        if(string.IsNullOrWhiteSpace(id)||id.Length>256||rate is null||!Rates.Contains(rate.Value))throw new InvalidDataException("格式请求参数无效。");
        return new(id,rate.Value);
    }

    private static bool Livejack(MMDevice device)=>device.DeviceFriendlyName.Contains("Livejack",StringComparison.OrdinalIgnoreCase);
    internal static Endpoint[] Read()
    {
        using var enumerator=new MMDeviceEnumerator();var result=new List<Endpoint>();
        foreach(var flow in new[]{DataFlow.Capture,DataFlow.Render})
            foreach(var device in enumerator.EnumerateAudioEndPoints(flow,DeviceState.Active))
                using(device)
                {
                    if(!Livejack(device))continue;
                    var bytes=ReadFormat(device);var mix=device.AudioClient.MixFormat;
                    result.Add(new(device.ID,device.FriendlyName,flow==DataFlow.Capture,BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)),(uint)mix.SampleRate,BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14)),BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2))));
                }
        return result.ToArray();
    }
    private static byte[] ReadFormat(MMDevice device)
    {
        if(device.Properties[FormatKey].Value is not byte[] bytes||bytes.Length<18)throw new InvalidDataException("Windows 未提供有效的默认音频格式。");
        return bytes;
    }
    internal static byte[] WithRate(byte[] source,uint rate)
    {
        if(!Rates.Contains(rate))throw new ArgumentOutOfRangeException(nameof(rate),"不支持的采样率。");
        if(source.Length<18||source.Length<18+BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(16)))throw new InvalidDataException("Windows 音频格式长度无效。");
        var tag=BinaryPrimitives.ReadUInt16LittleEndian(source);
        var align=BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(12));
        if(tag is not (1 or 3 or 0xfffe)||align==0)throw new InvalidDataException("仅支持 PCM / 浮点默认音频格式。");
        var copy=(byte[])source.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(4),rate);
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(8),checked(rate*align));
        return copy;
    }
    // SetDeviceFormat notifies the Windows audio engine; writing the property store alone does not.
    [ComImport,Guid("f8679f50-850a-41cf-9c72-430f290290c8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigFormat
    {
        [PreserveSig]int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)]string id,IntPtr format);
        [PreserveSig]int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)]string id,[MarshalAs(UnmanagedType.Bool)]bool defaults,IntPtr format);
        [PreserveSig]int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)]string id);
        [PreserveSig]int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)]string id,IntPtr format,IntPtr mixFormat);
    }
    private static void Write(MMDevice device,byte[] bytes)
    {
        var policy=(IPolicyConfigFormat)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"))!)!;
        var pointer=Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes,0,pointer,bytes.Length);
            Marshal.ThrowExceptionForHR(policy.SetDeviceFormat(device.ID,pointer,IntPtr.Zero));
        }
        finally{Marshal.FreeCoTaskMem(pointer);Marshal.ReleaseComObject(policy);}
    }
    // The elevated entry point revalidates the endpoint identity and bounded rate; it never trusts a supplied format blob.
    internal static string ApplyRequest(string requestPath)
    {
        try
        {
            var request=ReadRequest(requestPath);
            using var enumerator=new MMDeviceEnumerator();using var device=enumerator.GetDevice(request.EndpointId);
            if(!Livejack(device)||device.State!=DeviceState.Active)throw new InvalidDataException("目标不是可用的 Livejack 音频端点。");
            var before=ReadFormat(device);var changed=WithRate(before,request.SampleRate);
            if(before.SequenceEqual(changed)&&device.AudioClient.MixFormat.SampleRate==request.SampleRate)return "Windows 已为所选采样率，无需修改。";
            try
            {
                Write(device,changed);
                var confirmed=false;
                for(int attempt=0;attempt<20;attempt++)
                {
                    try
                    {
                        using var checkEnumerator=new MMDeviceEnumerator();using var check=checkEnumerator.GetDevice(request.EndpointId);
                        if(ReadFormat(check).SequenceEqual(changed)&&check.AudioClient.MixFormat.SampleRate==request.SampleRate){confirmed=true;break;}
                    }
                    catch(COMException) when(attempt<19){} // A resetting endpoint can be temporarily invalidated.
                    Thread.Sleep(100);
                }
                if(!confirmed)throw new IOException("Windows 未确认音频引擎切换；已尝试恢复原格式。请在 Windows 声音属性中修改。");
            }
            catch
            {
                try{Write(device,before);}
                catch(Exception restore){throw new IOException("Windows 未确认切换，且恢复失败；请检查声音属性。"+restore.Message);}
                throw;
            }
            return $"Windows 已确认 {request.SampleRate} Hz。正在使用端点的软件可能需要重新连接。";
        }
        catch(Exception e){return "Windows 设置失败："+e.Message;}
    }
    internal static async Task<string> ApplyAsync(string endpointId,uint rate)
    {
        if(!Rates.Contains(rate))throw new ArgumentOutOfRangeException(nameof(rate));
        var folder=Storage;Directory.CreateDirectory(folder);
        var request=Path.Combine(folder,"audio-format-"+Guid.NewGuid().ToString("N")+".json");var report=request+".txt";
        try
        {
            File.WriteAllText(request,JsonSerializer.Serialize(new Request(endpointId,rate)));
            using var identity=WindowsIdentity.GetCurrent();
            if(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))return await Task.Run(()=>ApplyRequest(request));
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas"};
            start.ArgumentList.Add("--set-audio-format");start.ArgumentList.Add(request);start.ArgumentList.Add(report);
            using var process=Process.Start(start)??throw new IOException("未启动格式设置程序。");
            await process.WaitForExitAsync();return File.Exists(report)?File.ReadAllText(report):"Windows 未返回设置结果。";
        }
        finally{if(File.Exists(request))File.Delete(request);if(File.Exists(report))File.Delete(report);}
    }
    internal static void OpenSoundProperties(bool capture)=>Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"control.exe")){UseShellExecute=true,Arguments=capture?"mmsys.cpl,,1":"mmsys.cpl,,0"});
}

using System.Diagnostics;
using System.Text.Json;
namespace Donner.Core;
public sealed class HostClient(ProcessStartInfo start, TimeSpan? timeout = null) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1,1);
    private readonly object lifecycle = new();
    private Process? process;
    private volatile bool disposed;
    public async Task<HostReply> SendAsync(HostRequest request)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Process current;
            lock(lifecycle)
            {
                ObjectDisposedException.ThrowIf(disposed,this);
                if(request.Operation=="detect")Reset();
                if(process is null || process.HasExited)
                {
                    Reset();
                    start.UseShellExecute=false; start.CreateNoWindow=true;
                    start.RedirectStandardInput=true; start.RedirectStandardOutput=true; start.RedirectStandardError=true;
                    start.StandardInputEncoding=new System.Text.UTF8Encoding(false);start.StandardOutputEncoding=new System.Text.UTF8Encoding(false);
                    process=Process.Start(start) ?? throw new IOException("无法启动设备辅助进程。");
                    _=process.StandardError.ReadToEndAsync();
                }
                current=process;
            }
            using var deadline=new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            try
            {
                await current.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(),deadline.Token).ConfigureAwait(false);
                await current.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                var line=await current.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false)
                    ?? throw new IOException("设备辅助进程已退出。");
                HostReply reply;
                try { reply=JsonSerializer.Deserialize<HostReply>(line) ?? throw new InvalidDataException("空响应。"); }
                catch(JsonException e) { throw new InvalidDataException("辅助进程响应格式无效。",e); }
                if(reply.Id!=request.Id) throw new InvalidDataException("辅助进程响应编号不匹配。");
                return reply;
            }
            catch(OperationCanceledException)
            {
                lock(lifecycle) Reset();
                throw new TimeoutException("设备调用超过时限，辅助进程已重置。设置可能已经改变，请重新检测。");
            }
            catch { lock(lifecycle) Reset(); throw; }
        }
        finally { gate.Release(); }
    }
    private void Reset()
    {
        if(process is null) return;
        try { if(!process.HasExited) process.Kill(entireProcessTree:true); }
        catch(InvalidOperationException) { }
        finally { process.Dispose();process=null; }
    }
    public void Dispose() { lock(lifecycle) { disposed=true;Reset(); } }
}
public sealed class PendingVolumes
{
    private readonly Dictionary<(uint,uint),Setting> values=[];
    public void Put(Setting setting)
    {
        if(setting.Kind!="volume") throw new InvalidDataException("只能合并音量设置。");
        values[(setting.Direction,setting.Channel)]=setting;
    }
    public Setting[] Drain() { var result=values.Values.ToArray();values.Clear();return result; }
}
public static class PresetCapture
{
    public static Preset From(DeviceSnapshot device,string name,bool linked)
    {
        if(!device.Connected || string.IsNullOrEmpty(device.DeviceId)) throw new InvalidDataException("请先连接设备。");
        var settings=new List<Setting>();
        foreach(var c in device.Channels)
        {
            if(c.CanVolume && c.Volume is {} volume) settings.Add(new("volume",c.Direction,c.Number,volume));
            if(c.CanMute && c.Muted is {} muted) settings.Add(new("mute",c.Direction,c.Number,muted?1:0));
        }
        if(device.SampleRate is {} rate && device.SampleRates.Contains(rate)) settings.Add(new("rate",Value:(int)rate));
        if(device.BufferSize is {} size && device.DeviceCount==1 && device.BufferRate!=0 && device.BufferSizes.Contains(size))
            settings.Add(new("buffer",Value:(int)size));
        if(device.SafeMode is {} safe&&device.DeviceCount==1&&device.BufferRate>0&&device.BufferSize is not null)
            settings.Add(new("safemode",Value:safe?1:0));
        if(device.Driver.WdmClients==0&&device.Driver.AsioClients==0)
        {
            if(device.Driver.StreamingMode is 1 or 2)
                settings.Add(new("streammode",Value:(int)device.Driver.StreamingMode.Value));
            if(device.Driver.FormatSelectionMode==1&&device.Driver.Dsd==false&&device.Driver.OutputFormat is {} format&&device.Driver.OutputFormats.Any(f=>f.Id==format.Id))
                settings.Add(new("format",Value:(int)format.Id));
        }
        var preset=new Preset(1,name,device.DeviceId,device.Demo,linked,settings.ToArray());
        ControlPolicy.ValidatePreset(device,preset);return preset;
    }
}

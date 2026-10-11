namespace Donner.Core;
public sealed class DeviceSession(Func<HostRequest,Task<HostReply>> send)
{
    public DeviceSnapshot Snapshot {get;private set;}=new();
    public bool Linked {get;set;}=true;
    private int generation;
    public void InvalidateHardware()
    {
        if(Snapshot.Demo)return;
        generation++;Snapshot=new() {Status="设备连接已改变，正在自动重新检测。"};
    }
    public void SetDemo(bool enabled)
    {
        generation++;Linked=true;
        Snapshot=enabled?new() {Connected=true,Demo=true,Status="演示模式 · 不会控制声卡",DeviceId="demo:livejack",DeviceCount=1,
            SampleRate=48000,SampleRates=[44100,48000,88200,96000,176400,192000],BufferSize=256,BufferRate=48000,
            BufferSizes=[8,16,32,64,128,256,512,1024,2048],Channels=[
                new(1,1,"输入 1",true,true,-32512,0,256,-2048,false),new(1,2,"输入 2",true,true,-32512,0,256,-4096,false),
                new(0,1,"输出左",true,true,-32512,0,256,-6144,false),new(0,2,"输出右",true,true,-32512,0,256,-6144,false)],
            Diagnostics=new() {["模式"]="演示数据，未向任何设备发送请求"} }:new() {Status="已退出演示，请重新检测真实设备"};
    }
    public async Task<HostReply> ApplyAsync(Setting[] settings)
    {
        foreach(var setting in settings) ControlPolicy.Validate(Snapshot,setting);
        if(Snapshot.Demo)
        {
            foreach(var s in settings)
                Snapshot=s.Kind switch {
                    "rate"=>Snapshot with {SampleRate=(uint)s.Value,BufferRate=(uint)s.Value},
                    "buffer"=>Snapshot with {BufferSize=(uint)s.Value},
                    "volume"=>Snapshot with {Channels=Snapshot.Channels.Select(c=>c.Direction==s.Direction&&c.Number==s.Channel?c with {Volume=(short)s.Value}:c).ToArray()},
                    "mute"=>Snapshot with {Channels=Snapshot.Channels.Select(c=>c.Direction==s.Direction&&c.Number==s.Channel?c with {Muted=s.Value==1}:c).ToArray()},
                    _=>Snapshot};
            return new("demo",true,"演示设置已更新（未控制真实设备）",Snapshot,settings.Select(s=>new OperationResult(s.Kind,true,"仅演示")).ToArray());
        }
        return await RequestAsync(new(Guid.NewGuid().ToString("N"),"apply",Settings:settings));
    }
    public async Task DetectAsync(string? path=null)
    {
        generation++;Snapshot=new();
        await RequestAsync(new(Guid.NewGuid().ToString("N"),"detect",path));
    }
    public async Task RefreshAsync(string? path=null)
    {
        if(Snapshot.Demo) return;
        if(!Snapshot.Connected)await DetectAsync(path);
        else await RequestAsync(new(Guid.NewGuid().ToString("N"),"read"));
    }
    private async Task<HostReply> RequestAsync(HostRequest request)
    {
        var current=generation;
        try
        {
            var reply=await send(request);
            if(current!=generation) throw new InvalidDataException("模式已改变，已丢弃旧响应。");
            Snapshot=reply.Snapshot??new() {Status=reply.Message};
            return reply;
        }
        catch(Exception e)
        {
            if(current==generation) Snapshot=new() {Status=e.Message};
            throw;
        }
    }
}

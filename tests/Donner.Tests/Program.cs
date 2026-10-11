using Donner.Core;
using System.Text.Json;
using System.Diagnostics;

if(args.Contains("--worker"))
{
    while(Console.ReadLine() is {} line)
    {
        var request=JsonSerializer.Deserialize<HostRequest>(line)!;
        if(request.Operation=="slow") Thread.Sleep(1500);
        var reply=new HostReply(request.Operation=="bad-id" ? "wrong" : request.Id,true,"fixture:"+Environment.ProcessId,new DeviceSnapshot {Status="未连接设备"});
        Console.WriteLine(JsonSerializer.Serialize(reply));
    }
    return 0;
}

var failures = 0; var passed = 0;
var temp = Path.Combine(Path.GetTempPath(), "donner-tests-" + Guid.NewGuid());
Directory.CreateDirectory(temp);
var device = new DeviceSnapshot { Connected = true, DeviceId = "livejack:test", SampleRate = 48000,
    SampleRates = [44100,48000], Channels = [
        new(0,1,"输出左",true,true,-25600,0,256,-2560,false),
        new(0,2,"输出右",true,true,-12800,0,256,-1280,true),
        new(1,1,"输入1",false,true,0,0,0,null,false)] };
void Test(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch(Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
void Check(bool value, string message) { if(!value) throw new Exception(message); }
void Reject(Action action) { try { action(); } catch(InvalidDataException) { return; }
    throw new Exception("应拒绝该输入"); }
Test("合法音量和采样率可应用",()=>{ControlPolicy.Validate(device,new("volume",0,1,-2560));ControlPolicy.Validate(device,new("rate",Value:44100));});
Test("无设备拒绝写入",()=>Reject(()=>ControlPolicy.Validate(device with {Connected=false},new("rate",Value:48000))));
Test("越界音量拒绝",()=>Reject(()=>ControlPolicy.Validate(device,new("volume",0,1,1))));
Test("非设备步进值拒绝",()=>Reject(()=>ControlPolicy.Validate(device,new("volume",0,1,-100))));
Test("不支持通道拒绝",()=>Reject(()=>ControlPolicy.Validate(device,new("volume",1,1,0))));
Test("静音不是任意整数",()=>Reject(()=>ControlPolicy.Validate(device,new("mute",0,1,2))));
Test("未枚举采样率拒绝",()=>Reject(()=>ControlPolicy.Validate(device,new("rate",Value:192000))));
Test("未确认缓冲能力拒绝",()=>Reject(()=>ControlPolicy.Validate(device,new("buffer",Value:64))));
Test("左右联动同时产生两路音量写入而不取消静音",()=>{var changes=ControlPolicy.VolumeChanges(device,0,1,-2560,true);Check(changes.Length==2 && changes.All(c=>c.Kind=="volume" && c.Value==-2560),"应只写两路音量");});
Test("联动值不兼容另一侧时拒绝整组操作",()=>Reject(()=>ControlPolicy.VolumeChanges(device,0,1,-15360,true)));
Test("输入通道不参与输出联动",()=>{var d=device with {Channels=[new(1,1,"输入",true,true,-25600,0,256,-2560,false)]};Check(ControlPolicy.VolumeChanges(d,1,1,-2560,true).Length==1,"只能写输入1");});
var preset = new Preset(1,"日常",device.DeviceId,false,true,[new("rate",Value:48000),new("mute",0,1,1)]);
Test("合法预设可通过能力检查",()=>ControlPolicy.ValidatePreset(device,preset));
Test("不同设备预设拒绝",()=>Reject(()=>ControlPolicy.ValidatePreset(device,preset with {DeviceId="other"})));
Test("演示预设不得应用真实设备",()=>Reject(()=>ControlPolicy.ValidatePreset(device,preset with {Demo=true})));
Test("未知预设版本拒绝",()=>Reject(()=>ControlPolicy.ValidatePreset(device,preset with {Version=99})));
Test("未知操作拒绝",()=>Reject(()=>ControlPolicy.Validate(device,new("firmware"))));
Test("重复配置项拒绝",()=>Reject(()=>ControlPolicy.ValidatePreset(device,preset with {Settings=[new("rate",Value:48000),new("rate",Value:44100)]})));
var store=new PresetStore(Path.Combine(temp,"presets"));
Test("预设中文保存与覆盖往返",()=>{store.Save(preset);store.Save(preset with {Linked=false});var all=store.ReadAll();Check(all.Length==1 && all[0].Name=="日常" && !all[0].Linked,"往返或覆盖失败");});
Test("预设名称不能穿越目录",()=>Reject(()=>store.Save(preset with {Name="../escape"})));
Test("删除预设",()=>{store.Delete("日常");Check(store.ReadAll().Length==0,"删除失败");});
Test("损坏文件不会被静默忽略",()=>{File.WriteAllText(Path.Combine(temp,"presets","bad.json"),"not json");Reject(()=>store.ReadAll());});
Test("捕获预设只包含可读写能力",()=>{var p=PresetCapture.From(device,"捕获",true);Check(!p.Settings.Any(s=>s.Kind=="volume"&&s.Direction==1),"不得保存不可控通道");Check(p.Settings.Any(s=>s.Kind=="mute"&&s.Direction==1),"应保存可读静音");Check(!p.Settings.Any(s=>s.Kind=="buffer"),"未知缓冲不能保存");});
Test("拖动音量合并为最后值",()=>{var pending=new PendingVolumes();pending.Put(new("volume",0,1,-2560));pending.Put(new("volume",0,1,-1280));pending.Put(new("volume",0,2,-512));var batch=pending.Drain();Check(batch.Length==2 && batch.Single(s=>s.Channel==1).Value==-1280,"不能积压旧音量");Check(pending.Drain().Length==0,"已取出的设置不应残留");});
ProcessStartInfo Worker() {
    var p=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"Donner.Tests.exe"));p.ArgumentList.Add("--worker");
    p.Environment["DOTNET_ROOT"]=Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),"../../.."));
    return p;
}
Test("隔离进程收发与编号匹配",()=>{using var client=new HostClient(Worker());var reply=client.SendAsync(new("req-1","detect")).GetAwaiter().GetResult();Check(reply.Id=="req-1" && reply.Snapshot is {Connected:false},"无设备结果不能变为连接成功");});
Test("错误响应编号被拒绝",()=>{using var client=new HostClient(Worker());Reject(()=>client.SendAsync(new("req-2","bad-id")).GetAwaiter().GetResult());});
Test("超时销毁旧进程后可以再次检测",()=>{using var client=new HostClient(Worker(),TimeSpan.FromMilliseconds(300));try{client.SendAsync(new("slow","slow")).GetAwaiter().GetResult();throw new Exception("未超时");}catch(TimeoutException){}var reply=client.SendAsync(new("new","detect")).GetAwaiter().GetResult();Check(reply.Id=="new","旧响应污染了新请求");});
Test("关闭客户端后拒绝新请求",()=>{var client=new HostClient(Worker());client.Dispose();try{client.SendAsync(new("x","detect")).GetAwaiter().GetResult();throw new Exception("未拒绝关闭后请求");}catch(ObjectDisposedException){}});
Directory.Delete(temp,true);
Test("音量能力结构按驱动协议解码",()=>{var bytes=Convert.FromHexString("03000000009C00000001000000000000");var info=NativeLayouts.VolumeInfo(bytes);Check(info.Volume && info.Mute && info.Minimum==-25600 && info.Maximum==0 && info.Step==256,"范围/能力解码失败");});
Test("ASIO尺寸按返回计数解码",()=>{var bytes=new byte[196];void U(int word,uint v)=>System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(word*4),v);U(1,48000);U(5,64);U(6,128);U(7,256);U(8,2);U(9,128);U(10,256);var info=NativeLayouts.AsioInfo(bytes);Check(info.Rate==48000 && info.Preferred==256 && info.Sizes.SequenceEqual(new uint[]{128,256}) && info.InputLatency==64,"结构字段错误");});
Test("ASIO异常计数拒绝",()=>Reject(()=>NativeLayouts.AsioInfo(Enumerable.Repeat((byte)255,196).ToArray())));
Test("音量短结构拒绝",()=>Reject(()=>NativeLayouts.VolumeInfo(new byte[8])));
Test("演示设置只改变演示状态",()=>{var session=new DeviceSession(_=>throw new Exception("不应调用真实辅助进程"));session.SetDemo(true);session.ApplyAsync([new("mute",0,1,1)]).GetAwaiter().GetResult();Check(session.Snapshot.Demo && session.Snapshot.Channels.Single(c=>c.Direction==0&&c.Number==1).Muted==true,"演示静音未更新");});
Test("退出演示时清空可写状态",()=>{var session=new DeviceSession(_=>throw new Exception("不应调用辅助进程"));session.SetDemo(true);session.SetDemo(false);Check(!session.Snapshot.Connected && !session.Snapshot.Demo,"演示值不应残留到真实设备");Reject(()=>session.ApplyAsync([new("rate",Value:48000)]).GetAwaiter().GetResult());});
Test("真实部分失败保留回读状态及失败项",()=>{var current=device with {Channels=[device.Channels[0] with {Muted=true},device.Channels[1]]};var session=new DeviceSession(r=>Task.FromResult(r.Operation=="detect"?new HostReply(r.Id,true,"ok",device):new HostReply(r.Id,false,"部分失败",current,[new("left",true,"ok"),new("right",false,"拒绝")])));session.DetectAsync().GetAwaiter().GetResult();var reply=session.ApplyAsync([new("mute",0,1,1),new("mute",0,2,1)]).GetAwaiter().GetResult();Check(!reply.Success && reply.Results!.Count(r=>!r.Success)==1 && session.Snapshot.Channels[0].Muted==true,"必须呈现回读和部分失败");});
Test("进程异常后清除可写设备状态",()=>{var session=new DeviceSession(r=>r.Operation=="detect"?Task.FromResult(new HostReply(r.Id,true,"ok",device)):throw new TimeoutException("timeout"));session.DetectAsync().GetAwaiter().GetResult();try{session.RefreshAsync().GetAwaiter().GetResult();}catch(TimeoutException){}Check(!session.Snapshot.Connected,"超时后仍可写入旧状态");});
Console.WriteLine($"结果：{passed} 通过，{failures} 失败");
Test("刷新不能覆盖尚未应用的采样率选择",()=>{var selection=new AudioSelection {Rate=44100};Check(selection.ResolveRate(device with {SampleRate=48000})==44100,"刷新丢失选择");selection.Clear();Check(selection.ResolveRate(device)==48000,"应用后应显示当前读数");});
Test("能力变化时清除不再支持的选择",()=>{var selection=new AudioSelection {Rate=192000,Buffer=256};Check(selection.ResolveRate(device)==48000 && selection.Rate is null,"不支持的采样率应清除");Check(selection.ResolveBuffer(device) is null && selection.Buffer is null,"不支持缓冲应清除");});
Console.WriteLine($"最终结果：{passed} 通过，{failures} 失败");
Test("损坏预设不阻止显示正常预设且报告错误",()=>{var dir=Path.Combine(Path.GetTempPath(),"donner-presets-"+Guid.NewGuid());try{var s=new PresetStore(dir);s.Save(preset);File.WriteAllText(Path.Combine(dir,"broken.json"),"broken");var valid=s.ReadAvailable(out var errors);Check(valid.Length==1&&valid[0].Name==preset.Name&&errors.Length==1&&errors[0].Contains("broken.json"),"必须保留正常预设及坏文件信息");}finally{Directory.Delete(dir,true);}});
Console.WriteLine($"全套结果：{passed} 通过，{failures} 失败");
Test("原生静音输出按完整32位读取",()=>{var result=NativeOutput.ReadInt32(pointer=>{System.Runtime.InteropServices.Marshal.WriteInt32(pointer,0x10000);return 0;});Check(result.Status==0&&result.Value==0x10000,"原生4字节输出被截断");});
Test("原生查询失败不使用输出数据",()=>{var result=NativeOutput.ReadInt32(pointer=>{System.Runtime.InteropServices.Marshal.WriteInt32(pointer,1);return 0xee000030;});Check(result.Status==0xee000030&&result.Value==0,"失败时不能使用输出值");});
Console.WriteLine($"验证结果：{passed} 通过，{failures} 失败");
Test("启动无设备后插入可通过后台刷新连接",()=>{
    var attached=false;var operations=new List<string>();
    var session=new DeviceSession(r=>{operations.Add(r.Operation);return Task.FromResult(new HostReply(r.Id,true,"检测",attached?device:new DeviceSnapshot {Status="未连接"}));});
    session.DetectAsync().GetAwaiter().GetResult();attached=true;
    session.RefreshAsync().GetAwaiter().GetResult();
    Check(session.Snapshot.Connected&&operations.SequenceEqual(new[]{"detect","detect"}),"未连接时后台必须重新枚举，不能停止检测");
});
Test("拔出后重新插入自动重新枚举且不写旧设置",()=>{
    var attached=true;var operations=new List<string>();
    var reconnected=device with {SampleRate=44100,Channels=[device.Channels[0] with {Volume=-512}]};
    var current=device;
    var session=new DeviceSession(r=>{operations.Add(r.Operation);return Task.FromResult(new HostReply(r.Id,true,"检测",attached?current:new DeviceSnapshot {Status="设备已断开"}));});
    session.DetectAsync().GetAwaiter().GetResult();attached=false;session.RefreshAsync().GetAwaiter().GetResult();
    Check(!session.Snapshot.Connected,"拔出后仍连接");
    attached=true;current=reconnected;session.RefreshAsync().GetAwaiter().GetResult();
    Check(session.Snapshot.Connected&&session.Snapshot.SampleRate==44100&&session.Snapshot.Channels[0].Volume==-512,"重连应读取新设置");
    Check(operations.SequenceEqual(new[]{"detect","read","detect"}),"重连只能检测和读取，不能写入");
});
Test("查询超时后下一次刷新可重连",()=>{
    var fail=false;var session=new DeviceSession(r=>r.Operation=="read"&&fail?throw new TimeoutException("设备掉线"):Task.FromResult(new HostReply(r.Id,true,"ok",device)));
    session.DetectAsync().GetAwaiter().GetResult();fail=true;
    try{session.RefreshAsync().GetAwaiter().GetResult();}catch(TimeoutException){}
    session.RefreshAsync().GetAwaiter().GetResult();Check(session.Snapshot.Connected,"超时后未重新检测");
});
Test("后台刷新在演示模式不访问硬件",()=>{
    var session=new DeviceSession(_=>throw new Exception("演示不应请求硬件"));session.SetDemo(true);session.RefreshAsync().GetAwaiter().GetResult();Check(session.Snapshot.Demo,"演示模式被退出");
});
Console.WriteLine($"重连验证结果：{passed} 通过，{failures} 失败");
Test("自动重连保留用户选择的API路径",()=>{
    HostRequest? request=null;var session=new DeviceSession(r=>{request=r;return Task.FromResult(new HostReply(r.Id,true,"ok",device));});
    session.RefreshAsync("C:\\custom\\DonnerUsbAudioapi_x64.dll").GetAwaiter().GetResult();
    Check(request is {Operation:"detect",ApiPath:"C:\\custom\\DonnerUsbAudioapi_x64.dll"},"重连丢失自定义DLL路径");
});
Test("连续无设备查询不会停止重连检测",()=>{
    var count=0;var session=new DeviceSession(r=>{Check(r.Operation=="detect","未连接时只能枚举");count++;return Task.FromResult(new HostReply(r.Id,true,"未连接",new DeviceSnapshot()));});
    for(var i=0;i<3;i++)session.RefreshAsync().GetAwaiter().GetResult();Check(count==3&&!session.Snapshot.Connected,"检测在等待设备时停止");
});
Console.WriteLine($"全部结果：{passed} 通过，{failures} 失败");
Test("音量百分比与原厂非线性刻度一致",()=>{
    Check(VolumeScale.ToRaw(1)==-17920&&VolumeScale.ToRaw(50)==-2816&&VolumeScale.ToRaw(100)==0,"没有采用原厂表");
    Check(VolumeScale.ToPercent(-2048)==58&&VolumeScale.ToPercent(-2560)==51,"回读应匹配原厂第一个相同档位");
    Check(VolumeScale.ToRaw(VolumeScale.ToPercent(-2816))==-2816,"回读不能改变音量");
});
Test("不支持或不可读的控件不出现在可用功能中",()=>{
    var absent=new ChannelState(1,1,"输入",false,false,0,0,0,null,null);
    Check(!DeviceCapabilities.HasVolume(absent)&&!DeviceCapabilities.HasMute(absent),"不支持的通道仍可用");
    Check(!DeviceCapabilities.HasVolume(device.Channels[2])&&DeviceCapabilities.HasMute(device.Channels[2]),"只支持静音时不能显示音量");
    Check(!DeviceCapabilities.HasVolume(device.Channels[0] with {Volume=null})&&!DeviceCapabilities.HasMute(device.Channels[0] with {Muted=null}),"读失败仍显示控制");
    Check(!DeviceCapabilities.HasRate(new())&&!DeviceCapabilities.HasBuffer(device),"未确认能力仍显示音频设置");
});
Console.WriteLine($"音量与能力结果：{passed} 通过，{failures} 失败");
Test("全部101档与原厂二进制表逐字节一致且符合设备步进",()=>{
    var bytes=new byte[202];var channel=new ChannelState(1,1,"输入",true,true,-32512,0,256,0,false);
    var d=device with {Channels=[channel]};
    for(var i=0;i<=100;i++){var raw=VolumeScale.ToRaw(i);System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i*2),raw);ControlPolicy.Validate(d,new("volume",1,1,raw));}
    Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))=="F66D2EE9CE8E5F363B07E90284D19E2052B67FDF98BA7ABD54FB089FD7619FD8","音量表与原厂不同");
});
Console.WriteLine($"最终全套：{passed} 通过，{failures} 失败");
Test("重新枚举使用新辅助进程清除旧连接状态",()=>{
    using var c=new HostClient(Worker());var first=c.SendAsync(new("first","detect")).GetAwaiter().GetResult();
    var read=c.SendAsync(new("read","read")).GetAwaiter().GetResult();Check(first.Message==read.Message,"普通读取不能重启辅助进程");
    var second=c.SendAsync(new("second","detect")).GetAwaiter().GetResult();Check(first.Message!=second.Message,"重新检测仍复用旧进程");
});
Console.WriteLine($"辅助进程重连：{passed} 通过，{failures} 失败");
Test("快速拔插通知使旧连接失效并丢弃正在返回的旧状态",()=>{
    var delayed=new TaskCompletionSource<HostReply>();
    var session=new DeviceSession(r=>r.Operation=="read"?delayed.Task:Task.FromResult(new HostReply(r.Id,true,"ok",device)));
    session.DetectAsync().GetAwaiter().GetResult();var reading=session.RefreshAsync();
    session.InvalidateHardware();Check(!session.Snapshot.Connected,"通知后旧连接仍有效");
    delayed.SetResult(new("old",true,"旧状态",device));
    Reject(()=>reading.GetAwaiter().GetResult());Check(!session.Snapshot.Connected,"旧响应恢复了失效设备");
    session.RefreshAsync().GetAwaiter().GetResult();Check(session.Snapshot.Connected,"通知后没有重新连接");
});
Test("拔插通知不退出演示模式",()=>{
    var session=new DeviceSession(_=>throw new Exception("不应访问硬件"));session.SetDemo(true);session.InvalidateHardware();Check(session.Snapshot.Demo,"通知破坏演示模式");
});
Console.WriteLine($"热插拔结果：{passed} 通过，{failures} 失败");
return failures==0?0:1;


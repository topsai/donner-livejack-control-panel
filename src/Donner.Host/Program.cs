using System.Text;
using System.Text.Json;
using Donner.Core;
using Donner.Host;

Console.InputEncoding=new UTF8Encoding(false);Console.OutputEncoding=new UTF8Encoding(false);
using var device=new NativeDevice();
while(Console.ReadLine() is {} line)
{
    string id="";
    HostReply reply;
    try
    {
        var request=JsonSerializer.Deserialize<HostRequest>(line) ?? throw new InvalidDataException("空请求。");
        id=request.Id;
        reply=request.Operation switch
        {
            "detect" => new(id,true,"检测完成",device.Detect(request.ApiPath)),
            "read" => new(id,true,"状态已刷新",device.Read()),
            "apply" => device.Apply(id,request.Settings ?? []),
            _ => throw new InvalidDataException("未知设备操作。")
        };
    }
    catch(Exception e)
    {
        device.Disconnect();
        reply=new(id,false,e.Message,new DeviceSnapshot {Status=e.Message,ApiPath=device.ApiPath});
    }
    Console.WriteLine(JsonSerializer.Serialize(reply));
}

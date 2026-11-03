using System.Buffers.Binary;
using Donner.Core;

namespace Donner.Panel;

internal static class MixerSampleRatesSmoke
{
    private sealed class FakeMixer:IMixerClient
    {
        internal readonly List<(string Key,float Value)> Writes=[];
        internal float Preferred=44100;
        internal bool DelayWrite;
        internal float? Pending;
        public bool Connected=>true;
        public string Status=>"测试连接";
        public bool Refresh(){if(Pending is {} p){Preferred=p;Pending=null;}return true;}
        public void Start(bool show=false)=>throw new Exception("测试不得启动音频引擎。");
        public float Get(string key)=>key=="Option.sr"?Preferred:0;
        public void Set(string key,float value){Writes.Add((key,value));if(key=="Option.sr"){if(DelayWrite)Pending=value;else Preferred=value;}}
        public string DeviceName(bool input,int index)=>"";
        public MixerDevice[] Devices(bool input)=>[];
        public void SetDevice(bool input,int index,MixerDevice device)=>throw new Exception("测试不得修改设备。");
        public float Level(bool input,int index)=>0;
        public void Dispose(){}
    }
    internal static async Task VerifyAsync()
    {
        Verify();
        using(var release=new ManualResetEventSlim(false))
        {
            int reads=0;var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var clock=System.Diagnostics.Stopwatch.StartNew();
            using var slow=new MixerSampleRates(new FakeMixer(),()=>new DeviceSnapshot(),_=>Task.CompletedTask,false,()=>{Interlocked.Increment(ref reads);started.TrySetResult();if(!release.Wait(4000))throw new IOException("测试读取超时");return [];});
            var first=slow.RefreshAsync();
            if(clock.ElapsedMilliseconds>500)throw new Exception("采样率查询阻塞了界面调用。");
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                bool serviced=false;await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(()=>serviced=true,System.Windows.Threading.DispatcherPriority.Input);
                await slow.RefreshAsync();
                if(!serviced||reads!=1||first.IsCompleted)throw new Exception("慢查询期间界面不能响应或查询发生重叠。");
            }
            finally{release.Set();}
            await first;
            slow.IsExpanded=true;if(slow.Visibility!=System.Windows.Visibility.Visible)throw new Exception("采样率设置无法展开。");
            slow.IsExpanded=false;if(slow.Visibility!=System.Windows.Visibility.Collapsed)throw new Exception("收起后仍占用采样率区域。");
        }
        using(var rates=new MixerSampleRates(new FakeMixer(),()=>new DeviceSnapshot(),_=>Task.CompletedTask,false,()=>[new("livejack-id","Livejack",true,48000,48000,24,2)]))
        {
            rates.RestoreFormats([new("livejack-id","Livejack",true,44100)]);await rates.RefreshAsync();
            if(rates.CaptureFormats().Single().Rate!=44100)throw new Exception("实际值覆盖了待恢复Windows格式。");
        }
        var client=new FakeMixer{DelayWrite=true};var hardwareWrites=0;
        using var control=new MixerSampleRates(client,()=>new DeviceSnapshot{Connected=true,SampleRate=44100,SampleRates=[44100,48000]},_=>{hardwareWrites++;return Task.CompletedTask;},false);
        control.Refresh();
        if(client.Writes.Count!=0||hardwareWrites!=0)throw new Exception("初始化或刷新修改了采样率。");
        var message=await control.ApplyBananaAsync(48000);
        if(!client.Writes.SequenceEqual(new[]{("Option.sr",48000f)})||client.Preferred!=48000||hardwareWrites!=0||!message.Contains("首选"))
            throw new Exception("首选保存未等待回读，或隐式重启/修改了硬件。");
        try{await control.ApplyBananaAsync(12345);throw new Exception("接受了无效 Banana 采样率。");}
        catch(ArgumentOutOfRangeException){}
        if(client.Writes.Count!=1)throw new Exception("无效采样率发送了写请求。");
    }
    internal static void Verify()
    {
        var storage=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DonnerControlPanel");
        var request=Path.Combine(storage,"audio-format-"+Guid.NewGuid().ToString("N")+".json");
        Directory.CreateDirectory(storage);
        if(!AudioEndpointFormats.ValidRequestPath(request)||!AudioEndpointFormats.ValidReportPath(request,request+".txt")
            ||AudioEndpointFormats.ValidReportPath(request,Path.Combine(storage,"other.txt"))||AudioEndpointFormats.ValidRequestPath(Path.Combine(Path.GetTempPath(),Path.GetFileName(request))))
            throw new Exception("提权请求路径边界校验失败。");
        try
        {
            foreach(var invalid in new[]{"{}","{\"EndpointId\":\"invalid\",\"SampleRate\":44100,\"SampleRate\":48000}","{\"EndpointId\":\"invalid\",\"SampleRate\":44100,\"Unexpected\":true}",new string('x',4097)})
            {
                File.WriteAllText(request,invalid);
                if(!AudioEndpointFormats.ApplyRequest(request).StartsWith("Windows 设置失败：",StringComparison.Ordinal))throw new Exception("无效提权请求被接受。");
            }
        }
        finally{File.Delete(request);}
        var original=new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(original,0xfffe);
        BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(4),44100);
        BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(8),176400);
        BinaryPrimitives.WriteUInt16LittleEndian(original.AsSpan(12),4);
        BinaryPrimitives.WriteUInt16LittleEndian(original.AsSpan(16),22);
        original[39]=123;
        var updated=AudioEndpointFormats.WithRate(original,48000);
        if(BinaryPrimitives.ReadUInt32LittleEndian(updated.AsSpan(4))!=48000||BinaryPrimitives.ReadUInt32LittleEndian(updated.AsSpan(8))!=192000||updated[39]!=123)
            throw new Exception("Windows 格式修改未保留格式布局。");
        if(BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(4))!=44100)throw new Exception("原始格式被修改。");
        foreach(var rate in new uint[]{0,12345,uint.MaxValue})
        {
            try{AudioEndpointFormats.WithRate(original,rate);throw new Exception("接受了无效采样率。");}
            catch(ArgumentOutOfRangeException){}
        }
        try{AudioEndpointFormats.WithRate(new byte[8],44100);throw new Exception("接受了损坏格式。");}
        catch(InvalidDataException){}
    }
}

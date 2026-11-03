using System.Runtime.InteropServices;
using System.Text.Json;
using Donner.Core;
namespace Donner.Panel;
internal static class MicrophoneEqChecks
{
    internal static void VerifyOutputBuffers(string report)
    {
        using var client=new VoicemeeterClient();var engine=client.InputEq;
        var process=typeof(MicrophoneEqInsert).GetMethod("Process",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.CreateDelegate<Func<IntPtr,int,IntPtr,int,int>>(engine);
        const int count=512;var buffer=Marshal.AllocHGlobal(16+256*8);var pointers=new List<IntPtr>();long allocated=0;
        try
        {
            for(int i=0;i<16+256*8;i++)Marshal.WriteByte(buffer,i,0);
            Marshal.WriteInt32(buffer,48000);Marshal.WriteInt32(buffer,4,count);Marshal.WriteInt32(buffer,8,98);Marshal.WriteInt32(buffer,12,64);
            for(int c=0;c<62;c++){var p=Marshal.AllocHGlobal(count*4);pointers.Add(p);Marshal.Copy(Enumerable.Repeat((float)(c+1)/100,count).ToArray(),0,p,count);Marshal.WriteIntPtr(buffer,16+c*8,p);}
            for(int c=0;c<40;c++){var p=Marshal.AllocHGlobal(count*4);pointers.Add(p);Marshal.WriteIntPtr(buffer,16+(128+c)*8,p);}
            long inputFrames=engine.Frames;var actual=new float[count];var spectrum=new float[2048];
            for(int bus=0;bus<5;bus++)
            {
                engine.OutputPreview=bus;for(int i=0;i<6;i++)process(IntPtr.Zero,20,buffer,0);
                engine.CopyOutputSpectrum(spectrum);float expected=(float)(22+bus*8+8)/100;
                if(spectrum.Any(x=>Math.Abs(x-expected)>.000001))throw new IOException("输出频谱未跟随所选总线或读错声道。");
                for(int c=0;c<40;c++){Marshal.Copy(pointers[62+c],actual,0,count);if(actual.Any(x=>x!=(float)(22+c+1)/100))throw new IOException("MAIN监看改动输出或错位串路。");}
            }
            if(engine.Frames!=inputFrames)throw new IOException("MAIN回调误增加输入处理帧数。");
            long before=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<100;i++)process(IntPtr.Zero,20,buffer,0);allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            if(allocated!=0||engine.CallbackFault!=0)throw new IOException("MAIN回调分配内存或异常。");
        }
        finally{foreach(var p in pointers)Marshal.FreeHGlobal(p);Marshal.FreeHGlobal(buffer);}
        File.WriteAllText(report,JsonSerializer.Serialize(new{Success=true,MaxHeader98_64=true,All40OutputsBitExact=true,FiveBusSpectra=true,InputFramesUnchanged=true,CallbackAllocatedBytes=allocated}));
    }
    internal static async Task VerifyAsync(string report)
    {
        using var client=new VoicemeeterClient();if(!client.Refresh())throw new IOException(client.Status);
        var engine=client.InputEq;
        // Test real ABI layout with isolated buffers before registering a live, flat insert.
        var process=typeof(MicrophoneEqInsert).GetMethod("Process",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.CreateDelegate<Func<IntPtr,int,IntPtr,int,int>>(engine);
        var buffer=Marshal.AllocHGlobal(16+256*8);var channels=new List<IntPtr>();
        bool synthetic=false;long allocated;
        try
        {
            for(int i=0;i<16+256*8;i++)Marshal.WriteByte(buffer,i,0);
            Marshal.WriteInt32(buffer,48000);Marshal.WriteInt32(buffer,4,512);Marshal.WriteInt32(buffer,8,22);Marshal.WriteInt32(buffer,12,22);
            for(int i=0;i<22;i++)
            {
                var source=Marshal.AllocHGlobal(512*4);var output=Marshal.AllocHGlobal(512*4);channels.Add(source);channels.Add(output);
                Marshal.Copy(Enumerable.Repeat((float)(i+.25),512).ToArray(),0,source,512);
                Marshal.WriteIntPtr(buffer,16+i*8,source);Marshal.WriteIntPtr(buffer,16+(128+i)*8,output);
            }
            for(int i=0;i<30;i++)process(IntPtr.Zero,10,buffer,0);
            var actual=new float[512];for(int i=0;i<22;i++){Marshal.Copy(channels[i*2+1],actual,0,512);if(actual.Any(x=>x!=(float)(i+.25)))throw new IOException("输入插入旁通未保留全部声道。");}
            long before=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<100;i++)process(IntPtr.Zero,10,buffer,0);allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            if(allocated!=0)throw new IOException($"回调路径存在逐帧分配：{allocated}");synthetic=true;
        }
        finally{foreach(var ptr in channels)Marshal.FreeHGlobal(ptr);Marshal.FreeHGlobal(buffer);}
        long initial=engine.Frames,outputInitial=engine.OutputFrames;engine.Monitor(true,0);engine.MonitorOutput(true,0);
        float peak=0,outputPeak=0;long liveFrames=0,liveOutputFrames=0;
        try{await Task.Delay(1500);engine.Maintain();var spectrum=new float[2048];engine.CopySpectrum(spectrum);peak=spectrum.Max(x=>Math.Abs(x));liveFrames=engine.Frames-initial;engine.CopyOutputSpectrum(spectrum);outputPeak=spectrum.Max(x=>Math.Abs(x));liveOutputFrames=engine.OutputFrames-outputInitial;if(liveOutputFrames<=0||!engine.MainRegistered)throw new IOException("输出频谱没有接到MAIN实际音频缓冲。");
            var graph=new EqGraph{Width=900,Height=300,SampleRate=engine.SampleRate};graph.UpdateSpectrum(spectrum,engine.HasAudio);graph.Measure(new System.Windows.Size(900,300));graph.Arrange(new System.Windows.Rect(0,0,900,300));graph.UpdateLayout();
            var image=new System.Windows.Media.Imaging.RenderTargetBitmap(900,300,96,96,System.Windows.Media.PixelFormats.Pbgra32);image.Render(graph);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));using(var file=File.Create(report+".png"))encoder.Save(file);if(engine.InputChannels<6||engine.OutputChannels<6)throw new IOException("实际麦克风插入声道数量不足。");if(engine.Frames<=initial)throw new IOException("Banana 输入插入未返回实际音频缓冲。");}
        finally{engine.MonitorOutput(false,0);engine.Monitor(false,0);await Task.Delay(600);engine.Maintain();engine.Dispose();}
        File.WriteAllText(report,JsonSerializer.Serialize(new{Success=true,SyntheticChannelBypass=synthetic,CallbackAllocatedBytes=allocated,LiveFrames=liveFrames,LiveOutputFrames=liveOutputFrames,OutputSpectrumPeak=outputPeak,SpectrumPeak=peak,BypassMonitoring=true,engine.SampleRate,engine.InputChannels,engine.OutputChannels,engine.CallbackFault,Released=!engine.Registered,FlatOnly=true},new JsonSerializerOptions{WriteIndented=true}));
    }
}

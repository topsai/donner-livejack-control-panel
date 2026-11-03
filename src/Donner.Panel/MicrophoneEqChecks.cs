using System.Runtime.InteropServices;
using System.Text.Json;
using Donner.Core;
namespace Donner.Panel;
internal static class MicrophoneEqChecks
{
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
        long initial=engine.Frames;engine.Monitor(true,0);
        float peak=0;long liveFrames=0;
        try{await Task.Delay(1500);engine.Maintain();var spectrum=new float[2048];engine.CopySpectrum(spectrum);peak=spectrum.Max(x=>Math.Abs(x));liveFrames=engine.Frames-initial;
            var graph=new EqGraph{Width=900,Height=300,SampleRate=engine.SampleRate};graph.UpdateSpectrum(spectrum,engine.HasAudio);graph.Measure(new System.Windows.Size(900,300));graph.Arrange(new System.Windows.Rect(0,0,900,300));graph.UpdateLayout();
            var image=new System.Windows.Media.Imaging.RenderTargetBitmap(900,300,96,96,System.Windows.Media.PixelFormats.Pbgra32);image.Render(graph);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));using(var file=File.Create(report+".png"))encoder.Save(file);if(engine.InputChannels<6||engine.OutputChannels<6)throw new IOException("实际麦克风插入声道数量不足。");if(engine.Frames<=initial)throw new IOException("Banana 输入插入未返回实际音频缓冲。");}
        finally{engine.Monitor(false,0);await Task.Delay(600);engine.Maintain();engine.Dispose();}
        File.WriteAllText(report,JsonSerializer.Serialize(new{Success=true,SyntheticChannelBypass=synthetic,CallbackAllocatedBytes=allocated,LiveFrames=liveFrames,SpectrumPeak=peak,BypassMonitoring=true,engine.SampleRate,engine.InputChannels,engine.OutputChannels,engine.CallbackFault,Released=!engine.Registered,FlatOnly=true},new JsonSerializerOptions{WriteIndented=true}));
    }
}

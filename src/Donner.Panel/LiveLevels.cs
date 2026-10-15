using Donner.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Donner.Panel;

internal sealed class LiveLevels : IDisposable
{
    private sealed class Source : IDisposable
    {
        private readonly object gate=new();
        private readonly MMDevice device;
        private readonly WasapiCapture capture;
        private readonly bool floating;
        private float left,right;
        public volatile bool Available;
        public int Packets;
        public string? Error;
        public Source(MMDevice device,bool loopback)
        {
            this.device=device;
            WasapiCapture? created=null;
            try
            {
                created=loopback?new WasapiLoopbackCapture(device):new WasapiCapture(device,true,50);capture=created;
                var format=capture.WaveFormat;
                floating=format.Encoding==WaveFormatEncoding.IeeeFloat||format is WaveFormatExtensible extensible&&extensible.SubFormat==new Guid("00000003-0000-0010-8000-00aa00389b71");
                bool pcm=format.Encoding==WaveFormatEncoding.Pcm||format is WaveFormatExtensible pcmFormat&&pcmFormat.SubFormat==new Guid("00000001-0000-0010-8000-00aa00389b71");
                if(!floating&&!pcm)throw new InvalidDataException("电平采样编码不受支持。");
                AudioPeaks.Stereo([],format.BitsPerSample,format.Channels,floating);
                capture.DataAvailable+=(_,e)=>
                {
                    Interlocked.Increment(ref Packets);
                    try
                    {
                        var peaks=AudioPeaks.Stereo(e.Buffer.AsSpan(0,e.BytesRecorded),format.BitsPerSample,format.Channels,floating);
                        lock(gate){left=Math.Max(left,peaks.Left);right=Math.Max(right,peaks.Right);}
                    }
                    catch(InvalidDataException error){Error=error.Message;Available=false;}
                };
                capture.RecordingStopped+=(_,e)=>{Available=false;if(e.Exception is not null)Error=e.Exception.Message;};
                Available=true;capture.StartRecording();
            }
            catch {try {created?.Dispose();}finally {device.Dispose();}throw;}
        }
        public (float,float) Read(){lock(gate){var result=(left,right);left=right=0;return result;}}
        public void Dispose(){Available=false;try {capture.StopRecording();}finally {try {capture.Dispose();}finally {device.Dispose();}}}
    }
    private Source? input,output;
    private long nextRetry=Environment.TickCount64+3000;
    public bool InputAvailable=>input?.Available==true;
    public bool OutputAvailable=>output?.Available==true;
    public int InputPackets=>input is {} source?Volatile.Read(ref source.Packets):0;
    public int OutputPackets=>output is {} source?Volatile.Read(ref source.Packets):0;
    public string? Error {get;private set;}
    public LiveLevels()
    {
        input=Open(DataFlow.Capture,false);output=Open(DataFlow.Render,true);
    }
    private Source? Open(DataFlow flow,bool loopback)
    {
            var matches=new List<MMDevice>();
            try
            {
                using var enumerator=new MMDeviceEnumerator();
                var endpoints=enumerator.EnumerateAudioEndPoints(flow,DeviceState.Active);
                foreach(var endpoint in endpoints)
                {
                    bool kept=false;
                    try {if(endpoint.FriendlyName.Contains("Livejack",StringComparison.OrdinalIgnoreCase)){matches.Add(endpoint);kept=true;}}
                    finally {if(!kept)endpoint.Dispose();}
                }
                if(matches.Count==1){var device=matches[0];matches.Clear();return new Source(device,loopback);}
            }
            catch(Exception error) when(error is System.Runtime.InteropServices.COMException or InvalidOperationException or InvalidDataException or NotSupportedException)
            {Error=error.Message;}
            finally {foreach(var endpoint in matches)endpoint.Dispose();}
            return null;
    }
    public void RetryStopped()
    {
        if(Environment.TickCount64<nextRetry)return;nextRetry=Environment.TickCount64+3000;
        void Release(Source? source){try {source?.Dispose();}catch(Exception error) when(error is System.Runtime.InteropServices.COMException or InvalidOperationException) {}}
        if(!InputAvailable){Release(input);input=Open(DataFlow.Capture,false);}
        if(!OutputAvailable){Release(output);output=Open(DataFlow.Render,true);}
    }
    public float[] Read()
    {
        var i=input?.Read()??(0f,0f);var o=output?.Read()??(0f,0f);
        return [i.Item1,i.Item2,o.Item1,o.Item2];
    }
    public void Dispose(){var i=input;var o=output;input=output=null;try {i?.Dispose();}finally {o?.Dispose();}}
}

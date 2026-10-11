namespace Donner.Core;
public sealed class AudioSelection
{
    public uint? Rate {get;set;}
    public uint? Buffer {get;set;}
    public uint? ResolveRate(DeviceSnapshot device)
    {
        if(!device.Connected){Rate=null;return null;}
        if(Rate is {} value && !device.SampleRates.Contains(value))Rate=null;
        return Rate??device.SampleRate;
    }
    public uint? ResolveBuffer(DeviceSnapshot device)
    {
        if(!device.Connected){Buffer=null;return null;}
        if(Buffer is {} value && !device.BufferSizes.Contains(value))Buffer=null;
        return Buffer??device.BufferSize;
    }
    public void Clear() {Rate=null;Buffer=null;}
}

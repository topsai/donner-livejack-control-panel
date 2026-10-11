namespace Donner.Core;
public static class VolumeScale
{
    // Original Donner x64 CPL SHA256 C186FCBE9D714768828F8095368C8E3E7EEC780BB60B38BFDC0F34300E2FD7BD.
    // 101 signed16 entries at VA 0x14004db10; get scans first matching entry at 0x14001df00.
    private static readonly short[] Values=[-32512,-17920,-15104,-13568,-12544,-11520,-11008,-10240,-9728,-9472,-8960,-8704,-8192,-7936,-7680,-7424,-7168,-6912,-6656,-6400,-6144,-6144,-5888,-5632,-5632,-5376,-5120,-5120,-4864,-4864,-4608,-4608,-4352,-4352,-4096,-4096,-4096,-3840,-3840,-3584,-3584,-3584,-3328,-3328,-3072,-3072,-2816,-2816,-2816,-2816,-2816,-2560,-2560,-2560,-2304,-2304,-2304,-2304,-2048,-2048,-2048,-2048,-1792,-1792,-1792,-1792,-1536,-1536,-1536,-1536,-1280,-1280,-1280,-1280,-1280,-1024,-1024,-1024,-1024,-1024,-768,-768,-768,-768,-768,-512,-512,-512,-512,-512,-512,-256,-256,-256,-256,-256,-256,-256,-256,-256,0];
    public static short ToRaw(int percent)=>Values[Math.Clamp(percent,0,100)];
    public static int ToPercent(short raw)
    {
        var exact=Array.IndexOf(Values,raw);if(exact>=0)return exact;
        // Values set outside the original UI can fall between its coarse low-volume entries.
        return Enumerable.Range(0,Values.Length).MinBy(i=>Math.Abs((int)Values[i]-raw));
    }
}
public static class DeviceCapabilities
{
    public static bool HasVolume(ChannelState c)=>c.CanVolume&&c.Volume is not null&&c.Step>0&&c.Maximum>c.Minimum;
    public static bool HasMute(ChannelState c)=>c.CanMute&&c.Muted is not null;
    public static bool HasRate(DeviceSnapshot d)=>d.Connected&&d.SampleRates.Length>0;
    public static bool HasBuffer(DeviceSnapshot d)=>d.Connected&&d.DeviceCount==1&&d.BufferRate>0&&d.BufferSizes.Length>0;
}
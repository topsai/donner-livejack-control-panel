using System.Buffers.Binary;
namespace Donner.Core;
public static class NativeLayouts
{
    public static (bool Volume,bool Mute,short Minimum,short Maximum,ushort Step) VolumeInfo(byte[] bytes)
    {
        if(bytes.Length!=16) throw new InvalidDataException("音量能力结构大小错误。");
        var flags=BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var min=BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(4));
        var max=BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(6));
        var step=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8));
        return ((flags&1)!=0 && min<=max && step>0,(flags&2)!=0,min,max,step);
    }
    public static (uint Rate,uint Preferred,uint[] Sizes,uint InputLatency,uint OutputLatency,uint Flags,bool Dsd) AsioInfo(byte[] bytes)
    {
        if(bytes.Length!=196) throw new InvalidDataException("ASIO 结构大小错误。");
        uint U(int word)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(word*4));
        var count=U(8);
        if(count>32) throw new InvalidDataException("ASIO 返回的缓冲数量超过协议上限。");
        return (U(1),U(7),Enumerable.Range(0,(int)count).Select(i=>U(9+i)).Where(v=>v>0&&v<=1048576).Distinct().ToArray(),U(5),U(6),U(4),(U(3)&1)!=0);
    }
    public static StreamFormat Format(byte[] bytes)
    {
        if(bytes.Length!=332)throw new InvalidDataException("音频格式结构大小错误。");
        uint U(int offset)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        if(U(0)==0||U(4) is 0 or >64||U(8) is 0 or >64)throw new InvalidDataException("音频格式返回值无效。");
        return new(U(0),U(4),U(8),System.Text.Encoding.Unicode.GetString(bytes,12,256).Split('\0')[0]);
    }
    public static ClockInfo Clock(byte[] bytes)
    {
        if(bytes.Length!=340)throw new InvalidDataException("时钟结构大小错误。");
        uint U(int offset)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        if(U(0)==0||U(12)>1||U(16)>1000000)throw new InvalidDataException("时钟返回值无效。");
        return new(U(0),System.Text.Encoding.Unicode.GetString(bytes,20,256).Split('\0')[0],U(12)==1,U(16));
    }
    public static uint BufferFlags(DeviceSnapshot device,bool? safeMode=null)
    {
        if(device.AsioFlags is not {} flags)throw new InvalidDataException("未读取安全模式状态。");
        return safeMode is {} enabled?(flags&~0x10000u)|(enabled?0x10000u:0):flags;
    }
}

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
    public static (uint Rate,uint Preferred,uint[] Sizes,uint InputLatency,uint OutputLatency) AsioInfo(byte[] bytes)
    {
        if(bytes.Length!=196) throw new InvalidDataException("ASIO 结构大小错误。");
        uint U(int word)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(word*4));
        var count=U(8);
        if(count>32) throw new InvalidDataException("ASIO 返回的缓冲数量超过协议上限。");
        return (U(1),U(7),Enumerable.Range(0,(int)count).Select(i=>U(9+i)).Where(v=>v>0&&v<=1048576).Distinct().ToArray(),U(5),U(6));
    }
}

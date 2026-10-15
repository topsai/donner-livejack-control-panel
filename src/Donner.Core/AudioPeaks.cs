using System.Buffers.Binary;
namespace Donner.Core;

public static class AudioPeaks
{
    public static (float Left,float Right) Stereo(ReadOnlySpan<byte> bytes,int bits,int channels,bool floating)
    {
        if(channels!=2||bits is not (16 or 24 or 32)||floating&&bits!=32)
            throw new InvalidDataException("电平采样格式不受支持。");
        int size=bits/8,frame=size*channels;float left=0,right=0;
        for(int offset=0;offset+frame<=bytes.Length;offset+=frame)
        {
            var l=Math.Abs(Sample(bytes.Slice(offset,size),bits,floating));var r=Math.Abs(Sample(bytes.Slice(offset+size,size),bits,floating));
            if(float.IsFinite(l))left=Math.Max(left,l);if(float.IsFinite(r))right=Math.Max(right,r);
        }
        return (Math.Clamp(left,0,1),Math.Clamp(right,0,1));
    }
    private static float Sample(ReadOnlySpan<byte> bytes,int bits,bool floating)=>floating
        ?BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes))
        :bits switch {16=>BinaryPrimitives.ReadInt16LittleEndian(bytes)/32768f,
            24=>((bytes[0]|bytes[1]<<8|bytes[2]<<16)<<8>>8)/8388608f,
            _=>BinaryPrimitives.ReadInt32LittleEndian(bytes)/2147483648f};
    public static double Db(float peak)=>peak>0?20*Math.Log10(peak):double.NegativeInfinity;
}

namespace Donner.Core;

public static class MixerFaderValue
{
    public static double Pointer(double value)
    {
        if(!double.IsFinite(value))throw new InvalidDataException("推子增益无效。");
        value=Math.Clamp(value,-60,12);
        return Math.Abs(value)<=1?0:Math.Round(value,1,MidpointRounding.AwayFromZero);
    }
}

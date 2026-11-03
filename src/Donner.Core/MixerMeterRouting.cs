namespace Donner.Core;

public static class MixerMeterRouting
{
    public static int UniqueBus(IReadOnlyList<string> outputDevices,Func<string,bool> targetsDevice)
    {
        int result=-1;
        for(int i=0;i<Math.Min(3,outputDevices.Count);i++)
            if(!string.IsNullOrWhiteSpace(outputDevices[i])&&targetsDevice(outputDevices[i]))
            {if(result>=0)return -1;result=i;}
        return result;
    }
    public static int OutputChannel(int bus,int channel)
    {
        if(bus is <0 or >4||channel is <0 or >7)throw new InvalidDataException("输出总线或声道无效。");
        return bus*8+channel;
    }
}

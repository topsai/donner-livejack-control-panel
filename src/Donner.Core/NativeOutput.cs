using System.Runtime.InteropServices;
namespace Donner.Core;
public static class NativeOutput
{
    public static (uint Status,int Value) ReadInt32(Func<IntPtr,uint> call)
    {
        var pointer=Marshal.AllocHGlobal(sizeof(int));
        try {Marshal.WriteInt32(pointer,0);var status=call(pointer);return (status,status==0?Marshal.ReadInt32(pointer):0);}
        finally {Marshal.FreeHGlobal(pointer);}
    }
}

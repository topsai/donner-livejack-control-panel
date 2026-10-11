using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace Donner.Host;
internal static class DeviceTopology
{
    public static string? Model(string instanceId)
    {
        if(!instanceId.StartsWith("USB\\VID_152A&PID_885C\\",StringComparison.OrdinalIgnoreCase))return null;
        try
        {
            using var key=Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\"+instanceId);
            var value=key?.GetValue("FriendlyName") as string??key?.GetValue("DeviceDesc") as string;
            var name=value?.Split(';').Last().Trim();
            return string.IsNullOrWhiteSpace(name)?null:name;
        }
        catch(Exception e) when(e is UnauthorizedAccessException or System.Security.SecurityException or IOException){return null;}
    }
    [StructLayout(LayoutKind.Sequential)] private struct InterfaceData
    { public uint Size;public Guid ClassGuid;public uint Flags;public IntPtr Reserved; }
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr SetupDiGetClassDevsW(ref Guid guid,IntPtr enumerator,IntPtr parent,uint flags);
    [DllImport("setupapi.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set,IntPtr device,ref Guid guid,uint index,ref InterfaceData data);
    [DllImport("setupapi.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    public static uint Count()
    {
        var guid=new Guid("8EBCE255-AA2D-4CD5-A142-5C8683F6905D");
        var set=SetupDiGetClassDevsW(ref guid,IntPtr.Zero,IntPtr.Zero,0x12);
        if(set==new IntPtr(-1))throw new Win32Exception(Marshal.GetLastWin32Error(),"无法检测设备连接变化。");
        try
        {
            uint count=0;
            while(true)
            {
                var data=new InterfaceData {Size=(uint)Marshal.SizeOf<InterfaceData>()};
                if(SetupDiEnumDeviceInterfaces(set,IntPtr.Zero,ref guid,count,ref data)){count++;continue;}
                var error=Marshal.GetLastWin32Error();
                if(error==259)return count;
                throw new Win32Exception(error,"无法枚举设备连接状态。");
            }
        }
        finally {_=SetupDiDestroyDeviceInfoList(set);}
    }
}

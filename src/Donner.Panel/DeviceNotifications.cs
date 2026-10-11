using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Donner.Panel;

internal sealed class DeviceNotifications : IDisposable
{
    internal static readonly Guid AudioInterface=new("8EBCE255-AA2D-4CD5-A142-5C8683F6905D");
    private static readonly Guid UsbInterface=new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    [StructLayout(LayoutKind.Sequential)] private struct Filter
    { public uint Size,Type,Reserved;public Guid ClassGuid;public ushort Name; }
    [DllImport("user32.dll",EntryPoint="RegisterDeviceNotificationW",SetLastError=true)] private static extern IntPtr Register(IntPtr window,ref Filter filter,uint flags);
    [DllImport("user32.dll",EntryPoint="UnregisterDeviceNotification")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool Unregister(IntPtr handle);
    [DllImport("user32.dll",EntryPoint="SendMessageW")] private static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr code,IntPtr data);
    private readonly HwndSource source;
    private readonly Action<string> changed;
    private readonly List<IntPtr> handles=[];
    public DeviceNotifications(IntPtr window,Action<string> changed)
    {
        this.changed=changed;source=HwndSource.FromHwnd(window)??throw new InvalidOperationException("窗口消息源不可用。");
        try
        {
            foreach(var guid in new[]{AudioInterface,UsbInterface})
            {
                var filter=new Filter {Size=(uint)Marshal.SizeOf<Filter>(),Type=5,ClassGuid=guid};
                var handle=Register(window,ref filter,0);
                if(handle==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"无法订阅设备拔插通知。");
                handles.Add(handle);
            }
            source.AddHook(Message);
        }
        catch {Dispose();throw;}
    }
    private IntPtr Message(IntPtr window,int message,IntPtr code,IntPtr data,ref bool handled)
    {
        if(message!=0x219||code.ToInt64() is not (0x8000 or 0x8004)||data==IntPtr.Zero)return IntPtr.Zero;
        var size=Marshal.ReadInt32(data);if(size<28||size>65536||Marshal.ReadInt32(data,4)!=5)return IntPtr.Zero;
        var guid=Marshal.PtrToStructure<Guid>(IntPtr.Add(data,12));
        if(guid!=AudioInterface)
        {
            if(guid!=UsbInterface||size<30)return IntPtr.Zero;
            var name=Marshal.PtrToStringUni(IntPtr.Add(data,28),(size-28)/2)??"";
            if(!name.Contains("VID_152A&PID_885C",StringComparison.OrdinalIgnoreCase))return IntPtr.Zero;
        }
        // Queue work; never enumerate or wait inside the Windows notification handler.
        source.Dispatcher.BeginInvoke(()=>changed(code.ToInt64()==0x8000?"已收到设备插入通知":"已收到设备拔出通知"));
        return IntPtr.Zero;
    }
    internal static void SendTestChange(IntPtr window)
    {
        var bytes=new byte[32];BitConverter.GetBytes(32).CopyTo(bytes,0);BitConverter.GetBytes(5).CopyTo(bytes,4);AudioInterface.ToByteArray().CopyTo(bytes,12);
        var data=Marshal.AllocHGlobal(bytes.Length);
        try {Marshal.Copy(bytes,0,data,bytes.Length);SendMessage(window,0x219,new IntPtr(0x8004),data);}
        finally {Marshal.FreeHGlobal(data);}
    }
    public void Dispose(){source.RemoveHook(Message);foreach(var handle in handles)Unregister(handle);handles.Clear();}
}

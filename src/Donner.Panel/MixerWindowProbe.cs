using System.Diagnostics;
using System.Runtime.InteropServices;
namespace Donner.Panel;
internal static class MixerWindowProbe
{
    private delegate bool EnumProc(IntPtr window,IntPtr data);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumProc callback,IntPtr data);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    internal static int VisibleCount()
    {
        var ids=new HashSet<int>();foreach(var process in Process.GetProcessesByName("voicemeeterpro_x64"))using(process)ids.Add(process.Id);
        foreach(var process in Process.GetProcessesByName("voicemeeterpro"))using(process)ids.Add(process.Id);
        int count=0;EnumWindows((window,_)=>{GetWindowThreadProcessId(window,out var process);if(ids.Contains((int)process)&&IsWindowVisible(window))count++;return true;},IntPtr.Zero);return count;
    }
}

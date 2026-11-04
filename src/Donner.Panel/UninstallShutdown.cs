using System.Security.Cryptography;
using System.Text;

namespace Donner.Panel;

// Per-user and per-installation: uninstalling one copy must not close another copy.
internal sealed class UninstallShutdown : IDisposable
{
    private readonly EventWaitHandle request;
    private readonly EventWaitHandle stopped;
    private readonly RegisteredWaitHandle registration;
    internal static string EventName(string executable,string identity)=>@"Local\DonnerControlPanel.Uninstall."+
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity+"|"+Path.GetFullPath(executable).ToUpperInvariant())));
    private static string Name=>EventName(Environment.ProcessPath!,System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);

    internal UninstallShutdown(Action close)
    {
        request=new(false,EventResetMode.AutoReset,Name);
        stopped=new(false,EventResetMode.ManualReset,Name+".Stopped");
        stopped.Reset();
        registration=ThreadPool.RegisterWaitForSingleObject(request,(_,_)=>close(),null,Timeout.Infinite,true);
    }
    internal static int Request()
    {
        try
        {
            using var request=EventWaitHandle.OpenExisting(Name);
            using var stopped=EventWaitHandle.OpenExisting(Name+".Stopped");
            request.Set();
            return stopped.WaitOne(TimeSpan.FromSeconds(20))?0:1;
        }
        catch(WaitHandleCannotBeOpenedException){return 0;}
    }
    public void Dispose()
    {
        registration.Unregister(null);
        stopped.Set();
        request.Dispose();stopped.Dispose();
    }
}

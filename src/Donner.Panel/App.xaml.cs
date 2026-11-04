using System.Windows;
namespace Donner.Panel;
public partial class App : System.Windows.Application
{
    private Mutex? instanceMutex;
    private EventWaitHandle? wakeEvent;
    private RegisteredWaitHandle? wakeRegistration;
    private UninstallShutdown? uninstallShutdown;
    protected override void OnExit(ExitEventArgs e)
    {uninstallShutdown?.Dispose();wakeRegistration?.Unregister(null);wakeEvent?.Dispose();if(instanceMutex is not null){instanceMutex.ReleaseMutex();instanceMutex.Dispose();}base.OnExit(e);}

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Length==1&&e.Args[0]=="--shutdown-for-uninstall")
        {Shutdown(UninstallShutdown.Request());return;}
        if(e.Args.Length==3&&e.Args[0]=="--set-audio-format")
        {
            if(!AudioEndpointFormats.ValidReportPath(e.Args[1],e.Args[2])){Shutdown(1);return;}
            var result=AudioEndpointFormats.ApplyRequest(e.Args[1]);var written=HelperReports.WriteNew(e.Args[2],result);Shutdown(!written||result.StartsWith("Windows 设置失败",StringComparison.Ordinal)?1:0);return;
        }
        if(e.Args.Length==3&&e.Args[0]=="--customize-audio-names")
        {
            var storage=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DonnerControlPanel");
            var report=Path.GetFullPath(e.Args[2]);
            if(!MixerEndpointNames.ValidCustomReportPath(storage,e.Args[1],report)){Shutdown(1);return;}
            try{var result=MixerEndpointNames.ApplyCustomRequest(storage,e.Args[1]);Shutdown(HelperReports.WriteNew(report,result)?0:1);}
            catch(Exception error){HelperReports.WriteNew(report,error.Message);Shutdown(1);}return;
        }
        if(e.Args.Length==2&&(e.Args[0]=="--simplify-audio-names"||e.Args[0]=="--restore-audio-names"))
        {
            try{var result=MixerEndpointNames.Change(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DonnerControlPanel"),e.Args[0]=="--restore-audio-names");var written=HelperReports.WriteNew(e.Args[1],result);Shutdown(!written||result.Contains('\n')?1:0);}
            catch(Exception error){HelperReports.WriteNew(e.Args[1],error.ToString());Shutdown(1);}return;
        }
        if(e.Args.Length==2&&e.Args[0]=="--verify-mixer-background")
        {
            try
            {
                using var client=new VoicemeeterClient();client.Start();
                for(var attempt=0;attempt<80&&!client.Refresh();attempt++)await Task.Delay(250);
                await Task.Delay(1000);
                var result=new{client.Connected,client.Status,VisibleWindows=MixerWindowProbe.VisibleCount()};
                File.WriteAllText(e.Args[1],System.Text.Json.JsonSerializer.Serialize(result));Shutdown(result.Connected&&result.VisibleWindows==0?0:1);
            }
            catch(Exception error){HelperReports.WriteNew(e.Args[1],error.ToString());Shutdown(1);}return;
        }
        if(e.Args.Length==2&&e.Args[0]=="--verify-microphone-eq")
        {
            try{await MicrophoneEqChecks.VerifyAsync(e.Args[1]);Shutdown(0);}
            catch(Exception error){File.WriteAllText(e.Args[1],error.ToString());Shutdown(1);}return;
        }
        if(e.Args.Length==2&&e.Args[0]=="--verify-output-buffers")
        {
            try{MicrophoneEqChecks.VerifyOutputBuffers(e.Args[1]);Shutdown(0);}catch(Exception error){File.WriteAllText(e.Args[1],error.ToString());Shutdown(1);}return;
        }
        if(e.Args.Length==2&&e.Args[0]=="--capture-mixer-configuration")
        {
            try{MixerConfigurationExport.Capture(e.Args[1]);Shutdown(0);}catch(Exception error){File.WriteAllText(e.Args[1],error.ToString());Shutdown(1);}return;
        }
        if(!e.Args.Contains("--smoke")&&!e.Args.Contains("--reconnect-check"))
        {
            string identity=System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value??Environment.UserName;
            var mutex=new Mutex(false,@"Local\DonnerControlPanel."+identity);bool acquired;
            try{acquired=mutex.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
            if(!acquired)
            {
                try{using var signal=EventWaitHandle.OpenExisting(@"Local\DonnerControlPanel.Wake."+identity);signal.Set();}catch(WaitHandleCannotBeOpenedException){}
                mutex.Dispose();Shutdown(0);return;
            }
            instanceMutex=mutex;wakeEvent=new(false,EventResetMode.AutoReset,@"Local\DonnerControlPanel.Wake."+identity);
            wakeRegistration=ThreadPool.RegisterWaitForSingleObject(wakeEvent,(_,_)=>Dispatcher.BeginInvoke(()=>{if(MainWindow is {} existing){existing.Show();existing.WindowState=WindowState.Normal;existing.Activate();}}),null,Timeout.Infinite,false);
        }
        var window=new MainWindow(e.Args);MainWindow=window;
        if(instanceMutex is not null)uninstallShutdown=new(()=>Dispatcher.BeginInvoke(()=>window.Close()));
        window.Show();
    }
}

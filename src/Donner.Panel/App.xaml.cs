using System.Windows;
namespace Donner.Panel;
public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Length==2&&(e.Args[0]=="--simplify-audio-names"||e.Args[0]=="--restore-audio-names"))
        {
            try{var result=MixerEndpointNames.Change(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DonnerControlPanel"),e.Args[0]=="--restore-audio-names");File.WriteAllText(e.Args[1],result);Shutdown(result.Contains('\n')?1:0);}
            catch(Exception error){File.WriteAllText(e.Args[1],error.ToString());Shutdown(1);}return;
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
            catch(Exception error){File.WriteAllText(e.Args[1],error.ToString());Shutdown(1);}return;
        }
        var window=new MainWindow(e.Args);MainWindow=window;window.Show();
    }
}

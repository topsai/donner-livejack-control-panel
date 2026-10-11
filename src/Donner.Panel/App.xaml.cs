using System.Windows;
namespace Donner.Panel;
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window=new MainWindow(e.Args);MainWindow=window;window.Show();
    }
}

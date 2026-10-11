using Microsoft.Win32;

namespace Donner.Panel;

internal sealed class StartupRegistration(string keyPath="Software\\Microsoft\\Windows\\CurrentVersion\\Run",string valueName="DonnerControlPanel")
{
    public string? Command {get {using var key=Registry.CurrentUser.OpenSubKey(keyPath);return key?.GetValue(valueName) as string;}}
    public bool Enabled=>Command is not null;
    public void Set(bool enabled,string executable)
    {
        using var key=Registry.CurrentUser.CreateSubKey(keyPath,true);
        if(enabled)
        {
            if(!File.Exists(executable))throw new FileNotFoundException("启动程序不存在。",executable);
            key.SetValue(valueName,$"\"{Path.GetFullPath(executable)}\" --startup",RegistryValueKind.String);
        }
        else key.DeleteValue(valueName,false);
    }
}

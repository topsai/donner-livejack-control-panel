using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Donner.Core;
using NAudio.CoreAudioApi;

namespace Donner.Panel;

internal static class MixerEndpointNames
{
    internal sealed record Endpoint(string Id,string Original,string Current,string Label,bool Capture);
    internal sealed record Backup(string Id,string Before,string Applied);
    private static string BaseName(string name)
    {int end=name.IndexOf(" (VB-Audio",StringComparison.OrdinalIgnoreCase);return end<0?name:name[..end];}
    private static bool Named(string actual,string label)=>actual==label||actual.StartsWith(label+" (",StringComparison.Ordinal);
    private static List<Backup> ReadBackups(string path)
    {
        if(!File.Exists(path))return [];
        var saved=JsonSerializer.Deserialize<List<Backup>>(File.ReadAllText(path))??throw new InvalidDataException("名称备份损坏。");
        if(saved.Any(x=>x is null||string.IsNullOrWhiteSpace(x.Id)||string.IsNullOrWhiteSpace(x.Before)||string.IsNullOrWhiteSpace(x.Applied))||saved.Select(x=>x.Id).Distinct().Count()!=saved.Count)
            throw new InvalidDataException("名称备份损坏；请保留文件以便恢复。");
        return saved;
    }
    internal static Endpoint[] List(string? storage=null)
    {
        using var enumerator=new MMDeviceEnumerator();var result=new List<Endpoint>();
        storage??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DonnerControlPanel");
        var backupPath=Path.Combine(storage,"mixer-endpoint-names.json");
        var backups=ReadBackups(backupPath);
        foreach(var flow in new[]{DataFlow.Render,DataFlow.Capture})
            foreach(var device in enumerator.EnumerateAudioEndPoints(flow,DeviceState.Active|DeviceState.Disabled|DeviceState.Unplugged))
                using(device)
                {
                    // Adapter identifies the vendor; saved endpoint IDs preserve the original descriptor after renaming.
                    if(!device.DeviceFriendlyName.Contains("Voicemeeter",StringComparison.OrdinalIgnoreCase))continue;
                    var original=device.Properties[PropertyKeys.PKEY_Device_DeviceDesc].Value as string??"";
                    var saved=backups.FirstOrDefault(x=>x.Id==device.ID);
                    if(saved is not null)original=BaseName(saved.Before);
                    if(MixerNames.Endpoint(original,flow==DataFlow.Capture) is {} label)
                        result.Add(new(device.ID,original,device.FriendlyName,label,flow==DataFlow.Capture));
                }
        return result.ToArray();
    }
    internal static string Change(string storage,bool restore)
    {
        Directory.CreateDirectory(storage);var path=Path.Combine(storage,"mixer-endpoint-names.json");
        var backups=ReadBackups(path);
        var endpoints=List(storage);int changed=0,skipped=0;var errors=new List<string>();
        foreach(var endpoint in endpoints)
        {
            var saved=backups.FirstOrDefault(x=>x.Id==endpoint.Id);
            if(restore&&saved is null)continue;
            if(restore&&!Named(endpoint.Current,saved!.Applied)){skipped++;continue;}
            if(!restore&&saved is not null&&!Named(endpoint.Current,saved.Applied)&&endpoint.Current!=saved.Before){skipped++;continue;}
            if(!restore&&saved is null&&endpoint.Current!=endpoint.Original&&!endpoint.Current.StartsWith(endpoint.Original+" (",StringComparison.Ordinal)){skipped++;continue;}
            if(!restore&&saved is null)
            {
                saved=new(endpoint.Id,endpoint.Current,endpoint.Label);backups.Add(saved);
                // Write original names durably before the first Windows modification.
                var temp=path+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(backups,new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,path,true);
            }
            try
            {
                SetName(endpoint.Id,restore?BaseName(saved!.Before):endpoint.Label);
                using var enumerator=new MMDeviceEnumerator();using var device=enumerator.GetDevice(endpoint.Id);
                if(restore?device.FriendlyName!=saved!.Before:!Named(device.FriendlyName,endpoint.Label))throw new IOException("Windows 未确认新名称。");
                changed++;
                if(restore){backups.Remove(saved!);var temp=path+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(backups));File.Move(temp,path,true);}
            }
            catch(Exception e) when(e is COMException or IOException or UnauthorizedAccessException){errors.Add(endpoint.Original+"："+e.Message);}
        }
        return $"已{(restore?"恢复":"简化")} {changed} 个设备名称，跳过 {skipped} 个自定义名称。"+(errors.Count==0?"":"\n"+string.Join("\n",errors));
    }
    private static void SetName(string id,string name)
    {
        using var enumerator=new MMDeviceEnumerator();using var device=enumerator.GetDevice(id);
        device.GetPropertyInformation(NAudio.CoreAudioApi.Interfaces.StorageAccessMode.ReadWrite);
        var pointer=Marshal.StringToCoTaskMemUni(name);
        try
        {
            var value=new NAudio.CoreAudioApi.Interfaces.PropVariant{vt=31,pointerValue=pointer};
            device.Properties.SetValue(PropertyKeys.PKEY_Device_DeviceDesc,value);device.Properties.Commit();
        }
        finally{Marshal.FreeCoTaskMem(pointer);}
    }
    internal static void Show(Window owner,string storage)
    {
        var window=new Window{Title="音频设备名称与用途",Owner=owner,Width=760,Height=550,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var panel=new DockPanel{Margin=new Thickness(16)};window.Content=panel;
        var actions=new StackPanel{Margin=new Thickness(0,12,0,0)};DockPanel.SetDock(actions,Dock.Bottom);panel.Children.Add(actions);
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap};actions.Children.Add(status);
        var buttons=new WrapPanel();actions.Children.Add(buttons);
        var text=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontSize=13};panel.Children.Add(text);
        void Refresh()=>text.Text="Windows 播放设备：把电脑或伴奏声音送进调音台。\nWindows 录音设备：供直播/会议软件读取混音或监听总线。\n重命名不改变设备ID、默认设备或路由；部分软件需要重启才能刷新名称。\n\n"+string.Join("\n\n",List(storage).Select(x=>$"{(x.Capture?"录音":"播放")} · {x.Label}\n当前：{x.Current}\n原厂：{x.Original}"));
        foreach(var restore in new[]{false,true})
        {
            var button=new Button{Content=restore?"恢复更名前名称":"简化 Windows 设备名称",Padding=new Thickness(12,8,12,8),Margin=new Thickness(0,8,10,0)};buttons.Children.Add(button);
            button.Click+=async(_,_)=>
            {
                buttons.IsEnabled=false;
                try
                {
                    using var identity=WindowsIdentity.GetCurrent();
                    if(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))status.Text=Change(storage,restore);
                    else
                    {
                        var report=Path.Combine(storage,"endpoint-rename-"+Guid.NewGuid().ToString("N")+".txt");
                        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas"};
                        start.ArgumentList.Add(restore?"--restore-audio-names":"--simplify-audio-names");start.ArgumentList.Add(report);
                        using var process=Process.Start(start)??throw new IOException("未启动名称修改程序。");
                        await process.WaitForExitAsync();status.Text=File.Exists(report)?File.ReadAllText(report):"未完成名称修改。";if(File.Exists(report))File.Delete(report);
                    }
                    Refresh();
                }
                catch(Exception e) when(e is COMException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception){status.Text=e.Message;}
                finally{buttons.IsEnabled=true;}
            };
        }
        Refresh();window.ShowDialog();
    }
}

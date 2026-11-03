using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using Donner.Core;
using NAudio.CoreAudioApi;
namespace Donner.Panel;
internal static class MixerEndpointNames
{
    internal sealed record Endpoint(string Id,string Original,string Current,string Label,bool Capture,string Descriptor="",bool Virtual=false,bool Active=true);
    internal sealed record Backup(string Id,string Before,string Applied,string? OriginalDescriptor=null,bool Custom=false);
    internal sealed record Rename(string Id,string ExpectedDescriptor,string Name);
    internal sealed record RenameRequest(int Version,Rename[] Changes);
    private static readonly object DisplayCacheLock=new();
    private static DateTime displayCacheStamp;
    private static long displayCacheLength=-1;
    private static Backup[] displayCache=[];
    internal static string? TryCustomDisplay(string name,string id)
    {
        lock(DisplayCacheLock)
        {
            try
            {
                var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DonnerControlPanel","mixer-endpoint-names.json");
                var info=new FileInfo(path);var stamp=info.Exists?info.LastWriteTimeUtc:DateTime.MinValue;var length=info.Exists?info.Length:-1;
                if(stamp!=displayCacheStamp||length!=displayCacheLength)
                {displayCache=[];displayCacheStamp=stamp;displayCacheLength=length;displayCache=ReadBackups(path).Where(x=>x.Custom).ToArray();}
                return CustomDisplay(name,id,displayCache);
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {return null;}
        }
    }
    internal static string? CustomDisplay(string name,string id,IReadOnlyList<Backup> backups)
    {
        bool Matches(Backup item)=>item.Custom&&(name==item.Applied||name.StartsWith(item.Applied+" (",StringComparison.Ordinal));
        var exact=backups.FirstOrDefault(x=>x.Id==id);
        if(exact is not null)return Matches(exact)?name:null;
        return backups.Count(Matches)==1?name:null;
    }
    private static string BaseName(string name){int end=name.IndexOf(" (VB-Audio",StringComparison.OrdinalIgnoreCase);return end<0?name:name[..end];}
    internal static string ValidateName(string? name)
    {
        if(string.IsNullOrWhiteSpace(name)||name!=name.Trim()||name.Length>120||name.Any(char.IsControl))throw new InvalidDataException("设备名称须为 1–120 个字符，不能含首尾空格、换行或控制字符。");
        return name;
    }
    internal static List<Backup> ReadBackups(string path)
    {
        if(!File.Exists(path))return [];
        var saved=JsonSerializer.Deserialize<List<Backup>>(File.ReadAllText(path),new JsonSerializerOptions{AllowDuplicateProperties=false})??throw new InvalidDataException("名称备份损坏。");
        if(saved.Any(x=>x is null||string.IsNullOrWhiteSpace(x.Id)||string.IsNullOrWhiteSpace(x.Before)||string.IsNullOrWhiteSpace(x.Applied)||x.OriginalDescriptor is not null&&string.IsNullOrWhiteSpace(x.OriginalDescriptor))||saved.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=saved.Count)throw new InvalidDataException("名称备份损坏；请保留文件以便恢复。");
        return saved;
    }
    private static void SaveBackups(string path,List<Backup> backups)
    {
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){JsonSerializer.Serialize(stream,backups,new JsonSerializerOptions{WriteIndented=true});stream.Flush(true);}
            File.Move(temp,path,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    internal static Endpoint[] List(string? storage=null)
    {
        using var enumerator=new MMDeviceEnumerator();var result=new List<Endpoint>();
        storage??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DonnerControlPanel");
        var backups=ReadBackups(Path.Combine(storage,"mixer-endpoint-names.json"));
        foreach(var flow in new[]{DataFlow.Render,DataFlow.Capture})foreach(var device in enumerator.EnumerateAudioEndPoints(flow,DeviceState.Active|DeviceState.Disabled|DeviceState.Unplugged))using(device)
        {
            var descriptor=device.Properties[PropertyKeys.PKEY_Device_DeviceDesc].Value as string??"";
            var saved=backups.FirstOrDefault(x=>x.Id==device.ID);
            var original=saved?.OriginalDescriptor??(saved is null?descriptor:BaseName(saved.Before));
            bool virtualDevice=device.DeviceFriendlyName.Contains("Voicemeeter",StringComparison.OrdinalIgnoreCase);
            var suggested=virtualDevice?MixerNames.Endpoint(original,flow==DataFlow.Capture):null;
            if(device.State!=DeviceState.Active&&suggested is null&&saved is null)continue;
            result.Add(new(device.ID,original,device.FriendlyName,suggested??descriptor,flow==DataFlow.Capture,descriptor,virtualDevice,device.State==DeviceState.Active));
        }
        return result.ToArray();
    }
    internal static bool Applied(Endpoint endpoint,Backup saved)=>endpoint.Descriptor==saved.Applied;
    internal static string Change(string storage,bool restore)
    {
        Directory.CreateDirectory(storage);var path=Path.Combine(storage,"mixer-endpoint-names.json");
        using var mutationLock=new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var backups=ReadBackups(path);
        int changed=0,skipped=0;var errors=new List<string>();
        foreach(var endpoint in List(storage))
        {
            var saved=backups.FirstOrDefault(x=>x.Id==endpoint.Id);
            if(restore&&saved is null)continue;
            if(!restore&&(!endpoint.Virtual||MixerNames.Endpoint(endpoint.Original,endpoint.Capture) is null))continue;
            if(restore&&!Applied(endpoint,saved!)){skipped++;continue;}
            if(!restore&&saved is not null&&(saved.Custom||!Applied(endpoint,saved)&&endpoint.Current!=saved.Before)){skipped++;continue;}
            var target=restore?saved!.OriginalDescriptor??BaseName(saved.Before):endpoint.Label;
            if(!restore&&saved is null){saved=new(endpoint.Id,endpoint.Current,target,endpoint.Descriptor);backups.Add(saved);SaveBackups(path,backups);}
            try
            {
                SetAndVerify(endpoint.Id,target,endpoint.Descriptor);changed++;
                if(restore){backups.Remove(saved!);SaveBackups(path,backups);}
            }
            catch(Exception e) when(e is COMException or IOException or UnauthorizedAccessException){errors.Add(endpoint.Original+"："+e.Message);}
        }
        return $"已{(restore?"恢复":"简化")} {changed} 个设备名称，跳过 {skipped} 个自定义名称。"+(errors.Count==0?"":"\n"+string.Join("\n",errors));
    }
    internal static RenameRequest ParseRequest(string json)
    {
        var request=JsonSerializer.Deserialize<RenameRequest>(json,new JsonSerializerOptions{UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,AllowDuplicateProperties=false})??throw new InvalidDataException("名称修改请求为空。");
        if(request.Version!=1||request.Changes is null||request.Changes.Length is <1 or >256)throw new InvalidDataException("名称修改请求格式不正确。");
        if(request.Changes.Any(x=>x is null||string.IsNullOrWhiteSpace(x.Id)||x.Id.Length>1024||x.Id.Any(char.IsControl)||string.IsNullOrWhiteSpace(x.ExpectedDescriptor)||x.ExpectedDescriptor.Length>1024||x.ExpectedDescriptor.Any(char.IsControl))||request.Changes.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=request.Changes.Length)throw new InvalidDataException("名称修改请求含无效或重复的设备。");
        foreach(var change in request.Changes)ValidateName(change.Name);
        return request;
    }
    internal static bool ValidCustomReportPath(string storage,string requestPath,string reportPath)
    {
        var full=Path.GetFullPath(requestPath);var name=Path.GetFileName(full);
        return Path.GetDirectoryName(full)!.Equals(Path.GetFullPath(storage).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase)&&name.StartsWith("endpoint-custom-",StringComparison.Ordinal)&&name.EndsWith(".json",StringComparison.Ordinal)&&name.Length==53&&Guid.TryParseExact(name[16..^5],"N",out _)&&Path.GetFullPath(reportPath).Equals(full+".txt",StringComparison.OrdinalIgnoreCase);
    }
    internal static string ApplyCustomRequest(string storage,string requestPath)
    {
        var full=Path.GetFullPath(requestPath);var directory=Path.GetFullPath(storage).TrimEnd(Path.DirectorySeparatorChar);var name=Path.GetFileName(full);
        if(!string.Equals(Path.GetDirectoryName(full),directory,StringComparison.OrdinalIgnoreCase)||!name.StartsWith("endpoint-custom-",StringComparison.Ordinal)||!name.EndsWith(".json",StringComparison.Ordinal)||name.Length!=53||!Guid.TryParseExact(name[16..^5],"N",out _))throw new InvalidDataException("名称修改请求必须位于本用户的面板数据目录。");
        var info=new FileInfo(full);if(!HelperReports.PlainPath(full)||info.Length>262144||(info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("名称修改请求文件不正确。");
        var request=ParseRequest(File.ReadAllText(full));var path=Path.Combine(storage,"mixer-endpoint-names.json");
        using var mutationLock=new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var backups=ReadBackups(path);
        var endpoints=List(storage).ToDictionary(x=>x.Id,StringComparer.Ordinal);
        foreach(var change in request.Changes)if(!endpoints.TryGetValue(change.Id,out var endpoint)||!endpoint.Active||endpoint.Descriptor!=change.ExpectedDescriptor)throw new InvalidDataException("设备列表或名称已变化，请重新打开名称窗口后再保存。");
        int changed=0;var errors=new List<string>();
        foreach(var change in request.Changes)
        {
            var endpoint=endpoints[change.Id];if(endpoint.Descriptor==change.Name)continue;
            var old=backups.FirstOrDefault(x=>x.Id==change.Id);
            var saved=old is null||!Applied(endpoint,old)?new Backup(endpoint.Id,endpoint.Current,change.Name,endpoint.Descriptor,true):old with{Applied=change.Name,Custom=true,OriginalDescriptor=old.OriginalDescriptor??BaseName(old.Before)};
            if(old is not null)backups.Remove(old);backups.Add(saved);SaveBackups(path,backups);
            try{SetAndVerify(change.Id,change.Name,endpoint.Descriptor);changed++;}
            catch(Exception e) when(e is COMException or IOException or UnauthorizedAccessException)
            {
                // Restore the prior backup only if the Windows descriptor demonstrably did not change.
                try
                {
                    using var enumerator=new MMDeviceEnumerator();using var device=enumerator.GetDevice(change.Id);
                    if((device.Properties[PropertyKeys.PKEY_Device_DeviceDesc].Value as string)==endpoint.Descriptor){backups.Remove(saved);if(old is not null)backups.Add(old);SaveBackups(path,backups);}
                }
                catch(Exception readError) when(readError is COMException or IOException or UnauthorizedAccessException){errors.Add("读取恢复信息："+readError.Message);}
                errors.Add(endpoint.Current+"："+e.Message);
            }
        }
        return $"已保存 {changed} 个 Windows 设备名称；面板设备列表将同步刷新。"+(errors.Count==0?"":"\n"+string.Join("\n",errors));
    }
    private static void SetAndVerify(string id,string name,string expected)
    {
        SetName(id,name,expected);using var enumerator=new MMDeviceEnumerator();using var device=enumerator.GetDevice(id);
        if((device.Properties[PropertyKeys.PKEY_Device_DeviceDesc].Value as string)!=name)throw new IOException("Windows 未确认新名称。");
    }
    private static void SetName(string id,string name,string expected)
    {
        using var enumerator=new MMDeviceEnumerator();using var device=enumerator.GetDevice(id);device.GetPropertyInformation(NAudio.CoreAudioApi.Interfaces.StorageAccessMode.ReadWrite);
        if((device.Properties[PropertyKeys.PKEY_Device_DeviceDesc].Value as string)!=expected)throw new IOException("设备名称刚刚被其他程序修改，请刷新后重试。");
        var pointer=Marshal.StringToCoTaskMemUni(name);
        try{var value=new NAudio.CoreAudioApi.Interfaces.PropVariant{vt=31,pointerValue=pointer};device.Properties.SetValue(PropertyKeys.PKEY_Device_DeviceDesc,value);device.Properties.Commit();}
        finally{Marshal.FreeCoTaskMem(pointer);}
    }
    internal static void Show(Window owner,string storage,Action<Window>? opened=null)
    {
        ReadBackups(Path.Combine(storage,"mixer-endpoint-names.json"));
        bool closed=false,writing=false;
        var window=new Window{Title="音频设备名称与用途",Owner=owner,Width=850,Height=650,WindowStartupLocation=WindowStartupLocation.CenterOwner};var panel=new DockPanel{Margin=new Thickness(16)};window.Content=panel;
        var intro=new TextBlock{Text="自定义 Windows 播放 / 录音设备名称，并同步到本面板。名称修改需要管理员权限。\n不改变设备 ID、默认设备、采样率或路由；其他软件可能需要重启刷新。\n如调音台按旧名称选择硬件，请在保存后重新选择对应设备。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};DockPanel.SetDock(intro,Dock.Top);panel.Children.Add(intro);
        var actions=new StackPanel{Margin=new Thickness(0,12,0,0)};DockPanel.SetDock(actions,Dock.Bottom);panel.Children.Add(actions);var status=new TextBlock{TextWrapping=TextWrapping.Wrap};actions.Children.Add(status);var buttons=new WrapPanel();actions.Children.Add(buttons);
        window.Closing+=(_,e)=>{if(writing)e.Cancel=true;else closed=true;};
        var rows=new StackPanel();panel.Children.Add(new ScrollViewer{Content=rows,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});var editors=new List<(Endpoint Endpoint,TextBox Editor)>();
        async Task RefreshAsync()
        {
            buttons.IsEnabled=false;rows.IsEnabled=false;
            try
            {
                var endpoints=await Task.Run(()=>List(storage));if(closed)return;
                rows.Children.Clear();editors.Clear();
                foreach(var endpoint in endpoints.OrderBy(x=>x.Capture).ThenBy(x=>!x.Active))
            {
                var row=new StackPanel{Margin=new Thickness(0,0,0,14)};
                row.Children.Add(new TextBlock{Text=$"{(endpoint.Capture?"录音 / 输入":"播放 / 输出")} · {endpoint.Current}"+(endpoint.Active?"":"（未连接 / 已禁用）"),TextWrapping=TextWrapping.Wrap,FontWeight=FontWeights.SemiBold});
                if(endpoint.Virtual)row.Children.Add(new TextBlock{Text="用途："+endpoint.Label,TextWrapping=TextWrapping.Wrap});
                row.Children.Add(new TextBlock{Text="更名前："+endpoint.Original,TextWrapping=TextWrapping.Wrap,Opacity=.7});
                var editor=new TextBox{Text=endpoint.Descriptor,MaxLength=120,IsEnabled=endpoint.Active,Margin=new Thickness(0,5,8,0)};row.Children.Add(editor);rows.Children.Add(row);editors.Add((endpoint,editor));
                }
            }
            catch(Exception e) when(e is COMException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException){if(!closed)status.Text=e.Message;}
            finally{if(!closed&&!writing){buttons.IsEnabled=true;rows.IsEnabled=true;}}
        }
        async Task Run(string command,string? request=null)
        {
            writing=true;buttons.IsEnabled=false;rows.IsEnabled=false;status.Text="正在保存并检查…";
            try
            {
                using var identity=WindowsIdentity.GetCurrent();
                if(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))status.Text=await Task.Run(()=>request is null?Change(storage,command=="--restore-audio-names"):ApplyCustomRequest(storage,request));
                else
                {
                    Directory.CreateDirectory(storage);var report=request is null?Path.Combine(storage,"endpoint-rename-"+Guid.NewGuid().ToString("N")+".txt"):request+".txt";var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas"};
                    start.ArgumentList.Add(command);if(request is not null)start.ArgumentList.Add(request);start.ArgumentList.Add(report);
                    using var process=Process.Start(start)??throw new IOException("未启动名称修改程序。");await process.WaitForExitAsync();status.Text=File.Exists(report)?File.ReadAllText(report):"未完成名称修改。";if(File.Exists(report))File.Delete(report);
                }
                await RefreshAsync();
            }
            catch(Exception e) when(e is COMException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception){status.Text=e.Message;}
            finally{writing=false;buttons.IsEnabled=true;rows.IsEnabled=true;if(request is not null&&File.Exists(request))File.Delete(request);}
        }
        var save=new Button{Content="保存自定义名称",Padding=new Thickness(12,8,12,8),Margin=new Thickness(0,8,10,0)};buttons.Children.Add(save);
        save.Click+=async(_,_)=>
        {
            try
            {
                var changes=editors.Where(x=>x.Endpoint.Active&&x.Editor.Text!=x.Endpoint.Descriptor).Select(x=>new Rename(x.Endpoint.Id,x.Endpoint.Descriptor,ValidateName(x.Editor.Text))).ToArray();
                if(changes.Length==0){status.Text="名称没有变化。";return;}
                Directory.CreateDirectory(storage);var request=Path.Combine(storage,"endpoint-custom-"+Guid.NewGuid().ToString("N")+".json");File.WriteAllText(request,JsonSerializer.Serialize(new RenameRequest(1,changes)));await Run("--customize-audio-names",request);
            }
            catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException){status.Text=e.Message;}
        };
        foreach(var restore in new[]{false,true})
        {
            var button=new Button{Content=restore?"恢复更名前名称":"简化虚拟设备名称",Padding=new Thickness(12,8,12,8),Margin=new Thickness(0,8,10,0)};buttons.Children.Add(button);button.Click+=async(_,_)=>await Run(restore?"--restore-audio-names":"--simplify-audio-names");
        }
        status.Text="正在读取设备…";
        window.Loaded+=async(_,_)=>{await RefreshAsync();if(!closed&&status.Text=="正在读取设备…")status.Text="";};
        window.Loaded+=(_,_)=>opened?.Invoke(window);
        window.ShowDialog();
    }
}

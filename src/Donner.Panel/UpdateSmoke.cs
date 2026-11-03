using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Donner.Core;
namespace Donner.Panel;
public partial class MainWindow
{
    private async Task VerifyUpdates()
    {
        if(GearUpdateDot.Visibility!=Visibility.Collapsed||AboutUpdateDot.Visibility!=Visibility.Collapsed)throw new Exception("未检查时错误显示更新红点。");
        var current=new ReleaseUpdate(CurrentVersion,"v"+CurrentVersion.ToString(3),"");
        var newer=new ReleaseUpdate(new Version(CurrentVersion.Major,CurrentVersion.Minor+1),$"v{CurrentVersion.Major}.{CurrentVersion.Minor+1}","新版本更新说明（界面测试数据）");
        var pending=new TaskCompletionSource<ReleaseUpdate?>();var task=CheckUpdatesAsync(()=>pending.Task);
        if(CheckUpdateButton.IsEnabled||task.IsCompleted)throw new Exception("更新检查未保持异步。");
        pending.SetResult(newer);await task;
        if(GearUpdateDot.Visibility!=Visibility.Visible||AboutUpdateDot.Visibility!=Visibility.Visible||OpenUpdateButton.Visibility!=Visibility.Visible)throw new Exception("发现新版本未同步显示提示。");
        AboutMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if(Pages.SelectedIndex!=5||GearUpdateDot.Visibility!=Visibility.Visible)throw new Exception("关于菜单未打开或错误清除红点。");
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);SaveImage("about-update.png");
        await CheckUpdatesAsync(()=>Task.FromException<ReleaseUpdate?>(new System.Net.Http.HttpRequestException("测试网络失败")));
        if(GearUpdateDot.Visibility!=Visibility.Visible||!CheckUpdateButton.IsEnabled)throw new Exception("暂时失败丢失已确认更新或无法重试。");
        await CheckUpdatesAsync(()=>Task.FromResult<ReleaseUpdate?>(current));
        if(GearUpdateDot.Visibility!=Visibility.Collapsed||AboutUpdateDot.Visibility!=Visibility.Collapsed||OpenUpdateButton.Visibility!=Visibility.Collapsed)throw new Exception("相同版本仍有红点。");
        await CheckUpdatesAsync(()=>Task.FromResult<ReleaseUpdate?>(new(new Version(0,1),"v0.1","")));
        if(GearUpdateDot.Visibility!=Visibility.Collapsed)throw new Exception("较旧版本误报。");
        ApplyUpdate(null);await CheckUpdatesAsync(()=>Task.FromException<ReleaseUpdate?>(new TaskCanceledException()));
        if(GearUpdateDot.Visibility!=Visibility.Collapsed||UpdateStatus.Text.Contains("最新"))throw new Exception("网络失败误报最新或显示红点。");
        ApplyUpdate(current);Pages.SelectedIndex=0;
        File.WriteAllText(Path.Combine(smokeDirectory!,"update-checks.json"),JsonSerializer.Serialize(new{Success=true,Checks=new[]{"异步请求","齿轮与关于红点同步","进入关于保留红点","相同与旧版无红点","失败重试及保留已知更新","无网络不误报最新"}}));
    }
}

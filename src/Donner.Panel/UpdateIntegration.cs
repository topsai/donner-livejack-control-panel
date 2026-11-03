using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Donner.Core;
namespace Donner.Panel;
public partial class MainWindow
{
    private readonly HttpClient updateHttp=new(){Timeout=TimeSpan.FromSeconds(10)};
    private readonly CancellationTokenSource updateCancellation=new();
    private readonly DispatcherTimer updateTimer=new(){Interval=TimeSpan.FromHours(6)};
    private bool checkingUpdate;
    private ReleaseUpdate? latestUpdate;
    private Version CurrentVersion=>typeof(MainWindow).Assembly.GetName().Version??new Version(0,0);
    private void InitializeUpdates()
    {
        Loaded+=async(_,_)=>{if(smokeDirectory is not null||reconnectCheckDirectory is not null)return;updateTimer.Start();await CheckUpdatesAsync();};
        updateTimer.Tick+=async(_,_)=>await CheckUpdatesAsync();
        Closed+=(_,_)=>{updateTimer.Stop();updateCancellation.Cancel();updateHttp.Dispose();updateCancellation.Dispose();};
    }
    private async void CheckUpdateClick(object sender,RoutedEventArgs e)=>await CheckUpdatesAsync();
    private async Task CheckUpdatesAsync(Func<Task<ReleaseUpdate?>>? fetch=null)
    {
        if(checkingUpdate||closed)return;checkingUpdate=true;CheckUpdateButton.IsEnabled=false;UpdateStatus.Text="正在检查更新…";
        try
        {
            var release=await (fetch is null?new GitHubUpdateClient(updateHttp).CheckAsync(updateCancellation.Token):fetch());
            if(closed)return;ApplyUpdate(release);
        }
        catch(Exception error) when(error is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {if(!closed){UpdateStatus.Text=latestUpdate?.IsNewerThan(CurrentVersion)==true?$"已发现新版本 {latestUpdate.Tag}；本次重新检查失败，请稍后重试。":"暂时无法检查更新，请稍后重试。";UpdateStatus.ToolTip=error.Message;}}
        finally{checkingUpdate=false;if(!closed)CheckUpdateButton.IsEnabled=true;}
    }
    private void ApplyUpdate(ReleaseUpdate? release)
    {
        latestUpdate=release;bool available=release?.IsNewerThan(CurrentVersion)==true;
        GearUpdateDot.Visibility=AboutUpdateDot.Visibility=OpenUpdateButton.Visibility=available?Visibility.Visible:Visibility.Collapsed;
        GearButton.ToolTip=available?"设置与其他页面 · 有新版本":"设置与其他页面";
        AboutMenuItem.ToolTip=available?$"发现新版本 {release!.Tag}":null;
        UpdateStatus.ToolTip=null;
        UpdateStatus.Text=available?$"发现新版本 {release!.Tag}，当前版本 v{CurrentVersion.ToString(3)}。":release is null?"暂无可用的正式发布版本。":$"当前已是最新版本（v{CurrentVersion.ToString(3)}）。";
        UpdateNotes.Text=available?release!.Notes:"";UpdateNotes.Visibility=available&&!string.IsNullOrWhiteSpace(release!.Notes)?Visibility.Visible:Visibility.Collapsed;
    }
    private void OpenUpdateClick(object sender,RoutedEventArgs e)
    {
        if(latestUpdate?.IsNewerThan(CurrentVersion)!=true)return;
        try{Process.Start(new ProcessStartInfo(latestUpdate.Page.AbsoluteUri){UseShellExecute=true});}
        catch(Exception error) when(error is System.ComponentModel.Win32Exception or InvalidOperationException){UpdateStatus.Text="无法打开下载页面："+error.Message;}
    }
}

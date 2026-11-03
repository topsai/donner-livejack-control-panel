using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Donner.Core;

namespace Donner.Panel;
public partial class MainWindow
{
    private MixerPanel? mixerPanel;
    private bool mixerEnabled,mixerToggleBusy;
    private long nextMixerStart,nextEngineRetry;
    private readonly DispatcherTimer mixerRetry=new(){Interval=TimeSpan.FromSeconds(3)};
    private string MixerSettingsPath=>Path.Combine(storage,"mixer.json");
    private void InitializeMixer()
    {
        Loaded+=(_,_)=>
        {
            if(smokeDirectory is not null)return;
            LoadMixerPreference();UpdateMixer();mixerRetry.Start();
        };
        mixerRetry.Tick+=(_,_)=>
        {
            if(!mixerEnabled)return;
            if(mixerPanel is null){UpdateMixer();return;}
            if(!mixerPanel.Client.Connected&&Environment.TickCount64>=nextEngineRetry)
            {nextEngineRetry=Environment.TickCount64+15000;try{mixerPanel.Client.Start();}catch(Exception error) when(error is IOException or System.ComponentModel.Win32Exception){MixerSettingsStatus.Text=error.Message;}}
        };
        Closed+=(_,_)=>{mixerRetry.Stop();mixerPanel?.Dispose();};
    }
    private void LoadMixerPreference()
    {
        try{mixerEnabled=MixerSettings.Load(MixerSettingsPath).Enabled;}
        catch(Exception e) when(e is InvalidDataException or IOException or UnauthorizedAccessException){mixerEnabled=false;Record(e.Message);}
        MixerEnableCheck.IsChecked=mixerEnabled;
    }
    private async void MixerEnableClick(object sender,RoutedEventArgs e)
    {
        if(mixerToggleBusy)return;mixerToggleBusy=true;
        try
        {
            bool desired=MixerEnableCheck.IsChecked==true;
            if(desired&&VoicemeeterClient.InstalledDirectory() is null)
            {
                // Let the checkmark render before opening the optional installation guide.
                await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Background);
                desired=new MixerSetupWindow{Owner=this}.ShowDialog()==true;
            }
            new MixerSettings(desired).Save(MixerSettingsPath);mixerEnabled=desired;MixerEnableCheck.IsChecked=desired;
            UpdateMixer();
        }
        catch(Exception error) when(error is InvalidDataException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {MixerEnableCheck.IsChecked=mixerEnabled;Record("调音台设置失败："+error.Message);}
        finally{mixerToggleBusy=false;}
    }
    private void UpdateMixer()
    {
        UpdateMixerNavigation();
        if(!mixerEnabled)
        {
            mixerPanel?.Dispose();mixerPanel=null;nextMixerStart=0;MixerSlot.Content=null;MixerSettingsStatus.Text="已关闭；不会安装或自动启动 Banana。";return;
        }
        if(mixerPanel is not null)return;
        if(VoicemeeterClient.InstalledDirectory() is null)
        {
            MixerSettingsStatus.Text="等待安装 Banana。安装完成并重启 Windows 后，可点击“重连调音台”。";
            MixerSlot.Content=new TextBlock{Text=MixerSettingsStatus.Text,Margin=new Thickness(14),FontSize=13};return;
        }
        if(Environment.TickCount64<nextMixerStart)return;
        // One automatic start attempt per enable/session; manual reconnect can retry.
        nextMixerStart=long.MaxValue;
        IMixerClient? client=null;
        try
        {
            client=new VoicemeeterClient();client.Start();
            mixerPanel=new MixerPanel(client,sampleRates:new MixerSampleRates(client,()=>session.Snapshot,async rate=>{if(busy)throw new IOException("正在执行其他设备操作，请稍后重试。");await FlushVolumes();selection.Clear();await Apply([new("rate",Value:checked((int)rate))]);}),editNames:()=>MixerNamesClick(this,new RoutedEventArgs()),eqStorage:storage,enabledPreference:()=>mixerEnabled);client=null;MixerSlot.Content=mixerPanel;
            MixerSettingsStatus.Text="已启用。打开 Donner 面板时会连接并按需在后台启动 Banana；关闭此开关不会强制退出已运行的 Banana。";
        }
        catch(Exception error) when(error is InvalidDataException or IOException or System.ComponentModel.Win32Exception or BadImageFormatException or EntryPointNotFoundException)
        {MixerSettingsStatus.Text=error.Message;MixerSlot.Content=new TextBlock{Text=error.Message+" 请完成安装并重启 Windows，或在应用设置中重连调音台。",Margin=new Thickness(14)};}
        finally{client?.Dispose();}
    }
    private void UpdateMixerNavigation()
    {
        if(!mixerEnabled&&Pages.SelectedItem==MixerPage)Pages.SelectedIndex=0;
        MixerPage.Visibility=mixerEnabled?Visibility.Visible:Visibility.Collapsed;
        MixerPageButton.Visibility=mixerEnabled?Visibility.Visible:Visibility.Collapsed;
        MixerPageButton.Content=Pages.SelectedItem==MixerPage?"主页":"调音台";
    }
    private void MixerPageClick(object sender,RoutedEventArgs e)
    {
        if(!mixerEnabled)return;
        Pages.SelectedIndex=Pages.SelectedItem==MixerPage?0:Pages.Items.IndexOf(MixerPage);
        UpdateMixerNavigation();
    }
    private void MixerReconnectClick(object sender,RoutedEventArgs e)
    {
        if(!mixerEnabled){MixerSettingsStatus.Text="请先勾选“启用调音台功能”。";return;}
        mixerPanel?.Dispose();mixerPanel=null;nextMixerStart=0;UpdateMixer();
    }
    private void MixerNamesClick(object sender,RoutedEventArgs e)
    {try{MixerEndpointNames.Show(this,storage);panelState=panelState with{Names=MixerEndpointNames.CaptureNames(storage)};SchedulePanelSave();mixerPanel?.RefreshDevices();nextMixerMeterRoute=0;}catch(Exception error) when(error is System.Runtime.InteropServices.COMException or IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException){Record(error.Message);}}
    private void MixerGuideClick(object sender,RoutedEventArgs e)
    {
        if(new MixerSetupWindow{Owner=this}.ShowDialog()!=true)return;
        try{new MixerSettings(true).Save(MixerSettingsPath);mixerEnabled=true;MixerEnableCheck.IsChecked=true;nextMixerStart=0;UpdateMixer();}
        catch(Exception error) when(error is InvalidDataException or IOException or UnauthorizedAccessException){Record(error.Message);}
    }
}

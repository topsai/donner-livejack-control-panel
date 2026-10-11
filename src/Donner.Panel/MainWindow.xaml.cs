using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Donner.Core;
using Microsoft.Win32;
using Forms=System.Windows.Forms;

namespace Donner.Panel;

public partial class MainWindow : Window
{
    private readonly HostClient client;
    private readonly DeviceSession session;
    private readonly PresetStore presets;
    private readonly PendingVolumes pending=new();
    private readonly AudioSelection selection=new();
    private readonly StartupRegistration startup=new();
    private readonly DispatcherTimer volumeTimer=new() {Interval=TimeSpan.FromMilliseconds(250)};
    private readonly DispatcherTimer refreshTimer=new() {Interval=TimeSpan.FromSeconds(3)};
    private readonly DispatcherTimer reconnectTimer=new() {Interval=TimeSpan.FromMilliseconds(500)};
    private DeviceNotifications? notifications;
    private int deviceChanges;
    private readonly string storage;
    private readonly string? smokeDirectory;
    private readonly string? reconnectCheckDirectory;
    private readonly List<string> realLog=[];
    private readonly List<string> demoLog=[];
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private Forms.ToolStripItem? trayMute;
    private bool busy,rendering,closed;
    private sealed record RateOption(uint Value,string Label);
    private sealed record ChannelView(Grid Row,TextBlock Label,Slider Slider,TextBox Number,CheckBox Mute)
    {
        public ChannelState State {get;set;}=null!;
        public int? SelectedPercent {get;set;}
    }
    private readonly Dictionary<(uint,uint),ChannelView> channelViews=[];

    public MainWindow(string[] args)
    {
        InitializeComponent();
        Title="Donner 中文控制面板 · v1.15";
        var smoke=Array.IndexOf(args,"--smoke");
        smokeDirectory=smoke>=0 && smoke+1<args.Length?Path.GetFullPath(args[smoke+1]):null;
        var check=Array.IndexOf(args,"--reconnect-check");
        reconnectCheckDirectory=check>=0&&check+1<args.Length?Path.GetFullPath(args[check+1]):null;
        var isolatedDirectory=smokeDirectory??reconnectCheckDirectory;
        if(args.Contains("--startup"))ShowActivated=false;
        storage=isolatedDirectory is null?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DonnerControlPanel")
            :Path.Combine(isolatedDirectory,"test-data");
        Directory.CreateDirectory(storage);
        presets=new PresetStore(Path.Combine(storage,"presets"));
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"Donner.Host.exe"));
        client=new HostClient(start);session=new DeviceSession(client.SendAsync);
        RateCombo.SelectionChanged+=(_,_)=>{if(!rendering&&RateCombo.SelectedItem is RateOption rate)selection.Rate=rate.Value;};
        BufferCombo.SelectionChanged+=(_,_)=>{if(!rendering&&BufferCombo.SelectedItem is uint size)selection.Buffer=size;};
        Pages.SelectionChanged+=(_,_)=>UpdatePageMenu();
        volumeTimer.Tick+=async(_,_)=>await FlushVolumes();
        refreshTimer.Tick+=async(_,_)=>await BackgroundPoll();
        reconnectTimer.Tick+=async(_,_)=>{reconnectTimer.Stop();await BackgroundPoll();};
        SourceInitialized+=(_,_)=>
        {
            try {notifications=new(new WindowInteropHelper(this).Handle,DeviceChanged);}
            catch(Win32Exception e){Record(e.Message+" 将继续定时检测。");}
        };
        PreviewKeyDown+=async(_,e)=>
        {
            if(e.Key==Key.F5){e.Handled=true;await Detect();}
            else if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.M){e.Handled=true;await MuteAll();}
            else if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.S){e.Handled=true;await SavePreset();}
        };
        StateChanged+=(_,_)=>{if(WindowState==WindowState.Minimized && tray is not null)Hide();};
        Closed+=(_,_)=>{closed=true;volumeTimer.Stop();refreshTimer.Stop();reconnectTimer.Stop();notifications?.Dispose();client.Dispose();tray?.Dispose();trayIcon?.Dispose();};
        Loaded+=async(_,_)=>
        {
            LoadPresets();Render();
            if(smokeDirectory is null) SetupTray();
            if(args.Contains("--startup"))Hide();
            if(isolatedDirectory is null)
            {
                try
                {
                    if(args.Contains("--enable-startup")||startup.Enabled)startup.Set(true,Environment.ProcessPath!);
                    StartupCheck.IsChecked=startup.Enabled;
                }
                catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {Record("开机启动设置无法读取或更新："+error.Message);}
            }
            await Detect();
            refreshTimer.Start();
            if(smokeDirectory is not null) await Smoke();
        };
    }
    private void SetupTray()
    {
        using(var stream=System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/donner.ico")).Stream)
        using(var icon=new System.Drawing.Icon(stream))trayIcon=(System.Drawing.Icon)icon.Clone();
        tray=new Forms.NotifyIcon {Icon=trayIcon,Text="Donner 中文控制面板",Visible=true};
        var menu=new Forms.ContextMenuStrip();
        menu.Items.Add("显示控制面板",null,(_,_)=>Restore());
        trayMute=menu.Items.Add("输出全部静音",null,async(_,_)=>{await Dispatcher.InvokeAsync(async()=>await MuteAll());});
        menu.Items.Add("退出",null,(_,_)=>Dispatcher.Invoke(Close));
        tray.ContextMenuStrip=menu;tray.DoubleClick+=(_,_)=>Restore();
    }
    private void Restore()=>Dispatcher.Invoke(()=>{Show();WindowState=WindowState.Normal;Activate();});
    private void GearClick(object sender,RoutedEventArgs e)
    {
        UpdatePageMenu();PagesMenu.PlacementTarget=(FrameworkElement)sender;PagesMenu.IsOpen=true;
    }
    private void PageClick(object sender,RoutedEventArgs e)
    {if(sender is MenuItem item&&int.TryParse(item.Tag?.ToString(),out var index)){Pages.SelectedIndex=index;UpdatePageMenu();}}
    private void UpdatePageMenu()
    {
        foreach(var item in PagesMenu.Items.OfType<MenuItem>())
        {item.IsCheckable=true;item.IsChecked=int.TryParse(item.Tag?.ToString(),out var index)&&index==Pages.SelectedIndex;}
    }
    private void Record(string message)
    {
        var demo=session.Snapshot.Demo;var line=$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {(demo?"[演示]":"[真实]")} {message}";
        var log=demo?demoLog:realLog;log.Add(line);if(log.Count>500)log.RemoveAt(0);
        try {Directory.CreateDirectory(Path.Combine(storage,"logs"));File.AppendAllText(Path.Combine(storage,"logs",demo?"demo.log":"device.log"),line+Environment.NewLine,Encoding.UTF8);}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException) { /* Keep in-memory diagnostics available. */ }
        Feedback.Text=message;UpdateDiagnostics();
    }
    private async Task Run(Func<Task> action,bool quiet=false)
    {
        if(busy||closed)return;busy=true;if(!quiet)EnableControls();
        try {await action();}
        catch(Exception e) when(e is IOException or InvalidDataException or TimeoutException or JsonException or Win32Exception or UnauthorizedAccessException)
        {Record(e.Message);}
        finally {busy=false;if(!closed){Render();if(!quiet)Activate();}}
    }
    private async Task Detect()
    {
        if(busy)return;pending.Drain();volumeTimer.Stop();selection.Clear();
        await Run(async()=>{await session.DetectAsync();Record(session.Snapshot.Status);});
    }
    private async Task BackgroundPoll()
    {
        if(busy||closed||session.Snapshot.Demo||ChannelsPanel.IsMouseCaptureWithin||volumeTimer.IsEnabled||RateCombo.IsDropDownOpen||BufferCombo.IsDropDownOpen)return;
        await Run(async()=>
        {
            var before=session.Snapshot;
            if(!before.Connected){pending.Drain();volumeTimer.Stop();selection.Clear();}
            try {await session.RefreshAsync();}
            finally
            {
                if(!session.Snapshot.Connected){pending.Drain();volumeTimer.Stop();selection.Clear();}
            }
            if(before.Connected!=session.Snapshot.Connected)Record(session.Snapshot.Connected?"设备已自动连接，已读取当前设置。":"设备已断开，正在等待重新连接。");
        },quiet:true);
    }
    private void DeviceChanged(string message)
    {
        if(closed||session.Snapshot.Demo)return;
        deviceChanges++;pending.Drain();volumeTimer.Stop();selection.Clear();
        session.InvalidateHardware();Record(message+"，旧连接已失效。");Render();
        reconnectTimer.Stop();reconnectTimer.Start();
    }
    private void EnableControls(DeviceSnapshot? snapshot=null)
    {
        var d=snapshot??session.Snapshot;
        DetectButton.IsEnabled=!busy;
        ChannelsPanel.IsEnabled=!busy && d.Connected;
        RateCombo.IsEnabled=RateApply.IsEnabled=!busy && d.Connected && d.SampleRates.Length>0;
        BufferCombo.IsEnabled=BufferApply.IsEnabled=!busy && d.Connected && d.DeviceCount==1 && d.BufferRate>0 && d.BufferSizes.Length>0;
        LinkedCheck.IsEnabled=!busy && d.Connected && new uint[]{1,2}.All(n=>d.Channels.Any(c=>c.Direction==0&&c.Number==n&&c.CanVolume&&c.Volume is not null));
        MuteAllButton.IsEnabled=!busy && d.Connected && d.Channels.Any(c=>c.Direction==0&&c.CanMute&&c.Muted is not null);
        SavePresetButton.IsEnabled=!busy && d.Connected;
        ApplyPresetButton.IsEnabled=!busy && d.Connected && PresetsList.SelectedItem is Preset;
        RateSection.Visibility=DeviceCapabilities.HasRate(d)?Visibility.Visible:Visibility.Collapsed;
        BufferSection.Visibility=DeviceCapabilities.HasBuffer(d)?Visibility.Visible:Visibility.Collapsed;
        var audio=DeviceCapabilities.HasRate(d)||DeviceCapabilities.HasBuffer(d);
        AudioTitle.Visibility=audio?Visibility.Visible:Visibility.Collapsed;
        LinkedCheck.Visibility=d.Connected&&new uint[]{1,2}.All(n=>d.Channels.Any(c=>c.Direction==0&&c.Number==n&&DeviceCapabilities.HasVolume(c)))?Visibility.Visible:Visibility.Collapsed;
        var mute=d.Connected&&d.Channels.Any(c=>c.Direction==0&&DeviceCapabilities.HasMute(c));
        MuteAllButton.Visibility=mute?Visibility.Visible:Visibility.Collapsed;
        var muteLabel=MuteOutputs(d) is {Length:>0} outputs&&outputs.All(c=>c.Muted==true)?"取消输出静音":"输出全部静音";
        MuteAllButton.Content=muteLabel;
        if(trayMute is not null){trayMute.Visible=mute;trayMute.Enabled=!busy&&mute;trayMute.Text=muteLabel;}
        if(Feedback.Text.StartsWith("快捷键："))Feedback.Text="快捷键：F5 重新检测 · Ctrl+S 保存预设"+(mute?" · Ctrl+M 切换输出静音":"");
        var channels=d.Connected&&d.Channels.Any(c=>DeviceCapabilities.HasVolume(c)||DeviceCapabilities.HasMute(c));
        ChannelHeader.Visibility=ChannelsPanel.Visibility=channels?Visibility.Visible:Visibility.Collapsed;
    }
    private void Render(DeviceSnapshot? snapshot=null)
    {
        rendering=true;
        var d=snapshot??session.Snapshot;
        DeviceStatus.Text=d.Status;
        DeviceSubtitle.Text=d.Connected?string.Join("   ·   ",new[]{d.Model is {} model?$"设备型号：{model}":d.Name,d.SampleRate is {} rate?$"{rate/1000d:0.###} kHz":null,d.BufferSize is {} size?$"{size} samples":null}.Where(s=>s is not null)):"安装原厂驱动并连接声卡后，可调节设备。";
        LatencySection.Visibility=d.Connected&&(d.InputLatencySamples is not null||d.OutputLatencySamples is not null)?Visibility.Visible:Visibility.Collapsed;
        ShowLatency(InputLatencyText,"输入延迟",d.Connected?d.InputLatencySamples:null,d.LatencySampleRate);
        ShowLatency(OutputLatencyText,"输出延迟",d.Connected?d.OutputLatencySamples:null,d.LatencySampleRate);
        if(!RateCombo.Items.Cast<RateOption>().Select(r=>r.Value).SequenceEqual(d.SampleRates))
            RateCombo.ItemsSource=d.SampleRates.Select(rate=>new RateOption(rate,$"{rate/1000d:0.###} kHz")).ToArray();
        RateCombo.SelectedItem=RateCombo.Items.Cast<RateOption>().FirstOrDefault(r=>r.Value==selection.ResolveRate(d));
        if(!BufferCombo.Items.Cast<uint>().SequenceEqual(d.BufferSizes))BufferCombo.ItemsSource=d.BufferSizes;
        BufferCombo.SelectedItem=selection.ResolveBuffer(d);
        LinkedCheck.IsChecked=session.Linked;
        RenderChannels(d);EnableControls(d);UpdateDiagnostics();rendering=false;
        if(reconnectCheckDirectory is not null)
        {
            try
            {
                Directory.CreateDirectory(reconnectCheckDirectory);
                File.WriteAllText(Path.Combine(reconnectCheckDirectory,"state.json"),JsonSerializer.Serialize(new {Version="1.15",ProcessId=Environment.ProcessId,Updated=DateTimeOffset.Now,d.Connected,d.Demo,d.Status,d.SampleRate,d.BufferSize,DeviceChanges=deviceChanges,NotificationRegistered=notifications is not null},new JsonSerializerOptions {WriteIndented=true}));
            }
            catch(IOException){}
        }
    }
    private static void ShowLatency(TextBlock text,string label,uint? samples,uint? rate)
    {
        text.Visibility=samples is null?Visibility.Collapsed:Visibility.Visible;
        text.Text=samples is {} value?rate is >0?$"{label}：{value*1000d/rate.Value:0.##} ms · {value} samples":$"{label}：{value} samples":"";
    }
    private void RenderChannels(DeviceSnapshot device)
    {
        var channels=device.Connected?device.Channels.Where(c=>DeviceCapabilities.HasVolume(c)||DeviceCapabilities.HasMute(c)).ToArray():[];
        var keys=channels.Select(c=>(c.Direction,c.Number)).ToArray();
        var oldKeys=ChannelsPanel.Children.Cast<Grid>().Select(row=>channelViews.Single(p=>ReferenceEquals(p.Value.Row,row)).Key);
        if(!oldKeys.SequenceEqual(keys)){ChannelsPanel.Children.Clear();channelViews.Clear();}
        foreach(var c in channels)
        {
            if(channelViews.TryGetValue((c.Direction,c.Number),out var existing))
            {
                UpdateChannel(existing,c);continue;
            }
            var row=new Grid {Margin=new Thickness(0,0,0,15)};
            row.ColumnDefinitions.Add(new(){Width=new GridLength(70)});row.ColumnDefinitions.Add(new());
            row.ColumnDefinitions.Add(new(){Width=new GridLength(86)});row.ColumnDefinitions.Add(new(){Width=new GridLength(60)});
            var label=new TextBlock {Text=c.Name,VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.SemiBold};row.Children.Add(label);
            var slider=new Slider {Minimum=0,Maximum=100,TickFrequency=1,SmallChange=1,LargeChange=10,IsSnapToTickEnabled=true,
                Value=c.Volume is {} value?VolumeScale.ToPercent(value):0,
                IsEnabled=DeviceCapabilities.HasVolume(c),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,15,0)};
            Grid.SetColumn(slider,1);row.Children.Add(slider);
            VolumePointer.Attach(slider);
            slider.LostMouseCapture+=(_,_)=>{if(!closed){volumeTimer.Stop();volumeTimer.Start();}};
            var number=new TextBox {Text=$"{slider.Value:0}",Width=58,Padding=new Thickness(5,3,5,3),VerticalAlignment=VerticalAlignment.Center,ToolTip="输入 0–100，按 Enter 应用；选中滑块后可用方向键每次调 1%。",MaxLength=3};
            var percentPanel=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
            percentPanel.Children.Add(number);percentPanel.Children.Add(new TextBlock {Text="%",Margin=new Thickness(4,0,0,0),VerticalAlignment=VerticalAlignment.Center});
            Grid.SetColumn(percentPanel,2);row.Children.Add(percentPanel);
            var mute=new CheckBox {Content="静音",IsChecked=c.Muted,IsEnabled=c.CanMute&&c.Muted is not null,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(mute,3);row.Children.Add(mute);
            var view=new ChannelView(row,label,slider,number,mute){State=c};
            UpdateChannel(view,c);
            channelViews.Add((c.Direction,c.Number),view);
            slider.ValueChanged+=(_,_)=>
            {
                if(rendering)return;
                var state=view.State;
                if(!number.IsKeyboardFocusWithin)number.Text=$"{slider.Value:0}";
                try
                {
                    var percent=(int)Math.Round(slider.Value);
                    var raw=VolumeScale.ToRaw(percent);
                    foreach(var setting in ControlPolicy.VolumeChanges(session.Snapshot,state.Direction,state.Number,raw,session.Linked))
                    {
                        pending.Put(setting);
                        if(channelViews.TryGetValue((setting.Direction,setting.Channel),out var selected))selected.SelectedPercent=percent;
                    }
                    volumeTimer.Stop();volumeTimer.Start();
                }
                catch(InvalidDataException e) {Record(e.Message);Render();}
            };
            number.LostKeyboardFocus+=(_,_)=>CommitPercent(view);
            number.PreviewKeyDown+=(_,e)=>
            {
                if(e.Key==Key.Enter){CommitPercent(view);number.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));e.Handled=true;}
                else if(e.Key==Key.Escape){number.Text=$"{slider.Value:0}";number.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));e.Handled=true;}
            };
            mute.Click+=async(_,_)=>{if(rendering||busy)return;var desired=mute.IsChecked==true?1:0;await FlushVolumes();await Apply([new("mute",c.Direction,c.Number,desired)]);};
            ChannelsPanel.Children.Add(row);
        }
    }
    private void UpdateChannel(ChannelView view,ChannelState state)
    {
        view.State=state;view.Label.Text=state.Name;
        if(!view.Slider.IsMouseCaptureWithin&&!volumeTimer.IsEnabled)
        {
            if(view.SelectedPercent is {} selected&&state.Volume!=VolumeScale.ToRaw(selected))view.SelectedPercent=null;
            view.Slider.Value=view.SelectedPercent??(state.Volume is {} value?VolumeScale.ToPercent(value):0);
        }
        view.Slider.IsEnabled=DeviceCapabilities.HasVolume(state);
        view.Slider.Visibility=((FrameworkElement)view.Number.Parent).Visibility=DeviceCapabilities.HasVolume(state)?Visibility.Visible:Visibility.Collapsed;
        view.Number.IsEnabled=DeviceCapabilities.HasVolume(state);
        if(!view.Number.IsKeyboardFocusWithin)view.Number.Text=$"{view.Slider.Value:0}";
        view.Mute.IsChecked=state.Muted;view.Mute.IsEnabled=DeviceCapabilities.HasMute(state);
        view.Mute.Visibility=DeviceCapabilities.HasMute(state)?Visibility.Visible:Visibility.Collapsed;
    }
    private void CommitPercent(ChannelView view)
    {
        if(rendering||busy||!DeviceCapabilities.HasVolume(view.State))return;
        if(!int.TryParse(view.Number.Text,out var percent)||percent<0||percent>100)
        {view.Number.Text=$"{view.Slider.Value:0}";Record("音量请输入 0–100 的整数。");return;}
        view.Slider.Value=percent;view.Number.Text=$"{percent}";
    }
    private async Task FlushVolumes()
    {
        if(busy||ChannelsPanel.IsMouseCaptureWithin)return;volumeTimer.Stop();var changes=pending.Drain();if(changes.Length>0)await Apply(changes);
    }
    private void StartupClick(object sender,RoutedEventArgs e)
    {
        try
        {
            startup.Set(StartupCheck.IsChecked==true,Environment.ProcessPath!);
            Record(startup.Enabled?"已开启随 Windows 启动，登录后在托盘运行。":"已关闭随 Windows 启动。");
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {StartupCheck.IsChecked=StartupCheck.IsChecked!=true;Record("开机启动设置失败："+error.Message);}
    }
    private async Task Apply(Setting[] changes)
    {
        await Run(async()=>
        {
            var reply=await session.ApplyAsync(changes);
            Record(reply.Message);
            foreach(var result in reply.Results??[])Record($"{result.Operation}：{(result.Success?"成功":"失败")} · {result.Message}");
        });
    }
    private async void DetectClick(object sender,RoutedEventArgs e)=>await Detect();
    private async void RateClick(object sender,RoutedEventArgs e)
    {var selected=RateCombo.SelectedItem as RateOption;if(selected is null)return;await FlushVolumes();selection.Clear();await Apply([new("rate",Value:(int)selected.Value)]);}
    private async void BufferClick(object sender,RoutedEventArgs e)
    {if(BufferCombo.SelectedItem is not uint selected)return;await FlushVolumes();selection.Clear();await Apply([new("buffer",Value:(int)selected)]);}
    private async void LinkedClick(object sender,RoutedEventArgs e)
    {
        if(busy)return;var enabled=LinkedCheck.IsChecked==true;
        if(!enabled){session.Linked=false;Record("已关闭左右输出联动");return;}
        await FlushVolumes();
        var left=session.Snapshot.Channels.FirstOrDefault(c=>c.Direction==0&&c.Number==1);
        if(left?.Volume is not {} value){session.Linked=false;Render();return;}
        await Run(async()=>
        {
            session.Linked=false;
            var changes=ControlPolicy.VolumeChanges(session.Snapshot,0,1,value,true);
            var reply=await session.ApplyAsync(changes);session.Linked=reply.Success;Record(reply.Message);
            foreach(var result in reply.Results??[])Record($"{result.Operation}：{(result.Success?"成功":"失败")} · {result.Message}");
        });
    }
    private async Task MuteAll()
    {
        if(busy)return;await FlushVolumes();
        var outputs=MuteOutputs(session.Snapshot);
        var target=outputs.Length>0&&outputs.All(c=>c.Muted==true)?0:1;
        var changes=outputs.Select(c=>new Setting("mute",0,c.Number,target)).ToArray();
        if(changes.Length==0){Record("当前没有可静音的输出通道。");return;}await Apply(changes);
    }
    private static ChannelState[] MuteOutputs(DeviceSnapshot device)=>device.Connected?device.Channels.Where(c=>c.Direction==0&&DeviceCapabilities.HasMute(c)).ToArray():[];
    private async void MuteAllClick(object sender,RoutedEventArgs e)=>await MuteAll();
    private void LoadPresets()
    {
        try {var selected=(PresetsList.SelectedItem as Preset)?.Name;PresetsList.ItemsSource=presets.ReadAvailable(out var errors);PresetsList.SelectedItem=PresetsList.Items.Cast<Preset>().FirstOrDefault(p=>p.Name==selected);foreach(var error in errors)Record(error);}
        catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException){Record(e.Message);}
    }
    private async Task SavePreset()
    {
        if(busy)return;
        await FlushVolumes();
        try {
            var p=PresetCapture.From(session.Snapshot,PresetName.Text,session.Linked);presets.Save(p);LoadPresets();Record("已保存预设："+p.Name);}
        catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException){Record(e.Message);}
    }
    private async void SavePresetClick(object sender,RoutedEventArgs e)=>await SavePreset();
    private void DeletePresetClick(object sender,RoutedEventArgs e)
    {
        if(PresetsList.SelectedItem is not Preset p)return;
        try {presets.Delete(p.Name);LoadPresets();Record("已删除预设："+p.Name);}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException){Record(error.Message);}
    }
    private void PresetSelected(object sender,SelectionChangedEventArgs e)
    {
        if(session is null)return;
        if(PresetsList.SelectedItem is Preset p) PresetPreview.Text=$"{p.Name}\n左右联动：{(p.Linked?"开启":"关闭")}\n\n"+
            string.Join("\n",p.Settings.Select(s=>s.Kind switch {"rate"=>$"采样率：{s.Value/1000d:0.###} kHz","buffer"=>$"缓冲：{s.Value} samples",
                "volume"=>$"{(s.Direction==1?"输入":"输出")} {s.Channel} 音量控制值：{s.Value}","mute"=>$"{(s.Direction==1?"输入":"输出")} {s.Channel}：{(s.Value==1?"静音":"取消静音")}",_=>s.Kind}));
        else PresetPreview.Text="从左侧选择预设，检查内容后点击应用。";
        EnableControls();
    }
    private async void ApplyPresetClick(object sender,RoutedEventArgs e)
    {
        if(PresetsList.SelectedItem is not Preset p)return;await FlushVolumes();selection.Clear();
        await Run(async()=>{ControlPolicy.ValidatePreset(session.Snapshot,p);var reply=await session.ApplyAsync(p.Settings);
            if(reply.Success)session.Linked=p.Linked;Record(reply.Message);foreach(var result in reply.Results??[])Record($"{result.Operation}：{result.Message}");});
    }
    private string Summary()
    {
        var d=session.Snapshot;
        return $"Donner 中文控制面板\n状态：{d.Status}\nAPI：{d.ApiVersion}\n路径：{d.ApiPath}\n设备：{d.DeviceId}\n采样率：{d.SampleRate?.ToString()??"暂不可用"} Hz\n缓冲：{d.BufferSize?.ToString()??"暂不可用"} samples\n"+
            string.Join("\n",d.Diagnostics.Select(p=>$"{p.Key}：{p.Value}"))+"\n\n最近操作：\n"+string.Join("\n",(d.Demo?demoLog:realLog).TakeLast(35));
    }
    private void UpdateDiagnostics(){if(session is not null){var text=Summary();if(DiagnosticsText.Text!=text)DiagnosticsText.Text=text;}}
    private void CopyClick(object sender,RoutedEventArgs e)
    {try{Clipboard.SetText(Summary());Record("诊断摘要已复制。");}catch(System.Runtime.InteropServices.ExternalException error){Record("剪贴板暂不可用："+error.Message);}}
    private void ExportClick(object sender,RoutedEventArgs e)
    {
        var dialog=new SaveFileDialog {Title="导出设备诊断",Filter="文本日志|*.txt",FileName=$"Donner-{(session.Snapshot.Demo?"demo":"device")}-{DateTime.Now:yyyyMMdd-HHmmss}.txt"};
        if(dialog.ShowDialog(this)!=true)return;
        try{File.WriteAllText(dialog.FileName,Summary(),Encoding.UTF8);Record("已导出："+dialog.FileName);}catch(Exception error) when(error is IOException or UnauthorizedAccessException){Record(error.Message);}
    }
    private void OpenLogsClick(object sender,RoutedEventArgs e)
    {
        try {var path=Path.Combine(storage,"logs");Directory.CreateDirectory(path);var start=new ProcessStartInfo("explorer.exe"){UseShellExecute=true};start.ArgumentList.Add(path);Process.Start(start);}
        catch(Exception error) when(error is IOException or Win32Exception or UnauthorizedAccessException){Record(error.Message);}
    }
    private void OpenPresetsClick(object sender,RoutedEventArgs e)
    {
        try{var start=new ProcessStartInfo("explorer.exe"){UseShellExecute=true};start.ArgumentList.Add(Path.Combine(storage,"presets"));Process.Start(start);}
        catch(Exception error) when(error is IOException or Win32Exception or UnauthorizedAccessException){Record(error.Message);}
    }
    private async Task Smoke()
    {
        try
        {
            Directory.CreateDirectory(smokeDirectory!);await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            SaveImage("device.png");
            var live=session.Snapshot;
            if(Pages.SelectedIndex!=0||Pages.SelectedContent is null)throw new Exception("启动时未显示设备控制页。");
            foreach(var menu in PagesMenu.Items.OfType<MenuItem>())
            {
                menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                if(Pages.SelectedIndex!=int.Parse(menu.Tag.ToString()!)||Pages.SelectedContent is null)throw new Exception("齿轮菜单未打开对应页面。");
            }
            Pages.SelectedIndex=0;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            if(live.InputLatencySamples is {} input&&(!InputLatencyText.Text.Contains(input.ToString())||InputLatencyText.Visibility!=Visibility.Visible))throw new Exception("未展示驱动输入延迟。");
            if(live.OutputLatencySamples is {} output&&(!OutputLatencyText.Text.Contains(output.ToString())||OutputLatencyText.Visibility!=Visibility.Visible))throw new Exception("未展示驱动输出延迟。");
            if(live.Model is {} model&&!DeviceSubtitle.Text.Contains(model))throw new Exception("未展示设备报告的型号。");
            await VerifyStableRefresh();
            if(notifications is null)throw new Exception("设备通知订阅失败。");
            DeviceNotifications.SendTestChange(new WindowInteropHelper(this).Handle);
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            if(deviceChanges==0||session.Snapshot.Connected)throw new Exception("Windows拔插通知未使旧连接失效。");
            var deadline=DateTime.UtcNow.AddSeconds(12);
            while(live.Connected&&(busy||!session.Snapshot.Connected)&&DateTime.UtcNow<deadline)await Task.Delay(50);
            if(live.Connected&&!session.Snapshot.Connected)throw new Exception("拔插通知后未自动重新连接。");
            var partial=live with {Connected=true,SampleRates=[],BufferSizes=[],Channels=[
                new(1,1,"仅静音",false,true,0,0,0,null,false),new(1,2,"无支持",false,false,0,0,0,null,null),
                new(0,1,"仅音量",true,false,-32512,0,256,-2048,null)]};
            Render(partial);
            if(ChannelsPanel.Children.Count!=2||RateSection.Visibility!=Visibility.Collapsed||BufferSection.Visibility!=Visibility.Collapsed||LinkedCheck.Visibility!=Visibility.Collapsed||MuteAllButton.Visibility!=Visibility.Collapsed)throw new Exception("未隐藏不支持的功能。");
            var muteOnly=(Grid)ChannelsPanel.Children[0];var volumeOnly=(Grid)ChannelsPanel.Children[1];
            if(muteOnly.Children.OfType<Slider>().Single().Visibility!=Visibility.Collapsed||volumeOnly.Children.OfType<CheckBox>().Single().Visibility!=Visibility.Collapsed)throw new Exception("通道仍显示不支持的控件。");
            SaveImage("capabilities.png");Render();
            session.SetDemo(false);Render();
            if(ChannelsPanel.Children.Count!=0||ChannelHeader.Visibility!=Visibility.Collapsed)throw new Exception("无设备时仍显示占位通道。");
            if(RateApply.IsEnabled||BufferApply.IsEnabled||MuteAllButton.IsEnabled)throw new Exception("未连接状态仍可写入。");
            await BackgroundPoll();
            if(live.Connected&&!session.Snapshot.Connected)throw new Exception("后台未自动恢复真实设备连接。");
            if(live.Connected&&(!RateApply.IsEnabled||!MuteAllButton.IsEnabled))throw new Exception("自动连接后未恢复可用控件。");
            session.SetDemo(true);Render();
            if(!RateApply.IsEnabled||!BufferApply.IsEnabled)throw new Exception("演示控制未开启。");
            var inputMute=session.Snapshot.Channels.Where(c=>c.Direction==1).Select(c=>c.Muted).ToArray();
            var volumeBeforeMute=session.Snapshot.Channels.Select(c=>c.Volume).ToArray();
            await MuteAll();
            if(session.Snapshot.Channels.Where(c=>c.Direction==0&&c.CanMute).Any(c=>c.Muted!=true))throw new Exception("全部静音未生效。");
            if(!Equals(MuteAllButton.Content,"取消输出静音"))throw new Exception("全部静音后按钮文字未切换。");
            await MuteAll();
            if(session.Snapshot.Channels.Where(c=>c.Direction==0&&c.CanMute).Any(c=>c.Muted!=false))throw new Exception("再次点击全部静音未取消静音。");
            if(!Equals(MuteAllButton.Content,"输出全部静音"))throw new Exception("取消静音后按钮文字未恢复。");
            await Apply([new("mute",0,1,1)]);await MuteAll();
            if(session.Snapshot.Channels.Where(c=>c.Direction==0&&c.CanMute).Any(c=>c.Muted!=true))throw new Exception("部分静音时未统一静音。");
            await MuteAll();
            if(!session.Snapshot.Channels.Where(c=>c.Direction==1).Select(c=>c.Muted).SequenceEqual(inputMute)||!session.Snapshot.Channels.Select(c=>c.Volume).SequenceEqual(volumeBeforeMute))throw new Exception("切换全部静音改变了输入或音量。");
            var dragSlider=channelViews[(1,1)].Slider;
            var beforeDrag=session.Snapshot.Channels.Single(c=>c.Direction==1&&c.Number==1).Volume;
            dragSlider.Value=99;await FlushVolumes();
            if(dragSlider.Value!=99)throw new Exception("1%档位回读后跳回其他百分比。");
            var percentView=channelViews[(1,1)];
            for(var percent=91;percent<=99;percent++)
            {percentView.Number.Text=percent.ToString();CommitPercent(percentView);await FlushVolumes();if(dragSlider.Value!=percent)throw new Exception("输入的1%档位未保留。");}
            percentView.Number.Text="101";CommitPercent(percentView);
            if(dragSlider.Value!=99||percentView.Number.Text!="99")throw new Exception("越界音量未拒绝。");
            percentView.Number.Text="bad";CommitPercent(percentView);
            if(dragSlider.Value!=99)throw new Exception("非法音量输入改变了设置。");
            await Run(async()=>await session.RefreshAsync(),quiet:true);
            if(dragSlider.Value!=99)throw new Exception("后台回读未保留用户百分比。");
            Slider.DecreaseSmall.Execute(null,dragSlider);await FlushVolumes();
            if(dragSlider.Value!=98)throw new Exception("方向键未按1%调节或回读跳档。");
            Slider.IncreaseSmall.Execute(null,dragSlider);await FlushVolumes();
            if(dragSlider.Value!=99)throw new Exception("方向键增加未按1%调节。");
            await Apply([new("volume",1,1,beforeDrag!.Value)]);
            dragSlider.ApplyTemplate();UpdateLayout();
            VolumePointer.Move(dragSlider,new Point(dragSlider.ActualWidth/2,dragSlider.ActualHeight/2));
            if(Math.Abs(dragSlider.Value-50)>1)throw new Exception("点击轨道未定位到鼠标位置。");
            Mouse.Capture(dragSlider);
            dragSlider.Value=37;
            Render();
            await Task.Delay(650);
            if(!dragSlider.IsEnabled||!dragSlider.IsMouseCaptured||dragSlider.Value!=37||session.Snapshot.Channels.Single(c=>c.Direction==1&&c.Number==1).Volume!=beforeDrag)
                throw new Exception("拖动中停顿导致音量提交、鼠标捕获丢失或滑块回跳。");
            Mouse.Capture(null);
            await Task.Delay(400);
            await FlushVolumes();
            if(session.Snapshot.Channels.Single(c=>c.Direction==1&&c.Number==1).Volume!=VolumeScale.ToRaw(37))throw new Exception("松开后音量未提交。");
            await Apply([new("volume",1,1,beforeDrag!.Value)]);
            await Apply([new("mute",0,1,1)]);
            if(session.Snapshot.Channels.Single(c=>c.Direction==0&&c.Number==1).Muted!=true)throw new Exception("演示静音失败。");
            SaveImage("demo.png");
            var inputSlider=((Grid)ChannelsPanel.Children[0]).Children.OfType<Slider>().Single();
            await VerifyStableRefresh();
            inputSlider.Value-=1;
            PresetName.Text="冒烟测试预设";await SavePreset();
            if(presets.ReadAll().Length!=1)throw new Exception("预设保存失败。");
            if(presets.ReadAll()[0].Settings.Single(s=>s.Kind=="volume"&&s.Direction==1&&s.Channel==1).Value!=-2304)throw new Exception("保存预设丢失了待应用音量。");
            Pages.SelectedIndex=1;PresetsList.SelectedIndex=0;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            ApplyPresetButton.BringIntoView();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var buttonPoint=ApplyPresetButton.TranslatePoint(new Point(0,ApplyPresetButton.ActualHeight),Pages);
            if(buttonPoint.Y>Pages.ActualHeight || buttonPoint.Y<0)throw new Exception("应用预设按钮无法滚动到可见区域。");
            SaveImage("presets.png");
            Pages.SelectedIndex=2;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);SaveImage("diagnostics.png");
            var registryPath="Software\\DonnerControlPanel\\Tests\\"+Guid.NewGuid();
            try
            {
                var registration=new StartupRegistration(registryPath);
                if(registration.Enabled)throw new Exception("测试启动项初始状态错误。");
                registration.Set(true,Environment.ProcessPath!);
                if(registration.Command!=$"\"{Environment.ProcessPath}\" --startup")throw new Exception("启动命令路径或参数错误。");
                registration.Set(false,Environment.ProcessPath!);
                if(registration.Enabled)throw new Exception("关闭后仍有启动项。");
            }
            finally {Registry.CurrentUser.DeleteSubKeyTree(registryPath,false);}
            Pages.SelectedIndex=3;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);SaveImage("settings.png");
            PanelGuide.VerifyNavigation();Pages.SelectedIndex=4;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);SaveImage("panel-guide.png");
            Width=840;Height=600;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            UpdateLayout();
            PanelGuide.GuideScroll.ScrollToEnd();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            UpdateLayout();
            var end=PanelGuide.GuideEnd.TranslatePoint(new Point(0,PanelGuide.GuideEnd.ActualHeight),PanelGuide.GuideScroll);
            SaveImage("panel-guide-end.png");
            if(end.Y>PanelGuide.GuideScroll.ActualHeight+1||end.Y<0)throw new Exception($"面板说明末尾不能滚动到可见区域：底部={end.Y},高度={PanelGuide.GuideScroll.ActualHeight},偏移={PanelGuide.GuideScroll.VerticalOffset},最大偏移={PanelGuide.GuideScroll.ScrollableHeight}。");
            Pages.SelectedIndex=0;Render(live);await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);SaveImage("device-small.png");
            File.WriteAllText(Path.Combine(smokeDirectory!,"smoke-result.json"),JsonSerializer.Serialize(new {Success=true,LiveConnected=live.Connected,live.SampleRate,live.BufferSize,Checks=new[]{"全部静音再次点击取消及部分静音切换","静音按钮文字跟随回读状态","切换输出静音不改变输入或音量","音量输入及方向键按1%调节","相同硬件值回读保留用户百分比","拒绝非法及越界百分比","音量轨道点击定位","拖动停顿及刷新保留鼠标捕获与滑块位置","松开后提交最终音量","开机启动注册、带空格路径引用及关闭","Windows拔插消息使旧连接失效并触发自动重新枚举","原厂音量表","按能力隐藏各控件及占位通道","无设备禁写","真实设备只读检测","未连接状态后台自动枚举并恢复控件","真实与演示后台刷新不重建控件或切换启用状态","刷新保留选项列表","演示静音","中文预设保存","五页渲染"}},new JsonSerializerOptions {WriteIndented=true}));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch(Exception error)
        {File.WriteAllText(Path.Combine(smokeDirectory!,"smoke-error.txt"),error.ToString());System.Windows.Application.Current.Shutdown(1);}
    }
    private async Task VerifyStableRefresh()
    {
        if(ChannelsPanel.Children.Count==0)return;
        var slider=((Grid)ChannelsPanel.Children[0]).Children.OfType<Slider>().Single();
        var rates=RateCombo.ItemsSource;var buffers=BufferCombo.ItemsSource;var transitions=0;
        DependencyPropertyChangedEventHandler track=(_,_)=>transitions++;
        slider.IsEnabledChanged+=track;
        try
        {
            for(var refresh=0;refresh<3;refresh++)await Run(async()=>await session.RefreshAsync(),quiet:true);
            if(!ReferenceEquals(slider,((Grid)ChannelsPanel.Children[0]).Children.OfType<Slider>().Single()))throw new Exception("后台刷新重建了音量控件。");
            if(transitions!=0)throw new Exception("后台刷新切换了控件启用状态。");
            if(!ReferenceEquals(rates,RateCombo.ItemsSource)||!ReferenceEquals(buffers,BufferCombo.ItemsSource))throw new Exception("后台刷新重建了相同的选项列表。");
        }
        finally {slider.IsEnabledChanged-=track;}
    }
    private void SaveImage(string name)
    {
        UpdateLayout();var visual=(FrameworkElement)Content;
        var image=new RenderTargetBitmap((int)visual.ActualWidth,(int)visual.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(smokeDirectory!,name));encoder.Save(file);
    }
}

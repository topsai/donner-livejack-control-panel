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
    private bool busy,rendering,closed,interactionLocked;
    private uint? selectedFormat,selectedStreamMode;
    private sealed record DriverOption(uint Value,string Label);
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
        StreamModeCombo.ItemsSource=new[]{new DriverOption(1,"按需开启"),new DriverOption(2,"持续开启")};
        OutputFormatCombo.SelectionChanged+=(_,_)=>{if(!rendering&&OutputFormatCombo.SelectedItem is DriverOption option)selectedFormat=option.Value;};
        StreamModeCombo.SelectionChanged+=(_,_)=>{if(!rendering&&StreamModeCombo.SelectedItem is DriverOption option)selectedStreamMode=option.Value;};
        Title="Donner 中文控制面板 · v1.21 · topsai (范斯特洛夫斯基)";
        AboutVersion.Text="版本 "+typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
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
        InitializeMeters();
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
            if(interactionLocked&&(IsOperationInput(e.OriginalSource)||e.Key==Key.F5||Keyboard.Modifiers==ModifierKeys.Control))
            {e.Handled=true;return;}
            if(e.Key==Key.F5){e.Handled=true;await Detect();}
            else if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.M){e.Handled=true;await MuteAll();}
            else if(Keyboard.Modifiers==ModifierKeys.Control && e.Key==Key.S){e.Handled=true;await SavePreset();}
        };
        PreviewMouseDown+=(_,e)=>{if(interactionLocked&&IsOperationInput(e.OriginalSource))e.Handled=true;};
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
    private void ProjectLinkClick(object sender,System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled=true;
        try {Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri){UseShellExecute=true});}
        catch(Exception error) when(error is Win32Exception or InvalidOperationException)
        {Feedback.Text="无法打开浏览器："+error.Message;}
    }
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
        if(busy||closed)return;busy=true;interactionLocked=!quiet;
        try {await action();}
        catch(Exception e) when(e is IOException or InvalidDataException or TimeoutException or JsonException or Win32Exception or UnauthorizedAccessException)
        {Record(e.Message);}
        finally {busy=false;interactionLocked=false;if(!closed)Render();}
    }
    private bool IsOperationInput(object source)
    {
        // Reject repeated writes without changing enabled styling, layout or focus.
        for(var node=source as DependencyObject;node is not null;node=node is Visual?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node))
            if(ReferenceEquals(node,ChannelsPanel)||ReferenceEquals(node,DetectButton)||ReferenceEquals(node,RateCombo)||ReferenceEquals(node,RateApply)||
               ReferenceEquals(node,BufferCombo)||ReferenceEquals(node,BufferApply)||ReferenceEquals(node,SafeModeCheck)||ReferenceEquals(node,OutputFormatCombo)||
               ReferenceEquals(node,OutputFormatApply)||ReferenceEquals(node,StreamModeCombo)||ReferenceEquals(node,StreamModeApply)||ReferenceEquals(node,LinkedCheck)||
               ReferenceEquals(node,MuteAllButton)||ReferenceEquals(node,SavePresetButton)||ReferenceEquals(node,ApplyPresetButton))return true;
        return false;
    }
    private async Task Detect()
    {
        if(busy)return;pending.Drain();volumeTimer.Stop();selection.Clear();selectedFormat=null;selectedStreamMode=null;
        await Run(async()=>{await session.DetectAsync();Record(session.Snapshot.Status);});
    }
    private async Task BackgroundPoll()
    {
        if(busy||closed||session.Snapshot.Demo||ChannelsPanel.IsMouseCaptureWithin||volumeTimer.IsEnabled||RateCombo.IsDropDownOpen||BufferCombo.IsDropDownOpen||OutputFormatCombo.IsDropDownOpen||StreamModeCombo.IsDropDownOpen)return;
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
        StopMeters();
        deviceChanges++;pending.Drain();volumeTimer.Stop();selection.Clear();
        session.InvalidateHardware();Record(message+"，旧连接已失效。");Render();
        reconnectTimer.Stop();reconnectTimer.Start();
    }
    private void EnableControls(DeviceSnapshot? snapshot=null)
    {
        var d=snapshot??session.Snapshot;
        DetectButton.IsEnabled=true;
        ChannelsPanel.IsEnabled=d.Connected;
        RateCombo.IsEnabled=RateApply.IsEnabled=d.Connected && d.SampleRates.Length>0;
        BufferCombo.IsEnabled=BufferApply.IsEnabled=d.Connected && d.DeviceCount==1 && d.BufferRate>0 && d.BufferSizes.Length>0;
        SafeModeCheck.Visibility=d.Connected&&d.SafeMode is not null&&d.DeviceCount==1&&d.BufferSize is not null?Visibility.Visible:Visibility.Collapsed;
        SafeModeCheck.IsEnabled=d.Connected&&d.BufferRate>0;
        OutputFormatSection.Visibility=d.Connected&&d.Driver.OutputFormats.Length>1&&d.Driver.OutputFormat is not null&&d.Driver.FormatSelectionMode==1&&d.Driver.Dsd==false?Visibility.Visible:Visibility.Collapsed;
        OutputFormatCombo.IsEnabled=OutputFormatApply.IsEnabled=d.Connected&&d.Driver.FormatSelectionMode==1&&d.Driver.Dsd==false&&d.Driver.WdmClients==0&&d.Driver.AsioClients==0;
        StreamModeSection.Visibility=d.Connected&&d.Driver.StreamingMode is 1 or 2?Visibility.Visible:Visibility.Collapsed;
        StreamModeCombo.IsEnabled=StreamModeApply.IsEnabled=d.Connected&&d.Driver.WdmClients==0&&d.Driver.AsioClients==0;
        LinkedCheck.IsEnabled=d.Connected && new uint[]{1,2}.All(n=>d.Channels.Any(c=>c.Direction==0&&c.Number==n&&c.CanVolume&&c.Volume is not null));
        MuteAllButton.IsEnabled=d.Connected && d.Channels.Any(c=>c.Direction==0&&c.CanMute&&c.Muted is not null);
        SavePresetButton.IsEnabled=d.Connected;
        ApplyPresetButton.IsEnabled=d.Connected && PresetsList.SelectedItem is Preset;
        RateSection.Visibility=DeviceCapabilities.HasRate(d)?Visibility.Visible:Visibility.Collapsed;
        BufferSection.Visibility=DeviceCapabilities.HasBuffer(d)?Visibility.Visible:Visibility.Collapsed;
        var audio=DeviceCapabilities.HasRate(d)||DeviceCapabilities.HasBuffer(d);
        AudioTitle.Visibility=audio?Visibility.Visible:Visibility.Collapsed;
        LinkedCheck.Visibility=d.Connected&&new uint[]{1,2}.All(n=>d.Channels.Any(c=>c.Direction==0&&c.Number==n&&DeviceCapabilities.HasVolume(c)))?Visibility.Visible:Visibility.Collapsed;
        var mute=d.Connected&&d.Channels.Any(c=>c.Direction==0&&DeviceCapabilities.HasMute(c));
        MuteAllButton.Visibility=mute?Visibility.Visible:Visibility.Collapsed;
        var muteLabel=MuteOutputs(d) is {Length:>0} outputs&&outputs.All(c=>c.Muted==true)?"取消输出静音":"输出全部静音";
        MuteAllButton.Content=muteLabel;
        if(trayMute is not null){trayMute.Visible=mute;trayMute.Enabled=mute;trayMute.Text=muteLabel;}
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
        RenderDriver(d);
        RenderChannels(d);EnableControls(d);UpdateDiagnostics();rendering=false;
        if(reconnectCheckDirectory is not null)
        {
            try
            {
                Directory.CreateDirectory(reconnectCheckDirectory);
                File.WriteAllText(Path.Combine(reconnectCheckDirectory,"state.json"),JsonSerializer.Serialize(new {Version="1.21",ProcessId=Environment.ProcessId,Updated=DateTimeOffset.Now,d.Connected,d.Demo,d.Status,d.SampleRate,d.BufferSize,DeviceChanges=deviceChanges,NotificationRegistered=notifications is not null},new JsonSerializerOptions {WriteIndented=true}));
            }
            catch(IOException){}
        }
    }
    private void RenderDriver(DeviceSnapshot d)
    {
        if(!d.Connected){selectedFormat=null;selectedStreamMode=null;}
        SafeModeCheck.IsChecked=d.SafeMode;
        var state=d.Driver;var lines=new List<string>();
        if(d.Connected)
        {
            if(state.InputFormat is {} input)lines.Add($"输入：{input.Bits} 位 / {input.Channels} 通道");
            if(state.OutputFormat is {} output)lines.Add($"输出：{output.Bits} 位 / {output.Channels} 通道");
            if(state.Clock is {} clock)lines.Add($"时钟：{(clock.Name.Contains("Internal Clock",StringComparison.OrdinalIgnoreCase)?"内部时钟":clock.Name)} · {(clock.Locked?"已锁定":"未锁定")} · {clock.Rate/1000d:0.###} kHz");
            if(state.Streaming is {} streaming)lines.Add($"音频流：{(streaming?"运行中":"空闲")}");
            if(state.WdmClients is {} wdm)lines.Add($"WDM 客户端：{wdm}");
            if(state.AsioClients is {} asio)lines.Add($"ASIO 客户端：{asio}");
        }
        var text=string.Join(Environment.NewLine,lines);if(DriverStateText.Text!=text)DriverStateText.Text=text;
        DriverStateText.Visibility=lines.Count>0?Visibility.Visible:Visibility.Collapsed;
        if(!OutputFormatCombo.Items.Cast<DriverOption>().Select(o=>o.Value).SequenceEqual(state.OutputFormats.Select(f=>f.Id)))
            OutputFormatCombo.ItemsSource=state.OutputFormats.Select(f=>new DriverOption(f.Id,$"{f.Bits} 位 / {f.Channels} 通道")).ToArray();
        OutputFormatCombo.SelectedItem=OutputFormatCombo.Items.Cast<DriverOption>().FirstOrDefault(o=>o.Value==(selectedFormat??state.OutputFormat?.Id));
        StreamModeCombo.SelectedItem=StreamModeCombo.Items.Cast<DriverOption>().FirstOrDefault(o=>o.Value==(selectedStreamMode??state.StreamingMode));
        foreach(var item in PagesMenu.Items.OfType<MenuItem>().Where(i=>i.Tag?.ToString()=="6"))
            item.Visibility=d.Connected&&(lines.Count>0||d.SafeMode is not null)?Visibility.Visible:Visibility.Collapsed;
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
        if(!oldKeys.SequenceEqual(keys)){ChannelsPanel.Children.Clear();channelViews.Clear();meterViews.Clear();}
        foreach(var c in channels)
        {
            if(channelViews.TryGetValue((c.Direction,c.Number),out var existing))
            {
                UpdateChannel(existing,c);continue;
            }
            var row=new Grid {Width=90,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(3,0,3,0),Background=new SolidColorBrush(Color.FromRgb(239,244,246))};
            row.RowDefinitions.Add(new(){Height=new GridLength(26)});row.RowDefinitions.Add(new(){Height=new GridLength(140)});
            row.RowDefinitions.Add(new(){Height=new GridLength(30)});row.RowDefinitions.Add(new(){Height=new GridLength(25)});
            row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new(){Width=new GridLength(22)});
            AddMeter(row,c.Direction,c.Number);
            var label=new TextBlock {Text=c.Name,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.SemiBold};Grid.SetColumnSpan(label,2);row.Children.Add(label);
            var slider=new Slider {Minimum=0,Maximum=100,TickFrequency=1,SmallChange=1,LargeChange=10,IsSnapToTickEnabled=true,
                Value=c.Volume is {} value?VolumeScale.ToPercent(value):0,
                IsEnabled=DeviceCapabilities.HasVolume(c),Orientation=Orientation.Vertical,Width=36,Height=140,HorizontalAlignment=HorizontalAlignment.Center,Style=(Style)FindResource("ChannelFader")};
            Grid.SetRow(slider,1);row.Children.Add(slider);
            VolumePointer.Attach(slider);
            slider.LostMouseCapture+=(_,_)=>{if(!closed){volumeTimer.Stop();volumeTimer.Start();}};
            var number=new TextBox {Text=$"{slider.Value:0}",Width=48,Padding=new Thickness(5,3,5,3),VerticalAlignment=VerticalAlignment.Center,ToolTip="输入 0–100，按 Enter 应用；选中推子后可用方向键每次调 1%。",MaxLength=3};
            var percentPanel=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
            percentPanel.Children.Add(number);percentPanel.Children.Add(new TextBlock {Text="%",Margin=new Thickness(4,0,0,0),VerticalAlignment=VerticalAlignment.Center});
            Grid.SetRow(percentPanel,2);Grid.SetColumnSpan(percentPanel,2);row.Children.Add(percentPanel);
            var mute=new CheckBox {Content="静音",IsChecked=c.Muted,IsEnabled=c.CanMute&&c.Muted is not null,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};Grid.SetRow(mute,3);Grid.SetColumnSpan(mute,2);row.Children.Add(mute);
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
        bool driverChange=changes.Any(s=>s.Kind is not ("volume" or "mute"));
        if(driverChange){meterSuspended=true;StopMeters();}
        try {await Run(async()=>
        {
            if(driverChange&&!session.Snapshot.Demo)await session.RefreshAsync();
            var reply=await session.ApplyAsync(changes);
            Record(reply.Message);
            foreach(var result in reply.Results??[])Record($"{result.Operation}：{(result.Success?"成功":"失败")} · {result.Message}");
        });} finally {meterSuspended=false;}
    }
    private async void DetectClick(object sender,RoutedEventArgs e)=>await Detect();
    private async void RateClick(object sender,RoutedEventArgs e)
    {var selected=RateCombo.SelectedItem as RateOption;if(selected is null)return;await FlushVolumes();selection.Clear();await Apply([new("rate",Value:(int)selected.Value)]);}
    private async void BufferClick(object sender,RoutedEventArgs e)
    {if(BufferCombo.SelectedItem is not uint selected)return;await FlushVolumes();selection.Clear();await Apply([new("buffer",Value:(int)selected)]);}
    private async void SafeModeClick(object sender,RoutedEventArgs e)
    {if(rendering||busy)return;var enabled=SafeModeCheck.IsChecked==true;await FlushVolumes();await Apply([new("safemode",Value:enabled?1:0)]);}
    private async void OutputFormatClick(object sender,RoutedEventArgs e)
    {if(OutputFormatCombo.SelectedItem is not DriverOption option)return;await FlushVolumes();selectedFormat=null;await Apply([new("format",Value:(int)option.Value)]);}
    private async void StreamModeClick(object sender,RoutedEventArgs e)
    {if(StreamModeCombo.SelectedItem is not DriverOption option)return;await FlushVolumes();selectedStreamMode=null;await Apply([new("streammode",Value:(int)option.Value)]);}
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
        meterSuspended=true;StopMeters();
        try {await Run(async()=>
        {
            if(!session.Snapshot.Demo)await session.RefreshAsync();
            var p=PresetCapture.From(session.Snapshot,PresetName.Text,session.Linked);presets.Save(p);LoadPresets();Record("已保存预设："+p.Name);
        });} finally {meterSuspended=false;}
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
                "volume"=>$"{(s.Direction==1?"输入":"输出")} {s.Channel} 音量控制值：{s.Value}","mute"=>$"{(s.Direction==1?"输入":"输出")} {s.Channel}：{(s.Value==1?"静音":"取消静音")}",
                "safemode"=>$"ASIO 安全模式：{(s.Value==1?"开启":"关闭")}","streammode"=>$"音频流：{(s.Value==2?"持续开启":"按需开启")}",
                "format"=>$"输出格式：{session.Snapshot.Driver.OutputFormats.FirstOrDefault(f=>f.Id==s.Value)?.Bits.ToString()??s.Value.ToString()} 位",_=>s.Kind}));
        else PresetPreview.Text="从左侧选择预设，检查内容后点击应用。";
        EnableControls();
    }
    private async void ApplyPresetClick(object sender,RoutedEventArgs e)
    {
        if(PresetsList.SelectedItem is not Preset p)return;await FlushVolumes();selection.Clear();selectedFormat=null;selectedStreamMode=null;
        meterSuspended=true;StopMeters();
        try {await Run(async()=>{if(!session.Snapshot.Demo)await session.RefreshAsync();ControlPolicy.ValidatePreset(session.Snapshot,p);var reply=await session.ApplyAsync(p.Settings);
            if(reply.Success)session.Linked=p.Linked;Record(reply.Message);foreach(var result in reply.Results??[])Record($"{result.Operation}：{result.Message}");});}
        finally {meterSuspended=false;}
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
            if(live.Connected)
            {
                meterDevice=live.DeviceId;levels=new();
                for(int i=0;i<25;i++){await Task.Delay(40);UpdateMeters();}
                File.WriteAllText(Path.Combine(smokeDirectory!,"live-levels.json"),JsonSerializer.Serialize(new {levels.InputAvailable,levels.OutputAvailable,levels.InputPackets,levels.OutputPackets,levels.Error,Peaks=displayedPeaks},new JsonSerializerOptions {WriteIndented=true}));
                SaveImage("levels-live.png");
                var source=typeof(LiveLevels).GetField("input",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)?.GetValue(levels);
                var capture=source?.GetType().GetField("capture",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)?.GetValue(source) as NAudio.CoreAudioApi.WasapiCapture;
                if(capture is not null&&levels.InputAvailable)
                {
                    capture.StopRecording();await Task.Delay(200);
                    if(levels.InputAvailable)throw new Exception("采集停止后状态未清除。");
                    var retryDeadline=DateTime.UtcNow.AddSeconds(5);
                    while(!levels.InputAvailable&&DateTime.UtcNow<retryDeadline){await Task.Delay(100);levels.RetryStopped();}
                    if(!levels.InputAvailable)throw new Exception("同一设备的采集停止后未自动恢复。");
                }
                await session.RefreshAsync();Render();PresetName.Text="电平预设检查";await SavePreset();
                if(levels is not null)throw new Exception("保存预设未暂停电平采集。");
                var meterPreset=presets.ReadAll().Single(p=>p.Name=="电平预设检查");
                if(session.Snapshot.Driver.WdmClients==0&&session.Snapshot.Driver.AsioClients==0&&session.Snapshot.Driver.FormatSelectionMode==1&&
                    (!meterPreset.Settings.Any(s=>s.Kind=="format")||!meterPreset.Settings.Any(s=>s.Kind=="streammode")))throw new Exception("电平采集影响了驱动预设保存。");
                presets.Delete("电平预设检查");LoadPresets();
                StopMeters();
            }
            Pages.SelectedIndex=6;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);SaveImage("driver-live.png");Pages.SelectedIndex=0;
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
            var partial=live with {Connected=true,SampleRates=[],BufferSizes=[],AsioFlags=null,Driver=new(),Channels=[
                new(1,1,"仅静音",false,true,0,0,0,null,false),new(1,2,"无支持",false,false,0,0,0,null,null),
                new(0,1,"仅音量",true,false,-32512,0,256,-2048,null)]};
            Render(partial);
            if(SafeModeCheck.Visibility!=Visibility.Collapsed||OutputFormatSection.Visibility!=Visibility.Collapsed||StreamModeSection.Visibility!=Visibility.Collapsed||DriverStateText.Visibility!=Visibility.Collapsed)throw new Exception("未隐藏不支持的原厂驱动功能。");
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
            await Apply([new("safemode",Value:1),new("format",Value:65538),new("streammode",Value:2)]);
            if(session.Snapshot.SafeMode!=true||session.Snapshot.Driver.OutputFormat?.Bits!=16||session.Snapshot.Driver.StreamingMode!=2)throw new Exception("驱动设置模拟切换失败。");
            await Apply([new("buffer",Value:512)]);
            if(session.Snapshot.SafeMode!=true)throw new Exception("缓冲调整清除了安全模式。");
            Pages.SelectedIndex=6;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);SaveImage("driver-demo.png");Pages.SelectedIndex=0;
            await Apply([new("safemode",Value:0),new("format",Value:65537),new("streammode",Value:1)]);
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
            if(dragSlider.Orientation!=Orientation.Vertical)throw new Exception("通道推子未竖向显示。");
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
            VolumePointer.Move(dragSlider,new Point(dragSlider.ActualWidth/2,0));
            if(dragSlider.Value!=100)throw new Exception("竖向推子顶部不是最大音量。");
            VolumePointer.Move(dragSlider,new Point(dragSlider.ActualWidth/2,dragSlider.ActualHeight));
            if(dragSlider.Value!=0)throw new Exception("竖向推子底部不是最小音量。");
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
            OutputFormatCombo.SelectedItem=OutputFormatCombo.Items.Cast<DriverOption>().Single(o=>o.Value==65538);
            StreamModeCombo.SelectedItem=StreamModeCombo.Items.Cast<DriverOption>().Single(o=>o.Value==2);
            ApplyPresetClick(ApplyPresetButton,new RoutedEventArgs());
            while(busy)await Task.Delay(20);
            if(OutputFormatCombo.SelectedItem is not DriverOption appliedFormat||appliedFormat.Value!=session.Snapshot.Driver.OutputFormat?.Id||
                StreamModeCombo.SelectedItem is not DriverOption appliedMode||appliedMode.Value!=session.Snapshot.Driver.StreamingMode)
                throw new Exception("应用预设后仍保留未应用的驱动选择。");
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
            Pages.SelectedIndex=5;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);SaveImage("about.png");
            if(end.Y>PanelGuide.GuideScroll.ActualHeight+1||end.Y<0)throw new Exception($"面板说明末尾不能滚动到可见区域：底部={end.Y},高度={PanelGuide.GuideScroll.ActualHeight},偏移={PanelGuide.GuideScroll.VerticalOffset},最大偏移={PanelGuide.GuideScroll.ScrollableHeight}。");
            Pages.SelectedIndex=0;Render(live);await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);SaveImage("device-small.png");
            foreach(var view in channelViews.Values.Where(v=>v.Mute.Visibility==Visibility.Visible))
                if(view.Mute.TranslatePoint(new Point(0,view.Mute.ActualHeight),Pages).Y>Pages.ActualHeight)throw new Exception("小窗口下静音控件被裁切。");
            File.WriteAllText(Path.Combine(smokeDirectory!,"smoke-result.json"),JsonSerializer.Serialize(new {Success=true,LiveConnected=live.Connected,live.SampleRate,live.BufferSize,Checks=new[]{"全部静音再次点击取消及部分静音切换","静音按钮文字跟随回读状态","切换输出静音不改变输入或音量","音量输入及方向键按1%调节","相同硬件值回读保留用户百分比","拒绝非法及越界百分比","音量轨道点击定位","拖动停顿及刷新保留鼠标捕获与滑块位置","松开后提交最终音量","开机启动注册、带空格路径引用及关闭","Windows拔插消息使旧连接失效并触发自动重新枚举","原厂音量表","按能力隐藏各控件及占位通道","无设备禁写","真实设备只读检测","未连接状态后台自动枚举并恢复控件","真实与演示后台刷新不重建控件或切换启用状态","刷新保留选项列表","演示静音","中文预设保存","七页渲染"}},new JsonSerializerOptions {WriteIndented=true}));
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
            await Run(async()=>
            {
                if(!slider.IsEnabled||!RateCombo.IsEnabled||!MuteAllButton.IsEnabled)
                    throw new Exception("点击操作使无关控件整页变灰。");
                var repeat=new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
                    {RoutedEvent=Mouse.PreviewMouseDownEvent};
                RateApply.RaiseEvent(repeat);
                if(!repeat.Handled)throw new Exception("操作期间未阻止重复点击。");
                await Task.Delay(30);
            });
            if(session.Snapshot.Demo)
            {
                var channel=session.Snapshot.Channels.First(c=>DeviceCapabilities.HasMute(c));
                await Apply([new("mute",channel.Direction,channel.Number,channel.Muted==true?0:1)]);
                await Apply([new("mute",channel.Direction,channel.Number,channel.Muted==true?1:0)]);
            }
            if(transitions!=0)throw new Exception("点击操作切换了无关控件的启用状态。");
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

using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Donner.Core;

namespace Donner.Panel;
public partial class MainWindow
{
    private sealed class TestMixer:IMixerClient
    {
        internal readonly Dictionary<string,float> Values=[];
        internal readonly List<(string,float)> Writes=[];
        internal readonly Dictionary<(bool,int),string> Names=[];
        public bool Connected {get;set;}=true;
        internal bool DelayWrites;
        public string Status=>Connected?"测试连接":"测试断开";
        public bool Refresh()=>Connected;
        public void Start(bool show=false){Starts.Add(show);}
        internal readonly List<bool> Starts=[];
        public float Get(string key)=>Values.GetValueOrDefault(key,key.EndsWith(".f")?1000:key.EndsWith(".q")?1:0);
        public void Set(string key,float value){if(!Connected)throw new IOException("Disconnected");MixerRules.Validate(key,value);if(!DelayWrites)Values[key]=value;Writes.Add((key,value));}
        public string DeviceName(bool input,int index)=>Names.GetValueOrDefault((input,index),"");
        public MixerDevice[] Devices(bool input)=>[new(1,""),new(3,"Livejack Audio Interface"),new(5,"Donner USB Audio Device")];
        public void SetDevice(bool input,int index,MixerDevice device){if(device.Type==5&&(input||index!=0))throw new IOException("ASIO位置错误");Names[(input,index)]=device.Name;}
        public float Level(bool input,int index)=>.04f+index*.08f;
        public void Dispose(){}
    }
    private sealed class ReadOnlyMixer(IMixerClient inner):IMixerClient
    {
        internal int WriteAttempts;
        public bool Connected=>inner.Connected;
        public string Status=>inner.Status;
        public bool Refresh()=>inner.Refresh();
        public void Start(bool show=false){WriteAttempts++;throw new IOException("截图期间不启动 Banana。");}
        public float Get(string key)=>inner.Get(key);
        public void Set(string key,float value){WriteAttempts++;throw new IOException("截图期间禁止修改 Banana 参数。");}
        public string DeviceName(bool input,int index)=>inner.DeviceName(input,index);
        public MixerDevice[] Devices(bool input)=>inner.Devices(input);
        public void SetDevice(bool input,int index,MixerDevice device){WriteAttempts++;throw new IOException("截图期间禁止修改设备。");}
        public float Level(bool input,int index)=>inner.Level(input,index);
        public float ChannelLevel(bool input,int index,int channel)=>inner.ChannelLevel(input,index,channel);
        public void Dispose()=>inner.Dispose();
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);if(child is T found)yield return found;
            foreach(var nested in Visuals<T>(child))yield return nested;
        }
    }
    private async Task VerifyMixer()
    {
        if(MixerEnableCheck.IsChecked==true||MixerPageButton.Visibility!=Visibility.Collapsed||MixerPage.Visibility!=Visibility.Collapsed||mixerPanel is not null)throw new Exception("调音台未保持默认关闭。");
        File.WriteAllText(MixerSettingsPath,"invalid json");LoadMixerPreference();UpdateMixer();
        if(mixerEnabled||MixerEnableCheck.IsChecked==true)throw new Exception("损坏设置自动启用调音台。");
        File.Delete(MixerSettingsPath);
        var cleanupGate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TrackMeterCleanup(cleanupGate.Task);var waitingForOldMeter=StopMetersAndWaitAsync();
        try{if(waitingForOldMeter.IsCompleted)throw new Exception("已离开主页的旧采集任务逃过设备写入等待。");}
        finally{cleanupGate.TrySetResult();}
        await waitingForOldMeter;
        await VerifyMicrophoneEqUi();
        var customChecks=MixerEndpointNamesSmoke.Verify();await MixerSampleRatesSmoke.VerifyAsync();await MixerHomeMetersSmoke.VerifyAsync();await BackgroundMeterSourceSmoke.VerifyAsync();
        File.WriteAllText(Path.Combine(smokeDirectory!,"mixer-extended-checks.json"),JsonSerializer.Serialize(new{Success=true,CustomNames=customChecks,SampleRates=true}));
        var nameDialogClock=System.Diagnostics.Stopwatch.StartNew();
        MixerEndpointNames.Show(this,storage,window=>
        {
            var close=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(250)};
            close.Tick+=(_,_)=>{close.Stop();window.Close();};close.Start();
        });
        if(nameDialogClock.ElapsedMilliseconds>1500)throw new Exception("设备名称窗口的后台读取阻塞了打开或关闭操作。");
        File.WriteAllText(Path.Combine(smokeDirectory!,"name-dialog-responsiveness.json"),JsonSerializer.Serialize(new{OpenAndCloseMilliseconds=nameDialogClock.Elapsed.TotalMilliseconds}));
        var namesBackup=Path.Combine(storage,"mixer-endpoint-names.json");
        foreach(var bad in new[]{"invalid json","[null]"})
        {
            File.WriteAllText(namesBackup,bad);MixerNamesClick(this,new RoutedEventArgs());
            if(File.ReadAllText(namesBackup)!=bad)throw new Exception("损坏名称备份被修改。");
        }
        File.Delete(namesBackup);
        var fake=new TestMixer();using var panel=new MixerPanel(fake,false);
        Visuals<Button>(panel).Single(x=>x.Content?.ToString()=="打开原界面").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Visuals<Button>(panel).Single(x=>x.Content?.ToString()=="后台运行").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(!fake.Starts.SequenceEqual(new[]{true,false}))throw new Exception("后台运行与原界面操作未分开。");
        MixerSlot.Content=panel;mixerEnabled=true;UpdateMixerNavigation();Pages.SelectedIndex=0;
        if(Visuals<MixerPanel>(Pages).Any())throw new Exception("主页仍包含调音台。");
        MixerPageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(Pages.SelectedItem!=MixerPage||MixerPageButton.Content?.ToString()!="主页")throw new Exception("调音台按钮未切换至独立页面。");
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        var scroll=Visuals<ScrollViewer>(Pages).First();scroll.ScrollToTop();
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);panel.Poll(true);
        if(fake.Writes.Count!=0)throw new Exception("首次读取擅自更改了调音台配置。");
        panel.Queue("Strip[5].Gain",0);panel.Queue("Strip[0].Gain",float.NaN);
        if(fake.Writes.Count!=0||panel.PendingCount!=0)throw new Exception("无效参数进入调音台写入队列。");
        var fader=Visuals<Slider>(panel).First();fader.Value=-9.5;panel.Poll(true);
        if(fake.Values.GetValueOrDefault("Strip[0].Gain")!=-9.5f)throw new Exception("调音台推子未写入正确条带。");
        fake.DelayWrites=true;fader.Value=-12;panel.Poll(true);
        if(fader.Value!=-12)throw new Exception("异步回读把刚调节的推子拉回旧位置。");
        fake.Values["Strip[0].Gain"]=-12;fake.DelayWrites=false;panel.Poll(true);
        var route=Visuals<CheckBox>(panel).First(x=>x.Content?.ToString()=="B1");route.IsChecked=true;route.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));panel.Poll(true);
        if(fake.Values.GetValueOrDefault("Strip[0].B1")!=1)throw new Exception("直播路由未写入。");
        var mute=Visuals<CheckBox>(panel).First(x=>x.Content?.ToString()=="静音");mute.IsChecked=true;mute.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));panel.Poll(true);
        if(fake.Values.GetValueOrDefault("Strip[0].Mute")!=1)throw new Exception("调音台静音未写入。");
        var count=fake.Writes.Count;fake.Values["Strip[1].Gain"]=-18;panel.Poll(true);
        if(!ReferenceEquals(fader,Visuals<Slider>(panel).First())||fake.Writes.Count!=count)throw new Exception("调音台刷新重建控件或触发回写。");
        var reset=Visuals<TextBox>(panel).First(x=>x.Tag?.ToString()=="Strip[0].Gain");
        void SubmitGain(TextBox field,string text){field.Text=text;field.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,Environment.TickCount,field,null){RoutedEvent=System.Windows.Input.Keyboard.LostKeyboardFocusEvent});panel.Poll(true);}
        SubmitGain(reset,"-6.3");if(Math.Abs(fake.Values.GetValueOrDefault("Strip[0].Gain")+6.3f)>.001||Math.Abs(fader.Value+6.3)>.001)throw new Exception("直接输入未同步推子与增益。");
        var writesBeforeInvalid=fake.Writes.Count;foreach(var bad in new[]{"13","-61","NaN","abc",""})SubmitGain(reset,bad);
        if(fake.Writes.Count!=writesBeforeInvalid)throw new Exception("无效分贝输入写入了设备。");
        fader.Value=-7;panel.Poll(true);SubmitGain(reset,"0");
        if(fader.Value!=0||fake.Values.GetValueOrDefault("Strip[0].Gain")!=0)throw new Exception("点击增益数值未精确归零。");
        fader.Value=-5;panel.Poll(true);
        fader.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Left){RoutedEvent=Control.PreviewMouseDoubleClickEvent});panel.Poll(true);
        if(fader.Value!=0||fake.Values.GetValueOrDefault("Strip[0].Gain")!=0)throw new Exception("双击推子未精确归零。");
        fader.Value=.1;panel.Poll(true);if(Math.Abs(fake.Values.GetValueOrDefault("Strip[0].Gain")-.1f)>.001)throw new Exception("零点吸附破坏了非鼠标微调。");
        var micButtons=Visuals<Button>(panel).Where(x=>x.Tag?.ToString()?.StartsWith("MicrophoneEq.")==true).ToArray();
        var inputEq=Visuals<EqEditor>(panel).Single();
        if(inputEq.SelectedTarget!=0||((SolidColorBrush)micButtons[0].Background).Color==((SolidColorBrush)micButtons[1].Background).Color)throw new Exception("默认麦克风EQ未高亮第一个输入。");
        micButtons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(inputEq.SelectedTarget!=1||((SolidColorBrush)micButtons[1].Background).Color==((SolidColorBrush)micButtons[0].Background).Color)throw new Exception("按钮选择未同步目标与高亮。");
        if(Visuals<ComboBox>(inputEq).Any(x=>x.Items.Count==3&&x.Items[0]?.ToString()=="硬件输入 1"))throw new Exception("麦克风输入下拉框仍显示。");
        int windowCount=System.Windows.Application.Current.Windows.Count;
        Visuals<Button>(panel).First(x=>x.Content?.ToString()=="麦克风 EQ").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(System.Windows.Application.Current.Windows.Count!=windowCount||Visuals<EqEditor>(panel).Count()!=1)throw new Exception("麦克风EQ未保持页面内编辑。");
        scroll.ScrollToTop();
        SaveImage("mixer-inputs.png");
        var tabs=Visuals<TabControl>(panel).Single();tabs.SelectedIndex=1;
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);panel.Poll(true);
        var outputReset=Visuals<TextBox>(panel).First(x=>x.Tag?.ToString()=="Bus[0].Gain");
        var outputFader=Visuals<Slider>(panel).First();outputFader.Value=6;panel.Poll(true);SubmitGain(outputReset,"0");
        if(outputFader.Value!=0||fake.Values.GetValueOrDefault("Bus[0].Gain")!=0||Math.Abs(fake.Values.GetValueOrDefault("Strip[0].Gain")-.1f)>.001)throw new Exception("输出归零作用到了错误的通道。");
        fake.Values["Bus[0].Mono"]=2;panel.Poll(true);
        if(Visuals<CheckBox>(panel).First(x=>x.Content?.ToString()=="单声道").IsChecked==true)throw new Exception("立体声反转被显示成了单声道。");
        var eqBusCombo=Visuals<ComboBox>(panel).First(x=>x.Items.Count==5&&x.Items[0]?.ToString()=="A1 监听");eqBusCombo.SelectedIndex=3;panel.Poll(true);
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        var freq=Visuals<TextBox>(panel).First(x=>x.Text=="1000");freq.Text="2500";freq.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,Environment.TickCount,freq,null){RoutedEvent=System.Windows.Input.Keyboard.LostKeyboardFocusEvent});panel.Poll(true);
        if(fake.Values.GetValueOrDefault("Bus[3].EQ.channel[0].cell[0].f")!=2500||fake.Values.GetValueOrDefault("Bus[3].EQ.channel[1].cell[0].f")!=2500)throw new Exception("EQ左右联动未作用于所选直播总线。");
        var outputEditor=Visuals<EqEditor>(panel).Single();
        outputEditor.Graph.BandChanged!(0,new(EqFilter.Bell,1500,-4,2));panel.Poll(true);
        if(fake.Values.GetValueOrDefault("Bus[3].EQ.channel[0].cell[0].gain")!=-4||fake.Values.GetValueOrDefault("Bus[3].EQ.channel[1].cell[0].gain")!=-4)throw new Exception("输出曲线未联动写入目标总线。");
        var linkedEq=Visuals<CheckBox>(outputEditor).Single(x=>x.Content?.ToString()=="左右联动");linkedEq.IsChecked=false;
        var channelChoice=Visuals<ComboBox>(outputEditor).Single(x=>x.Items.Count==2&&x.Items[0]?.ToString()=="左");channelChoice.SelectedIndex=1;
        outputEditor.Graph.BandChanged!(0,new(EqFilter.Bell,1500,3,2));panel.Poll(true);
        if(fake.Values.GetValueOrDefault("Bus[3].EQ.channel[0].cell[0].gain")!=-4||fake.Values.GetValueOrDefault("Bus[3].EQ.channel[1].cell[0].gain")!=3)throw new Exception("输出曲线未保持单侧编辑隔离。");
        fake.Values["Bus[3].EQ.channel[1].cell[0].gain"]=1;await Task.Delay(2200);
        if(Math.Abs(outputEditor.Graph.Settings.Bands[0].Gain-1)>.001)throw new Exception("输出曲线没有跟随外部参数回读。");
        linkedEq.IsChecked=true;
        Visuals<Button>(outputEditor).Single(x=>x.Content?.ToString()=="恢复平直").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));panel.Poll(true);
        if(fake.Values.GetValueOrDefault("Bus[3].EQ.channel[0].cell[0].gain")!=0||fake.Values.GetValueOrDefault("Bus[3].EQ.channel[1].cell[0].gain")!=0)throw new Exception("联动恢复平直没有重置两侧。");
        outputEditor.BringIntoView();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);SaveImage("mixer-output-eq-inline.png");scroll.ScrollToTop();
        SaveImage("mixer-eq.png");
        count=fake.Writes.Count;fake.Connected=false;panel.Poll(true);panel.Queue("Strip[0].Gain",0);panel.Poll(true);
        if(panel.ControlsEnabled||fake.Writes.Count!=count||panel.PendingCount!=0)throw new Exception("断开后仍可提交调音台参数。");
        fake.Connected=true;panel.Poll(true);if(!panel.ControlsEnabled)throw new Exception("调音台恢复后未启用。");
        var guide=new MixerSetupWindow{Owner=this};
        guide.Loaded+=(_,_)=>Dispatcher.BeginInvoke(new Action(()=>guide.Close()));
        if(guide.ShowDialog()==true)throw new Exception("取消安装引导仍启用调音台。");
        var signed=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../build/banana-sdk/VoicemeeterProSetup.exe"));
        bool signatureTested=File.Exists(signed);
        if(signatureTested)await MixerSetupWindow.VerifyInstaller(signed,CancellationToken.None);
        bool rejected=false;try{await MixerSetupWindow.VerifyInstaller(Environment.ProcessPath!,CancellationToken.None);}catch(IOException){rejected=true;}
        if(!rejected)throw new Exception("未签名的程序通过安装签名验证。");
        File.WriteAllText(Path.Combine(smokeDirectory!,"mixer-checks.json"),JsonSerializer.Serialize(new{Success=true,RealBananaTested=false,OfficialInstallerSignatureTested=signatureTested,Checks=new[]{"默认关闭","无首次参数写入","推子、静音和直播路由","外部参数回读且控件复用","EQ目标及左右联动","断线禁写与恢复","取消安装引导","安装包签名校验"}},new JsonSerializerOptions{WriteIndented=true}));
        MixerPageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(Pages.SelectedIndex!=0||MixerPageButton.Content?.ToString()!="调音台")throw new Exception("主页按钮未返回主页。");
        if(!ReferenceEquals(MixerSlot.Content,panel))throw new Exception("切换页面丢失调音台实例。");
        MixerPageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));mixerEnabled=false;UpdateMixerNavigation();
        if(Pages.SelectedIndex!=0||MixerPageButton.Visibility!=Visibility.Collapsed)throw new Exception("停用调音台未返回主页并隐藏按钮。");
        MixerSlot.Content=null;
        await CaptureLiveMixer();
    }
    private async Task CaptureLiveMixer()
    {
        bool connected=false;int writes=0;string? error=null;var width=Width;var height=Height;var feedback=Feedback.Text;
        try
        {
            if(VoicemeeterClient.InstalledDirectory() is null)return;
            using var client=new ReadOnlyMixer(new VoicemeeterClient());
            if(!client.Refresh())return;
            connected=true;Feedback.Text="已连接 Voicemeeter Banana";using var livePanel=new MixerPanel(client,false,new MixerSampleRates(client,()=>session.Snapshot,_=>throw new IOException("截图期间禁止修改声卡。"),false),()=>{});
            mixerEnabled=true;UpdateMixerNavigation();MixerSlot.Content=livePanel;Pages.SelectedItem=MixerPage;
            Width=1100;Height=820;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            livePanel.Poll(true);
            if(!livePanel.ControlsEnabled)throw new IOException("真实 Banana 参数读取失败，未生成已连接截图。");
            var rates=Visuals<MixerSampleRates>(livePanel).Single();await rates.RefreshAsync();await VerifyNativeRateResponsiveness(rates);rates.IsExpanded=true;
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);SaveImage("mixer-live-rates.png");rates.IsExpanded=false;
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);SaveImage("mixer-live-inputs.png");
            Visuals<TabControl>(livePanel).Single().SelectedIndex=1;
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);livePanel.Poll(true);
            Visuals<ScrollViewer>(Pages).First().ScrollToTop();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            SaveImage("mixer-live-eq.png");
            mixerPanel=livePanel;Pages.SelectedIndex=0;MeterCheck.IsChecked=true;nextMixerMeterRoute=0;await RefreshMixerHomeOutputIdentityAsync();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateMeters();
            var routed=ReadMixerHomeOutput(out var meterAvailable,out var outputLeft,out var outputRight);
            File.WriteAllText(Path.Combine(smokeDirectory!,"mixer-home-meters.json"),JsonSerializer.Serialize(new{MappedToLivejack=routed,OutputAvailable=meterAvailable,Bus=mixerOutputBus,OutputLeft=outputLeft,OutputRight=outputRight,LoopbackCaptureRequested=levels?.CapturesOutput},new JsonSerializerOptions{WriteIndented=true}));
            if(routed&&(!meterAvailable||levels?.CapturesOutput==true))throw new IOException("主页没有使用独占兼容的 Banana 输出电平。");
            Width=900;Height=620;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);SaveImage("home-with-mixer.png");
            var held=levels;levels=null;MeterCheck.IsChecked=false;
            try{UpdateMeters();if(meterViews.Values.Any(x=>x.Visibility==Visibility.Visible))throw new IOException("关闭电平后仍显示调音台输出。");}
            finally{held?.Dispose();if(held is not null)await held.WaitForSourcesAsync();}
            MeterCheck.IsChecked=true;StopMeters();mixerPanel=null;
            writes=client.WriteAttempts;
            if(writes!=0)throw new IOException("真实截图发生参数写入尝试，已阻止。");
        }
        catch(Exception e) when(e is IOException or System.ComponentModel.Win32Exception){error=e.Message;}
        finally
        {
            await StopMetersAndWaitAsync();mixerPanel=null;MixerSlot.Content=null;mixerEnabled=false;UpdateMixerNavigation();Width=width;Height=height;Feedback.Text=feedback;
            File.WriteAllText(Path.Combine(smokeDirectory!,"mixer-live-check.json"),JsonSerializer.Serialize(new{Connected=connected,WriteAttempts=writes,AudioPathTested=false,Error=error},new JsonSerializerOptions{WriteIndented=true}));
        }
    }
}

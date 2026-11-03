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
        public void Start(){}
        public float Get(string key)=>Values.GetValueOrDefault(key,key.EndsWith(".f")?1000:key.EndsWith(".q")?1:0);
        public void Set(string key,float value){if(!Connected)throw new IOException("Disconnected");MixerRules.Validate(key,value);if(!DelayWrites)Values[key]=value;Writes.Add((key,value));}
        public string DeviceName(bool input,int index)=>Names.GetValueOrDefault((input,index),"");
        public MixerDevice[] Devices(bool input)=>[new(1,""),new(3,"Livejack Audio Interface"),new(5,"Donner USB Audio Device")];
        public void SetDevice(bool input,int index,MixerDevice device){if(device.Type==5&&(input||index!=0))throw new IOException("ASIO位置错误");Names[(input,index)]=device.Name;}
        public float Level(bool input,int index)=>.04f+index*.08f;
        public void Dispose(){}
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
        if(MixerEnableCheck.IsChecked==true||MixerSlot.Visibility!=Visibility.Collapsed||mixerPanel is not null)throw new Exception("调音台未保持默认关闭。");
        File.WriteAllText(MixerSettingsPath,"invalid json");LoadMixerPreference();UpdateMixer();
        if(mixerEnabled||MixerEnableCheck.IsChecked==true)throw new Exception("损坏设置自动启用调音台。");
        File.Delete(MixerSettingsPath);
        var fake=new TestMixer();using var panel=new MixerPanel(fake,false);
        MixerSlot.Visibility=Visibility.Visible;MixerSlot.Content=panel;Pages.SelectedIndex=0;
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        var scroll=Visuals<ScrollViewer>(Pages).First();scroll.ScrollToBottom();
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
        SaveImage("mixer-inputs.png");
        var tabs=Visuals<TabControl>(panel).Single();tabs.SelectedIndex=1;
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);panel.Poll(true);
        fake.Values["Bus[0].Mono"]=2;panel.Poll(true);
        if(Visuals<CheckBox>(panel).First(x=>x.Content?.ToString()=="单声道").IsChecked==true)throw new Exception("立体声反转被显示成了单声道。");
        var eqBusCombo=Visuals<ComboBox>(panel).First(x=>x.Items.Count==5&&x.Items[0]?.ToString()=="A1 监听");eqBusCombo.SelectedIndex=3;panel.Poll(true);
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        var freq=Visuals<TextBox>(panel).First(x=>x.Text=="1000");freq.Text="2500";freq.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,Environment.TickCount,freq,null){RoutedEvent=System.Windows.Input.Keyboard.LostKeyboardFocusEvent});panel.Poll(true);
        if(fake.Values.GetValueOrDefault("Bus[3].EQ.channel[0].cell[0].f")!=2500||fake.Values.GetValueOrDefault("Bus[3].EQ.channel[1].cell[0].f")!=2500)throw new Exception("EQ左右联动未作用于所选直播总线。");
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
        MixerSlot.Content=null;MixerSlot.Visibility=Visibility.Collapsed;scroll.ScrollToTop();
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Donner.Panel;

public partial class FrontPanelGuide : UserControl
{
    private sealed record Part(string Title,string Location,string Function,string Usage);
    private readonly List<Button> markers=[];
    public FrontPanelGuide()
    {
        InitializeComponent();
        SizeChanged+=(_,_)=>MapView.MaxHeight=Math.Clamp(ActualHeight*.32,96,150);
        var parts=new Part[]
        {
            new("01 · INPUT 1 / DYNA MIC PREAMP","最左侧大接口","第一路组合输入：XLR 接麦克风，中央 6.35 mm 插孔接乐器或线路信号。","接吉他或贝斯时选 INST；接键盘或调音台的线路输出时选 LINE。"),
            new("02 · GAIN 1","第一路输入右上方旋钮","调节第一路输入的放大增益，决定录入电脑的信号电平。","演唱或演奏最响的段落时逐渐增加，留出余量；电平到顶或失真时降低 GAIN。"),
            new("03 · INST / LINE 1","GAIN 1 下方开关","切换第一路 6.35 mm 输入的信号类型与输入阻抗。","INST 适合直接接被动拾音器吉他、贝斯；LINE 适合键盘、合成器或其他线路输出。"),
            new("04 · INPUT 2 / DYNA MIC PREAMP","第二个大接口","第二路组合输入，与第一路独立，可接另一支麦克风或另一件乐器。","可同时录人声与吉他；录音软件中分别选择输入 1、输入 2，即可分轨处理。"),
            new("05 · GAIN 2","第二路输入右上方旋钮","调节第二路输入增益，作用与 GAIN 1 相同。","两路增益分别调整；输入太小先检查接线与 INST / LINE，再调 GAIN。"),
            new("06 · INST / LINE 2","GAIN 2 下方开关","切换第二路 6.35 mm 输入的乐器模式或线路模式。","按第二路所接设备选择，不必与第一路相同。线路输入是单声道，立体声源需使用两路输入。"),
            new("07 · DIRECT / PLAYBACK","中间上方小旋钮","调节直接输入监听与电脑播放声音的混合比例。DIRECT 听声卡本地输入，PLAYBACK 听电脑回放。","录音时可同时听自己和伴奏。直接监听延迟低；要听软件效果，应使用软件输入监听并偏向 PLAYBACK。"),
            new("08 · MONO / STEREO","DIRECT / PLAYBACK 下方开关","切换直接监听的声像：MONO 将两路合到中央，STEREO 保留两路的左右声像。","单支麦克风监听可选 MONO，避免只在一边听到。录音轨道的单声道或立体声仍在录音软件中选择。"),
            new("09 · 48V","中间下方圆形按钮","为 XLR 麦克风输入提供 48V 幻象电源，供需要幻象供电的电容麦克风使用。","确认麦克风需要 48V 后再开启。接线前关闭 48V 并调低监听音量；特殊或铝带麦克风先核对其说明书。"),
            new("10 · IN1 / IN2 / OUT 电平屏","面板中央显示屏","显示输入 1、输入 2 与输出的信号电平，便于观察是否有信号以及是否接近满幅。","输入电平到顶时降低对应 GAIN；屏幕显示的是信号电平，不能当作旋钮位置或音量百分比。"),
            new("11 · MONITOR","屏幕右侧大旋钮","调节监听音箱的输出响度。","连接音箱后从低音量开始调整；调大 MONITOR 是让监听更响，不能代替 GAIN 提高麦克风录音电平。"),
            new("12 · 耳机音量旋钮","最右侧上方小旋钮","调节耳机输出的监听响度。","插入耳机前先调低，再逐渐增加；可与音箱的 MONITOR 音量分别调整。"),
            new("13 · 耳机输出接口","最右侧下方，耳机图标处","6.35 mm TRS 立体声耳机输出，用来听输入监听、电脑回放或二者的混合。","3.5 mm 耳机需使用立体声转接头；录人声时可用耳机监听，减少音箱声音再次进入麦克风。")
        };
        // Centers on the supplied panel photograph, in the fixed 1000 x 246 map.
        Point[] centers=[new(102,116),new(203,72),new(203,166),new(298,116),new(398,74),new(398,165),new(507,74),new(467,165),new(534,151),new(640,113),new(798,117),new(934,75),new(929,145)];
        for(var index=0;index<parts.Length;index++)
        {
            var target=index;
            var button=new Button {Content=$"{index+1:00}",Style=(Style)FindResource("GuideMarker"),ToolTip=parts[index].Title};
            System.Windows.Automation.AutomationProperties.SetName(button,parts[index].Title);
            Canvas.SetLeft(button,centers[index].X-18);Canvas.SetTop(button,centers[index].Y-18);
            button.Click+=(_,_)=>PartsPicker.SelectedIndex=target;
            markers.Add(button);PanelMap.Children.Add(button);
        }
        PartsPicker.ItemsSource=parts;PartsPicker.SelectedIndex=0;
    }
    private void PartSelected(object sender,SelectionChangedEventArgs e)
    {
        PartDetail.DataContext=PartsPicker.SelectedItem;
        for(var index=0;index<markers.Count;index++)
        {
            var selected=index==PartsPicker.SelectedIndex;
            markers[index].Background=selected?new SolidColorBrush(Color.FromRgb(18,120,107)):Brushes.White;
            markers[index].Foreground=selected?Brushes.White:new SolidColorBrush(Color.FromRgb(18,120,107));
        }
        GuideScroll.ScrollToTop();
    }
    internal void VerifyNavigation()
    {
        for(var index=0;index<markers.Count;index++)
        {
            markers[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(PartsPicker.SelectedIndex!=index||!ReferenceEquals(PartDetail.DataContext,PartsPicker.SelectedItem))throw new Exception("面板编号没有显示对应说明。");
        }
        PartsPicker.SelectedIndex=0;
    }
}

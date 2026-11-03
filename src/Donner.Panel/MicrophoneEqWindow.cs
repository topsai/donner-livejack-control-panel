using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Donner.Core;

namespace Donner.Panel;

internal sealed class MicrophoneEqWindow:Window
{
    private readonly MicrophoneEqRack rack;
    internal readonly EqGraph Graph=new();
    private readonly ComboBox input=new(){Width=145,Foreground=Brushes.Black,ItemsSource=new[]{"硬件输入 1","硬件输入 2","硬件输入 3"},Margin=new Thickness(0,0,18,0)};
    private readonly CheckBox enabled=new(){Content="启用麦克风 EQ",Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,FontSize=14};
    private readonly CheckBox bandEnabled=new(){Content="启用频段",Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,12,0)};
    private readonly ComboBox type=new(){Width=130,Foreground=Brushes.Black,ItemsSource=new[]{"Bell · 钟形","低切 · High Pass","高切 · Low Pass","低架 · Low Shelf","高架 · High Shelf","陷波 · Notch"},Margin=new Thickness(6,0,12,0)};
    private readonly TextBox frequency=Number(74),gain=Number(62),q=Number(62),preamp=Number(62);
    private readonly TextBlock status=new(){FontSize=12,Foreground=new SolidColorBrush(Color.FromRgb(165,181,197)),Margin=new Thickness(0,8,0,0),TextWrapping=TextWrapping.Wrap};
    private readonly StackPanel bandButtons=new(){Orientation=Orientation.Horizontal};
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(80)};
    private readonly float[] samples=new float[2048];
    private bool updating;
    private string? editError;
    private int index,selected;
    internal MicrophoneEqWindow(MicrophoneEqRack rack)
    {
        this.rack=rack;Title="麦克风参数 EQ · Donner";Width=1000;Height=640;MinWidth=820;MinHeight=530;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=new SolidColorBrush(Color.FromRgb(22,24,31));Foreground=Brushes.White;FontFamily=new FontFamily("Microsoft YaHei UI");FontSize=13;
        var body=new Grid{Margin=new Thickness(18)};Content=body;
        foreach(var height in new[]{GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})body.RowDefinitions.Add(new(){Height=height});
        var header=new DockPanel{Margin=new Thickness(0,0,0,12)};body.Children.Add(header);
        var reset=new Button{Content="恢复平直",FontSize=12,Padding=new Thickness(10,6,10,6),Margin=new Thickness(12,0,0,0)};DockPanel.SetDock(reset,Dock.Right);header.Children.Add(reset);
        var headerLeft=new StackPanel{Orientation=Orientation.Horizontal};header.Children.Add(headerLeft);headerLeft.Children.Add(input);headerLeft.Children.Add(enabled);
        headerLeft.Children.Add(new TextBlock{Text="输出增益",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(20,0,8,0)});headerLeft.Children.Add(preamp);headerLeft.Children.Add(new TextBlock{Text="dB",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5,0,0,0)});
        Grid.SetRow(Graph,1);body.Children.Add(Graph);
        var editor=new StackPanel{Margin=new Thickness(0,10,0,0)};Grid.SetRow(editor,2);body.Children.Add(editor);editor.Children.Add(bandButtons);
        for(int i=0;i<6;i++)
        {
            int band=i;var button=new Button{Content=$"频段 {i+1}",Tag=i,FontSize=12,Padding=new Thickness(15,6,15,6),Margin=new Thickness(0,0,8,0),Background=new SolidColorBrush(EqGraph.Colors[i]),Foreground=Brushes.Black};bandButtons.Children.Add(button);button.Click+=(_,_)=>SelectBand(band);
        }
        var fields=new WrapPanel{Margin=new Thickness(0,10,0,0)};editor.Children.Add(fields);
        fields.Children.Add(type);fields.Children.Add(bandEnabled);AddField(fields,"频率",frequency,"Hz");AddField(fields,"增益",gain,"dB");AddField(fields,"Q",q,"");
        editor.Children.Add(new TextBlock{Text="拖动节点：左右调频率，上下调增益；滚轮调 Q。黄色为总频响，灰色为麦克风输入频谱（−90～0 dBFS）。",FontSize=11,Foreground=new SolidColorBrush(Color.FromRgb(146,158,178)),Margin=new Thickness(0,12,0,0),TextWrapping=TextWrapping.Wrap});
        var footer=new StackPanel();Grid.SetRow(footer,3);body.Children.Add(footer);footer.Children.Add(status);
        footer.Children.Add(new TextBlock{Text="只处理所选硬件输入，伴奏保持原样。关闭本窗口后 EQ 继续工作；退出 Donner 面板后恢复原声。每次启动默认旁通。",FontSize=11,Foreground=new SolidColorBrush(Color.FromRgb(146,158,178)),Margin=new Thickness(0,6,0,0),TextWrapping=TextWrapping.Wrap});
        frequency.Tag="Frequency";gain.Tag="Gain";q.Tag="Q";preamp.Tag="Preamp";
        input.SelectionChanged+=(_,_)=>{if(!updating&&input.SelectedIndex>=0)SelectInput(input.SelectedIndex);};
        enabled.Click+=(_,_)=>Apply(Current with{Enabled=enabled.IsChecked==true});
        bandEnabled.Click+=(_,_)=>ChangeBand(Current.Bands[selected] with{Enabled=bandEnabled.IsChecked==true});
        type.SelectionChanged+=(_,_)=>{if(!updating&&type.SelectedIndex>=0)ChangeBand(Current.Bands[selected] with{Type=(EqFilter)type.SelectedIndex});};
        foreach(var box in new[]{frequency,gain,q,preamp})
        {
            box.LostKeyboardFocus+=(_,_)=>Commit();
            box.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){Commit();box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));e.Handled=true;}};
        }
        reset.Click+=(_,_)=>Apply(MicrophoneEqSettings.Flat() with{Enabled=Current.Enabled});
        Graph.SelectionChanged=SelectBand;Graph.BandChanged=(band,value)=>{selected=band;ChangeBand(value);};
        timer.Tick+=(_,_)=>
        {
            rack.Maintain();if(!IsVisible||WindowState==WindowState.Minimized)return;
            Graph.SampleRate=rack.SampleRate;rack.Spectrum(index,samples);Graph.UpdateSpectrum(samples,rack.HasAudio);
            status.Text=editError??(rack.Status+(rack.Running?$" · {rack.SampleRate/1000d:0.###} kHz":""));
        };
        Loaded+=(_,_)=>timer.Start();Closed+=(_,_)=>timer.Stop();SelectInput(0);
    }
    private MicrophoneEqSettings Current=>rack.Get(index);
    private static TextBox Number(double width)=>new(){Width=width,Padding=new Thickness(5,4,5,4),FontSize=13,Foreground=Brushes.Black,Background=new SolidColorBrush(Color.FromRgb(232,236,242))};
    private static void AddField(WrapPanel row,string name,TextBox value,string unit)
    {
        var group=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,12,5)};row.Children.Add(group);
        group.Children.Add(new TextBlock{Text=name,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,5,0)});group.Children.Add(value);group.Children.Add(new TextBlock{Text=unit,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,0,0)});
    }
    internal void SelectInput(int value)
    {
        index=Math.Clamp(value,0,2);updating=true;input.SelectedIndex=index;updating=false;RefreshFields();
    }
    private void SelectBand(int value){selected=value;RefreshFields();}
    private void ChangeBand(EqBand value)
    {
        var config=Current;config.Bands[selected]=value;Apply(config);
    }
    private void Apply(MicrophoneEqSettings value)
    {
        try{rack.Set(index,value);editError=null;status.Text=rack.Status;}
        catch(Exception e) when(e is IOException or InvalidDataException){status.Text=editError=e.Message;}
        RefreshFields();
    }
    private void Commit()
    {
        if(updating)return;
        if(!double.TryParse(frequency.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double f)||!double.TryParse(gain.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double g)||!double.TryParse(q.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double quality)||!double.TryParse(preamp.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double output))
        {status.Text=editError="请输入有效数字。";RefreshFields();return;}
        var config=Current;config.Bands[selected]=config.Bands[selected] with{Frequency=f,Gain=g,Q=quality};Apply(config with{Preamp=output});
    }
    private void RefreshFields()
    {
        updating=true;try
        {
            var config=Current;var band=config.Bands[selected];enabled.IsChecked=config.Enabled;bandEnabled.IsChecked=band.Enabled;type.SelectedIndex=(int)band.Type;
            frequency.Text=band.Frequency.ToString("0.##",CultureInfo.CurrentCulture);gain.Text=band.Gain.ToString("0.0",CultureInfo.CurrentCulture);q.Text=band.Q.ToString("0.##",CultureInfo.CurrentCulture);preamp.Text=config.Preamp.ToString("0.0",CultureInfo.CurrentCulture);
            gain.IsEnabled=band.Type is EqFilter.Bell or EqFilter.LowShelf or EqFilter.HighShelf;
            Graph.Settings=config;Graph.Selected=selected;Graph.SampleRate=rack.SampleRate;Graph.InvalidateVisual();
            for(int i=0;i<6;i++)((Button)bandButtons.Children[i]).Opacity=i==selected?1:.55;
        }
        finally{updating=false;}
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Donner.Core;

namespace Donner.Panel;

internal sealed class EqEditor:Border,IDisposable
{
    private readonly IEqEditorSource source;
    private readonly ComboBox channel=new(){Width=65,MinWidth=65,ItemsSource=new[]{"左","右"},SelectedIndex=0,Foreground=Brushes.Black,Margin=new Thickness(0,0,10,0)};
    private readonly CheckBox linked=new(){Content="左右联动",IsChecked=true,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,14,0)};
    internal readonly EqGraph Graph=new();
    private readonly ComboBox input=new(){Width=145,Foreground=Brushes.Black,ItemsSource=new[]{"硬件输入 1","硬件输入 2","硬件输入 3"},Margin=new Thickness(0,0,18,0)};
    private readonly CheckBox enabled=new(){Content="启用麦克风 EQ",Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,FontSize=14};
    private readonly CheckBox bandEnabled=new(){Content="启用频段",Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,12,0)};
    private readonly ComboBox type=new(){Width=130,Foreground=Brushes.Black,ItemsSource=new[]{"Bell · 钟形","低切 · High Pass","高切 · Low Pass","低架 · Low Shelf","高架 · High Shelf","陷波 · Notch","带通 · Band Pass"},Margin=new Thickness(6,0,12,0)};
    private readonly TextBox frequency=Number(74),gain=Number(62),q=Number(62),preamp=Number(62);
    private readonly TextBlock status=new(){FontSize=12,Foreground=new SolidColorBrush(Color.FromRgb(165,181,197)),Margin=new Thickness(0,8,0,0),TextWrapping=TextWrapping.Wrap};
    private readonly StackPanel bandButtons=new(){Orientation=Orientation.Horizontal};
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(80)};
    private readonly float[] samples=new float[2048];
    private bool updating;
    private string? editError,readError;
    private int index,selected;
    internal EqEditor(IEqEditorSource source)
    {
        this.source=source;if(source.IsOutput)timer.Interval=TimeSpan.FromMilliseconds(500);MinWidth=0;MinHeight=480;Margin=new Thickness(0,16,0,0);Padding=new Thickness(14);CornerRadius=new CornerRadius(8);
        Background=new SolidColorBrush(Color.FromRgb(22,24,31));TextElement.SetForeground(this,Brushes.White);TextElement.SetFontFamily(this,new FontFamily("Microsoft YaHei UI"));TextElement.SetFontSize(this,13);
        input.ItemsSource=source.Targets;
        enabled.Content=source.IsOutput?"启用输出 EQ":"启用麦克风 EQ";
        Graph.MinimumQ=source.IsOutput?1:.1;Graph.MaximumQ=source.IsOutput?100:20;Graph.MaximumGain=source.IsOutput?12:24;
        var body=new Grid();Child=body;
        foreach(var height in new[]{GridLength.Auto,new GridLength(260),GridLength.Auto,GridLength.Auto})body.RowDefinitions.Add(new(){Height=height});
        var header=new DockPanel{Margin=new Thickness(0,0,0,12)};body.Children.Add(header);
        var reset=new Button{Content="恢复平直",FontSize=12,Padding=new Thickness(10,6,10,6),Margin=new Thickness(12,0,0,0)};DockPanel.SetDock(reset,Dock.Right);header.Children.Add(reset);
        var headerLeft=new WrapPanel();header.Children.Add(headerLeft);headerLeft.Children.Add(input);if(source.IsOutput){headerLeft.Children.Add(channel);headerLeft.Children.Add(linked);}headerLeft.Children.Add(enabled);
        if(!source.IsOutput){headerLeft.Children.Add(new TextBlock{Text="输出增益",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(20,0,8,0)});headerLeft.Children.Add(preamp);headerLeft.Children.Add(new TextBlock{Text="dB",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5,0,0,0)});}
        Grid.SetRow(Graph,1);body.Children.Add(Graph);
        var editor=new StackPanel{Margin=new Thickness(0,10,0,0)};Grid.SetRow(editor,2);body.Children.Add(editor);editor.Children.Add(bandButtons);
        for(int i=0;i<6;i++)
        {
            int band=i;var button=new Button{Content=$"频段 {i+1}",Tag=i,FontSize=12,Padding=new Thickness(15,6,15,6),Margin=new Thickness(0,0,8,0),Background=new SolidColorBrush(EqGraph.Colors[i]),Foreground=Brushes.Black};bandButtons.Children.Add(button);button.Click+=(_,_)=>SelectBand(band);
        }
        var fields=new WrapPanel{Margin=new Thickness(0,10,0,0)};editor.Children.Add(fields);
        fields.Children.Add(type);fields.Children.Add(bandEnabled);AddField(fields,"频率",frequency,"Hz");AddField(fields,"增益",gain,"dB");AddField(fields,"Q",q,"");
        editor.Children.Add(new TextBlock{Text=source.IsOutput?"拖动节点调节频率、增益；滚轮调 Q。黄色为总频响。输出增益范围 ±12 dB，Q 范围 1–100。":"拖动节点：左右调频率，上下调增益；滚轮调 Q。黄色为总频响，灰色为麦克风输入频谱（−90～0 dBFS）。",FontSize=11,Foreground=new SolidColorBrush(Color.FromRgb(146,158,178)),Margin=new Thickness(0,12,0,0),TextWrapping=TextWrapping.Wrap});
        var footer=new StackPanel();Grid.SetRow(footer,3);body.Children.Add(footer);footer.Children.Add(status);
        footer.Children.Add(new TextBlock{Text=source.IsOutput?"处理送往所选输出总线的混合声音。左右联动仅将本次修改同步到两侧。":"只处理所选硬件输入，伴奏保持原样。切换页面后继续处理；退出 Donner 面板后恢复原声。每次启动默认旁通。",FontSize=11,Foreground=new SolidColorBrush(Color.FromRgb(146,158,178)),Margin=new Thickness(0,6,0,0),TextWrapping=TextWrapping.Wrap});
        frequency.Tag="Frequency";gain.Tag="Gain";q.Tag="Q";preamp.Tag="Preamp";
        input.SelectionChanged+=(_,_)=>{if(!updating&&input.SelectedIndex>=0)SelectInput(input.SelectedIndex);};
        channel.SelectionChanged+=(_,_)=>{if(!updating)RefreshFields();};
        enabled.Click+=(_,_)=>TryEdit(()=>Current with{Enabled=enabled.IsChecked==true});
        bandEnabled.Click+=(_,_)=>TryEdit(()=>{var c=Current;c.Bands[selected]=c.Bands[selected] with{Enabled=bandEnabled.IsChecked==true};return c;});
        type.SelectionChanged+=(_,_)=>{if(!updating&&type.SelectedIndex>=0)TryEdit(()=>{var c=Current;c.Bands[selected]=c.Bands[selected] with{Type=(EqFilter)type.SelectedIndex};return c;});};
        foreach(var box in new[]{frequency,gain,q,preamp})
        {
            box.LostKeyboardFocus+=(_,_)=>Commit();
            box.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){Commit();box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));e.Handled=true;}};
        }
        reset.Click+=(_,_)=>{try{source.Reset(index,Math.Max(0,channel.SelectedIndex),linked.IsChecked==true);editError=null;}catch(Exception e) when(e is IOException or InvalidDataException){editError=e.Message;}RefreshFields();};
        Graph.SelectionChanged=SelectBand;Graph.BandChanged=(band,value)=>{selected=band;ChangeBand(value);};
        timer.Tick+=(_,_)=>
        {
            if(!IsVisible)return;source.Maintain();
            Graph.SampleRate=source.SampleRate;source.Spectrum(index,samples);Graph.UpdateSpectrum(samples,source.HasAudio);
            if(source.IsOutput&&!Graph.IsMouseCaptureWithin&&!type.IsDropDownOpen&&!input.IsDropDownOpen&&!channel.IsDropDownOpen&&!new[]{frequency,gain,q,preamp}.Any(x=>x.IsKeyboardFocusWithin))RefreshFields();
            status.Text=editError??readError??source.Status;
        };
        Loaded+=(_,_)=>timer.Start();Unloaded+=(_,_)=>timer.Stop();IsVisibleChanged+=(_,_)=>{if(IsVisible)timer.Start();else timer.Stop();};SelectInput(0);
    }
    private MicrophoneEqSettings Current=>source.Read(index,Math.Max(0,channel.SelectedIndex));
    private static TextBox Number(double width)=>new(){Width=width,Padding=new Thickness(5,4,5,4),FontSize=13,Foreground=Brushes.Black,Background=new SolidColorBrush(Color.FromRgb(232,236,242))};
    private static void AddField(WrapPanel row,string name,TextBox value,string unit)
    {
        var group=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,12,5)};row.Children.Add(group);
        group.Children.Add(new TextBlock{Text=name,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,5,0)});group.Children.Add(value);group.Children.Add(new TextBlock{Text=unit,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,0,0)});
    }
    internal void SelectInput(int value)
    {
        index=Math.Clamp(value,0,source.Targets.Length-1);updating=true;input.SelectedIndex=index;updating=false;RefreshFields();
    }
    private void SelectBand(int value){selected=value;RefreshFields();}
    private void ChangeBand(EqBand value)
    {
        TryEdit(()=>{var config=Current;config.Bands[selected]=value;return config;});
    }
    private void TryEdit(Func<MicrophoneEqSettings> edit)
    {
        try{Apply(edit());}catch(Exception e) when(e is IOException or InvalidDataException){status.Text=editError=e.Message;}
    }
    private void Apply(MicrophoneEqSettings value)
    {
        try{source.Apply(index,Math.Max(0,channel.SelectedIndex),linked.IsChecked==true,value);editError=null;status.Text=source.Status;}
        catch(Exception e) when(e is IOException or InvalidDataException){status.Text=editError=e.Message;}
        RefreshFields();
    }
    private void Commit()
    {
        if(updating)return;
        if(!double.TryParse(frequency.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double f)||!double.TryParse(gain.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double g)||!double.TryParse(q.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double quality)||!double.TryParse(preamp.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double output))
        {status.Text=editError="请输入有效数字。";RefreshFields();return;}
        TryEdit(()=>{var config=Current;config.Bands[selected]=config.Bands[selected] with{Frequency=f,Gain=g,Q=quality};return config with{Preamp=output};});
    }
    public void Dispose()=>timer.Stop();
    private void RefreshFields()
    {
        updating=true;try
        {
            var config=Current;readError=null;var band=config.Bands[selected];enabled.IsChecked=config.Enabled;bandEnabled.IsChecked=band.Enabled;type.SelectedIndex=(int)band.Type;
            frequency.Text=band.Frequency.ToString("0.##",CultureInfo.CurrentCulture);gain.Text=band.Gain.ToString("0.0",CultureInfo.CurrentCulture);q.Text=band.Q.ToString("0.##",CultureInfo.CurrentCulture);preamp.Text=config.Preamp.ToString("0.0",CultureInfo.CurrentCulture);
            gain.IsEnabled=band.Type is EqFilter.Bell or EqFilter.LowShelf or EqFilter.HighShelf;
            Graph.Settings=config;Graph.Selected=selected;Graph.SampleRate=source.SampleRate;Graph.InvalidateVisual();
            for(int i=0;i<6;i++)((Button)bandButtons.Children[i]).Opacity=i==selected?1:.55;
        }
        catch(Exception e) when(e is IOException or InvalidDataException){status.Text=readError=e.Message;}
        finally{updating=false;}
    }
}

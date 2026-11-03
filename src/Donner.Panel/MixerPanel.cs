using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Donner.Core;

namespace Donner.Panel;

internal sealed class MixerPanel:Border,IDisposable
{
    private readonly IMixerClient client;
    private readonly IDisposable? settings;
    private readonly TextBlock status=new(){FontSize=12,VerticalAlignment=VerticalAlignment.Center};
    private readonly StackPanel controls=new();
    private readonly List<Action> readers=[];
    private readonly List<Action> meters=[];
    private readonly List<(ComboBox Combo,bool Input,int Index)> devices=[];
    private readonly Dictionary<string,float> pending=new(StringComparer.Ordinal);
    private readonly Dictionary<string,(float Value,long Deadline)> awaiting=new(StringComparer.Ordinal);
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(80)};
    private long nextRead,nextConnection;
    private bool updating,wasConnected,disposed;
    private readonly ComboBox eqBus=new(){MinWidth=100,Width=130};
    private readonly ComboBox eqChannel=new(){MinWidth=60,Width=70,ItemsSource=new[]{"左","右"},SelectedIndex=0};
    private readonly CheckBox eqLinked=new(){Content="左右联动",IsChecked=true,VerticalAlignment=VerticalAlignment.Center};
    private readonly CheckBox eqEnabled=new(){Content="启用 EQ",VerticalAlignment=VerticalAlignment.Center};
    private readonly StackPanel eqRows=new();
    private readonly List<Action> eqReaders=[];
    internal IMixerClient Client=>client;
    internal int PendingCount=>pending.Count;
    internal bool ControlsEnabled=>controls.IsEnabled;
    internal static string BusName(int i)=>i switch{0=>"A1 监听",1=>"A2 输出",2=>"A3 输出",3=>"B1 直播",_=>"B2 直播"};
    public MixerPanel(IMixerClient client,bool automaticPolling=true,FrameworkElement? sampleRates=null,Action? editNames=null)
    {
        this.client=client;Background=new SolidColorBrush(Color.FromRgb(248,250,251));BorderBrush=new SolidColorBrush(Color.FromRgb(227,233,237));
        BorderThickness=new Thickness(1);CornerRadius=new CornerRadius(8);Padding=new Thickness(14);Margin=new Thickness(0);
        var body=new StackPanel();Child=body;var heading=new DockPanel();body.Children.Add(heading);
        var open=new Button{Content="打开原界面",FontSize=12,Padding=new Thickness(10,6,10,6),Margin=new Thickness(10,0,0,0)};DockPanel.SetDock(open,Dock.Right);heading.Children.Add(open);
        open.Click+=(_,_)=>Attempt(()=>client.Start(true));
        var background=new Button{Content="后台运行",FontSize=12,Padding=new Thickness(10,6,10,6),Margin=new Thickness(10,0,0,0)};
        DockPanel.SetDock(background,Dock.Right);heading.Children.Insert(0,background);background.Click+=(_,_)=>Attempt(()=>client.Start());
        if(editNames is not null)
        {
            var names=new Button{Content="设备名称",FontSize=12,Padding=new Thickness(10,6,10,6),Margin=new Thickness(10,0,0,0)};
            DockPanel.SetDock(names,Dock.Right);heading.Children.Insert(0,names);names.Click+=(_,_)=>editNames();
        }
        var titleAndRates=new Grid();titleAndRates.ColumnDefinitions.Add(new(){Width=GridLength.Auto});titleAndRates.ColumnDefinitions.Add(new());
        titleAndRates.Children.Add(new TextBlock{Text="调音台",FontSize=18,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});
        if(sampleRates is MixerSampleRates rates){Grid.SetColumn(rates.HeaderView,1);titleAndRates.Children.Add(rates.HeaderView);}
        heading.Children.Add(titleAndRates);
        var connection=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(10,0,0,0),VerticalAlignment=VerticalAlignment.Center};
        var refresh=new Button{Content="刷新设备",FontSize=12,Padding=new Thickness(10,5,10,5),Margin=new Thickness(0,0,12,0)};connection.Children.Add(refresh);connection.Children.Add(status);
        status.MaxWidth=330;status.TextTrimming=TextTrimming.CharacterEllipsis;status.SetBinding(ToolTipProperty,new System.Windows.Data.Binding("Text"){Source=status});
        refresh.Click+=(_,_)=>Attempt(()=>{if(client.Refresh())RefreshDevices();Poll(true);});
        if(sampleRates is not null){body.Children.Add(sampleRates);settings=sampleRates as IDisposable;}
        body.Children.Add(controls);
        var tabs=new TabControl{Background=Brushes.Transparent,BorderThickness=new Thickness(0),Margin=new Thickness(0,8,0,0),Tag=connection,Template=(ControlTemplate)FindResource("MixerTabsTemplate")};controls.Children.Add(tabs);
        var inputs=new System.Windows.Controls.Primitives.UniformGrid{Rows=1};var outputs=new System.Windows.Controls.Primitives.UniformGrid{Rows=1};
        tabs.Items.Add(new TabItem{Header="输入混音",Content=inputs,FontSize=13,Padding=new Thickness(12,6,12,6)});
        var outputBody=new StackPanel();outputBody.Children.Add(outputs);
        tabs.Items.Add(new TabItem{Header="输出与 EQ",Content=outputBody,FontSize=13,Padding=new Thickness(12,6,12,6)});
        for(int i=0;i<5;i++){BuildStrip(inputs,true,i);BuildStrip(outputs,false,i);}
        BuildEq(outputBody);
        body.Children.Add(new TextBlock{Text="电脑或伴奏软件选择“电脑声音 → 调音台 (VAIO)”或“伴奏 / 第二路声音 → 调音台 (AUX)”；直播软件选择“直播混音 1 (B1)”或“直播混音 2 (B2)”。未更名时对应 Voicemeeter Input / AUX Input、Out B1 / B2。",FontSize=12,Foreground=new SolidColorBrush(Color.FromRgb(91,114,125)),Margin=new Thickness(0,10,0,0)});
        timer.Tick+=(_,_)=>Poll();if(automaticPolling)timer.Start();Poll(true);
    }
    private void BuildStrip(System.Windows.Controls.Panel parent,bool input,int index)
    {
        var box=new StackPanel{Margin=new Thickness(4,8,4,0)};parent.Children.Add(box);
        var prefix=$"{(input?"Strip":"Bus")}[{index}]";
        box.Children.Add(new TextBlock{Text=input?index<3?$"硬件输入 {index+1}":index==3?"电脑声音 VAIO":"电脑声音 AUX":BusName(index),FontWeight=FontWeights.SemiBold,FontSize=13,HorizontalAlignment=HorizontalAlignment.Center});
        if(index<3)
        {
            var combo=new ComboBox{MinWidth=0,Height=29,FontSize=11,Margin=new Thickness(0,6,0,4),ToolTip="选择音频设备；更改会重新连接音频引擎。"};box.Children.Add(combo);devices.Add((combo,input,index));
            combo.SelectionChanged+=(_,_)=>{if(!updating&&combo.SelectedItem is MixerDevice choice)Attempt(()=>client.SetDevice(input,index,choice));};
        }
        else box.Children.Add(new TextBlock{Text=input?"虚拟播放输入":"虚拟录音输出",FontSize=11,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,10,0,8)});
        var faderRow=new Grid{Height=94};faderRow.ColumnDefinitions.Add(new());faderRow.ColumnDefinitions.Add(new(){Width=new GridLength(14)});box.Children.Add(faderRow);
        var slider=new Slider{Orientation=Orientation.Vertical,IsMoveToPointEnabled=true,Minimum=-60,Maximum=12,TickFrequency=.1,IsSnapToTickEnabled=true,SmallChange=.1,LargeChange=1,Height=90,Width=36,Style=(Style)FindResource("ChannelFader")};faderRow.Children.Add(slider);
        var meter=new ChannelMeter{Width=10,Height=88,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(meter,1);faderRow.Children.Add(meter);
        meters.Add(()=>meter.Peak=client.Level(input,index));
        var gainRow=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,4,0,4)};box.Children.Add(gainRow);
        var value=new TextBox{Text="0.0",Tag=prefix+".Gain",Width=58,FontSize=12,TextAlignment=TextAlignment.Center,Padding=new Thickness(3,2,3,2),ToolTip="输入 -60 至 +12 dB，支持一位小数；按 Enter 或离开输入框应用。输入 0 归零。"};gainRow.Children.Add(value);
        gainRow.Children.Add(new TextBlock{Text="dB",FontSize=12,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,0,0)});
        string GainText()=>slider.Value.ToString("0.0",CultureInfo.CurrentCulture);
        bool enteringGain=false;
        void CommitGain()
        {
            if(updating)return;
            if(double.TryParse(value.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var gain)&&double.IsFinite(gain)&&gain>=-60&&gain<=12)
            {
                gain=Math.Round(gain,1,MidpointRounding.AwayFromZero);
                enteringGain=true;try{slider.Value=gain;}finally{enteringGain=false;}
                Queue(prefix+".Gain",(float)gain);value.Text=GainText();
            }
            else{status.Text="分贝请输入 -60 至 +12 的数字。";value.Text=GainText();}
        }
        value.LostKeyboardFocus+=(_,_)=>CommitGain();
        value.KeyDown+=(_,e)=>
        {
            if(e.Key==System.Windows.Input.Key.Enter){CommitGain();value.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));e.Handled=true;}
            else if(e.Key==System.Windows.Input.Key.Escape){value.Text=GainText();value.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));e.Handled=true;}
        };
        slider.ToolTip="-60 至 +12 dB；靠近 0 dB 自动吸附。双击归零；方向键按 0.1 dB 微调。";
        void ResetGain(){slider.Focus();slider.Value=0;if(!updating)Queue(prefix+".Gain",0);}
        slider.PreviewMouseDoubleClick+=(_,e)=>{if(e.ChangedButton==System.Windows.Input.MouseButton.Left){ResetGain();e.Handled=true;}};
        slider.ValueChanged+=(_,_)=>
        {
            if(!updating&&!enteringGain&&(slider.IsMouseCaptureWithin||slider.IsMouseOver&&System.Windows.Input.Mouse.LeftButton==System.Windows.Input.MouseButtonState.Pressed))
            {var snapped=MixerFaderValue.Pointer(slider.Value);if(snapped!=slider.Value)slider.Value=snapped;}
            if(!value.IsKeyboardFocusWithin)value.Text=GainText();if(!updating)Queue(prefix+".Gain",(float)slider.Value);
        };
        readers.Add(()=>{if(!slider.IsMouseCaptureWithin&&!value.IsKeyboardFocusWithin&&!pending.ContainsKey(prefix+".Gain")){slider.Value=ReadValue(prefix+".Gain");value.Text=GainText();}});
        var switches=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Center};box.Children.Add(switches);
        AddCheck(switches,"静音",prefix+".Mute");AddCheck(switches,"单声道",prefix+".Mono");
        if(input)
        {
            var routes=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,7,0,5)};box.Children.Add(routes);
            foreach(var route in MixerRules.Routes)AddCheck(routes,route,prefix+"."+route);
            if(index<3){AddNumber(box,"压缩",prefix+".Comp",0,10,.1f);AddNumber(box,"噪声门",prefix+".Gate",0,10,.1f);}
            else for(int band=1;band<=3;band++)AddNumber(box,new[]{"低频","中频","高频"}[band-1],prefix+$".EQGain{band}",-12,12,.1f);
        }
    }
    private void AddCheck(System.Windows.Controls.Panel parent,string caption,string key)
    {
        var check=new CheckBox{Content=caption,FontSize=11,Margin=new Thickness(0,2,7,2)};parent.Children.Add(check);
        check.Click+=(_,_)=>{if(!updating)Queue(key,check.IsChecked==true?1:0);};
        readers.Add(()=>{if(!pending.ContainsKey(key))check.IsChecked=Math.Abs(ReadValue(key)-1)<.001f;});
    }
    private void AddNumber(System.Windows.Controls.Panel parent,string caption,string key,float minimum,float maximum,float step)
    {
        var row=new DockPanel{Margin=new Thickness(0,4,0,0)};parent.Children.Add(row);
        var number=new TextBox{Width=47,Padding=new Thickness(3,2,3,2),FontSize=12};DockPanel.SetDock(number,Dock.Right);row.Children.Add(number);
        row.Children.Add(new TextBlock{Text=caption,FontSize=12,VerticalAlignment=VerticalAlignment.Center});
        void Commit()
        {
            if(updating)return;
            if(float.TryParse(number.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var v)&&float.IsFinite(v)&&v>=minimum&&v<=maximum)Queue(key,MathF.Round(v/step)*step);
            else{status.Text=$"{caption}请输入 {minimum}–{maximum}。";nextRead=0;}
        }
        number.LostKeyboardFocus+=(_,_)=>Commit();number.KeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Enter){Commit();e.Handled=true;}};
        readers.Add(()=>{if(!number.IsKeyboardFocusWithin&&!pending.ContainsKey(key))number.Text=ReadValue(key).ToString("0.0",CultureInfo.CurrentCulture);});
    }
    private void BuildEq(System.Windows.Controls.Panel parent)
    {
        var bar=new WrapPanel{Margin=new Thickness(0,14,0,8)};parent.Children.Add(bar);
        bar.Children.Add(new TextBlock{Text="六段参数 EQ",FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,12,0),VerticalAlignment=VerticalAlignment.Center});
        eqBus.ItemsSource=Enumerable.Range(0,5).Select(BusName).ToArray();eqBus.SelectedIndex=0;
        bar.Children.Add(eqBus);bar.Children.Add(eqChannel);bar.Children.Add(eqLinked);eqEnabled.Margin=new Thickness(12,0,0,0);bar.Children.Add(eqEnabled);
        parent.Children.Add(eqRows);
        eqBus.SelectionChanged+=(_,_)=>RebuildEq();eqChannel.SelectionChanged+=(_,_)=>RebuildEq();
        eqEnabled.Click+=(_,_)=>{if(!updating)Queue($"Bus[{eqBus.SelectedIndex}].EQ.on",eqEnabled.IsChecked==true?1:0);};
        RebuildEq();
    }
    private void RebuildEq()
    {
        eqReaders.Clear();eqRows.Children.Clear();int bus=eqBus.SelectedIndex,channel=eqChannel.SelectedIndex;
        if(bus<0||channel<0)return;
        for(int cell=0;cell<6;cell++)
        {
            int c=cell;var row=new WrapPanel{Margin=new Thickness(0,3,0,3)};eqRows.Children.Add(row);
            var on=new CheckBox{Content=$"频段 {cell+1}",FontSize=12,Width=75,VerticalAlignment=VerticalAlignment.Center};row.Children.Add(on);
            string Key(string f)=>MixerRules.EqKeys(bus,c,f,false,channel)[0];
            void Write(string field,float v){if(updating)return;foreach(var key in MixerRules.EqKeys(bus,c,field,eqLinked.IsChecked==true,channel))Queue(key,v);}
            on.Click+=(_,_)=>Write("on",on.IsChecked==true?1:0);
            eqReaders.Add(()=>{if(!pending.ContainsKey(Key("on")))on.IsChecked=ReadValue(Key("on"))>.5f;});
            foreach(var spec in new[]{(Field:"f",Label:"Hz",Min:20f,Max:20000f),(Field:"gain",Label:"dB",Min:-12f,Max:12f),(Field:"q",Label:"Q",Min:1f,Max:100f)})
            {
                var n=new TextBox{Width=spec.Field=="f"?66:48,Padding=new Thickness(4,3,4,3),FontSize=12,Margin=new Thickness(0,0,4,0)};row.Children.Add(n);
                row.Children.Add(new TextBlock{Text=spec.Label,FontSize=12,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,0)});
                void Commit(){if(updating)return;if(float.TryParse(n.Text,out var v)&&float.IsFinite(v)&&v>=spec.Min&&v<=spec.Max)Write(spec.Field,v);else status.Text=$"{spec.Label}请输入 {spec.Min}–{spec.Max}。";}
                n.LostKeyboardFocus+=(_,_)=>Commit();n.KeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Enter){Commit();e.Handled=true;}};
                eqReaders.Add(()=>{if(!n.IsKeyboardFocusWithin&&!pending.ContainsKey(Key(spec.Field)))n.Text=ReadValue(Key(spec.Field)).ToString("0.##",CultureInfo.CurrentCulture);});
            }
        }
        nextRead=0;
    }
    internal void Queue(string key,float value)
    {try{if(!client.Connected)throw new IOException("Banana 未连接，未应用更改。");MixerRules.Validate(key,value);pending[key]=value;}catch(Exception e) when(e is InvalidDataException or IOException){status.Text=e.Message;}}
    private float ReadValue(string key)
    {
        var actual=client.Get(key);
        if(awaiting.TryGetValue(key,out var sent))
        {
            if(Math.Abs(actual-sent.Value)<.001f)awaiting.Remove(key);
            else if(Environment.TickCount64<sent.Deadline)return sent.Value;
            else{awaiting.Remove(key);status.Text="部分参数未获 Banana 确认，已恢复显示实际值。";}
        }
        return actual;
    }
    internal void RefreshDevices()
    {
        var input=client.Devices(true);var output=client.Devices(false);
        updating=true;try{foreach(var d in devices){var source=(d.Input?input:output).Where(v=>v.Type!=5||!d.Input&&d.Index==0).ToArray();d.Combo.ItemsSource=source;var name=client.DeviceName(d.Input,d.Index);d.Combo.SelectedItem=source.FirstOrDefault(v=>v.Name==name&&v.Type==3)??source.FirstOrDefault(v=>v.Name==name);}}finally{updating=false;}
    }
    internal void Poll(bool force=false)
    {
        if(disposed)return;
        Attempt(()=>
        {
            long now=Environment.TickCount64;
            if(force||now>=nextConnection){client.Refresh();nextConnection=now+1000;status.Text=client.Status;}
            controls.IsEnabled=client.Connected;
            if(!client.Connected){pending.Clear();awaiting.Clear();wasConnected=false;return;}
            if(!wasConnected){RefreshDevices();wasConnected=true;force=true;}
            if(pending.Count>0)
            {
                var writes=pending.ToArray();pending.Clear();foreach(var pair in writes){client.Set(pair.Key,pair.Value);awaiting[pair.Key]=(pair.Value,now+1500);}nextRead=0;
            }
            if(!IsVisible&&!force)return;
            foreach(var meter in meters)meter();
            if(force||now>=nextRead)
            {
                updating=true;
                try{foreach(var read in readers)read();foreach(var read in eqReaders)read();eqEnabled.IsChecked=ReadValue($"Bus[{eqBus.SelectedIndex}].EQ.on")>.5f;
                    foreach(var d in devices)if(!d.Combo.IsDropDownOpen&&!d.Combo.IsKeyboardFocusWithin){var name=client.DeviceName(d.Input,d.Index);if(d.Combo.SelectedItem is not MixerDevice selected||selected.Name!=name)d.Combo.SelectedItem=d.Combo.Items.Cast<MixerDevice>().FirstOrDefault(x=>x.Name==name);}}
                finally{updating=false;}
                nextRead=now+500;
            }
        });
    }
    private void Attempt(Action action)
    {try{action();}catch(Exception e) when(e is InvalidDataException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception){status.Text=e.Message;pending.Clear();awaiting.Clear();controls.IsEnabled=false;wasConnected=false;}}
    public void Dispose(){if(disposed)return;disposed=true;timer.Stop();settings?.Dispose();pending.Clear();awaiting.Clear();client.Dispose();}
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Donner.Core;

namespace Donner.Panel;

internal sealed class MixerSampleRates:Border,IDisposable
{
    private readonly IMixerClient client;
    private readonly Func<DeviceSnapshot> snapshot;
    private readonly Func<uint,Task> setHardwareRate;
    private readonly TextBlock summary=new(){FontSize=12,TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock status=new(){FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,0)};
    private readonly TextBlock hardwareValue=new(){FontSize=12,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock bananaValue=new(){FontSize=12,VerticalAlignment=VerticalAlignment.Center};
    private readonly ComboBox banana=RateCombo(AudioEndpointFormats.Rates);
    private readonly ComboBox hardware=RateCombo([]);
    private readonly StackPanel editor=new();
    private readonly StackPanel windowsRows=new();
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(2)};
    private readonly Dictionary<string,TextBlock> endpointValues=[];
    private AudioEndpointFormats.Endpoint[] endpoints=[];
    private string endpointIds="";
    private bool busy,disposed;

    internal MixerSampleRates(IMixerClient client,Func<DeviceSnapshot> snapshot,Func<uint,Task> setHardwareRate,bool automaticPolling=true)
    {
        this.client=client;this.snapshot=snapshot;this.setHardwareRate=setHardwareRate;
        BorderBrush=new SolidColorBrush(Color.FromRgb(227,233,237));BorderThickness=new Thickness(1);CornerRadius=new CornerRadius(5);Padding=new Thickness(9);Margin=new Thickness(0,7,0,8);
        var body=new StackPanel();Child=body;body.Children.Add(summary);
        var expander=new Expander{Header="采样率设置",FontSize=12,Margin=new Thickness(0,5,0,0),Content=editor};body.Children.Add(expander);
        editor.Children.Add(new TextBlock{Text="Windows 默认格式、Banana 首选值与硬件实际速率彼此独立。应用可能短暂中断音频；请逐项设置并检查回读。",FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,7)});
        editor.Children.Add(windowsRows);
        var bananaRow=Row("Banana 首选",banana,bananaValue);editor.Children.Add(bananaRow);
        AddButton(bananaRow,"保存首选",()=>ApplyBananaAsync(SelectedRate(banana)));
        AddButton(bananaRow,"重启引擎",RestartBananaAsync);
        var hardwareRow=Row("Donner 硬件",hardware,hardwareValue);editor.Children.Add(hardwareRow);
        AddButton(hardwareRow,"应用硬件",async()=>
        {
            var rate=SelectedRate(hardware);var before=snapshot();
            if(!before.Connected||!before.SampleRates.Contains(rate))throw new IOException("硬件已断开或不支持所选采样率。");
            await setHardwareRate(rate);
            if(snapshot().SampleRate!=rate)throw new IOException("驱动尚未确认所选硬件采样率，请刷新后检查。");
            return $"Donner 硬件已确认 {Format(rate)}。";
        });
        body.Children.Add(status);
        timer.Tick+=(_,_)=>Refresh();if(automaticPolling)timer.Start();Refresh();
    }
    private static ComboBox RateCombo(IEnumerable<uint> rates)
    {
        var text=new FrameworkElementFactory(typeof(TextBlock));text.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding{StringFormat="{0:N0} Hz"});
        return new(){ItemsSource=rates.ToArray(),ItemTemplate=new DataTemplate{VisualTree=text},Width=105,FontSize=12,Margin=new Thickness(7,0,7,0)};
    }
    private static uint SelectedRate(ComboBox combo)=>combo.SelectedItem is uint rate?rate:throw new InvalidDataException("请选择采样率。");
    private static string Format(uint? rate)=>rate is >0?(rate.Value/1000d).ToString("0.###",CultureInfo.InvariantCulture)+" kHz":"未知";
    private static WrapPanel Row(string title,ComboBox combo,TextBlock current)
    {
        var row=new WrapPanel{Margin=new Thickness(0,4,0,4)};
        row.Children.Add(new TextBlock{Text=title,MinWidth=94,FontSize=12,VerticalAlignment=VerticalAlignment.Center});row.Children.Add(combo);row.Children.Add(current);return row;
    }
    private void AddButton(WrapPanel row,string text,Func<Task<string>> apply)
    {
        var button=new Button{Content=text,FontSize=12,Padding=new Thickness(8,3,8,3),Margin=new Thickness(7,0,0,0)};row.Children.Add(button);
        button.Click+=async(_,_)=>
        {
            if(busy||disposed)return;busy=true;editor.IsEnabled=false;status.Text="正在应用并检查…";
            try{status.Text=await apply();}
            catch(Exception e){status.Text=e.Message;}
            finally{busy=false;if(!disposed){editor.IsEnabled=true;Refresh();}}
        };
    }
    internal async Task<string> ApplyBananaAsync(uint rate)
    {
        if(!AudioEndpointFormats.Rates.Contains(rate))throw new ArgumentOutOfRangeException(nameof(rate));
        if(!client.Connected)throw new IOException("Banana 未连接。");
        client.Set("Option.sr",rate);
        for(int i=0;i<40;i++)
        {
            await Task.Delay(100);if(disposed)return "设置检查已取消。";
            if(client.Refresh()&&Math.Abs(client.Get("Option.sr")-rate)<1)return $"Banana 首选已保存为 {Format(rate)}；点击“重启引擎”使其生效，硬件实际速率请看回读。";
        }
        throw new IOException("Banana 未确认首选采样率；未自动重启引擎。");
    }
    private async Task<string> RestartBananaAsync()
    {
        if(!client.Connected)throw new IOException("Banana 未连接。");
        client.Set("Command.Restart",1);await Task.Delay(500);
        if(disposed)return "重启检查已取消。";
        var connected=client.Refresh();
        return connected?"已发送 Banana 引擎重启并恢复连接。请检查硬件实际速率和音频。":"已发送引擎重启；Banana 暂未恢复连接，请稍后检查。";
    }
    internal void Refresh()
    {
        if(disposed)return;
        var parts=new List<string>();
        try
        {
            endpoints=AudioEndpointFormats.Read();var ids=string.Join("|",endpoints.Select(x=>x.Id));
            if(ids!=endpointIds){endpointIds=ids;BuildWindowsRows();}
            foreach(var endpoint in endpoints)
            {
                var current="默认 "+Format(endpoint.DefaultRate)+(endpoint.EngineRate==endpoint.DefaultRate?"":" / 引擎 "+Format(endpoint.EngineRate));
                endpointValues[endpoint.Id].Text=current;
                parts.Add($"Windows {(endpoint.Capture?"录音":"播放")} {Format(endpoint.DefaultRate)}");
            }
            if(endpoints.Length==0)parts.Add("Windows Livejack 未连接");
        }
        catch(Exception e){parts.Add("Windows 格式读取失败");summary.ToolTip=e.Message;}
        try
        {
            uint? preferred=client.Connected?checked((uint)Math.Max(0,client.Get("Option.sr"))):null;
            bananaValue.Text=preferred==0?"当前 自动":client.Connected?"当前 "+Format(preferred):"未连接";
            if(banana.SelectedIndex<0&&preferred is {} selected&&AudioEndpointFormats.Rates.Contains(selected))banana.SelectedItem=selected;
            parts.Add("Banana 首选 "+(preferred==0?"自动":Format(preferred)));
        }
        catch(Exception e){bananaValue.Text="读取失败";bananaValue.ToolTip=e.Message;parts.Add("Banana 首选 未知");}
        var device=snapshot();hardwareValue.Text=device.Connected?"实际 "+Format(device.SampleRate):"未连接";
        var available=device.SampleRates.Where(AudioEndpointFormats.Rates.Contains).ToArray();
        if(hardware.ItemsSource is not uint[] old||!old.SequenceEqual(available))
        {
            var selected=hardware.SelectedItem;hardware.ItemsSource=available;
            hardware.SelectedItem=selected is uint rate&&available.Contains(rate)?rate:device.SampleRate;
        }
        parts.Add("Donner 实际 "+(device.Connected?Format(device.SampleRate):"未连接"));summary.Text=string.Join("  ·  ",parts);
    }
    private void BuildWindowsRows()
    {
        windowsRows.Children.Clear();endpointValues.Clear();
        foreach(var endpoint in endpoints)
        {
            var combo=RateCombo(AudioEndpointFormats.Rates);combo.SelectedItem=endpoint.DefaultRate;
            var value=new TextBlock{FontSize=12,VerticalAlignment=VerticalAlignment.Center};endpointValues[endpoint.Id]=value;
            var row=Row(endpoint.Capture?"Windows 录音":"Windows 播放",combo,value);row.ToolTip=endpoint.Name;windowsRows.Children.Add(row);
            AddButton(row,"应用 Windows",()=>AudioEndpointFormats.ApplyAsync(endpoint.Id,SelectedRate(combo)));
            AddButton(row,"声音属性",()=>{AudioEndpointFormats.OpenSoundProperties(endpoint.Capture);return Task.FromResult("在声音面板选择“"+endpoint.Name+"”→ 属性 → 高级，修改默认格式后返回查看回读。");});
        }
    }
    public void Dispose(){disposed=true;timer.Stop();}
}

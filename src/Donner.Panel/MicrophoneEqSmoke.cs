using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Donner.Core;
namespace Donner.Panel;
public partial class MainWindow
{
    private async Task VerifyMicrophoneEqUi()
    {
        string path=Path.Combine(smokeDirectory!,"eq-isolated","settings.json");
        using(var rack=new MicrophoneEqRack(null,path))
        {
            var config=rack.Get(1);config.Bands[0]=new(EqFilter.HighPass,80,0,.707);config.Bands[2]=new(EqFilter.Bell,600,-5,1.4);config.Bands[4]=new(EqFilter.Bell,4000,3,1);
            rack.Set(1,config);using var editor=new EqEditor(rack);editor.SelectInput(1);
            var old=MixerSlot.Content;bool oldEnabled=mixerEnabled;MixerSlot.Content=editor;mixerEnabled=true;UpdateMixerNavigation();Pages.SelectedItem=MixerPage;
            try
            {
                await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                var field=Visuals<TextBox>(editor).Single(x=>x.Tag?.ToString()=="Frequency");field.Text="120";
                field.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,Environment.TickCount,field,null){RoutedEvent=Keyboard.LostKeyboardFocusEvent});
                if(rack.Get(1).Bands[0].Frequency!=120||rack.Get(0).Bands[0].Frequency!=80)throw new Exception("麦克风EQ字段未独立应用。");
                var before=rack.Get(1).Bands[0];field.Text="NaN";field.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,Environment.TickCount,field,null){RoutedEvent=Keyboard.LostKeyboardFocusEvent});
                if(rack.Get(1).Bands[0]!=before)throw new Exception("EQ接收了无效数字。");
                var graph=editor.Graph;
                if(Math.Abs(graph.XFrequency(graph.FrequencyX(1000))-1000)>.001)throw new Exception("对数坐标无法往返。");
                graph.BandChanged!(2,new(EqFilter.Bell,800,-4.5,2));
                if(rack.Get(1).Bands[2].Frequency!=800)throw new Exception("曲线拖动未更改实际EQ设置。");
                editor.SelectInput(0);editor.SelectInput(1);
                if(editor.Graph.Settings.Bands[2].Gain!=-4.5)throw new Exception("切换输入丢失EQ设置。");
                await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                void Capture(string name)
                {
                    var bitmap=new RenderTargetBitmap((int)editor.ActualWidth,(int)editor.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(editor);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(smokeDirectory!,name));png.Save(file);
                }
                Capture("microphone-eq.png");editor.Width=760;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Capture("microphone-eq-compact.png");
            }
            finally{MixerSlot.Content=old;mixerEnabled=oldEnabled;UpdateMixerNavigation();Pages.SelectedIndex=0;}
        }
        using(var restored=new MicrophoneEqRack(null,path))if(restored.Get(1).Enabled||restored.Get(1).Bands[2].Gain!=-4.5)throw new Exception("EQ参数未保存或重启擅自启用。");
        foreach(string corrupt in new[]{"[]","[null,null,null]","not json"})
        {
            File.WriteAllText(path,corrupt);using var fallback=new MicrophoneEqRack(null,path);
            if(fallback.Get(0).Enabled||fallback.Get(0).Bands[0].Frequency!=80||File.ReadAllText(path)!=corrupt)throw new Exception("损坏EQ配置没有安全回退或被自动覆盖。");
        }
        File.WriteAllText(Path.Combine(smokeDirectory!,"microphone-eq-ui.json"),JsonSerializer.Serialize(new{Success=true,InputIsolation=true,InvalidInputRejected=true,CurveEdits=true,PersistedAndBypassed=true}));
    }
}

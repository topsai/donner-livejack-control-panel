using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Donner.Core;

namespace Donner.Panel;
public partial class MainWindow
{
    private readonly DispatcherTimer meterTimer=new() {Interval=TimeSpan.FromMilliseconds(40)};
    private readonly Dictionary<(uint,uint),ChannelMeter> meterViews=[];
    private readonly float[] displayedPeaks=new float[4];
    private LiveLevels? levels;
    private string? meterDevice;
    private bool meterSuspended;
    private void InitializeMeters()
    {
        meterTimer.Tick+=(_,_)=>UpdateMeters();
        Loaded+=(_,_)=>{if(smokeDirectory is null)meterTimer.Start();};
        Closed+=(_,_)=>{meterTimer.Stop();StopMeters();};
    }
    private void StopMeters()
    {
        var previous=levels;levels=null;meterDevice=null;
        try {previous?.Dispose();}
        catch(Exception error) when(error is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        Array.Clear(displayedPeaks);
        foreach(var view in meterViews.Values)view.Visibility=Visibility.Hidden;
    }
    private void MeterCheckClick(object sender,RoutedEventArgs e){StopMeters();UpdateMeters();}
    private void UpdateMeters()
    {
        if(closed)return;
        var d=session.Snapshot;
        bool wanted=MeterCheck.IsChecked==true&&IsVisible&&WindowState!=WindowState.Minimized&&Pages.SelectedIndex==0&&d.Connected&&!d.Demo&&d.DeviceCount==1&&!meterSuspended;
        if(!wanted){if(levels is not null)StopMeters();return;}
        if(meterDevice!=d.DeviceId)
        {
            StopMeters();meterDevice=d.DeviceId;
            try {levels=new();if(levels.Error is {} error)Record("实时电平读取："+error);}
            catch(Exception error) when(error is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {Record("实时电平读取："+error.Message);}
        }
        if(levels is null)return;
        levels.RetryStopped();
        var peaks=levels.Read();
        foreach(var pair in meterViews)
        {
            var (direction,channel)=pair.Key;int index=(direction==1?0:2)+(int)channel-1;
            if(index is <0 or >3)continue;
            var view=pair.Value;bool available=direction==1?levels.InputAvailable:levels.OutputAvailable;
            view.Visibility=available?Visibility.Visible:Visibility.Hidden;
            if(!available){displayedPeaks[index]=0;continue;}
            displayedPeaks[index]=Math.Max(peaks[index],displayedPeaks[index]*.65f);
            view.Peak=displayedPeaks[index];
        }
    }
    private void AddMeter(Grid row,uint direction,uint channel)
    {
        var bar=new ChannelMeter {Width=14,Height=136,Margin=new Thickness(0,2,4,2),Visibility=Visibility.Hidden,ToolTip=direction==1?"输入信号实时峰值":"电脑送往声卡的输出峰值（不包含硬件直通监听）"};
        Grid.SetRow(bar,1);Grid.SetColumn(bar,1);row.Children.Add(bar);
        meterViews[(direction,channel)]=bar;
    }
}

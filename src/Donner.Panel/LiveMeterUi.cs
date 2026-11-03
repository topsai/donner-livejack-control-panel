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
    private string? meterDevice,lastMeterError;
    private bool meterSuspended;
    private Task pendingMeterCleanup=Task.CompletedTask;
    private void TrackMeterCleanup(Task cleanup)=>pendingMeterCleanup=pendingMeterCleanup.IsCompleted?cleanup:Task.WhenAll(pendingMeterCleanup,cleanup);
    private void InitializeMeters()
    {
        meterTimer.Tick+=(_,_)=>UpdateMeters();
        Loaded+=(_,_)=>{if(smokeDirectory is null)meterTimer.Start();};
        Closed+=(_,_)=>{meterTimer.Stop();StopMeters();};
    }
    private void StopMeters()
    {
        var previous=levels;levels=null;meterDevice=null;lastMeterError=null;
        try {previous?.Dispose();}
        catch(Exception error) when(error is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException) { }
        if(previous is not null)TrackMeterCleanup(previous.WaitForSourcesAsync());
        Array.Clear(displayedPeaks);
        foreach(var view in meterViews.Values)view.Visibility=Visibility.Hidden;
    }
    private async Task StopMetersAndWaitAsync()
    {
        StopMeters();await pendingMeterCleanup;
    }
    private void MeterCheckClick(object sender,RoutedEventArgs e){StopMeters();UpdateMeters();}
    private void UpdateMeters()
    {
        if(closed)return;
        var d=session.Snapshot;
        bool wanted=MeterCheck.IsChecked==true&&IsVisible&&WindowState!=WindowState.Minimized&&Pages.SelectedIndex==0&&d.Connected&&!d.Demo&&d.DeviceCount==1&&!meterSuspended;
        if(!wanted){if(levels is not null||meterDevice is not null)StopMeters();return;}
        bool fromMixer=ReadMixerHomeOutput(out bool mixerAvailable,out float mixerLeft,out float mixerRight);
        if(meterDevice!=d.DeviceId||meterMixerOutput!=fromMixer)
        {
            StopMeters();meterDevice=d.DeviceId;
            meterMixerOutput=fromMixer;
            try {levels=new(captureOutput:!fromMixer);}
            catch(Exception error) when(error is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
            {Record("实时电平读取："+error.Message);}
        }
        levels?.RetryStopped();
        var currentError=levels?.Error;
        if(currentError!=lastMeterError){lastMeterError=currentError;if(currentError is not null)Record("实时电平读取："+currentError);}
        var peaks=levels?.Read()??new float[4];
        if(fromMixer){peaks[2]=mixerLeft;peaks[3]=mixerRight;}
        foreach(var pair in meterViews)
        {
            var (direction,channel)=pair.Key;int index=(direction==1?0:2)+(int)channel-1;
            if(index is <0 or >3)continue;
            var view=pair.Value;bool available=direction==1?levels?.InputAvailable==true:fromMixer?mixerAvailable:levels?.OutputAvailable==true;
            if(direction==0)view.ToolTip=fromMixer?$"Banana A{mixerOutputBus+1} 送往 Livejack 的{(channel==1?"左":"右")}声道峰值（不包含硬件直通监听）":"电脑送往声卡的输出峰值（不包含硬件直通监听）";
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

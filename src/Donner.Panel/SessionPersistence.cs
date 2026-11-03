using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Donner.Core;
namespace Donner.Panel;
internal sealed record PanelSessionState
{
    public double Width {get;init;}=900;
    public double Height {get;init;}=620;
    public double? Left {get;init;}
    public double? Top {get;init;}
    public int Page {get;init;}
    public bool Levels {get;init;}=true;
    public NamedEndpoint[] Names {get;init;}=[];
    public Dictionary<string,Preset> Hardware {get;init;}=[];
    public Dictionary<string,int> Percents {get;init;}=[];
    public void Validate()
    {
        if(!double.IsFinite(Width)||Width is <840 or >16384||!double.IsFinite(Height)||Height is <600 or >16384||Left is {} l&&!double.IsFinite(l)||Top is {} t&&!double.IsFinite(t)||Page is <0 or >7||Hardware is null||Hardware.Count>32||Percents is null||Percents.Count>256||Percents.Any(x=>x.Value is <0 or >100))throw new InvalidDataException("应用配置无效。");
        new ApplicationConfiguration(Levels,false,Percents,Names).Validate();
        foreach(var pair in Hardware){if(pair.Value is null||pair.Key!=pair.Value.DeviceId||pair.Value.Demo)throw new InvalidDataException("设备会话绑定无效。");ControlPolicy.ValidateFormat(pair.Value);}
    }
}
public partial class MainWindow
{
    private PanelSessionState panelState=new();
    private readonly DispatcherTimer panelSaveTimer=new(){Interval=TimeSpan.FromMilliseconds(500)};
    private string? restoredHardware;
    private readonly HashSet<string> deferredHardware=[];
    private bool closingForSave,allowFinalClose,loadingPanelState;
    private string PanelStatePath=>Path.Combine(storage,"application-session.json");
    private static string HardwareKey(Setting s)=>$"{s.Kind}:{s.Direction}:{s.Channel}";
    private string PercentKey(uint direction,uint channel)=>$"{session.Snapshot.DeviceId}|{direction}|{channel}";
    private void InitializeSessionPersistence()
    {
        loadingPanelState=true;
        try
        {
            panelState=ConfigurationFiles.Load<PanelSessionState>(PanelStatePath,x=>x.Validate())??new();
            if(smokeDirectory is null)
            {
                Width=Math.Min(panelState.Width,SystemParameters.VirtualScreenWidth);Height=Math.Min(panelState.Height,SystemParameters.VirtualScreenHeight);
                if(panelState.Left is {} left&&panelState.Top is {} top&&left+Width>SystemParameters.VirtualScreenLeft&&left<SystemParameters.VirtualScreenLeft+SystemParameters.VirtualScreenWidth&&top+Height>SystemParameters.VirtualScreenTop&&top<SystemParameters.VirtualScreenTop+SystemParameters.VirtualScreenHeight){WindowStartupLocation=WindowStartupLocation.Manual;Left=left;Top=top;}
                MeterCheck.IsChecked=panelState.Levels;
            }
        }
        catch(Exception e) when(e is IOException or InvalidDataException){Record(e.Message);}
        finally{loadingPanelState=false;}
        panelSaveTimer.Tick+=(_,_)=>{panelSaveTimer.Stop();try{SavePanelSession();}catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException){Record("配置保存失败："+error.Message);}};
        SizeChanged+=(_,_)=>SchedulePanelSave();LocationChanged+=(_,_)=>SchedulePanelSave();Pages.SelectionChanged+=(_,_)=>SchedulePanelSave();MeterCheck.Click+=(_,_)=>SchedulePanelSave();
        Loaded+=(_,_)=>{if(smokeDirectory is null)Pages.SelectedIndex=panelState.Page==7&&!mixerEnabled?0:Math.Min(panelState.Page,Pages.Items.Count-1);};
        Closing+=async(_,e)=>
        {
            if(allowFinalClose||smokeDirectory is not null)return;e.Cancel=true;if(closingForSave)return;closingForSave=true;
            try
            {
                Mouse.Capture(null);Keyboard.ClearFocus();
                for(int i=0;i<200&&busy;i++)await Task.Delay(50);
                if(busy)throw new IOException("正在应用设置，请稍后再退出。");
                await FlushVolumes();mixerPanel?.FlushConfiguration();RememberHardwareState();SavePanelSession();
                allowFinalClose=true;Close();
            }
            catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException){Record(error.Message);MessageBox.Show(this,"设置尚未保存，窗口将保持打开。\n"+error.Message,"Donner",MessageBoxButton.OK,MessageBoxImage.Warning);}
            finally{closingForSave=false;}
        };
        nameRestoreTimer.Tick+=async(_,_)=>{nameRestoreTimer.Stop();await RestoreSessionNames();};
        Loaded+=(_,_)=>{if(smokeDirectory is null)ScheduleNamesRestore();};
        Closed+=(_,_)=>{panelSaveTimer.Stop();nameRestoreTimer.Stop();};
    }
    private void SchedulePanelSave()
    {if(loadingPanelState||smokeDirectory is not null||closed)return;panelSaveTimer.Stop();panelSaveTimer.Start();}
    private void SavePanelSession()
    {
        if(smokeDirectory is not null)return;
        var bounds=WindowState==WindowState.Normal?new Rect(Left,Top,ActualWidth,ActualHeight):RestoreBounds;
        if(bounds.Width<840||bounds.Height<600)return;
        foreach(var channel in channelViews)if(channel.Value.State.Volume is {} raw&&channel.Value.SelectedPercent is {} percent&&VolumeScale.ToRaw(percent)==raw)panelState.Percents[PercentKey(channel.Key.Item1,channel.Key.Item2)]=percent;
        panelState=panelState with{Width=bounds.Width,Height=bounds.Height,Left=bounds.Left,Top=bounds.Top,Page=Pages.SelectedIndex,Levels=MeterCheck.IsChecked==true};
        ConfigurationFiles.Save(PanelStatePath,panelState,x=>x.Validate());
    }
    private void RememberHardwareState(Setting[]? explicitChanges=null)
    {
        var device=session.Snapshot;if(smokeDirectory is not null||!device.Connected||device.Demo)return;
        var snapshot=PresetCapture.From(device,"上次设置",session.Linked);
        var entries=ReadableHardwareSettings(snapshot.Settings).ToDictionary(HardwareKey);
        if(panelState.Hardware.TryGetValue(device.DeviceId,out var previous))foreach(var item in previous.Settings)if(!entries.ContainsKey(HardwareKey(item))||deferredHardware.Contains(HardwareKey(item)))entries[HardwareKey(item)]=item;
        foreach(var item in explicitChanges??[]){entries[HardwareKey(item)]=item;deferredHardware.Remove(HardwareKey(item));}
        panelState.Hardware[device.DeviceId]=snapshot with{Settings=entries.Values.ToArray()};restoredHardware=device.DeviceId;SchedulePanelSave();
    }
    private Setting[] ReadableHardwareSettings(Setting[] settings)
    {
        var result=settings.ToDictionary(HardwareKey);var driver=session.Snapshot.Driver;
        if(driver.OutputFormat is {Id:>0} format&&driver.OutputFormats.Any(x=>x.Id==format.Id)){var value=new Setting("format",0,Value:(int)format.Id);result[HardwareKey(value)]=value;}
        if(driver.StreamingMode is 1 or 2){var value=new Setting("streammode",Value:(int)driver.StreamingMode.Value);result[HardwareKey(value)]=value;}
        return result.Values.ToArray();
    }
    private static int? HardwareValue(DeviceSnapshot d,Setting s)=>s.Kind switch
    {
        "rate"=>(int?)d.SampleRate,"buffer"=>(int?)d.BufferSize,"safemode"=>d.SafeMode is {} b?b?1:0:null,"format"=>(int?)d.Driver.OutputFormat?.Id,"streammode"=>(int?)d.Driver.StreamingMode,
        "volume"=>d.Channels.FirstOrDefault(x=>x.Direction==s.Direction&&x.Number==s.Channel)?.Volume,
        "mute"=>d.Channels.FirstOrDefault(x=>x.Direction==s.Direction&&x.Number==s.Channel)?.Muted is {} b?b?1:0:null,_=>null
    };
    private async Task RestoreHardwareSession()
    {
        var device=session.Snapshot;if(smokeDirectory is not null||device.Demo)return;
        if(!device.Connected){restoredHardware=null;return;}
        if(!panelState.Hardware.TryGetValue(device.DeviceId,out var desired)){RememberHardwareState();return;}
        var candidates=desired.Settings.Where(x=>restoredHardware!=device.DeviceId||deferredHardware.Contains(HardwareKey(x))).OrderBy(x=>x.Kind=="rate"?0:x.Kind=="buffer"?1:2).ToArray();
        session.Linked=desired.Linked;restoredHardware=device.DeviceId;
        foreach(var setting in candidates)
        {
            if(HardwareValue(session.Snapshot,setting)==setting.Value){deferredHardware.Remove(HardwareKey(setting));continue;}
            try
            {
                ControlPolicy.Validate(session.Snapshot,setting);await StopMetersAndWaitAsync();
                var reply=await session.ApplyAsync([setting]);if(!reply.Success)throw new IOException(reply.Message);deferredHardware.Remove(HardwareKey(setting));
            }
            catch(Exception error) when(error is IOException or InvalidDataException){deferredHardware.Add(HardwareKey(setting));}
        }
    }
    private async Task<MixerConfiguration?> CapturePresetMixer()
    {
        if(session.Snapshot.Demo)return null;
        mixerPanel?.FlushConfiguration();
        var current=mixerPanel?.CaptureConfiguration()??ConfigurationFiles.Load<MixerConfiguration>(Path.Combine(storage,"mixer-session.json"),x=>x.Validate());
        current??=new MixerConfiguration{Enabled=mixerEnabled};
        if(mixerEnabled)
        {
            var formats=await Task.Run(AudioEndpointFormats.Read);var collected=current.WindowsRates.ToDictionary(x=>x.Id);
            foreach(var format in formats)if(!collected.ContainsKey(format.Id))collected[format.Id]=new(format.Id,format.Name,format.Capture,format.DefaultRate);
            current=current with{WindowsRates=collected.Values.ToArray()};
        }
        return current with{Enabled=mixerEnabled};
    }
    private async Task RestorePresetMixer(MixerConfiguration? configuration)
    {
        if(configuration is null)return;configuration.Validate();
        if(configuration.Enabled&&VoicemeeterClient.InstalledDirectory() is null)throw new IOException("预设包含调音台，请先在应用设置中安装 Banana。");
        mixerPanel?.FlushConfiguration();mixerEnabled=configuration.Enabled;MixerEnableCheck.IsChecked=mixerEnabled;new MixerSettings(mixerEnabled).Save(MixerSettingsPath);
        if(!mixerEnabled){UpdateMixer();ConfigurationFiles.Save(Path.Combine(storage,"mixer-session.json"),configuration,x=>x.Validate());}
        else
        {
            ConfigurationFiles.Save(Path.Combine(storage,"mixer-session.json"),configuration,x=>x.Validate());
            if(mixerPanel is null){nextMixerStart=0;UpdateMixer();}else mixerPanel.ApplyConfiguration(configuration);

        }
    }
}

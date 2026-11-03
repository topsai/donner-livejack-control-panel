using System.Windows.Controls;
using System.Windows.Threading;
using Donner.Core;
namespace Donner.Panel;
internal sealed partial class MixerPanel
{
    private string? sessionPath,sessionNotice;
    private MixerConfiguration? savedConfiguration;
    private bool sessionReady,restoringSession,saveBlocked;
    private long nextSessionRestore,deviceRefreshDue;
    private readonly HashSet<(bool Input,int Index)> unresolved=[];
    private readonly Dictionary<int,long> inputBypassFences=[];
    private readonly DispatcherTimer sessionSaveTimer=new(){Interval=TimeSpan.FromMilliseconds(450)};
    private Func<bool> enabledPreference=()=>true;
    private TabControl? mixerTabs;
    private void InitializePersistence(string? directory,Func<bool>? enabled)
    {
        enabledPreference=enabled??(()=>true);
        if(directory is null){sessionReady=true;return;}
        if(settings is MixerSampleRates rates)rates.ConfigurationChanged+=MarkSessionDirty;
        sessionPath=Path.Combine(directory,"mixer-session.json");
        try
        {
            savedConfiguration=ConfigurationFiles.Load<MixerConfiguration>(sessionPath,x=>x.Validate());
            if(savedConfiguration is {} loaded){if(settings is MixerSampleRates savedRates)savedRates.RestoreFormats(loaded.WindowsRates);restoringSession=true;microphoneEq.LoadDesired(loaded.InputEq);inputEditor.RestoreView(loaded.View.Input);outputEditor.RestoreView(loaded.View.Output);if(mixerTabs is not null)mixerTabs.SelectedIndex=loaded.View.Tab;restoringSession=false;}
        }
        catch(Exception e) when(e is InvalidDataException or IOException){saveBlocked=true;sessionNotice=e.Message;}
        sessionSaveTimer.Tick+=(_,_)=>{sessionSaveTimer.Stop();SaveConfigurationNow();};
    }
    private void MarkSessionDirty()
    {
        if(restoringSession||sessionPath is null||disposed)return;
        sessionSaveTimer.Stop();sessionSaveTimer.Start();
    }
    private static string Prefix((bool Input,int Index) target)=>$"{(target.Input?"Strip":"Bus")}[{target.Index}].";
    internal MixerConfiguration CaptureConfiguration()
    {
        if(!client.Connected||!sessionReady)
        {
            if(savedConfiguration is null)throw new IOException("调音台未连接且没有可保存的配置。");
            var offline=new Dictionary<string,float>(savedConfiguration.Parameters);foreach(var pair in pending)offline[pair.Key]=pair.Value;
            return savedConfiguration with{Enabled=enabledPreference(),Parameters=offline,InputEq=microphoneEq.Capture(),View=CaptureMixerView()};
        }
        var parameters=new Dictionary<string,float>(StringComparer.Ordinal);
        foreach(string key in MixerRules.PersistentParameters)
        {
            float value=pending.TryGetValue(key,out var queued)?queued:ReadValue(key);
            try{MixerRules.Validate(key,value);parameters.Add(key,value);}catch(InvalidDataException){if(savedConfiguration?.Parameters.TryGetValue(key,out var old)==true)parameters.Add(key,old);}
        }
        var bindings=new List<MixerBinding>();
        foreach(var slot in devices)
        {
            string name=client.DeviceName(slot.Input,slot.Index);
            var choice=slot.Combo.Items.Cast<MixerDevice>().FirstOrDefault(x=>x.Name==name&&x.Type==(slot.Combo.SelectedItem as MixerDevice)?.Type)??slot.Combo.Items.Cast<MixerDevice>().FirstOrDefault(x=>x.Name==name);
            if(choice is not null)bindings.Add(new(slot.Input,slot.Index,choice.Type,choice.Name,choice.Id));
            else if(savedConfiguration?.Devices.FirstOrDefault(x=>x.Input==slot.Input&&x.Index==slot.Index) is {} previous)bindings.Add(previous);
        }
        var result=new MixerConfiguration{Enabled=enabledPreference(),Parameters=parameters,Devices=bindings.ToArray(),InputEq=microphoneEq.Capture(),View=CaptureMixerView()};
        if(savedConfiguration is not null)
        {
            foreach(var slot in unresolved)
            {
                foreach(var pair in savedConfiguration.Parameters.Where(x=>x.Key.StartsWith(Prefix(slot),StringComparison.Ordinal)))parameters[pair.Key]=pair.Value;
                var desired=savedConfiguration.Devices.First(x=>x.Input==slot.Input&&x.Index==slot.Index);bindings.RemoveAll(x=>x.Input==slot.Input&&x.Index==slot.Index);bindings.Add(desired);
            }
        }
        foreach(var pair in pending)parameters[pair.Key]=pair.Value;
        var windows=savedConfiguration?.WindowsRates.ToDictionary(x=>x.Id)??new Dictionary<string,WindowsRateBinding>();
        foreach(var format in (settings as MixerSampleRates)?.CaptureFormats()??[])windows[format.Id]=format;
        result=result with{Devices=bindings.ToArray(),WindowsRates=windows.Values.ToArray()};result.Validate();return result;
    }
    private MixerViewState CaptureMixerView()=>new(Math.Clamp(mixerTabs?.SelectedIndex??0,0,1),inputEditor.CaptureView(),outputEditor.CaptureView(),(settings as MixerSampleRates)?.IsExpanded==true);
    internal void ApplyConfiguration(MixerConfiguration configuration)
    {
        configuration.Validate();if(settings is MixerSampleRates rates)rates.RestoreFormats(configuration.WindowsRates);pending.Clear();awaiting.Clear();inputBypassFences.Clear();microphoneEq.LoadDesired(configuration.InputEq);savedConfiguration=configuration;saveBlocked=false;sessionReady=false;nextSessionRestore=0;
        if(sessionPath is not null)ConfigurationFiles.Save(sessionPath,configuration,x=>x.Validate());
        if(client.Connected)RestoreSession();
    }
    private void RestoreSession()
    {
        if(sessionReady||Environment.TickCount64<nextSessionRestore)return;
        restoringSession=true;
        try
        {
            if(savedConfiguration is null)
            {
                for(int i=0;i<3;i++)microphoneEq.RestoreInput(i);
                sessionReady=true;SaveConfigurationNow();return;
            }
            var wanted=savedConfiguration;unresolved.Clear();
            foreach(var binding in wanted.Devices)
            {
                var slot=devices.Single(x=>x.Input==binding.Input&&x.Index==binding.Index);
                var candidates=slot.Combo.Items.Cast<MixerDevice>().Where(x=>x.Type==binding.Type).ToArray();
                var matches=candidates.Where(x=>binding.Id.Length>0?x.Id==binding.Id:x.Name==binding.Name).ToArray();
                var match=matches.Length==1?matches[0]:matches.SingleOrDefault(x=>x.Name==binding.Name);
                if(match is null){unresolved.Add((binding.Input,binding.Index));continue;}
                if(client.DeviceName(binding.Input,binding.Index)!=match.Name||(slot.Combo.SelectedItem as MixerDevice)?.Type!=match.Type)
                {
                    if(binding.Input)
                    {
                        if(!inputBypassFences.TryGetValue(binding.Index,out var fence)){fence=microphoneEq.SuspendInput(binding.Index);inputBypassFences[binding.Index]=fence;}
                        if(!microphoneEq.BypassReady(fence)){nextSessionRestore=Environment.TickCount64+60;return;}
                    }
                    client.SetDevice(binding.Input,binding.Index,match);
                }
                if(client.DeviceName(binding.Input,binding.Index)==match.Name){updating=true;try{slot.Combo.SelectedItem=match;}finally{updating=false;}}
                if(client.DeviceName(binding.Input,binding.Index)!=match.Name)unresolved.Add((binding.Input,binding.Index));
            }
            foreach(var slot in unresolved)if(slot.Input)microphoneEq.SuspendInput(slot.Index);
            foreach(var pair in wanted.Parameters)
            {
                if(unresolved.Any(x=>pair.Key.StartsWith(Prefix(x),StringComparison.Ordinal)))continue;
                if(Math.Abs(client.Get(pair.Key)-pair.Value)>.0001){client.Set(pair.Key,pair.Value);awaiting[pair.Key]=(pair.Value,Environment.TickCount64+1500);}
            }
            microphoneEq.LoadDesired(wanted.InputEq);
            for(int i=0;i<3;i++)if(!unresolved.Contains((true,i)))microphoneEq.RestoreInput(i);
            outputEditor.InvalidateSource();
            inputEditor.RestoreView(wanted.View.Input);outputEditor.RestoreView(wanted.View.Output);
            if(mixerTabs is not null)mixerTabs.SelectedIndex=wanted.View.Tab;
            if(settings is MixerSampleRates rates)rates.IsExpanded=wanted.View.RatesExpanded;
            sessionReady=true;sessionNotice=unresolved.Count>0?"部分设备未连接，已保留其设置，刷新设备后继续恢复。":null;
            nextRead=0;
        }
        catch(Exception e) when(e is IOException or InvalidDataException){nextSessionRestore=Environment.TickCount64+3000;sessionNotice="配置已保留，恢复未完成："+e.Message;}
        finally{restoringSession=false;}
    }
    private void SaveConfigurationNow(bool strict=false)
    {
        if(sessionPath is null)return;
        if(saveBlocked){if(strict)throw new InvalidDataException(sessionNotice??"配置文件无法读取，未覆盖原文件。");return;}
        if(!sessionReady&&savedConfiguration is null)return;
        try{var current=CaptureConfiguration();ConfigurationFiles.Save(sessionPath,current,x=>x.Validate());savedConfiguration=current;}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException){sessionNotice="配置保存失败，原文件保留："+e.Message;if(strict)throw;}
    }
    internal void FlushConfiguration()
    {
        sessionSaveTimer.Stop();if(client.Connected)Poll(true);SaveConfigurationNow(true);
    }
    internal void ScheduleDeviceRefresh()=>deviceRefreshDue=Environment.TickCount64+1500;
    private void DeviceChoiceChanged(bool input,int index)
    {
        if(updating||restoringSession)return;
        bool pendingDevice=unresolved.Remove((input,index));
        if(pendingDevice){sessionReady=false;nextSessionRestore=Environment.TickCount64+200;inputBypassFences.Remove(index);}
        if(savedConfiguration is not null&&devices.First(x=>x.Input==input&&x.Index==index).Combo.SelectedItem is MixerDevice choice)
            savedConfiguration=savedConfiguration with{Devices=savedConfiguration.Devices.Where(x=>x.Input!=input||x.Index!=index).Append(new(input,index,choice.Type,choice.Name,choice.Id)).ToArray()};
        MarkSessionDirty();
    }
}

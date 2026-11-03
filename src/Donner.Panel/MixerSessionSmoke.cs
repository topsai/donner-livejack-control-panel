using System.Text.Json;
using System.Windows;
using Donner.Core;
namespace Donner.Panel;
public partial class MainWindow
{
    private void VerifyMixerSessions()
    {
        string directory=Path.Combine(smokeDirectory!,"session-fixture");Directory.CreateDirectory(directory);
        var first=new TestMixer();first.Values["Option.sr"]=44100;
        using(var panel=new MixerPanel(first,false,eqStorage:directory))
        {
            panel.Queue("Strip[0].Gain",-8.5f);panel.Queue("Strip[0].B1",1);panel.Poll(true);
            var complete=panel.CaptureConfiguration();complete.InputEq[0]=complete.InputEq[0] with{Enabled=true};
            complete=complete with{View=new(1,new(1,2,0,true),new(3,4,1,false))};panel.ApplyConfiguration(complete);panel.FlushConfiguration();
        }
        var second=new TestMixer();second.Values["Option.sr"]=44100;
        using(var panel=new MixerPanel(second,false,eqStorage:directory))
        {
            var saved=panel.CaptureConfiguration();
            if(second.Get("Strip[0].Gain")!=-8.5f||second.Get("Strip[0].B1")!=1||!saved.InputEq[0].Enabled||saved.View.Output?.Target!=3||saved.View.Input?.Band!=2)throw new Exception("重启没有恢复调音台或EQ启用/编辑器配置。");
            second.Connected=false;panel.Queue("Strip[0].Gain",-10); // Disabled controls reject new commands; preserve existing settings.
            panel.FlushConfiguration();
        }
        var wanted=new MixerConfiguration{Devices=[new(true,0,3,"Missing microphone","missing-id")],Parameters=new(){["Strip[0].Gain"]=-22},InputEq=[MicrophoneEqSettings.Flat() with{Enabled=true},MicrophoneEqSettings.Flat(),MicrophoneEqSettings.Flat()]};
        var missing=new TestMixer();missing.Names[(true,0)]="Another microphone";missing.Values["Strip[0].Gain"]=-3;
        using(var panel=new MixerPanel(missing,false))
        {
            panel.ApplyConfiguration(wanted);panel.Poll(true);
            if(missing.Get("Strip[0].Gain")!=-3||missing.Writes.Any(x=>x.Item1.StartsWith("Strip[0].")))throw new Exception("缺失设备的参数被应用到另一个麦克风。");
            var snapshot=panel.CaptureConfiguration();if(snapshot.Devices.Single(x=>x.Input&&x.Index==0).Name!="Missing microphone"||snapshot.Parameters["Strip[0].Gain"]!=-22||!snapshot.InputEq[0].Enabled)throw new Exception("缺失设备时丢失了待恢复配置。");
        }
        var parameters=new Dictionary<string,float>();
        foreach(string key in MixerRules.PersistentParameters)parameters[key]=key=="Option.sr"?44100:key.EndsWith(".f")?750:key.EndsWith(".q")?.73f:key.EndsWith(".Gain")||key.EndsWith(".gain")||key.Contains("EQGain")?-3.5f:key.EndsWith(".Comp")||key.EndsWith(".Gate")?2.5f:1;
        var full=new MixerConfiguration{Parameters=parameters,Devices=[new(true,0,3,"Livejack Audio Interface"),new(false,0,5,"Donner USB Audio Device")],InputEq=[MicrophoneEqSettings.Flat() with{Enabled=true},MicrophoneEqSettings.Flat(),MicrophoneEqSettings.Flat()],View=new(1,new(0,9),new(4,5,1,false))};
        var file=Path.Combine(smokeDirectory!,"fixture-export.json");var fixture=new Preset(2,"导入导出全量验证","fixture",false,true,[],full);PresetStore.ExportFile(file,fixture);var imported=PresetStore.ImportFile(file);
        var target=new TestMixer();
        using(var panel=new MixerPanel(target,false))
        {
            panel.ApplyConfiguration(imported.Mixer!);panel.Poll(true);
            foreach(var pair in parameters)if(Math.Abs(target.Get(pair.Key)-pair.Value)>.0001)throw new Exception("预设导入后实际参数不一致："+pair.Key);
            if(panel.CaptureConfiguration().InputEq[0].Bands.Length!=10||panel.CaptureConfiguration().View.Input?.Band!=9)throw new Exception("十段EQ或频段选择未恢复。");
            target.Connected=false;panel.Poll(true);target.Values.Clear();target.Connected=true;panel.Poll(true);
            foreach(var pair in parameters)if(Math.Abs(target.Get(pair.Key)-pair.Value)>.0001)throw new Exception("重连丢失配置："+pair.Key);
            panel.ApplyConfiguration(full with{Devices=[new(true,0,3,"Livejack Audio Interface","different-id")]});
            target.Writes.Clear();target.Values["Strip[0].Gain"]=-2;panel.Poll(true);
            if(target.Get("Strip[0].Gain")!=-2||target.Writes.Any(x=>x.Item1.StartsWith("Strip[0].")))throw new Exception("错误地按同名匹配了不同ID设备。");
        }
        var shared=new TestMixer{InputDevices=[new(1,""),new(3,"First microphone","shared-id"),new(3,"Second microphone","shared-id")]};shared.Names[(true,0)]="First microphone";
        using(var panel=new MixerPanel(shared,false))
        {
            panel.ApplyConfiguration(new MixerConfiguration{Devices=[new(true,0,3,"First microphone","shared-id")],Parameters=new(){["Strip[0].Gain"]=-7}});panel.Poll(true);
            if(shared.Get("Strip[0].Gain")!=-7||shared.DeviceName(true,0)!="First microphone")throw new Exception("共享硬件ID的设备没有按名称进一步区分。");
        }
        var queued=new TestMixer{InputDevices=[new(1,""),new(3,"Expected microphone","expected-id")]};queued.Names[(true,0)]="Expected microphone";
        using(var panel=new MixerPanel(queued,false))
        {
            panel.ApplyConfiguration(new MixerConfiguration{Devices=[new(true,0,3,"Expected microphone","expected-id")],Parameters=new(){["Strip[0].Gain"]=-4}});
            panel.Queue("Strip[0].Gain",-11);panel.Poll(true);queued.Connected=false;panel.Poll(true);
            if(panel.CaptureConfiguration().Parameters["Strip[0].Gain"]!=-11)throw new Exception("已提交更改在自动保存前断线丢失。");
            queued.Connected=true;panel.Poll(true);panel.Queue("Strip[0].Gain",-8);queued.Connected=false;panel.Poll(true);
            queued.InputDevices=[new(1,""),new(3,"Other microphone","other-id")];queued.Names[(true,0)]="Other microphone";queued.Values["Strip[0].Gain"]=-2;queued.Writes.Clear();queued.Connected=true;panel.Poll(true);
            if(queued.Get("Strip[0].Gain")!=-2||queued.Writes.Any(x=>x.Item1.StartsWith("Strip[0]."))||panel.CaptureConfiguration().Parameters["Strip[0].Gain"]!=-8)throw new Exception("挂起操作写到了不同设备或保存时丢失。");
        }
        File.WriteAllText(Path.Combine(smokeDirectory!,"mixer-session-checks.json"),JsonSerializer.Serialize(new{Success=true,RestartAndEnabled=true,EditorView=true,MissingDeviceDeferred=true,DeviceParametersIsolated=true,AllExportedParametersApplied=parameters.Count,ReconnectReapplied=true,StableIdCollisionDeferred=true,SharedHardwareIdDisambiguated=true,PendingWritesDeferredWithoutLoss=true,SubmittedWriteSurvivesEarlyDisconnect=true}));
    }
}
internal static class MixerConfigurationExport
{
    internal static void Capture(string report)
    {
        using var client=new VoicemeeterClient();if(!client.Refresh())throw new IOException(client.Status);
        using var panel=new MixerPanel(client,false);var config=panel.CaptureConfiguration();config=config with{WindowsRates=AudioEndpointFormats.Read().Select(x=>new WindowsRateBinding(x.Id,x.Name,x.Capture,x.DefaultRate)).ToArray()};config.Validate();
        File.WriteAllText(report+".devices.json",JsonSerializer.Serialize(new{Input=client.Devices(true),Output=client.Devices(false)}));
        File.WriteAllText(report,JsonSerializer.Serialize(config,new JsonSerializerOptions{WriteIndented=true}));
    }
}

using System.Windows;
using System.Windows.Controls;
using Donner.Core;

namespace Donner.Panel;
internal static partial class Program
{
    private static int passed,failed;
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    private static void Test(string name,Action test)
    {try{test();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+": "+error.Message);}}
    [STAThread] private static int Main(string[] args)
    {
        if(args.Length==2&&args[0]=="--installer-worker")
        {
            var deadline=Environment.TickCount64+10000;
            while(!File.Exists(args[1])&&Environment.TickCount64<deadline)Thread.Sleep(10);
            return File.Exists(args[1])?0:1;
        }
        var app=new Application();
        app.Resources["ChannelFader"]=new Style(typeof(Slider));app.Resources["MixerTabsTemplate"]=new ControlTemplate(typeof(TabControl));
        Test("离线采样率落盘、导出和重开保留新值及未连接端点",OfflineRates);
        Test("同名不同ID且旧配置没有ID时不猜测设备",()=>Binding([new(1,""),new(3,"Microphone","one"),new(3,"Microphone","two")],new(true,0,3,"Microphone"),false));
        Test("共享ID仍同名的候选安全保留为未解析",()=>Binding([new(1,""),new(3,"Microphone","shared"),new(3,"Microphone","shared")],new(true,0,3,"Microphone","shared"),false));
        Test("唯一同名的旧配置仍可恢复",()=>Binding([new(1,""),new(3,"Microphone","one")],new(true,0,3,"Microphone"),true));
        Test("共享ID可按唯一名称恢复",()=>Binding([new(1,""),new(3,"First microphone","shared"),new(3,"Microphone","shared")],new(true,0,3,"Microphone","shared"),true));
        Test("不同ID不能回退到同名设备",()=>Binding([new(1,""),new(3,"Microphone","other")],new(true,0,3,"Microphone","expected"),false));
        Test("缺失设备不阻塞其他总线恢复",()=>Binding([new(1,"")],new(true,0,3,"Microphone","expected"),false));
        InstallerTests();
        Console.WriteLine($"Panel regression checks: {passed} passed, {failed} failed.");
        app.Shutdown();return failed==0?0:1;
    }
    private static void OfflineRates()
    {
        using var fixture=new Fixture();var fake=new FakeMixer();
        using(var rates=new MixerSampleRates(fake,()=>new DeviceSnapshot(),_=>Task.CompletedTask,false,()=>[]))
        using(var panel=new MixerPanel(fake,false,sampleRates:rates,eqStorage:fixture.Directory))
        {
            panel.ApplyConfiguration(new(){WindowsRates=[new("capture","Livejack",true,44100),new("unplugged","Unplugged",false,96000)]});
            rates.RestoreFormats([new("capture","Livejack",true,48000)]);fake.Connected=false;panel.FlushConfiguration();
            var captured=panel.CaptureConfiguration();Check(captured.WindowsRates.Single(x=>x.Id=="capture").Rate==48000,"Offline capture lost latest rate");
            Check(captured.WindowsRates.Single(x=>x.Id=="unplugged").Rate==96000,"Disconnected endpoint lost desired rate");
            var saved=ConfigurationFiles.Load<MixerConfiguration>(Path.Combine(fixture.Directory,"mixer-session.json"),x=>x.Validate())!;
            Check(saved.WindowsRates.Single(x=>x.Id=="capture").Rate==48000,"Saved stale rate");
            var preset=new Preset(2,"Fixture","fixture",false,false,[],captured);var export=Path.Combine(fixture.Directory,"export.json");
            PresetStore.ExportFile(export,preset);Check(PresetStore.ImportFile(export).Mixer!.WindowsRates.Single(x=>x.Id=="capture").Rate==48000,"Export lost rate");
        }
        fake=new FakeMixer();using var reopenedRates=new MixerSampleRates(fake,()=>new DeviceSnapshot(),_=>Task.CompletedTask,false,()=>[]);
        using var reopened=new MixerPanel(fake,false,sampleRates:reopenedRates,eqStorage:fixture.Directory);
        Check(reopenedRates.CaptureFormats().Single(x=>x.Id=="capture").Rate==48000,"Restart did not restore latest rate");
    }
    private static void Binding(MixerDevice[] devices,MixerBinding binding,bool expected)
    {
        var fake=new FakeMixer{InputDevices=devices};using var panel=new MixerPanel(fake,false);
        fake.Names[(true,0)]="Another microphone";
        panel.ApplyConfiguration(new(){Devices=[binding],Parameters=new(){["Strip[0].Gain"]=-12,["Bus[1].Gain"]=-5}});panel.Poll(true);
        Check(panel.ControlsEnabled,"Ambiguous/missing device disabled entire mixer");
        Check(fake.Get("Bus[1].Gain")==-5,"Unrelated bus was not restored");
        if(expected)Check(fake.Get("Strip[0].Gain")==-12&&fake.DeviceName(true,0)==binding.Name,"Unique match did not restore");
        else
        {
            Check(!fake.Writes.Any(x=>x.Key.StartsWith("Strip[0].",StringComparison.Ordinal)),"Parameters written to ambiguous/missing input");
            Check(fake.DeviceWrites.Count==0,"Guessed ambiguous/missing device");
            Check(panel.CaptureConfiguration().Devices.Single(x=>x.Input&&x.Index==0)==binding,"Lost unresolved binding");
        }
    }
    private sealed class Fixture:IDisposable
    {
        internal string Directory {get;}=Path.Combine(Path.GetTempPath(),"Donner-PanelTests-"+Guid.NewGuid().ToString("N"));
        internal Fixture()=>System.IO.Directory.CreateDirectory(Directory);
        public void Dispose()=>System.IO.Directory.Delete(Directory,true);
    }
    private sealed class FakeMixer:IMixerClient
    {
        public bool Connected{get;set;}=true;public string Status=>"Fixture";
        public MixerDevice[] InputDevices=[new(1,"")];
        internal readonly Dictionary<(bool,int),string> Names=[];
        private readonly Dictionary<string,float> values=[];
        internal readonly List<(string Key,float Value)> Writes=[];
        internal readonly List<MixerDevice> DeviceWrites=[];
        public bool Refresh()=>Connected;
        public void Start(bool show=false)=>throw new Exception("Tests must not start real audio");
        public float Get(string key)=>values.TryGetValue(key,out var value)?value:key.EndsWith(".f",StringComparison.Ordinal)?1000:key.EndsWith(".q",StringComparison.Ordinal)?1:key=="Option.sr"?48000:0;
        public void Set(string key,float value){MixerRules.Validate(key,value);values[key]=value;Writes.Add((key,value));}
        public string DeviceName(bool input,int index)=>Names.GetValueOrDefault((input,index),"");
        public MixerDevice[] Devices(bool input)=>input?InputDevices:[new(1,"")];
        public void SetDevice(bool input,int index,MixerDevice device){DeviceWrites.Add(device);Names[(input,index)]=device.Name;}
        public float Level(bool input,int index)=>0;
        public void Dispose(){}
    }
}

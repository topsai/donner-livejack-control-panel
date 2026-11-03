using System.Text.Json;
namespace Donner.Panel;
// Pure validation/temporary-fixture checks: never writes Windows audio properties.
internal static class MixerEndpointNamesSmoke
{
    internal static string Verify()
    {
        int checks=0;
        void Assert(bool condition){checks++;if(!condition)throw new InvalidDataException("Endpoint name smoke assertion failed: "+checks);}
        void Reject(Action action)
        {
            checks++;try{action();}catch(Exception e) when(e is InvalidDataException or JsonException){return;}
            throw new InvalidDataException("Endpoint name smoke accepted invalid input: "+checks);
        }
        Assert(MixerEndpointNames.ValidateName("我的麦克风 🎙")=="我的麦克风 🎙");
        foreach(var value in new string?[]{null,""," "," leading","trailing ","a\nb","a\0b",new string('x',121)})Reject(()=>MixerEndpointNames.ValidateName(value));
        Assert(MixerEndpointNames.ValidateName(new string('x',120)).Length==120);
        var good="{\"Version\":1,\"Changes\":[{\"Id\":\"endpoint-id\",\"ExpectedDescriptor\":\"麦克风\",\"Name\":\"直播话筒\"}]}";
        Assert(MixerEndpointNames.ParseRequest(good).Changes[0].Name=="直播话筒");
        var custom=new[]{new MixerEndpointNames.Backup("physical-id","old","Voicemeeter Input","old",true)};
        Assert(MixerEndpointNames.CustomDisplay("Voicemeeter Input (Realtek)","physical-id",custom)=="Voicemeeter Input (Realtek)");
        Assert(MixerEndpointNames.CustomDisplay("Voicemeeter Input (Realtek)","native-id",custom)=="Voicemeeter Input (Realtek)");
        Assert(MixerEndpointNames.CustomDisplay("Voicemeeter Input changed","physical-id",custom) is null);
        Assert(MixerEndpointNames.CustomDisplay("Voicemeeter Input","unknown",[custom[0],custom[0] with{Id="other"}]) is null);
        var physical=new MixerEndpointNames.Endpoint("physical-id","原厂","Voicemeeter Input (Realtek)","",true,"Voicemeeter Input");
        Assert(MixerEndpointNames.Applied(physical,custom[0]));
        Assert(!MixerEndpointNames.Applied(physical with{Descriptor="用户后来在 Windows 改名"},custom[0]));
        foreach(var value in new[]{"null","{}","{\"Version\":2,\"Changes\":[]}","{\"Version\":1,\"Changes\":[null]}",good.Replace("\"Version\":1","\"Version\":1,\"Version\":1"),good.Replace("\"Name\":","\"Command\":\"anything\",\"Name\":"),good.Replace("直播话筒",""),good.Replace("endpoint-id",""),good.Replace("\"Changes\":[{","\"Changes\":[{\"Id\":\"duplicate\","),"{\"Version\":1,\"Changes\":[{\"Id\":\"x\",\"ExpectedDescriptor\":\"old\",\"Name\":\"n\"},{\"Id\":\"x\",\"ExpectedDescriptor\":\"old\",\"Name\":\"n\"}]}"})Reject(()=>MixerEndpointNames.ParseRequest(value));
        var fixture=Path.Combine(Path.GetTempPath(),"donner-names-test-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            foreach(var value in new[]{"{bad","null","[null]","[{\"Id\":\"x\",\"Before\":\"before\",\"Applied\":\"\"}]"})
            {File.WriteAllText(fixture,value);Reject(()=>MixerEndpointNames.ReadBackups(fixture));Assert(File.ReadAllText(fixture)==value);}
            File.WriteAllText(fixture,"[{\"Id\":\"physical-id\",\"Before\":\"原名 (Realtek)\",\"Applied\":\"直播\",\"OriginalDescriptor\":\"原名\",\"Custom\":true}]");
            Assert(MixerEndpointNames.ReadBackups(fixture)[0].OriginalDescriptor=="原名");
            File.WriteAllText(fixture,"[{\"Id\":\"legacy-id\",\"Before\":\"Voicemeeter Input (VB-Audio Voicemeeter VAIO)\",\"Applied\":\"电脑声音\"}]");
            Assert(MixerEndpointNames.ReadBackups(fixture)[0].OriginalDescriptor is null);
        }
        finally{if(File.Exists(fixture))File.Delete(fixture);}
        Reject(()=>MixerEndpointNames.ApplyCustomRequest(Path.GetTempPath(),Path.Combine(Path.GetTempPath(),"arbitrary.json")));
        var report=Path.Combine(Path.GetTempPath(),"donner-report-"+Guid.NewGuid().ToString("N")+".txt");
        try
        {
            Assert(HelperReports.WriteNew(report,"original"));Assert(!HelperReports.WriteNew(report,"replacement"));Assert(File.ReadAllText(report)=="original");
        }
        finally{if(File.Exists(report))File.Delete(report);}
        return $"Endpoint name validation passed: {checks} checks; no Windows endpoint writes.";
    }
}

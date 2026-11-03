using System.Text.Json;

namespace Donner.Core;

public sealed record MixerSettings(bool Enabled=false)
{
    public static MixerSettings Load(string path)
    {
        if(!File.Exists(path))return new();
        try{return JsonSerializer.Deserialize<MixerSettings>(File.ReadAllText(path))??throw new InvalidDataException("调音台设置为空。");}
        catch(JsonException e){throw new InvalidDataException("调音台设置损坏，已保持关闭。",e);}
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary=path+".tmp";File.WriteAllText(temporary,JsonSerializer.Serialize(this));File.Move(temporary,path,true);
    }
}

public static class MixerRules
{
    public static readonly string[] Routes=["A1","A2","A3","B1","B2"];
    private static readonly Dictionary<string,(float Min,float Max,bool Integer)> ranges=Build();
    private static Dictionary<string,(float,float,bool)> Build()
    {
        var r=new Dictionary<string,(float,float,bool)>(StringComparer.Ordinal);
        for(int i=0;i<5;i++)
        {
            foreach(var prefix in new[]{"Strip","Bus"})
            {r[$"{prefix}[{i}].Gain"]=(-60,12,false);r[$"{prefix}[{i}].Mute"]=(0,1,true);r[$"{prefix}[{i}].Mono"]=(0,1,true);}
            foreach(var route in Routes)r[$"Strip[{i}].{route}"]=(0,1,true);
            if(i<3){r[$"Strip[{i}].Comp"]=(0,10,false);r[$"Strip[{i}].Gate"]=(0,10,false);}
            else for(int band=1;band<=3;band++)r[$"Strip[{i}].EQGain{band}"]=(-12,12,false);
            r[$"Bus[{i}].EQ.on"]=(0,1,true);
            for(int channel=0;channel<8;channel++)for(int cell=0;cell<6;cell++)
            {
                var p=$"Bus[{i}].EQ.channel[{channel}].cell[{cell}].";
                r[p+"on"]=(0,1,true);r[p+"type"]=(0,6,true);r[p+"f"]=(20,20000,false);
                r[p+"gain"]=(-12,12,false);r[p+"q"]=(1,100,false);
            }
        }
        return r;
    }
    public static void Validate(string parameter,float value)
    {
        if(parameter=="Option.sr"&&new float[]{44100,48000,88200,96000,176400,192000}.Contains(value))return;
        if(parameter=="Command.Restart"&&value==1)return;
        if(!ranges.TryGetValue(parameter,out var r)||!float.IsFinite(value)||value<r.Min||value>r.Max||(r.Integer&&value!=MathF.Round(value)))
            throw new InvalidDataException("调音台参数或数值不受支持："+parameter);
    }
    public static int InputLevelIndex(int strip)=>strip switch {0=>0,1=>2,2=>4,3=>6,4=>14,_=>throw new InvalidDataException("输入条带无效。")};
    public static string[] EqKeys(int bus,int cell,string field,bool linked,int channel=0)
    {
        var key=$"Bus[{bus}].EQ.channel[{channel}].cell[{cell}].{field}";
        if(!ranges.ContainsKey(key)||channel is <0 or >1)throw new InvalidDataException("EQ目标无效。");
        return linked?new[]{0,1}.Select(c=>$"Bus[{bus}].EQ.channel[{c}].cell[{cell}].{field}").ToArray():[key];
    }
}

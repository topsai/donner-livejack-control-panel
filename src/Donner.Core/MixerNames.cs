namespace Donner.Core;

public static class MixerNames
{
    public static string? Endpoint(string description,bool capture)
    {
        if(!capture)return description switch
        {
            "Voicemeeter Input"=>"电脑声音 → 调音台 (VAIO)",
            "Voicemeeter AUX Input"=>"伴奏 / 第二路声音 → 调音台 (AUX)",
            "Voicemeeter VAIO3 Input"=>"扩展电脑声音 (VAIO3 / Potato)",
            _ when System.Text.RegularExpressions.Regex.IsMatch(description,@"^Voicemeeter In [1-5]$")=>"扩展播放通道 ("+description+")",
            _=>null
        };
        return description switch
        {
            "Voicemeeter Output" or "Voicemeeter Out B1"=>"直播混音 1 (B1)",
            "Voicemeeter Aux Output" or "Voicemeeter AUX Output" or "Voicemeeter Out B2"=>"直播混音 2 (B2)",
            "Voicemeeter VAIO3 Output" or "Voicemeeter Out B3"=>"扩展混音输出 (B3 / Potato)",
            "Voicemeeter Out A1"=>"监听总线录音 (A1)",
            "Voicemeeter Out A2"=>"监听总线录音 (A2)",
            "Voicemeeter Out A3"=>"监听总线录音 (A3)",
            "Voicemeeter Out A4"=>"扩展监听录音 (A4 / Potato)",
            "Voicemeeter Out A5"=>"扩展监听录音 (A5 / Potato)",
            _=>null
        };
    }
    public static string Display(string name)
    {
        foreach(var capture in new[]{false,true})
        {
            var direct=Endpoint(name,capture);if(direct is not null)return direct;
            int end=name.IndexOf(" (");if(end>0&&Endpoint(name[..end],capture) is {} label)return label;
        }
        return name;
    }
}

using System.Globalization;
using System.Text;
using Donner.Core;
namespace Donner.Panel;
internal static class PresetDescription
{
    internal static string Format(Preset p,StreamFormat[] formats)
    {
        var text=new StringBuilder();text.AppendLine("声卡设置");
        var rate=p.Settings.FirstOrDefault(x=>x.Kind=="rate");var buffer=p.Settings.FirstOrDefault(x=>x.Kind=="buffer");
        var timing=new List<string>();if(rate is not null)timing.Add($"采样率 {rate.Value/1000d:0.###} kHz");if(buffer is not null)timing.Add($"ASIO 缓冲 {buffer.Value} samples");if(timing.Count>0)text.AppendLine(string.Join("  ·  ",timing));
        text.AppendLine($"左右输出联动：{(p.Linked?"开启":"关闭")}");
        foreach(var group in p.Settings.Where(x=>x.Kind is "volume" or "mute").GroupBy(x=>(x.Direction,x.Channel)).OrderBy(x=>x.Key.Direction==1?0:1).ThenBy(x=>x.Key.Channel))
        {
            string label=group.Key.Direction==1?$"输入 {group.Key.Channel}":group.Key.Channel==1?"输出左":group.Key.Channel==2?"输出右":$"输出 {group.Key.Channel}";
            var values=new List<string>();var volume=group.FirstOrDefault(x=>x.Kind=="volume");var mute=group.FirstOrDefault(x=>x.Kind=="mute");
            if(volume is not null){int percent=VolumeScale.ToPercent((short)volume.Value);string key=$"{p.DeviceId}|{group.Key.Direction}|{group.Key.Channel}";if(p.Application?.Percents.TryGetValue(key,out int exact)==true&&VolumeScale.ToRaw(exact)==volume.Value)percent=exact;values.Add(percent+"%");}
            if(mute is not null)values.Add(mute.Value==1?"静音":"未静音");text.AppendLine(label+"："+string.Join(" · ",values));
        }
        foreach(var setting in p.Settings.Where(x=>x.Kind is "format" or "safemode" or "streammode"))text.AppendLine(setting.Kind switch
        {
            "format"=>formats.FirstOrDefault(x=>x.Id==setting.Value) is {} f?$"输出格式：{f.Bits} 位":"输出格式：已保存",
            "safemode"=>$"ASIO 安全模式：{(setting.Value==1?"开启":"关闭")}",_=>$"音频流：{(setting.Value==2?"持续开启":"按需开启")}",
        });
        text.AppendLine();
        if(p.Mixer is {} mixer)
        {
            text.AppendLine("调音台 · "+(mixer.Enabled?"开启":"关闭（保留设置）"));
            foreach(var binding in mixer.Devices.Where(x=>x.Input&&x.Name.Length>0).OrderBy(x=>x.Index))text.AppendLine($"输入 {binding.Index+1}：{new MixerDevice(binding.Type,binding.Name,binding.Id)}");
            if(mixer.Parameters.TryGetValue("Option.sr",out float preferred))text.AppendLine("Banana 首选："+(preferred>0?$"{preferred/1000:0.###} kHz":"自动"));
            foreach(var windows in mixer.WindowsRates)text.AppendLine($"Windows {(windows.Capture?"录音":"播放")}：{windows.Rate/1000d:0.###} kHz");
            text.AppendLine("已包含通道音量、静音、单声道、路由、压缩、噪声门与 EQ 设置。");
            text.AppendLine();text.AppendLine("输入 EQ");
            for(int i=0;i<mixer.InputEq.Length;i++)text.AppendLine($"输入 {i+1}：{(mixer.InputEq[i].Enabled?"开启":"关闭")} · {mixer.InputEq[i].Bands.Length} 段");
            text.AppendLine("输出 EQ：已包含各总线和声道的参数及开关");
        }
        else text.AppendLine("旧版设备预设 · 应用时保留当前调音台设置");
        if(p.Application is {} app){text.AppendLine();text.AppendLine("应用设置");text.AppendLine($"实时电平：{(app.Levels?"开启":"关闭")}  ·  随 Windows 启动：{(app.Startup?"开启":"关闭")}");if(app.Names.Length>0)text.AppendLine($"自定义设备名称：{app.Names.Length} 项");}
        return text.ToString().TrimEnd();
    }
}

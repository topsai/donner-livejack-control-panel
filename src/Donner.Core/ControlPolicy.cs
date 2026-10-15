namespace Donner.Core;

public static class ControlPolicy
{
    public static void Validate(DeviceSnapshot device, Setting setting)
    {
        if (!device.Connected) throw new InvalidDataException("设备未连接，不能修改设置。");
        if (setting.Kind == "rate")
        {
            if (setting.Value <= 0 || !device.SampleRates.Contains((uint)setting.Value))
                throw new InvalidDataException("设备未确认支持该采样率。");
            return;
        }
        if (setting.Kind == "buffer")
        {
            if (device.DeviceCount != 1 || device.BufferRate == 0 || setting.Value <= 0 ||
                !device.BufferSizes.Contains((uint)setting.Value))
                throw new InvalidDataException("未确认缓冲能力，或当前连接了多个设备。");
            return;
        }
        if(setting.Kind=="safemode")
        {
            if(device.DeviceCount!=1||device.AsioFlags is null||device.BufferRate==0||device.BufferSize is null||setting.Value is not (0 or 1))
                throw new InvalidDataException("安全模式不可用。");
            return;
        }
        if(setting.Kind=="streammode")
        {
            if(device.Driver.StreamingMode is not (1 or 2)||setting.Value is not (1 or 2)||device.Driver.WdmClients!=0||device.Driver.AsioClients!=0)
                throw new InvalidDataException("音频流模式不可用，或音频软件正在使用设备。");
            return;
        }
        if(setting.Kind=="format")
        {
            if(setting.Direction!=0||device.Driver.FormatSelectionMode!=1||device.Driver.Dsd!=false||
                device.Driver.WdmClients!=0||device.Driver.AsioClients!=0||setting.Value<=0||!device.Driver.OutputFormats.Any(f=>f.Id==setting.Value))
                throw new InvalidDataException("输出格式不可用，或音频软件正在使用设备。");
            return;
        }
        var c = device.Channels.SingleOrDefault(c => c.Direction == setting.Direction && c.Number == setting.Channel)
            ?? throw new InvalidDataException("设备未确认该通道。");
        switch (setting.Kind)
        {
            case "volume":
                if (!c.CanVolume || c.Volume is null || c.Step == 0 || c.Minimum > c.Maximum ||
                    setting.Value < c.Minimum || setting.Value > c.Maximum || (setting.Value - c.Minimum) % c.Step != 0)
                    throw new InvalidDataException("音量超出设备范围或不符合步进。");
                break;
            case "mute":
                if (!c.CanMute || c.Muted is null || setting.Value is not (0 or 1))
                    throw new InvalidDataException("设备不支持该静音操作。");
                break;
            default: throw new InvalidDataException("未知设置类型。");
        }
    }
    public static Setting[] VolumeChanges(DeviceSnapshot device, uint direction, uint channel, short value, bool linked)
    {
        var changes = linked && direction == 0
            ? new[] { new Setting("volume",0,1,value), new Setting("volume",0,2,value) }
            : [new Setting("volume",direction,channel,value)];
        foreach (var change in changes) Validate(device,change);
        return changes;
    }
    public static void ValidatePreset(DeviceSnapshot device, Preset preset)
    {
        ValidateFormat(preset);
        if (preset.Demo != device.Demo || preset.DeviceId != device.DeviceId)
            throw new InvalidDataException("预设设备或演示模式与当前设备不一致。");
        foreach (var setting in preset.Settings) Validate(device,setting);
    }
    public static void ValidateFormat(Preset preset)
    {
        if (preset.Version != 1 || string.IsNullOrWhiteSpace(preset.DeviceId) || preset.Settings is null ||
            preset.Settings.Length > 32 || preset.Settings.Any(s => s is null ||
            s.Kind is not ("rate" or "buffer" or "volume" or "mute" or "safemode" or "streammode" or "format")))
            throw new InvalidDataException("预设格式不受支持。");
        ValidateName(preset.Name);
        if (preset.Settings.Select(s => $"{s.Kind}:{s.Direction}:{s.Channel}").Distinct().Count() != preset.Settings.Length)
            throw new InvalidDataException("预设包含重复配置项。");
    }
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 60 || name != name.Trim() ||
            name.Contains('/') || name.Contains('\\') || name.Contains("..") ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.'))
            throw new InvalidDataException("名称须为 1–60 个字符，不能包含路径或非法字符。");
    }
}
public sealed class PresetStore
{
    private readonly string directory;
    public PresetStore(string directory) { this.directory=Path.GetFullPath(directory); Directory.CreateDirectory(this.directory); }
    private string FileName(string name)
    {
        ControlPolicy.ValidateName(name);
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)));
        return Path.Combine(directory,hash+".json");
    }
    public void Save(Preset preset)
    {
        ControlPolicy.ValidateFormat(preset);
        var file=FileName(preset.Name); var temporary=file+".tmp";
        File.WriteAllText(temporary,System.Text.Json.JsonSerializer.Serialize(preset, new System.Text.Json.JsonSerializerOptions {WriteIndented=true}));
        File.Move(temporary,file,true);
    }
    public Preset[] ReadAll()
    {
        var presets=ReadAvailable(out var errors);
        if(errors.Length>0)throw new InvalidDataException(string.Join(Environment.NewLine,errors));
        return presets;
    }
    public Preset[] ReadAvailable(out string[] errors)
    {
        var presets=new List<Preset>();
        var failures=new List<string>();
        foreach(var file in Directory.EnumerateFiles(directory,"*.json"))
        {
            try
            {
                if(new FileInfo(file).Length>65536) throw new InvalidDataException("预设文件过大。");
                var preset=System.Text.Json.JsonSerializer.Deserialize<Preset>(File.ReadAllText(file))
                    ?? throw new InvalidDataException("空预设文件。");
                ControlPolicy.ValidateFormat(preset); presets.Add(preset);
            }
            catch(Exception e) when(e is System.Text.Json.JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {failures.Add($"预设文件无法读取：{Path.GetFileName(file)} · {e.Message}");}
        }
        errors=failures.ToArray();
        return presets.OrderBy(p=>p.Name,StringComparer.Ordinal).ToArray();
    }
    public void Delete(string name) => File.Delete(FileName(name));
}

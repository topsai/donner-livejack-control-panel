namespace Donner.Core;

public sealed record ChannelState(uint Direction, uint Number, string Name, bool CanVolume, bool CanMute,
    short Minimum, short Maximum, ushort Step, short? Volume, bool? Muted);
public sealed record StreamFormat(uint Id,uint Bits,uint Channels,string Name);
public sealed record ClockInfo(uint Id,string Name,bool Locked,uint Rate);
public sealed record DriverState
{
    public StreamFormat? InputFormat {get;init;}
    public StreamFormat? OutputFormat {get;init;}
    public StreamFormat[] OutputFormats {get;init;}=[];
    public uint? FormatSelectionMode {get;init;}
    public ClockInfo? Clock {get;init;}
    public bool? Streaming {get;init;}
    public uint? WdmClients {get;init;}
    public uint? AsioClients {get;init;}
    public uint? StreamingMode {get;init;}
    public bool? Dsd {get;init;}
}
public sealed record DeviceSnapshot
{
    public bool Connected { get; init; }
    public bool Demo { get; init; }
    public string Status { get; init; } = "尚未检测设备";
    public string DeviceId { get; init; } = "";
    public string Name { get; init; } = "Donner Livejack";
    public string? Model { get; init; }
    public uint? InputLatencySamples { get; init; }
    public uint? OutputLatencySamples { get; init; }
    public uint? LatencySampleRate { get; init; }
    public string ApiPath { get; init; } = "";
    public string ApiVersion { get; init; } = "";
    public uint DeviceCount { get; init; }
    public uint? SampleRate { get; init; }
    public uint[] SampleRates { get; init; } = [];
    public uint? BufferSize { get; init; }
    public uint BufferRate { get; init; }
    public uint[] BufferSizes { get; init; } = [];
    public uint? AsioFlags {get;init;}
    public bool? SafeMode => AsioFlags is {} flags?(flags&0x10000)!=0:null;
    public DriverState Driver {get;init;}=new();
    public ChannelState[] Channels { get; init; } = [];
    public Dictionary<string,string> Diagnostics { get; init; } = [];
}
public sealed record Setting(string Kind, uint Direction = 0, uint Channel = 0, int Value = 0);
public sealed record Preset(int Version, string Name, string DeviceId, bool Demo, bool Linked, Setting[] Settings);
public sealed record OperationResult(string Operation, bool Success, string Message);
public sealed record HostRequest(string Id, string Operation, string? ApiPath = null, Setting[]? Settings = null);
public sealed record HostReply(string Id, bool Success, string Message, DeviceSnapshot? Snapshot = null,
    OperationResult[]? Results = null);

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Donner.Core;

public sealed record ReleaseUpdate(Version Version,string Tag,string Notes)
{
    public Uri Page=>new("https://github.com/topsai/donner-livejack-control-panel/releases/tag/"+Uri.EscapeDataString(Tag));
    private static Version Normalize(Version version)=>new(version.Major,version.Minor,Math.Max(0,version.Build),Math.Max(0,version.Revision));
    public bool IsNewerThan(Version current)=>Normalize(Version)>Normalize(current);
    public static ReleaseUpdate? Parse(string json)
    {
        using var document=JsonDocument.Parse(json);var root=document.RootElement;
        if(root.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("更新信息格式无效。");
        bool Flag(string key)=>root.TryGetProperty(key,out var flag)&&flag.ValueKind is JsonValueKind.True or JsonValueKind.False?flag.GetBoolean():throw new InvalidDataException("更新信息缺少发布状态。");
        if(Flag("draft")||Flag("prerelease"))return null;
        var tag=root.TryGetProperty("tag_name",out var name)&&name.ValueKind==JsonValueKind.String?name.GetString()??"":"";
        if(!Regex.IsMatch(tag,@"^[vV]?\d+\.\d+(?:\.\d+){0,2}$")||!System.Version.TryParse(tag.TrimStart('v','V'),out var version))throw new InvalidDataException("无法识别发布版本号。");
        var notes=root.TryGetProperty("body",out var body)&&body.ValueKind==JsonValueKind.String?body.GetString()??"":"";
        return new(version,tag,notes.Length>2000?notes[..2000]+"…":notes);
    }
}

public sealed class GitHubUpdateClient(HttpClient client)
{
    public const string LatestApi="https://api.github.com/repos/topsai/donner-livejack-control-panel/releases/latest";
    public async Task<ReleaseUpdate?> CheckAsync(CancellationToken cancellation=default)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,LatestApi);
        request.Headers.UserAgent.ParseAdd("DonnerControlPanel");request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response=await client.SendAsync(request,cancellation);
        if(response.StatusCode==HttpStatusCode.NotFound)return null;
        response.EnsureSuccessStatusCode();return ReleaseUpdate.Parse(await response.Content.ReadAsStringAsync(cancellation));
    }
}

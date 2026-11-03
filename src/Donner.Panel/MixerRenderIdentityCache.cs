using System.Collections.Immutable;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Donner.Panel;

// This cache owns only Windows endpoint metadata, never a window, session or Voicemeeter client.
// A worker finishing after the window closes can therefore only publish immutable metadata.
internal sealed class MixerRenderIdentityCache
{
    internal sealed record Endpoint(string Id,string Name,string Descriptor,bool Target);
    internal sealed record Inventory(ImmutableArray<Endpoint> Endpoints,long ReadStarted);
    private readonly object sync=new();
    private readonly Func<ImmutableArray<Endpoint>> read;
    private readonly Func<long> clock;
    private Task? inFlight;
    private Inventory? inventory;
    private long nextRefresh;
    internal const long RefreshMilliseconds=5000;
    internal const long FreshMilliseconds=10000;

    internal MixerRenderIdentityCache(Func<ImmutableArray<Endpoint>>? read=null,Func<long>? clock=null)
    {this.read=read??ReadWindows;this.clock=clock??(()=>Environment.TickCount64);}

    internal Inventory? Current
    {
        get
        {
            lock(sync)
            {
                var age=inventory is null?long.MaxValue:clock()-inventory.ReadStarted;
                return age is >=0 and <=FreshMilliseconds?inventory:null;
            }
        }
    }

    // Called only on demand by the home meter path; there is no timer or recurring hidden-page work.
    internal Task RefreshAsync()
    {
        lock(sync)
        {
            if(inFlight is {IsCompleted:false})return inFlight;
            long now=clock();
            if(now<nextRefresh)return Task.CompletedTask;
            nextRefresh=now+RefreshMilliseconds;
            return inFlight=Task.Run(()=>
            {
                Inventory? result=null;
                try{result=new(read(),now);}
                catch(Exception error) when(error is IOException or COMException or InvalidOperationException or UnauthorizedAccessException){}
                lock(sync)inventory=result;
            });
        }
    }

    internal static bool Targets(Inventory inventory,string name,uint deviceCount)
    {
        if(string.IsNullOrWhiteSpace(name))return false;
        var endpoints=inventory.Endpoints;
        // MME truncates names to 31 characters. Reject all ambiguous aliases, including aliases
        // shared by a physical Livejack and another adapter or by two active endpoint identities.
        var matches=endpoints.Where(e=>e.Name.Equals(name,StringComparison.OrdinalIgnoreCase)
            ||e.Descriptor.Equals(name,StringComparison.OrdinalIgnoreCase)
            ||name.Length==31&&(e.Name.StartsWith(name,StringComparison.OrdinalIgnoreCase)
                ||e.Descriptor.StartsWith(name,StringComparison.OrdinalIgnoreCase))).ToArray();
        if(matches.Length>0)return matches.Length==1&&matches[0].Target;
        return name.Equals("Donner USB Audio Device",StringComparison.OrdinalIgnoreCase)
            &&deviceCount==1&&endpoints.Count(e=>e.Target)==1;
    }

    private static ImmutableArray<Endpoint> ReadWindows()
    {
        var result=ImmutableArray.CreateBuilder<Endpoint>();
        using var enumerator=new MMDeviceEnumerator();
        foreach(var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active))
            using(endpoint)result.Add(new(endpoint.ID,endpoint.FriendlyName,
                endpoint.Properties[PropertyKeys.PKEY_Device_DeviceDesc].Value as string??"",
                endpoint.DeviceFriendlyName.Contains("Livejack",StringComparison.OrdinalIgnoreCase)));
        return result.ToImmutable();
    }
}

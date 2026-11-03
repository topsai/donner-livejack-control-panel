using System.Collections.Immutable;
using Donner.Core;

namespace Donner.Panel;

// Pure fixtures: no Windows reads, SDK calls, audio configuration writes or engine starts.
internal static class MixerHomeMetersSmoke
{
    internal static async Task VerifyAsync()
    {
        void Assert(bool condition,string message)
        {if(!condition)throw new InvalidDataException("Home meter cache: "+message);}
        var live=new MixerRenderIdentityCache.Endpoint("live","我的监听 (Livejack Audio Interface)","我的监听",true);
        var other=new MixerRenderIdentityCache.Endpoint("other","Speakers (Other Adapter)","Speakers",false);
        MixerRenderIdentityCache.Inventory Inventory(params MixerRenderIdentityCache.Endpoint[] endpoints)=>new([..endpoints],0);
        bool Target(MixerRenderIdentityCache.Inventory inventory,string name,uint count=1)=>MixerRenderIdentityCache.Targets(inventory,name,count);
        var identity=Inventory(live,other);
        Assert(Target(identity,live.Name)&&Target(identity,live.Descriptor),"custom display/descriptor must retain adapter identity");
        Assert(!Target(identity,other.Name)&&!Target(identity,"Livejack"),"unknown and unrelated names must be rejected");
        Assert(!Target(Inventory(live,other with{Descriptor=live.Descriptor}),live.Descriptor),"duplicate descriptors must be rejected");
        Assert(!Target(Inventory(live,live with{Id="second"}),live.Name),"distinct duplicate endpoint identities must be rejected");
        var longName=new string('x',31)+" (Livejack Audio Interface)";
        Assert(Target(Inventory(live with{Name=longName}),longName[..31]),"MME 31-character alias must match");
        Assert(!Target(Inventory(live with{Name=longName},other with{Name=longName+" other"}),longName[..31]),"ambiguous truncated names must be rejected");
        Assert(Target(identity,"Donner USB Audio Device"),"ASIO fallback needs one active physical Livejack");
        Assert(!Target(Inventory(other),"Donner USB Audio Device")&&!Target(identity,"Donner USB Audio Device",2),"ASIO fallback must reject disconnected/multiple hardware");
        Assert(!Target(Inventory(live,other with{Name="Donner USB Audio Device"}),"Donner USB Audio Device"),"a conflicting Windows alias must defeat fallback");
        Assert(MixerMeterRouting.UniqueBus([live.Name,"",""],name=>Target(identity,name))==0,"unique output bus");
        Assert(MixerMeterRouting.UniqueBus([live.Name,live.Descriptor,""],name=>Target(identity,name))==-1,"duplicate buses must be rejected");

        long now=0;int reads=0,active=0,maximum=0;
        using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ImmutableArray<MixerRenderIdentityCache.Endpoint> endpoints=[live,other];bool fail=false;
        var cache=new MixerRenderIdentityCache(()=>
        {
            reads++;maximum=Math.Max(maximum,Interlocked.Increment(ref active));entered.TrySetResult();
            try{if(!release.Wait(TimeSpan.FromSeconds(10)))throw new IOException("fixture timeout");if(fail)throw new IOException("fixture unavailable");return endpoints;}
            finally{Interlocked.Decrement(ref active);}
        },()=>now);
        Assert(reads==0&&cache.Current is null,"constructor must not enumerate endpoints");
        var pending=cache.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for(int i=0;i<200;i++)Assert(ReferenceEquals(pending,cache.RefreshAsync())&&cache.Current is null,"refresh must remain single-flight and nonblocking");
            // Yielding here exercises the caller's dispatcher while the fake Windows reader is blocked.
            await Task.Yield();Assert(!pending.IsCompleted&&reads==1&&maximum==1,"caller must progress independently of the reader");
        }
        finally{release.Set();}
        await pending;
        Assert(cache.Current is {} first&&Target(first,live.Descriptor),"completed immutable inventory must publish");
        await cache.RefreshAsync();Assert(reads==1,"fresh cache must throttle refreshes");
        now=10001;Assert(cache.Current is null,"stale inventory must be unavailable");
        endpoints=[other];await cache.RefreshAsync();
        Assert(cache.Current is {} unplugged&&!Target(unplugged,live.Name),"disconnect must invalidate target route");
        now+=5000;endpoints=[live with{Name="新监听",Descriptor="新监听"}];await cache.RefreshAsync();
        Assert(cache.Current is {} renamed&&Target(renamed,"新监听")&&!Target(renamed,live.Name),"reconnect/custom rename must replace old aliases");
        now+=5000;fail=true;await cache.RefreshAsync();
        Assert(cache.Current is null&&maximum==1,"failed read must clear cached identities without overlap");
    }
}

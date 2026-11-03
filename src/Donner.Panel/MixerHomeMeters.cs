using Donner.Core;

namespace Donner.Panel;
public partial class MainWindow
{
    private bool meterMixerOutput;
    private int mixerOutputBus=-1;
    private long nextMixerMeterRoute;
    private readonly MixerRenderIdentityCache mixerRenderIdentities=new();
    private MixerRenderIdentityCache.Inventory? mixerRouteIdentity;
    // Smoke callers may await the first inventory without moving any SDK calls off the dispatcher.
    internal Task RefreshMixerHomeOutputIdentityAsync()=>mixerRenderIdentities.RefreshAsync();
    private bool ReadMixerHomeOutput(out bool available,out float left,out float right)
    {
        available=false;left=right=0;
        var api=mixerPanel?.Client;
        if(!mixerEnabled||api?.Connected!=true){mixerOutputBus=-1;nextMixerMeterRoute=0;return false;}
        try
        {
            _=mixerRenderIdentities.RefreshAsync();
            var identities=mixerRenderIdentities.Current;
            if(identities is null){mixerOutputBus=-1;nextMixerMeterRoute=0;return false;}
            if(!ReferenceEquals(mixerRouteIdentity,identities)){mixerRouteIdentity=identities;nextMixerMeterRoute=0;}
            if(Environment.TickCount64>=nextMixerMeterRoute)
            {
                nextMixerMeterRoute=Environment.TickCount64+500;
                // DeviceName/GetLevel remain on the SDK's owning UI thread. Native device enumeration
                // is unnecessary when Windows names identify a unique active physical endpoint.
                mixerOutputBus=MixerMeterRouting.UniqueBus(Enumerable.Range(0,3).Select(i=>api.DeviceName(false,i)).ToArray(),
                    name=>MixerRenderIdentityCache.Targets(identities,name,session.Snapshot.DeviceCount));
            }
            if(mixerOutputBus<0)return false;
            left=api.ChannelLevel(false,mixerOutputBus,0);right=api.ChannelLevel(false,mixerOutputBus,1);available=true;
            return true;
        }
        catch(Exception error) when(error is IOException or InvalidDataException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {return mixerOutputBus>=0;}
    }
}

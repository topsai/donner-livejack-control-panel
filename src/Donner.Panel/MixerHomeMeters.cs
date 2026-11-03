using Donner.Core;
using NAudio.CoreAudioApi;

namespace Donner.Panel;
public partial class MainWindow
{
    private bool meterMixerOutput;
    private int mixerOutputBus=-1;
    private long nextMixerMeterRoute;
    private bool ReadMixerHomeOutput(out bool available,out float left,out float right)
    {
        available=false;left=right=0;
        var api=mixerPanel?.Client;
        if(!mixerEnabled||api?.Connected!=true){mixerOutputBus=-1;nextMixerMeterRoute=0;return false;}
        try
        {
            if(Environment.TickCount64>=nextMixerMeterRoute)
            {
                nextMixerMeterRoute=Environment.TickCount64+500;
                var endpoints=new List<(string Id,string Name,string Descriptor,bool Target)>();
                using(var enumerator=new MMDeviceEnumerator())
                    foreach(var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active))
                        using(endpoint)endpoints.Add((endpoint.ID,endpoint.FriendlyName,endpoint.Properties[PropertyKeys.PKEY_Device_DeviceDesc].Value as string??"",endpoint.DeviceFriendlyName.Contains("Livejack",StringComparison.OrdinalIgnoreCase)));
                var native=api.Devices(false);
                bool Targets(string name)
                {
                    if(name.Equals("Donner USB Audio Device",StringComparison.OrdinalIgnoreCase)&&session.Snapshot.DeviceCount==1)return true;
                    var described=native.Where(x=>x.Name==name).ToArray();
                    var identified=endpoints.Where(e=>described.Any(x=>e.Id.Equals(x.Id,StringComparison.OrdinalIgnoreCase))).ToArray();
                    // MME descriptions can truncate to 31 characters. Require a unique endpoint match.
                    var matches=endpoints.Where(e=>e.Name==name||e.Descriptor==name||name.Length>=31&&e.Name.StartsWith(name,StringComparison.Ordinal)).ToArray();
                    if(identified.Length>1||matches.Length>1)return false;
                    if(identified.Length==1)return identified[0].Target&&(matches.Length==0||matches[0].Id==identified[0].Id);
                    return matches.Length==1&&matches[0].Target;
                }
                mixerOutputBus=MixerMeterRouting.UniqueBus(Enumerable.Range(0,3).Select(i=>api.DeviceName(false,i)).ToArray(),Targets);
            }
            if(mixerOutputBus<0)return false;
            left=api.ChannelLevel(false,mixerOutputBus,0);right=api.ChannelLevel(false,mixerOutputBus,1);available=true;
            return true;
        }
        catch(Exception error) when(error is IOException or InvalidDataException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {return mixerOutputBus>=0;}
    }
}

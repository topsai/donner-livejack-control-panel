using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
namespace Donner.Panel;
public partial class MainWindow
{
    private async Task VerifyNativeRateResponsiveness(MixerSampleRates rates)
    {
        var clock=Stopwatch.StartNew();var last=clock.Elapsed.TotalMilliseconds;var gaps=new List<double>();
        var heartbeat=new DispatcherTimer(DispatcherPriority.Input){Interval=TimeSpan.FromMilliseconds(10)};
        heartbeat.Tick+=(_,_)=>{var now=clock.Elapsed.TotalMilliseconds;gaps.Add(now-last);last=now;};
        heartbeat.Start();
        try
        {
            await Task.Delay(50);var query=Stopwatch.StartNew();await rates.RefreshAsync();query.Stop();await Task.Delay(40);
            var ordered=gaps.Order().ToArray();var maximum=ordered.LastOrDefault();
            File.WriteAllText(Path.Combine(smokeDirectory!,"ui-responsiveness.json"),JsonSerializer.Serialize(new{WindowsQueryMilliseconds=query.Elapsed.TotalMilliseconds,DispatcherTicks=gaps.Count,MaximumDispatcherGapMilliseconds=maximum,P95DispatcherGapMilliseconds=ordered.Length==0?0:ordered[(int)((ordered.Length-1)*.95)]},new JsonSerializerOptions{WriteIndented=true}));
            if(query.ElapsedMilliseconds>1000&&(gaps.Count<10||maximum>500))throw new IOException("Windows 慢查询仍阻塞了界面调度。");
        }
        finally{heartbeat.Stop();}
    }
}

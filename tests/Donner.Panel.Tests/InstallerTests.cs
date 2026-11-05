using System.ComponentModel;
using System.Diagnostics;

namespace Donner.Panel;
internal static partial class Program
{
    private static void InstallerTests()
    {
        Test("下载取消后清理本次临时文件",()=>WorkspaceFailure(new OperationCanceledException("download cancelled")));
        Test("签名校验失败后清理下载和解压文件",()=>WorkspaceFailure(new IOException("signature invalid")));
        Test("安装器启动失败后清理临时文件",()=>WorkspaceFailure(new Win32Exception("launch failed")));
        Test("任务开始前取消也不遗留临时目录",()=>
        {
            using var cancellation=new CancellationTokenSource();cancellation.Cancel();
            bool called=false;
            try{Task.Run(()=>InstallerWorkspace.RunAsync((_,_)=>{called=true;return Task.CompletedTask;},cancellation.Token)).GetAwaiter().GetResult();throw new Exception("Cancellation was ignored");}
            catch(OperationCanceledException){}
            Check(!called,"Cancelled preparation was executed");
        });
        Test("运行中的安装器保留文件，退出后清理且不影响旁边文件",RunningInstaller);
    }
    private static void WorkspaceFailure(Exception expected)
    {
        using var fixture=new Fixture();string unrelated=Path.Combine(fixture.Directory,"unrelated.txt");File.WriteAllText(unrelated,"keep");
        string? attempt=null;
        try
        {
            Task.Run(()=>InstallerWorkspace.RunAsync((directory,_)=>
            {
                attempt=directory;File.WriteAllText(Path.Combine(directory,"VoicemeeterSetup.zip"),"fixture");
                Directory.CreateDirectory(Path.Combine(directory,"nested"));File.WriteAllText(Path.Combine(directory,"nested","setup.exe"),"fixture");
                return Task.FromException(expected);
            })).GetAwaiter().GetResult();
            throw new Exception("Failure was swallowed");
        }
        catch(Exception actual) when(ReferenceEquals(actual,expected)){}
        Check(attempt is not null&&!Directory.Exists(attempt),"Attempt files remained after failure/cancellation");
        Check(File.ReadAllText(unrelated)=="keep","Cleanup touched unrelated data");
    }
    private static void RunningInstaller()
    {
        using var fixture=new Fixture();string release=Path.Combine(fixture.Directory,"release.txt");
        var started=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation=new CancellationTokenSource();
        var cleanup=Task.Run(()=>InstallerWorkspace.RunAsync(async(directory,token)=>
        {
            string payload=Path.Combine(directory,"VoicemeeterSetup.zip");File.WriteAllText(payload,"fixture");
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
            start.ArgumentList.Add("--installer-worker");start.ArgumentList.Add(release);
            using var process=Process.Start(start)??throw new IOException("Cannot start installer fixture");
            started.TrySetResult(directory);
            await process.WaitForExitAsync(); // Match the production lifetime: never cancel an active installer wait.
            Check(process.ExitCode==0,"Installer fixture did not exit normally");
        },cancellation.Token));
        string? workspace=null;
        try
        {
            workspace=started.Task.WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
            cancellation.Cancel();
            Check(File.Exists(Path.Combine(workspace,"VoicemeeterSetup.zip")),"Deleted files while installer was running");
            Check(!cleanup.IsCompleted,"Installer wait ended before the process exited");
        }
        finally{File.WriteAllText(release,"exit");cleanup.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();}
        Check(workspace is not null&&!Directory.Exists(workspace),"Installer files remained after process exit");
        Check(File.Exists(release),"Cleanup deleted a sibling directory");
    }
}

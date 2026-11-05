namespace Donner.Panel;

// Each attempt owns exactly one newly created directory. Never clean by a broad temp prefix.
internal static class InstallerWorkspace
{
    internal static async Task RunAsync(Func<string,CancellationToken,Task> action,CancellationToken cancellation=default)
    {
        string directory=Path.Combine(Path.GetTempPath(),"Donner-Banana-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            await action(directory,cancellation);
        }
        finally{await CleanupAsync(directory);}
    }
    private static async Task CleanupAsync(string directory)
    {
        // Only this invocation's generated direct child of TEMP may be recursively removed.
        string full=Path.GetFullPath(directory);
        string root=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        string name=Path.GetFileName(full);
        if(!string.Equals(Path.GetDirectoryName(full),root,StringComparison.OrdinalIgnoreCase)
            ||!name.StartsWith("Donner-Banana-",StringComparison.Ordinal)||!Guid.TryParseExact(name[14..],"N",out _))
            throw new IOException("拒绝清理非本次安装创建的临时目录。");
        for(int attempt=0;;attempt++)
        {
            try
            {
                if(!Directory.Exists(full))return;
                if((File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new IOException("安装临时目录已变成目录链接，未执行清理。");
                Directory.Delete(full,true);return;
            }
            catch(Exception error) when((error is IOException or UnauthorizedAccessException)&&attempt<9)
            {await Task.Delay(100);}
        }
    }
}

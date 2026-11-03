namespace Donner.Panel;

// The source factory and cleanup can block in Windows audio drivers. Neither runs on the caller.
internal sealed class BackgroundMeterSource<T>(Func<T?> open,Func<T,bool> available):IDisposable where T:class,IDisposable
{
    private readonly object sync=new();
    private T? source;
    private bool disposed;
    private Task pending=Task.CompletedTask;
    private string? error;
    internal T? Current {get{lock(sync)return source;}}
    internal string? Error {get{lock(sync)return error;}}
    internal Task Pending {get{lock(sync)return pending;}}
    internal void Request()
    {
        lock(sync)
        {
            if(disposed||!pending.IsCompleted||source is {} current&&available(current))return;
            var previous=source;source=null;
            pending=Task.Run(()=>
            {
                Release(previous);
                T? created=null;
                try
                {
                    lock(sync){if(disposed)return;}
                    created=open();
                    lock(sync)
                    {
                        if(!disposed){source=created;created=null;error=null;}
                    }
                }
                catch(Exception failure){lock(sync)error=failure.Message;}
                finally{Release(created);}
            });
        }
    }
    private void Release(T? item)
    {
        try{item?.Dispose();}
        catch(Exception failure){lock(sync)error=failure.Message;}
    }
    public void Dispose()
    {
        lock(sync)
        {
            if(disposed)return;disposed=true;var previous=source;source=null;
            if(previous is not null)pending=Task.WhenAll(pending,Task.Run(()=>Release(previous)));
        }
    }
}

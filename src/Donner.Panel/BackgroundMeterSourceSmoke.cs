namespace Donner.Panel;

internal static class BackgroundMeterSourceSmoke
{
    private sealed class FakeSource:IDisposable
    {
        internal bool Available=true;
        internal int Disposals;
        internal Action? OnDispose;
        public void Dispose(){Available=false;Interlocked.Increment(ref Disposals);OnDispose?.Invoke();}
    }
    internal static async Task VerifyAsync()
    {
        void Assert(bool condition,string message)
        {if(!condition)throw new InvalidDataException("Background meter source: "+message);}
        using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source=new FakeSource();int opens=0,readerThread=0;
        using var slot=new BackgroundMeterSource<FakeSource>(()=>
        {
            readerThread=Environment.CurrentManagedThreadId;Interlocked.Increment(ref opens);entered.TrySetResult();
            if(!release.Wait(TimeSpan.FromSeconds(10)))throw new IOException("fixture timeout");return source;
        },s=>s.Available);
        Assert(opens==0,"construction must not open audio");
        int callerThread=Environment.CurrentManagedThreadId;slot.Request();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for(int i=0;i<200;i++)slot.Request();
            await Task.Yield();Assert(opens==1&&slot.Current is null&&!slot.Pending.IsCompleted,"slow open must allow caller progress without overlap");
            // WPF smoke invokes this on its dispatcher, so its reader must run elsewhere.
            if(SynchronizationContext.Current is not null)Assert(readerThread!=callerThread,"reader must leave dispatcher");
            slot.Dispose();Assert(slot.Current is null&&!slot.Pending.IsCompleted,"disposal must not wait for open");
        }
        finally{release.Set();}
        await slot.Pending;
        Assert(slot.Current is null&&source.Disposals==1,"late source must be disposed once without publication");
        slot.Request();Assert(opens==1,"disposed slot must never reopen");

        var first=new FakeSource();var second=new FakeSource();opens=0;
        using var retry=new BackgroundMeterSource<FakeSource>(()=>++opens==1?first:second,s=>s.Available);
        retry.Request();await retry.Pending;Assert(ReferenceEquals(retry.Current,first),"ready source must publish");
        retry.Request();Assert(opens==1,"healthy source must not reopen");
        first.Available=false;retry.Request();await retry.Pending;
        Assert(first.Disposals==1&&ReferenceEquals(retry.Current,second)&&opens==2,"retry must dispose previous before replacing");
        using var disposeRelease=new ManualResetEventSlim();
        second.OnDispose=()=>{if(!disposeRelease.Wait(TimeSpan.FromSeconds(10)))throw new IOException("fixture timeout");};
        retry.Dispose();
        try{Assert(retry.Current is null,"disposal must clear the published source immediately");await Task.Yield();}
        finally{disposeRelease.Set();}
        await retry.Pending;Assert(second.Disposals==1,"published source must dispose once");
        using var failed=new BackgroundMeterSource<FakeSource>(()=>throw new IOException("expected fixture failure"),s=>s.Available);
        failed.Request();await failed.Pending;Assert(failed.Current is null&&failed.Error=="expected fixture failure","background failure must be observed");
    }
}

using Donner.Panel;
using System.Reflection;

static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
Check(UninstallShutdown.EventName(@"C:\One\app.exe","user1")==UninstallShutdown.EventName(@"c:\one\APP.exe","user1"),"Path case differs");
Check(UninstallShutdown.EventName(@"C:\One\app.exe","user1")!=UninstallShutdown.EventName(@"C:\Two\app.exe","user1"),"Another install shares event");
Check(UninstallShutdown.EventName(@"C:\One\app.exe","user1")!=UninstallShutdown.EventName(@"C:\One\app.exe","user2"),"Another user shares event");
Check(UninstallShutdown.Request()==0,"No running instance should succeed");
int closed=0;
UninstallShutdown? listener=null;
try
{
    listener=new(()=>{Interlocked.Increment(ref closed);listener!.Dispose();});
    Check(await Task.Run(UninstallShutdown.Request)==0,"Close acknowledgement failed");
    Check(closed==1,"Close must be requested exactly once");
    listener=null;
}
finally{listener?.Dispose();}
closed=0;
try
{
    listener=new(()=>{if(Interlocked.Increment(ref closed)==2)listener!.Dispose();});
    // First close is cancelled (for example, while a configuration file is locked).
    var request=(EventWaitHandle)typeof(UninstallShutdown).GetField("request",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(listener)!;
    request.Set();Check(SpinWait.SpinUntil(()=>Volatile.Read(ref closed)==1,2000),"Initial close was not requested");
    Check(await Task.Run(UninstallShutdown.Request)==0,"A cancelled close must allow another uninstall request");
    Check(closed==2,"Retry must request closing again");listener=null;
}
finally{listener?.Dispose();}
Console.WriteLine("6 uninstall shutdown checks passed; cancelled close can be retried without terminating the panel.");

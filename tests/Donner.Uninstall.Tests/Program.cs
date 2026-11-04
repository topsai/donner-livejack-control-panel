using Donner.Panel;

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
Console.WriteLine("5 uninstall shutdown checks passed; path/user isolation and graceful close confirmed.");

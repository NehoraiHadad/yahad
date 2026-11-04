using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;

// The real Store and data-lease handoff behind the real launcher. The fixed
// locations and SIDs of an installed machine are covered by machine acceptance.
internal static class StoreIntegrationTests
{
    private static void Require(bool value,string code="assertion failed"){if(!value)throw new Exception(code);}
    private static async Task Refused(Func<Task> run,string code)
    {
        try{await run();}catch(IOException error)when(error.Message.StartsWith(code,StringComparison.Ordinal)){return;}
        throw new Exception("expected refusal "+code);
    }
    private static async Task<ServiceFailure> RunOnce(StoreFixture f)
    {
        var controller=new ServiceController(new StoreStart(f));var ran=false;
        var result=await controller.RunAsync(r=>{if(r.State==ServiceState.Running){ran=true;controller.Control(ServiceDefinition.Stop);}});
        Require(result.Failure!=ServiceFailure.None||(ran&&result.QuiescenceConfirmed));return result.Failure;
    }
    internal static async Task RunAsync(Func<string,Func<StoreFixture,Task>,Task> check,string node)
    {
        await check("real Store first start on empty data creates the current schema",async f=>
        {
            var database=Path.Combine(f.Data,"app.sqlite");File.Delete(database);f.ClearSignals();
            Require(await RunOnce(f)==ServiceFailure.None&&File.Exists(database));Require(await f.ReadSchema(node)==f.Schema);
        });
        await check("real Store runs through the service lifecycle, refuses inherited hooks and holds the lease until exit",async f=>
        {
            f.ClearSignals();var previousNode=Environment.GetEnvironmentVariable("NODE_OPTIONS");var previousHooks=Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS");ServiceFailure failure;
            try
            {
                Environment.SetEnvironmentVariable("NODE_OPTIONS","--require refused-host-hook");Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS","refused-host-hook");
                failure=await RunOnce(f);
            }
            finally{Environment.SetEnvironmentVariable("NODE_OPTIONS",previousNode);Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS",previousHooks);}
            Require(failure==ServiceFailure.None&&f.Control.HeldAfterClose);using var released=WindowsFileBoundary.AcquireDataLease(f.Data);Require(File.Exists(Path.Combine(f.Data,"native-closed")));
        });
        await check("child duplicate prevents a second writer even after host lease handle is closed",async f=>
        {
            f.ClearSignals();var lease=WindowsFileBoundary.AcquireDataLease(f.Data);var launcher=f.CreateLauncher(f.RawSpec(lease));IContainedAppSession? session=null;
            try
            {
                session=await launcher.StartAsync(default);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));await launcher.WaitForHealthyAsync(session,deadline.Token);lease.Dispose();
                try{using var wrong=WindowsFileBoundary.AcquireDataLease(f.Data);throw new Exception("second writer entered");}catch(Win32Exception e)when(e.NativeErrorCode==32){}
                await session.RequestGracefulStopAsync(deadline.Token);await session.WaitForRootExitAsync(deadline.Token);await session.WaitForTreeEmptyAsync(deadline.Token);Require(f.Control.HeldAfterClose);
            }
            finally{lease.Dispose();if(session is not null){using var clean=new CancellationTokenSource(TimeSpan.FromSeconds(5));await session.TerminateAndWaitForTreeEmptyAsync(clean.Token);await session.DisposeAsync();}}
            using var final=WindowsFileBoundary.AcquireDataLease(f.Data);
        });
        await check("wrong host-lease nonce cannot authorize Store",async f=>
        {
            using var lease=WindowsFileBoundary.AcquireDataLease(f.Data);var descriptor=f.PathOf("bad-lease.json");
            File.WriteAllText(descriptor,JsonSerializer.Serialize(new HostLeaseGrant(1,Environment.ProcessId,4,f.Data,new string('a',64))));
            await Refused(()=>{WindowsFileBoundary.ValidateHostLease(descriptor,f.Data,Environment.ProcessId,new string('b',64));return Task.CompletedTask;},"NATIVE_HANDOFF_IDENTITY");
        });
        await check("callback failure after duplicating the data lease observes root death before returning",async f=>
        {
            using var lease=WindowsFileBoundary.AcquireDataLease(f.Data);System.Diagnostics.Process? witness=null;var marker=f.PathOf("callback-child-ran");
            try
            {
                await Refused(()=>
                {
                    _=f.StartJob(node,["-e","require('node:fs').writeFileSync(process.argv[1],'ran')",marker],f.Root,LaunchEnvironment.Clean(f.Root),(process,pid)=>
                    {witness=System.Diagnostics.Process.GetProcessById(pid);_=witness.SafeHandle;_=lease.DuplicateTo(process);throw new IOException("INJECTED_CALLBACK_FAILURE");});
                    return Task.CompletedTask;
                },"INJECTED_CALLBACK_FAILURE");
                Require(witness is not null&&witness.WaitForExit(0)&&!File.Exists(marker));
            }
            finally{witness?.Dispose();}
        });
        await check("real Store recovers a crash-left WAL under the lease without losing the committed row",async f=>
        {
            f.ClearSignals();var database=Path.Combine(f.Data,"app.sqlite");var signal=f.PathOf("wal-ready");
            const string writer="const fs=require('node:fs'),{DatabaseSync}=require('node:sqlite');const db=new DatabaseSync(process.argv[1]);db.exec('PRAGMA journal_mode=WAL;CREATE TABLE IF NOT EXISTS crash_probe(value INTEGER);INSERT INTO crash_probe VALUES(73)');fs.writeFileSync(process.argv[2],'ready');setInterval(()=>{},1000)";
            using(var job=f.StartJob(node,["-e",writer,database,signal],f.Root,LaunchEnvironment.Clean(f.Root)))
            {using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(!File.Exists(signal))await Task.Delay(20,limit.Token);await job.TerminateAndWaitForTreeEmptyAsync(limit.Token);}
            Require(File.Exists(database+"-wal"),"crash left no WAL");
            Require(await RunOnce(f)==ServiceFailure.None);
            var output=f.PathOf("crash-probe.txt");const string reader="const{DatabaseSync}=require('node:sqlite'),fs=require('node:fs');const db=new DatabaseSync(process.argv[1],{readOnly:true});fs.writeFileSync(process.argv[2],String(db.prepare('SELECT value FROM crash_probe').get().value));db.close()";
            using(var job=f.StartJob(node,["-e",reader,database,output],f.Root,LaunchEnvironment.Clean(f.Root)))
            {using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(15));Require(await job.WaitAsync(limit.Token)==0);await job.WaitForTreeEmptyAsync(limit.Token);}
            Require(File.ReadAllText(output)=="73");
        });
    }
    private sealed class StoreStart(StoreFixture fixture):IServiceStartAuthorizer
    {
        public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)
        {
            var lease=WindowsFileBoundary.AcquireDataLease(fixture.Data);
            try{return Task.FromResult(new AuthorizedServiceStart(fixture.CreateLauncher(fixture.RawSpec(lease)),lease));}
            catch{lease.Dispose();throw;}
        }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;

if(args is not ["--node",var node,"--report",var report])throw new IOException("PRIVATE_NODE_AND_REPORT_REQUIRED");
node=Path.GetFullPath(node);var nodeHash=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(node)));
var root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());var checks=new List<object>();var failures=0;
void Assert(bool condition,string reason="assertion failed"){if(!condition)throw new Exception(reason);}
async Task Check(string name,Func<Task> test)
{
    try{await test();Console.WriteLine("PASS "+name);checks.Add(new{name,status="PASS"});}
    catch(Exception e){failures++;Console.WriteLine("FAIL "+name+" "+e);checks.Add(new{name,status="FAIL",reason=e.GetType().Name});}
}
async Task Reject(Func<Task> action)
{try{await action();}catch(Exception e)when(e is ServiceStartDenied or IOException or UnauthorizedAccessException){return;}throw new Exception("expected rejection");}
string Session()=>SafeDirectory.CreatePrivateSession(root);
const string sid="S-1-5-80-1-2-3-4-5";
ServiceTokenObservation Good()=>new(sid,true,false,["SeChangeNotifyPrivilege"]);

await Check("SCM native status ABI is seven DWORDs",()=>{Assert(Marshal.SizeOf<NativeScmApi.NativeStatus>()==28);return Task.CompletedTask;});
await Check("actual current token observation cannot grant the acceptance service SID",()=>
{
    var observation=new WindowsServiceTokenReader().Read();
    try{ServiceIdentityPolicy.Require(observation,sid);throw new Exception("interactive token granted service");}catch(ServiceStartDenied denied){Assert(denied.Failure==ServiceFailure.IdentityDenied);}return Task.CompletedTask;
});
foreach(var token in new[]{Good() with{UserSid="S-1-5-18"},Good() with{ServiceLogon=false},Good() with{Administrator=true},Good() with{Privileges=["SeChangeNotifyPrivilege","SeDebugPrivilege"]},Good() with{Privileges=[]}})
await Check("identity rejects "+JsonSerializer.Serialize(token),()=>
{try{ServiceIdentityPolicy.Require(token,sid);throw new Exception("unsafe identity accepted");}catch(ServiceStartDenied){}return Task.CompletedTask;});
await Check("dedicated identity policy accepts only expected minimal observation",()=>{ServiceIdentityPolicy.Require(Good(),sid);return Task.CompletedTask;});
var descriptor=JsonSerializer.SerializeToUtf8Bytes(new{schema=1,service=ServiceDefinition.Name,installationId=new string('a',32),activationReceiptDigest=new string('b',64)});
await Check("bootstrap descriptor exact schema parses",()=>{Assert(WindowsInstallationReader.Parse(descriptor).InstallationId==new string('a',32));return Task.CompletedTask;});
foreach(var text in new[]{"{}","{\"schema\":1,\"schema\":1}",Encoding.UTF8.GetString(descriptor).Replace("Yahad.Acceptance","OtherService"),Encoding.UTF8.GetString(descriptor).Replace("\"schema\":1","\"schema\":1,\"executable\":\"evil.exe\"")})
await Check("bootstrap rejects malformed duplicate foreign or executable fields "+text.Length,()=>
{try{WindowsInstallationReader.Parse(Encoding.UTF8.GetBytes(text));throw new Exception("bad bootstrap accepted");}catch(IOException){}return Task.CompletedTask;});
await Check("identity denial occurs before bootstrap or activation access",async()=>
{
    var reader=new Reader();var resolver=new Resolver();var auth=new InstalledStartAuthorizer(new Tokens(Good() with{Administrator=true},sid),reader,resolver);
    await Reject(()=>auth.AuthorizeAsync(default));Assert(reader.Reads==0&&resolver.Calls==0);
});
await Check("bootstrap ACL/read failure cannot reach activation",async()=>
{
    var reader=new Reader{Denied=true};var resolver=new Resolver();var auth=new InstalledStartAuthorizer(new Tokens(Good(),sid),reader,resolver);
    try{await auth.AuthorizeAsync(default);throw new Exception("bootstrap failure accepted");}catch(ServiceStartDenied denied){Assert(denied.Failure==ServiceFailure.BootstrapUnavailable);}Assert(resolver.Calls==0);
});
await Check("default production activation resolver always fails closed",async()=>
{
    var auth=new InstalledStartAuthorizer(new Tokens(Good(),sid),new Reader(),new UnconfiguredActivationResolver());
    try{await auth.AuthorizeAsync(default);throw new Exception("unprovisioned activation accepted");}catch(ServiceStartDenied denied){Assert(denied.Failure==ServiceFailure.ActivationUnavailable);}
});
await Check("SCM application registers before first status and rejects extra start arguments",()=>
{
    var api=new FakeScm{Arguments=[ServiceDefinition.Name,"untrusted"]};var auth=new TestAuthorization(new FakeApp());var fatal=0;
    Assert(new ServiceApplication(api,auth,_=>fatal++).Run()==1);Assert(api.Calls.Take(3).SequenceEqual(new[]{"dispatch","register","status"}));
    Assert(auth.Calls==0&&fatal==0&&api.Reports.Single().ServiceSpecificExitCode==(uint)ServiceFailure.ArgumentsRejected);return Task.CompletedTask;
});
await Check("authorization failure never reports RUNNING or launches a child",async()=>
{
    var app=new FakeApp();var controller=new ServiceController(new TestAuthorization(app){Deny=true});var reports=new List<ServiceReport>();var result=await controller.RunAsync(reports.Add);
    Assert(result.QuiescenceConfirmed&&result.Failure==ServiceFailure.IdentityDenied&&app.Calls.Count==0&&!reports.Any(r=>r.State==ServiceState.Running));Assert(reports[^1].Win32ExitCode==1066);
});
await Check("stop during authorization cancels startup without blocking handler",async()=>
{
    var app=new FakeApp();var auth=new TestAuthorization(app){Wait=true};var controller=new ServiceController(auth);var reports=new List<ServiceReport>();var running=controller.RunAsync(reports.Add);
    await auth.Entered.Task;var watch=Stopwatch.StartNew();Assert(controller.Control(ServiceDefinition.Stop)==0);Assert(watch.ElapsedMilliseconds<100);
    var result=await running.WaitAsync(TimeSpan.FromSeconds(3));Assert(result.Failure==ServiceFailure.None&&app.Calls.Count==0&&!reports.Any(r=>r.State==ServiceState.Running));
});
await Check("recovery barrier denies launch before process creation",async()=>
{
    var app=new FakeApp{DenyRecovery=true};var reports=new List<ServiceReport>();var result=await new ServiceController(new TestAuthorization(app)).RunAsync(reports.Add);
    Assert(result.Failure==ServiceFailure.StartFailed&&app.Calls.SequenceEqual(new[]{"barrier"})&&!reports.Any(r=>r.State==ServiceState.Running));
});
await Check("noncooperative read-only recovery barrier times out without launching a process",async()=>
{
    var app=new FakeApp{StuckRecovery=true};var reports=new List<ServiceReport>();var result=await new ServiceController(new TestAuthorization(app),TimeSpan.FromMilliseconds(150)).RunAsync(reports.Add).WaitAsync(TimeSpan.FromSeconds(3));
    Assert(result.Failure==ServiceFailure.StartFailed&&result.QuiescenceConfirmed&&!app.Calls.Contains("start")&&!reports.Any(r=>r.State==ServiceState.Running));
});
await Check("authorization returning after cancellation has its resources revoked",async()=>
{
    var app=new FakeApp();var resource=new RevokedResource();var late=new LateAuthorization();var controller=new ServiceController(late);var reports=new List<ServiceReport>();var running=controller.RunAsync(reports.Add);
    controller.Control(ServiceDefinition.Stop);await running.WaitAsync(TimeSpan.FromSeconds(3));late.Completion.TrySetResult(new(new("late"),app,app,resource));
    await resource.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert(app.Calls.Count==0&&!reports.Any(r=>r.State==ServiceState.Running));
});
await Check("normal lifecycle reports real milestones and graceful stop before STOPPED",async()=>
{
    var app=new FakeApp();var controller=new ServiceController(new TestAuthorization(app));var reports=new List<ServiceReport>();
    var result=await controller.RunAsync(r=>{reports.Add(r);if(r.State==ServiceState.Running)Assert(controller.Control(ServiceDefinition.Stop)==0);});
    Assert(result.Failure==ServiceFailure.None&&result.QuiescenceConfirmed&&app.Calls.IndexOf("graceful")<app.Calls.IndexOf("tree-empty"));
    Assert(reports.Where(r=>r.State==ServiceState.StartPending).Select(r=>r.Checkpoint).SequenceEqual(new uint[]{1,2}));
    Assert(reports.All(r=>r.State==ServiceState.Running?r.AcceptedControls==5:r.AcceptedControls==0));Assert(reports[^1].State==ServiceState.Stopped&&reports[^1].Win32ExitCode==0);
    Assert(controller.Control(ServiceDefinition.Stop)==1062&&controller.Control(ServiceDefinition.Interrogate)==0&&controller.Control(128)==120);
});
foreach(var control in new[]{ServiceDefinition.Stop,ServiceDefinition.Shutdown})
foreach(var phase in new[]{"graceful","root","tree"})
await Check("requested control "+control+" with stuck "+phase+" stop reports failure after forced cleanup",async()=>
{
    var app=new FakeApp{StuckGraceful=phase=="graceful",StuckRoot=phase=="root",StuckTree=phase=="tree"};
    var controller=new ServiceController(new TestAuthorization(app),TimeSpan.FromSeconds(2),TimeSpan.FromMilliseconds(100));var reports=new List<ServiceReport>();
    var result=await controller.RunAsync(r=>{reports.Add(r);if(r.State==ServiceState.Running)controller.Control(control);}).WaitAsync(TimeSpan.FromSeconds(3));
    Assert(result.Failure==ServiceFailure.StopFailed&&result.QuiescenceConfirmed&&app.Calls.Contains("abort"));
    Assert(reports[^1].State==ServiceState.Stopped&&reports[^1].Win32ExitCode==1066&&reports[^1].ServiceSpecificExitCode==(uint)ServiceFailure.StopFailed);
});
await Check("failed health aborts owned session and never reports RUNNING",async()=>
{
    var app=new FakeApp{BadHealth=true};var reports=new List<ServiceReport>();var result=await new ServiceController(new TestAuthorization(app)).RunAsync(reports.Add);
    Assert(result.Failure==ServiceFailure.StartFailed&&result.QuiescenceConfirmed&&app.Calls.Contains("abort")&&!reports.Any(r=>r.State==ServiceState.Running));
});
await Check("unexpected root exit is failure without automatic restart or update",async()=>
{
    var app=new FakeApp();var reports=new List<ServiceReport>();var result=await new ServiceController(new TestAuthorization(app)).RunAsync(r=>{reports.Add(r);if(r.State==ServiceState.Running)app.Exit.TrySetResult(17);});
    Assert(result.Failure==ServiceFailure.UnexpectedExit&&result.QuiescenceConfirmed&&app.Calls.Count(c=>c=="start")==1&&reports[^1].ServiceSpecificExitCode==(uint)ServiceFailure.UnexpectedExit);
});
await Check("unknown cleanup never emits STOPPED and invokes failed native exit contract",()=>
{
    var app=new FakeApp{CleanupUnknown=true};var api=new FakeScm();api.OnReport=r=>{if(r.State==ServiceState.Running)api.Handler!(ServiceDefinition.Stop);};var fatal=0;
    Assert(new ServiceApplication(api,new TestAuthorization(app),_=>fatal++).Run()==1);Assert(fatal==1&&!api.Reports.Any(r=>r.State==ServiceState.Stopped));app.Exit.TrySetResult(1);return Task.CompletedTask;
});
await Check("status transport failure triggers failed exit without false readiness",()=>
{
    var api=new FakeScm{RejectStatus=true};var fatal=0;Assert(new ServiceApplication(api,new TestAuthorization(new FakeApp()),_=>fatal++).Run()==1&&fatal==1);return Task.CompletedTask;
});
await Check("service host has no payload update authority",async()=>{await Reject(()=>new NoHostUpdateAuthority().ActivateAsync(new("candidate"),null!,default));});

foreach(var mode in new[]{"normal","bad-peer","timeout","unexpected"})
await Check("non-listening real native job with synthetic control channel: "+mode,async()=>
{
    var directory=Session();var fake=new FileControlFactory(directory,mode);var environment=new Dictionary<string,string>{{"HOST","127.0.0.1"},{"CONNECTOR","mock"},{"ALLOW_REAL_WHATSAPP","no"}};
    var spec=new VerifiedProcessSpec(node,nodeHash,["-e",ChildScript.Code,directory,mode],directory,directory,node,environment);
    var launcher=new ServiceProcessLauncher(spec,fake);var barrier=new Barrier();var auth=new TestAuthorization(null!,()=>new(new("verified-test"),launcher,barrier));
    var controller=new ServiceController(auth,TimeSpan.FromSeconds(10),TimeSpan.FromMilliseconds(mode=="timeout"?1000:5000));var reports=new List<ServiceReport>();
    var before=Environment.GetEnvironmentVariable("NODE_OPTIONS");Environment.SetEnvironmentVariable("NODE_OPTIONS","--require untrusted-hook");
    try
    {
        var result=await controller.RunAsync(r=>{reports.Add(r);if(r.State==ServiceState.Running&&mode=="normal")controller.Control(ServiceDefinition.Stop);});
        var observation=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"observed.json"))).RootElement;
        Assert(observation.GetProperty("PATH").GetString()==""&&observation.GetProperty("FFMPEG_PATH").GetString()==node&&observation.GetProperty("NODE_OPTIONS").ValueKind==JsonValueKind.Null);
        Assert(!Alive(observation.GetProperty("pid").GetInt32())&&!Alive(observation.GetProperty("grandchild").GetInt32())&&result.QuiescenceConfirmed);
        Assert(mode=="normal"?result.Failure==ServiceFailure.None:result.Failure!=ServiceFailure.None);
        if(mode is "bad-peer" or "timeout")Assert(!reports.Any(r=>r.State==ServiceState.Running));
    }
    finally{Environment.SetEnvironmentVariable("NODE_OPTIONS",before);}
});
await Check("unknown child environment keys refuse before opening any control endpoint",async()=>
{
    var directory=Session();var fake=new FileControlFactory(directory,"normal");
    var spec=new VerifiedProcessSpec(node,nodeHash,[],directory,directory,node,new Dictionary<string,string>{{"NODE_OPTIONS","--require evil"}});
    await Reject(()=>new ServiceProcessLauncher(spec,fake).StartAsync(new("test"),default));Assert(fake.Created==0);
});
await Check("changed child image hash refuses before process/channel creation",async()=>
{
    var directory=Session();var fake=new FileControlFactory(directory,"normal");var spec=new VerifiedProcessSpec(node,new string('0',64),[],directory,directory,node,new Dictionary<string,string>());
    await Reject(()=>new ServiceProcessLauncher(spec,fake).StartAsync(new("test"),default));Assert(fake.Created==0);
});
await Check("observed admission checkpoints follow work and detach before terminal status",async()=>
{
    var auth=new ObservedTestAuthorizer(new FakeApp());var controller=new ServiceController(auth);var reports=new List<ServiceReport>();
    var result=await controller.RunAsync(r=>{reports.Add(r);if(r.State==ServiceState.Running)controller.Control(ServiceDefinition.Stop);});
    Assert(result.Failure==ServiceFailure.None&&reports.Count(r=>r.State==ServiceState.StartPending)>=5);var count=reports.Count;auth.Pulse();Assert(reports.Count==count);
});
await Check("observed admission status transport failure cannot become readiness",async()=>
{
    var auth=new ObservedTestAuthorizer(new FakeApp());var result=await new ServiceController(auth).RunAsync(r=>{if(r.State==ServiceState.StartPending&&r.Checkpoint==2)throw new IOException("STATUS_FAILED");});
    Assert(result.Failure==ServiceFailure.StatusTransport);
});
await Check("observed admission budget remains capped at five minutes",()=>
{
    try{_ =new ServiceController(new ObservedTestAuthorizer(new FakeApp()){Budget=TimeSpan.FromHours(1)});throw new Exception("unbounded admission accepted");}catch(ArgumentOutOfRangeException){}return Task.CompletedTask;
});
await Check("stalled observed admission invents no checkpoints and cancels before app launch",async()=>
{
    var auth=new WaitingObservedAuthorizer();var controller=new ServiceController(auth);var reports=new List<ServiceReport>();
    var run=controller.RunAsync(reports.Add);await auth.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));var count=reports.Count;await Task.Delay(50);Assert(reports.Count==count);
    controller.Control(ServiceDefinition.Stop);var result=await run.WaitAsync(TimeSpan.FromSeconds(2));Assert(result.Failure==ServiceFailure.None&&!reports.Any(r=>r.State==ServiceState.Running));
});
await Check("large admission budget does not extend graceful stop budget",async()=>
{
    var controller=new ServiceController(new ObservedTestAuthorizer(new FakeApp{StuckGraceful=true}),lifecycleDeadline:TimeSpan.FromMilliseconds(50));
    var result=await controller.RunAsync(r=>{if(r.State==ServiceState.Running)controller.Control(ServiceDefinition.Stop);}).WaitAsync(TimeSpan.FromSeconds(2));Assert(result.Failure==ServiceFailure.StopFailed);
});
File.WriteAllText(report,JsonSerializer.Serialize(new{kind="SCM-host-noninstalling-contracts",passed=checks.Count-failures,failed=failures,
    nativeScmInvoked=false,serviceInstalled=false,listenersOpened=false,realNativeJobTests=4,privateNodeSha256=nodeHash,tests=checks},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{checks.Count-failures} passed; {failures} failed. No SCM calls/listeners. Fixture: {root}");return failures==0?0:1;

static bool Alive(int id){try{using var process=Process.GetProcessById(id);return !process.HasExited;}catch(ArgumentException){return false;}}
internal sealed class Tokens(ServiceTokenObservation observation,string expected):IServiceTokenReader
{public ServiceTokenObservation Read()=>observation;public string ExpectedServiceSid()=>expected;}
internal sealed class Reader:IInstallationReader
{internal int Reads;internal bool Denied;public InstallationBinding Read(){Reads++;if(Denied)throw new IOException("ACL_DENIED");return new(new string('a',32),new string('b',64));}}
internal sealed class Resolver:IInstalledActivationResolver
{internal int Calls;public Task<AuthorizedServiceStart> ResolveAsync(InstallationBinding binding,CancellationToken cancellation){Calls++;throw new ServiceStartDenied(ServiceFailure.ActivationUnavailable);}}
internal sealed class Barrier:IAppRecoveryBarrier
{public Task RequireStartAllowedAsync(AppRelease release,CancellationToken cancellation)=>Task.CompletedTask;}
internal sealed class TestAuthorization(FakeApp app,Func<AuthorizedServiceStart>? factory=null):IServiceStartAuthorizer
{
    internal int Calls;internal bool Deny,Wait;internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)
    {Calls++;Entered.TrySetResult();if(Deny)throw new ServiceStartDenied(ServiceFailure.IdentityDenied);if(Wait)await Task.Delay(Timeout.Infinite,cancellation);return factory?.Invoke()??new(new("test"),app,app);}
}
internal sealed class FakeApp:IPrivateAppLauncher,IAppRecoveryBarrier
{
    internal readonly List<string> Calls=[];internal readonly TaskCompletionSource<int> Exit=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool DenyRecovery,BadHealth,CleanupUnknown,StuckRecovery,StuckGraceful,StuckRoot,StuckTree;
    public Task RequireStartAllowedAsync(AppRelease release,CancellationToken cancellation){Calls.Add("barrier");if(StuckRecovery)return new TaskCompletionSource().Task;if(DenyRecovery)throw new IOException("RECOVERY_DENIED");return Task.CompletedTask;}
    public Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken cancellation){Calls.Add("start");return Task.FromResult<IContainedAppSession>(new Session(this));}
    public Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken cancellation){Calls.Add("healthy");if(BadHealth)throw new IOException("UNHEALTHY");return Task.CompletedTask;}
    private sealed class Session(FakeApp owner):IContainedAppSession
    {
        public Task RequestGracefulStopAsync(CancellationToken cancellation){owner.Calls.Add("graceful");if(owner.StuckGraceful)return Task.Delay(Timeout.Infinite,cancellation);if(!owner.StuckRoot)owner.Exit.TrySetResult(0);return Task.CompletedTask;}
        public Task<int> WaitForRootExitAsync(CancellationToken cancellation)=>owner.Exit.Task.WaitAsync(cancellation);
        public Task WaitForTreeEmptyAsync(CancellationToken cancellation){owner.Calls.Add("tree-empty");if(owner.StuckTree)return Task.Delay(Timeout.Infinite,cancellation);if(owner.CleanupUnknown)throw new IOException("UNKNOWN");return Task.CompletedTask;}
        public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation){owner.Calls.Add("abort");if(owner.CleanupUnknown)throw new IOException("UNKNOWN");owner.Exit.TrySetResult(1);return Task.CompletedTask;}
        public ValueTask DisposeAsync(){owner.Calls.Add("disposed");return ValueTask.CompletedTask;}
    }
}
internal sealed class FakeScm:IScmApi
{
    internal string[] Arguments=[ServiceDefinition.Name];internal Func<uint,uint>? Handler;internal Action<ServiceReport>? OnReport;internal bool RejectStatus;
    internal readonly List<string> Calls=[];internal readonly List<ServiceReport> Reports=[];
    public void Dispatch(string name,Action<string[]> main){Calls.Add("dispatch");main(Arguments);}
    public void Register(string name,Func<uint,uint> handler){Calls.Add("register");Handler=handler;}
    public void Publish(ServiceReport report){Calls.Add("status");if(Handler is null||RejectStatus)throw new IOException("STATUS_UNAVAILABLE");Reports.Add(report);OnReport?.Invoke(report);}
}
internal sealed class FileControlFactory(string directory,string mode):IChildControlFactory
{
    internal int Created;
    public IChildControl Create(){Created++;return new Channel(directory,mode);}
    private sealed class Channel(string directory,string mode):IChildControl
    {
        public string Endpoint=>"synthetic-file-control-no-listener";
        public async Task<ChildReady> ReadyAsync(int root,CancellationToken cancellation)
        {
            while(true)
            {
                cancellation.ThrowIfCancellationRequested();
                try{using var observed=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"observed.json")));if(observed.RootElement.GetProperty("pid").GetInt32()==root)break;}
                catch(Exception error)when(error is IOException or JsonException){}
                await Task.Delay(10,cancellation);
            }
            if(mode=="timeout")await Task.Delay(Timeout.Infinite,cancellation);
            return new((uint)(mode=="bad-peer"?root+1:root),"ready");
        }
        public Task StopAsync(CancellationToken cancellation){File.WriteAllText(Path.Combine(directory,"stop"),"stop");return Task.CompletedTask;}
        public void Dispose(){}
    }
}
internal static class ChildScript
{
    internal const string Code="""
        const fs=require('node:fs'),path=require('node:path'),{spawn}=require('node:child_process');
        const root=process.argv[1],mode=process.argv[2];
        const grandchild=spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{stdio:'ignore',windowsHide:true});
        fs.writeFileSync(path.join(root,'observed.json'),JSON.stringify({pid:process.pid,grandchild:grandchild.pid,PATH:process.env.PATH,FFMPEG_PATH:process.env.FFMPEG_PATH,NODE_OPTIONS:process.env.NODE_OPTIONS||null}));
        if(mode==='unexpected')setTimeout(()=>process.exit(17),150);
        const watch=setInterval(()=>{if(fs.existsSync(path.join(root,'stop'))){clearInterval(watch);grandchild.once('exit',()=>process.exit(0));grandchild.kill();}},10);
        """;
}

internal sealed class RevokedResource:IDisposable
{internal readonly TaskCompletionSource Disposed=new(TaskCreationOptions.RunContinuationsAsynchronously);public void Dispose()=>Disposed.TrySetResult();}
internal sealed class LateAuthorization:IServiceStartAuthorizer
{internal readonly TaskCompletionSource<AuthorizedServiceStart> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)=>Completion.Task;}

internal sealed class ObservedTestAuthorizer(FakeApp app):IServiceStartAuthorizer,IObservedAdmission
{
    internal TimeSpan Budget=TimeSpan.FromMinutes(5);public TimeSpan AdmissionBudget=>Budget;public event Action? ObservedWork;
    internal void Pulse()=>ObservedWork?.Invoke();
    public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)=>Task.Run(()=>
    {for(var i=0;i<3;i++){cancellation.ThrowIfCancellationRequested();_ =System.Security.Cryptography.SHA256.HashData(new byte[4096]);Pulse();}return new AuthorizedServiceStart(new("observed"),app,app);},cancellation);
}

internal sealed class WaitingObservedAuthorizer:IServiceStartAuthorizer,IObservedAdmission
{
    internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TimeSpan AdmissionBudget=>TimeSpan.FromMinutes(5);public event Action? ObservedWork;
    public async Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)
    {ObservedWork?.Invoke();Entered.TrySetResult();await Task.Delay(Timeout.Infinite,cancellation);throw new IOException("unreachable");}
}

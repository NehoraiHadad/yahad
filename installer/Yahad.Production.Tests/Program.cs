using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;

string? preparedPayload=null;
if(args.Length==10&&args[^2]=="--prepared"){preparedPayload=args[^1];args=args[..^2];}
if(args is not ["--node",var node,"--repo",var repo,"--helper",var helper,"--report",var report])throw new IOException("PRIVATE_INPUTS_REQUIRED");
var results=new List<object>();var failures=0;
void Assert(bool b,string reason="assertion failed"){if(!b)throw new Exception(reason);}
async Task Check(string name,Func<Task> run){try{await run();results.Add(new{name,status="PASS"});Console.WriteLine("PASS "+name);}catch(Exception e){failures++;results.Add(new{name,status="FAIL",reason=e.GetType().Name});Console.WriteLine("FAIL "+name+" "+e);}}
async Task Reject(Func<Task> run,string? code=null)
{try{await run();}catch(Exception e)when(e is IOException or Win32Exception or JsonException or CryptographicException or FormatException){if(code is not null&&e.Message!=code)throw new Exception("wrong refusal: "+e.Message);return;}throw new Exception("unexpected admission");}
using var f=await ProductionFixture.Create(node,repo,helper);
await Check("production admin boundary rejects current-user fixture records",async()=>{await Reject(async()=>{using var grant=await new InstalledActivationResolver(f.Root,new AdminInstalledControlFiles(),f.Control,()=>f.Now).ResolveAsync(f.Binding,default);});});
await Check("production resolver refuses absent protected activation state",async()=>{var binding=f.Binding;var bytes=File.ReadAllBytes(f.PathOf("activation.json"));File.Delete(f.PathOf("activation.json"));try{await Reject(async()=>{using var grant=await new InstalledActivationResolver(f.Root,new PrivateControlFiles(),f.Control,()=>f.Now).ResolveAsync(binding,default);});}finally{File.WriteAllBytes(f.PathOf("activation.json"),bytes);}});
await Check("stale bootstrap digest cannot select a different protected activation",async()=>
{var binding=f.Binding with{ActivationReceiptDigest=new string('0',64)};await Reject(async()=>{using var grant=await new InstalledActivationResolver(f.Root,new PrivateControlFiles(),f.Control,()=>f.Now).ResolveAsync(binding,default);},"INSTALLED_ACTIVATION_ANCHOR");});
await Check("missing owner record refuses the concrete resolver",async()=>
{var path=f.PathOf("owner-intent.json");var bytes=File.ReadAllBytes(path);File.Delete(path);try{await Reject(async()=>{using var grant=await f.Resolve();});}finally{File.WriteAllBytes(path,bytes);}});
await Check("locked selected payload refuses without changing selected data",async()=>
{using var locked=new FileStream(Path.Combine(f.Package,"runtime/caddy/caddy.exe"),FileMode.Open,FileAccess.Write,FileShare.None);await Reject(async()=>{using var grant=await f.Resolve();});});
await Check("omitted owner expiry is not silently promoted to a persistent capability",async()=>
{
    var document=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(f.PathOf("owner-intent.json")))!.AsObject();document.Remove("ExpiresUnix");
    await Reject(()=>{NativeJson.Read<InstalledOwnerIntent>(JsonSerializer.SerializeToUtf8Bytes(document));return Task.CompletedTask;});
});
await Check("same concrete resolver reads fixture capabilities and observes actual Store schema 5",async()=>{using var grant=await f.Resolve();await grant.Recovery.RequireStartAllowedAsync(grant.Release,default);Assert(f.Control.Creates==0);});
foreach(var mutation in new[]{"epoch","head","owner","policy","pin","pending","phase"})
await Check("protected-state binding refuses "+mutation,async()=>
{
    f.Reset();try
    {
        if(mutation=="epoch")f.AnchorEpoch=2;if(mutation=="head")f.AnchorDigest=new string('0',64);if(mutation=="owner")f.OwnerRelease=new string('0',64);if(mutation=="phase")f.Phase="prepared";
        f.Publish();if(mutation=="policy")File.AppendAllText(f.Policy,"changed");if(mutation=="pin")File.AppendAllText(Path.Combine(f.Dataset,"update-pin"),"changed");if(mutation=="pending")File.WriteAllText(f.PathOf("PENDING.JSON"),"interrupted");
        await Reject(async()=>{using var grant=await f.Resolve();});
    }finally{f.Reset();}
});
await Check("owner expiration is observed again at mutable start barrier",async()=>
{f.Reset();f.Expiry=f.Now.AddMinutes(1).ToUnixTimeSeconds();f.Publish();using var grant=await f.Resolve();f.Now=f.Now.AddMinutes(2);try{await Reject(()=>grant.Recovery.RequireStartAllowedAsync(grant.Release,default),"INSTALLED_OWNER_EXPIRED");}finally{f.Now=f.Now.AddMinutes(-2);}});
await Check("exact pinned release bytes cannot be substituted",async()=>
{f.Reset();var path=Path.Combine(f.Package,"runtime/caddy/caddy.exe");File.AppendAllText(path,"changed");try{await Reject(async()=>{using var grant=await f.Resolve();},"INSTALLED_RELEASE_BYTES");}finally{File.WriteAllText(path,"synthetic-caddy-not-executed");}});
await Check("protected-control anchor cannot conceal a crash-left WAL",async()=>
{f.Reset();File.WriteAllText(Path.Combine(f.Data,"app.sqlite-wal"),"synthetic-residue");try{await Reject(async()=>{using var grant=await f.Resolve();},"INSTALLED_WAL_RECONCILIATION_REQUIRED");Assert(File.ReadAllText(Path.Combine(f.Data,"app.sqlite-wal"))=="synthetic-residue");}finally{File.Delete(Path.Combine(f.Data,"app.sqlite-wal"));}});
await Check("concrete production resolver launches real mutable Store through continuous handoff",async()=>
{
    f.Reset();var authorizer=new DirectAuthorization(f);var controller=new ServiceController(authorizer);var ran=false;
    var previousNode=Environment.GetEnvironmentVariable("NODE_OPTIONS");var previousHooks=Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS");ServiceFailure failure=ServiceFailure.StartFailed;
    try
    {
        Environment.SetEnvironmentVariable("NODE_OPTIONS","--require refused-host-hook");Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS","refused-host-hook");
        var result=await controller.RunAsync(r=>{if(r.State==ServiceState.Running){ran=true;controller.Control(ServiceDefinition.Stop);}});failure=result.Failure;
    }
    finally{Environment.SetEnvironmentVariable("NODE_OPTIONS",previousNode);Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS",previousHooks);}
    Assert(ran&&failure==ServiceFailure.None&&f.Control.HeldAfterClose);using var released=WindowsFileBoundary.AcquireDataLease(f.Data);Assert(File.Exists(Path.Combine(f.Data,"native-closed")));
});
await Check("child duplicate prevents a second writer even after host lease handle is closed",async()=>
{
    f.Reset();f.ClearSignals();var lease=WindowsFileBoundary.AcquireDataLease(f.Data);var spec=f.RawSpec(lease);
    var launcher=new ServiceProcessLauncher(spec,f.Control);IContainedAppSession? session=null;
    try
    {
        session=await launcher.StartAsync(new("lease-test"),default);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));await launcher.WaitForHealthyAsync(session,deadline.Token);lease.Dispose();
        try{using var wrong=WindowsFileBoundary.AcquireDataLease(f.Data);throw new Exception("second writer entered");}catch(Win32Exception e)when(e.NativeErrorCode==32){}
        await session.RequestGracefulStopAsync(deadline.Token);await session.WaitForRootExitAsync(deadline.Token);await session.WaitForTreeEmptyAsync(deadline.Token);Assert(f.Control.HeldAfterClose);
    }
    finally{lease.Dispose();if(session is not null){using var clean=new CancellationTokenSource(TimeSpan.FromSeconds(5));await session.TerminateAndWaitForTreeEmptyAsync(clean.Token);await session.DisposeAsync();}}
    using var final=WindowsFileBoundary.AcquireDataLease(f.Data);
});
await Check("wrong host-lease nonce or data identity cannot authorize Store",async()=>
{
    f.Reset();using var lease=WindowsFileBoundary.AcquireDataLease(f.Data);var descriptor=Path.Combine(f.Root,"bad-lease.json");
    File.WriteAllText(descriptor,JsonSerializer.Serialize(new HostLeaseGrant(1,Environment.ProcessId,4,f.Data,new string('a',64))));
    await Reject(()=>{WindowsFileBoundary.ValidateHostLease(descriptor,f.Data,Environment.ProcessId,new string('b',64));return Task.CompletedTask;},"NATIVE_HANDOFF_IDENTITY");
});
await Check("callback failure after duplicating the data lease observes root death before returning",async()=>
{
    f.Reset();using var lease=WindowsFileBoundary.AcquireDataLease(f.Data);System.Diagnostics.Process? witness=null;
    try
    {
        await Reject(()=>{_ =JobProcess.Start(node,["-e","require('node:fs').writeFileSync(process.argv[1],'ran')",Path.Combine(f.Data,"native-ready")],f.Root,LaunchEnvironment.Clean(f.Root),(process,pid)=>
        {witness=System.Diagnostics.Process.GetProcessById(pid);_ =witness.SafeHandle;_ =lease.DuplicateTo(process);throw new IOException("INJECTED_CALLBACK_FAILURE");});return Task.CompletedTask;},"INJECTED_CALLBACK_FAILURE");
        Assert(witness is not null&&witness.WaitForExit(0)&&!File.Exists(Path.Combine(f.Data,"native-ready")));
    }
    finally{witness?.Dispose();}
});
await Check("failure before job assignment observes the separate suspended root and empty job",async()=>
{
    f.Reset();var fault=new JobProcessFaultInjection{FailBeforeAssignment=true};
    await Reject(()=>{_ =JobProcess.StartWithFault(node,["-e",""],f.Root,LaunchEnvironment.Clean(f.Root),null,fault);return Task.CompletedTask;},"INJECTED_BEFORE_ASSIGNMENT_FAILURE");
    Assert(fault.CreatedProcess is not null&&fault.RootExitObserved&&fault.TreeEmptyObserved);
});
await Check("controller releases a failed handoff reservation only after observed root and tree exit",async()=>
{
    f.Reset();var fault=new JobProcessFaultInjection{FailAfterHandoff=true};var resource=new StartReservation(WindowsFileBoundary.AcquireDataLease(f.Data),fault);
    var launcher=new ServiceProcessLauncher(f.RawSpec(resource.Lease),f.Control,fault);var reports=new List<ServiceReport>();
    try
    {
        var result=await new ServiceController(new StartFailureAuthorization(launcher,resource)).RunAsync(reports.Add);
        Assert(result.Failure==ServiceFailure.StartFailed&&result.QuiescenceConfirmed&&resource.Released&&fault.RootExitObserved&&fault.TreeEmptyObserved);
        Assert(!reports.Any(r=>r.State==ServiceState.Running)&&!File.Exists(Path.Combine(f.Data,"native-ready")));
        using var released=WindowsFileBoundary.AcquireDataLease(f.Data);
    }
    finally{if(!resource.Released)resource.Dispose();}
});
foreach(var missing in new[]{"root","tree"})
await Check("unobserved "+missing+" after failed handoff retains session and reservation without STOPPED",async()=>
{
    f.Reset();var fault=new JobProcessFaultInjection{FailAfterHandoff=true,RootObservationUnavailable=missing=="root",TreeObservationUnavailable=missing=="tree"};
    var resource=new StartReservation(WindowsFileBoundary.AcquireDataLease(f.Data),fault);var launcher=new ServiceProcessLauncher(f.RawSpec(resource.Lease),f.Control,fault);var reports=new List<ServiceReport>();
    try
    {
        var result=await new ServiceController(new StartFailureAuthorization(launcher,resource)).RunAsync(reports.Add);
        Assert(result.Failure==ServiceFailure.QuiescenceUnknown&&!result.QuiescenceConfirmed&&!resource.Released&&fault.FailedStartSession is not null);
        Assert(!reports.Any(r=>r.State is ServiceState.Running or ServiceState.Stopped));
        try{using var wrong=WindowsFileBoundary.AcquireDataLease(f.Data);throw new Exception("reservation released while observation unavailable");}catch(Win32Exception e)when(e.NativeErrorCode==32){}
        var grants=Directory.GetFiles(Path.Combine(f.Root,"temporary"),"lease.json",SearchOption.AllDirectories);Assert(grants.Length==1);
        try{using var write=new FileStream(grants[0],FileMode.Open,FileAccess.Write,FileShare.None);throw new Exception("handoff pin released before observation");}catch(IOException){}
    }
    finally
    {
        // A separate explicit retry uses real OS observations. Clearing this
        // failure-only hook does not inject a successful exit observation.
        fault.RootObservationUnavailable=false;fault.TreeObservationUnavailable=false;
        if(fault.FailedStartSession is { } session){using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(5));await session.TerminateAndWaitForTreeEmptyAsync(bound.Token);await session.DisposeAsync();}
        Assert(fault.RootExitObserved&&fault.TreeEmptyObserved);resource.Dispose();
    }
    using var released=WindowsFileBoundary.AcquireDataLease(f.Data);
});
foreach(var missing in new[]{"root","tree"})
await Check("actual admission SQLite probe retains reservation when "+missing+" observation is unavailable",async()=>
{
    f.Reset();var fault=new JobProcessFaultInjection{FailAfterHandoff=true,RootObservationUnavailable=missing=="root",TreeObservationUnavailable=missing=="tree"};
    var controller=new ServiceController(new ProbeAuthorization(f,fault));var reports=new List<ServiceReport>();
    var result=await controller.RunAsync(reports.Add);
    Assert(result.Failure==ServiceFailure.QuiescenceUnknown&&!result.QuiescenceConfirmed&&!reports.Any(r=>r.State is ServiceState.Running or ServiceState.Stopped));
    try{using var wrong=WindowsFileBoundary.AcquireDataLease(f.Data);throw new Exception("probe reservation released");}catch(Win32Exception e)when(e.NativeErrorCode==32){}
    fault.RootObservationUnavailable=false;fault.TreeObservationUnavailable=false;
    using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(5));await controller.RetryAdmissionCleanupAsync(bound.Token);
    Assert(fault.RootExitObserved&&fault.TreeEmptyObserved);using var released=WindowsFileBoundary.AcquireDataLease(f.Data);
});
await Check("actual recovery probe failure retains recovery lease and private scratch until observed retry",async()=>
{
    f.Reset();f.ClearSignals();var fault=new JobProcessFaultInjection{FailAfterHandoff=true,TreeObservationUnavailable=true};
    using var empty=JobProcess.Start(node,["-e",""],f.Root,LaunchEnvironment.Clean(f.Root));await empty.WaitAsync(default);await empty.WaitForTreeEmptyAsync(default);
    var inspector=new NativeSqliteInspector(node,ProductionFixture.Hash(File.ReadAllBytes(node)),fault);var recovery=await NativeRecoveryFixture.OpenAsync(f.Dataset,empty,inspector);ProbeOwnershipException? owned=null;
    try
    {
        try{await recovery.BackupAsync();throw new Exception("unknown probe accepted");}catch(ProbeOwnershipException error){owned=error;}
        await Reject(()=>{recovery.Dispose();return Task.CompletedTask;},"RECOVERY_PROBE_QUIESCENCE_UNCONFIRMED");
        await Reject(()=>{inspector.Dispose();return Task.CompletedTask;},"RECOVERY_PROBE_QUIESCENCE_UNCONFIRMED");
        try{using var wrong=WindowsFileBoundary.AcquireDataLease(f.Data);throw new Exception("recovery lease released");}catch(Win32Exception e)when(e.NativeErrorCode==32){}
        Assert(Directory.GetFiles(Path.Combine(f.Dataset,".recovery-probes"),"app.sqlite",SearchOption.AllDirectories).Length==1);
    }
    finally
    {
        fault.TreeObservationUnavailable=false;if(owned is not null){using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(5));await owned.RetryCleanupAsync(bound.Token);}
        recovery.Dispose();inspector.Dispose();
    }
    Assert(!Directory.GetFiles(Path.Combine(f.Dataset,".recovery-probes"),"app.sqlite",SearchOption.AllDirectories).Any());using var released=WindowsFileBoundary.AcquireDataLease(f.Data);
});
NativeBackupResult? backup=null;NativeRestoreResult? restored=null;
await Check("actual recovery output is verified but cannot start before protected commit",async()=>
{
    f.Reset();f.ClearSignals();using var empty=JobProcess.Start(node,["-e",""],f.Root,LaunchEnvironment.Clean(f.Root));await empty.WaitAsync(default);await empty.WaitForTreeEmptyAsync(default);
    using(var inspector=new NativeSqliteInspector(node,ProductionFixture.Hash(File.ReadAllBytes(node))))
    using(var recovery=await NativeRecoveryFixture.OpenAsync(f.Dataset,empty,inspector))
    {backup=await recovery.BackupAsync();restored=await recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5);}
    f.Recovery=new(f.OriginalDataId,backup.BackupId,restored.OperationId,backup.ReceiptDigest);f.DataId=Guid.NewGuid().ToString("N");f.Phase="recovered";f.Publish();
    await Reject(async()=>{using var grant=await f.Resolve();},"INSTALLED_RECOVERY_COMMIT_REQUIRED");Assert(restored.ObservedSchema==5);
});
await Check("actual receipt MAC corruption is refused even with matching control digest",async()=>
{
    if(backup is null||restored is null)throw new Exception("backup prerequisite failed");f.ResetRecovery(backup,restored);
    var path=Path.Combine(f.Dataset,"backup-"+backup.BackupId,"receipt.json");var original=File.ReadAllBytes(path);
    var envelope=NativeJson.Read<RecoveryEnvelope>(original);File.WriteAllBytes(path,JsonSerializer.SerializeToUtf8Bytes(envelope with{Mac=new string('0',64)}));
    f.Recovery=f.Recovery! with{ReceiptDigest=ProductionFixture.Hash(File.ReadAllBytes(path))};f.Publish();
    try{await Reject(async()=>{using var grant=await f.Resolve();},"RECOVERY_ADMISSION_MAC");}finally{File.WriteAllBytes(path,original);}
});
await Check("actual completed-journal truncation refuses recovered selection",async()=>
{
    if(backup is null||restored is null)throw new Exception("backup prerequisite failed");f.ResetRecovery(backup,restored);
    var journal=Path.Combine(f.Dataset,"restore-"+restored.OperationId);var last=Directory.GetFiles(journal,"*.json").Order(StringComparer.Ordinal).Last();var bytes=File.ReadAllBytes(last);File.Delete(last);
    try{await Reject(async()=>{using var grant=await f.Resolve();});}finally{File.WriteAllBytes(last,bytes);}
});
await Check("changed actual recovered plaintext refuses without overwriting original data",async()=>
{
    if(backup is null||restored is null)throw new Exception("backup prerequisite failed");f.ResetRecovery(backup,restored);var original=ProductionFixture.Hash(File.ReadAllBytes(Path.Combine(f.Dataset,"data/app.sqlite")));
    var file=Path.Combine(restored.RestoredDirectory,"data/diagnostics-activation-test");File.WriteAllText(file,"unexpected");
    try{await Reject(async()=>{using var grant=await f.Resolve();},"RECOVERY_ADMISSION_CLOSURE");Assert(original==ProductionFixture.Hash(File.ReadAllBytes(Path.Combine(f.Dataset,"data/app.sqlite"))));}finally{File.Delete(file);}
});
await Check("protected committed recovery selection launches real Store and restarts after writes",async()=>
{
    if(backup is null||restored is null)throw new Exception("backup prerequisite failed");f.ResetRecovery(backup,restored);f.Phase="committed";f.Publish();f.ClearSignals();
    var committedBinding=f.Binding;
    for(var pass=0;pass<2;pass++)
    {
        var controller=new ServiceController(new DirectAuthorization(f));var result=await controller.RunAsync(r=>{if(r.State==ServiceState.Running)controller.Control(ServiceDefinition.Stop);});
        Assert(result.Failure==ServiceFailure.None&&f.Control.HeldAfterClose&&f.Binding==committedBinding,"restart "+pass+" failure="+result.Failure+" held="+f.Control.HeldAfterClose+" anchorSame="+(f.Binding==committedBinding)+" stopError="+f.Control.StopError);f.ClearSignals();
    }
});
using var writerJob=JobProcess.Start(node,["-e",""],f.Root,LaunchEnvironment.Clean(f.Root));await writerJob.WaitAsync(default);await writerJob.WaitForTreeEmptyAsync(default);
void PrepareState(){f.Reset();File.WriteAllBytes(f.PathOf("bootstrap.json"),JsonSerializer.SerializeToUtf8Bytes(new{schema=1,service=ServiceDefinition.Name,installationId=f.InstallationId,activationReceiptDigest=f.Binding.ActivationReceiptDigest}));}
ProtectedOperation Operation(string kind="activate")
{
    var head=NativeJson.Read<InstalledActivation>(File.ReadAllBytes(f.PathOf("activation.json")));var web=NativeJson.Read<InstalledWeb>(File.ReadAllBytes(f.PathOf("web.json")));
    return new(1,kind,f.InstallationId,head.Epoch,f.Binding.ActivationReceiptDigest,head.ReleaseDigest,head.DataId,head.MinimumSchema,head.MaximumSchema,head.MasterKeyDigest,head.PolicyDigest,head.PinDigest,web,null,f.Now.AddMinutes(2).ToUnixTimeSeconds(),Guid.NewGuid().ToString("N"));
}
async Task Denied(Func<Task> run){try{await run();}catch(Exception e)when(e is ServiceStartDenied or IOException or Win32Exception or UnauthorizedAccessException or JsonException or OperationCanceledException){return;}throw new Exception("transaction unexpectedly accepted");}
ProtectedStateTransactionWriter Writer(IOwnerOperationAuthentication auth,JobProcess? job=null)=>new(f.Root,f.InstallationId,job??writerJob,auth,f.Control,()=>f.Now);
await Check("protected transaction publishes authenticated owner and coherent records consumed by actual resolver",async()=>
{
    PrepareState();var op=Operation();var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);var binding=await Writer(auth).CommitAsync(op);
    Assert(binding==f.Binding);using var grant=await f.Resolve();var owner=NativeJson.Read<InstalledOwnerIntent>(File.ReadAllBytes(f.PathOf("owner-intent.json")));
    Assert(owner.Subject=="boundary-owner"&&owner.Nonce==op.Nonce&&NativeJson.Read<InstalledEpoch>(File.ReadAllBytes(f.PathOf("epoch.json"))).Epoch==2&&!File.Exists(f.PathOf("pending.json")));
});
await Check("transaction nonce is single-use even for a newly authenticated replay",async()=>
{
    PrepareState();var op=Operation();var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);var writer=Writer(auth);await writer.CommitAsync(op);await Denied(()=>writer.CommitAsync(op));
});
await Check("client cannot supply owner principal or grant scope fields",async()=>
{
    PrepareState();var document=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(Operation()))!.AsObject();document["Subject"]="client-owner";await Denied(()=>{NativeJson.Read<ProtectedOperation>(JsonSerializer.SerializeToUtf8Bytes(document));return Task.CompletedTask;});
});
foreach(var kind in new[]{"scope","principal-installation","operation-digest","stale-epoch","stale-digest","expired","release","data"})
await Check("transaction refuses bound-operation mismatch: "+kind,async()=>
{
    PrepareState();var op=Operation();if(kind=="stale-epoch")op=op with{ExpectedEpoch=2};if(kind=="stale-digest")op=op with{ExpectedActivationDigest=new string('0',64)};if(kind=="expired")op=op with{ExpiresUnix=f.Now.ToUnixTimeSeconds()-1};if(kind=="release")op=op with{ReleaseDigest=new string('0',64)};if(kind=="data")op=op with{DataId=Guid.NewGuid().ToString("N")};
    var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);auth.Wrong=kind;var before=f.Binding;
    await Denied(()=>Writer(auth).CommitAsync(op));Assert(before==f.Binding&&!File.Exists(f.PathOf("pending.json")));
});
await Check("exclusive authority denies concurrent committer",async()=>
{
    PrepareState();var op=Operation();var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);using var held=WindowsFileBoundary.AcquireDataLease(Path.Combine(f.Root,".authority"));await Denied(()=>Writer(auth).CommitAsync(op));
});
await Check("actual busy app job blocks publication without a quiescence Boolean",async()=>
{
    PrepareState();var op=Operation();var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);
    using var busy=JobProcess.Start(node,["-e","setInterval(()=>{},1000)"],f.Root,LaunchEnvironment.Clean(f.Root));
    try{using var bound=new CancellationTokenSource(150);await Denied(()=>Writer(auth,busy).CommitAsync(op,cancellation:bound.Token));Assert(!File.Exists(f.PathOf("pending.json")));}
    finally{await busy.TerminateAndWaitForTreeEmptyAsync(default);}
});
await Check("actual selected-data lease blocks publication",async()=>
{
    PrepareState();var op=Operation();var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);using var lease=WindowsFileBoundary.AcquireDataLease(f.Data);await Denied(()=>Writer(auth).CommitAsync(op));Assert(!File.Exists(f.PathOf("pending.json")));
});
foreach(var point in new[]{"pending-published","nonce-reserved","record-owner-intent.json","record-web.json","partial-activation.json","record-activation.json","record-epoch.json","record-bootstrap.json","commit-receipt"})
await Check("interrupted protected transaction stays blocked then consistently resumes: "+point,async()=>
{
    PrepareState();var op=Operation();var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);var writer=Writer(auth);
    await Denied(()=>writer.CommitAsync(op,p=>{if(p==point)throw new IOException("INJECTED_TRANSACTION_INTERRUPTION");}));
    Assert(File.Exists(f.PathOf("pending.json")));await Denied(async()=>{using var blocked=await f.Resolve();});
    var result=await writer.ResumeAsync(op);Assert(result==f.Binding&&!File.Exists(f.PathOf("pending.json")));using var grant=await f.Resolve();
    await Denied(()=>writer.CommitAsync(op));
});
await Check("pending contents and operation binding cannot be swapped during recovery",async()=>
{
    PrepareState();var op=Operation();var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);var writer=Writer(auth);
    await Denied(()=>writer.CommitAsync(op,p=>{if(p=="pending-published")throw new IOException("interrupt");}));
    var path=f.PathOf("pending.json");var bytes=File.ReadAllBytes(path);var pending=NativeJson.Read<ProtectedPending>(bytes);pending.After["activation.json"]=Convert.ToBase64String("tampered"u8.ToArray());File.WriteAllBytes(path,JsonSerializer.SerializeToUtf8Bytes(pending));
    await Denied(()=>writer.ResumeAsync(op));File.WriteAllBytes(path,bytes);await writer.ResumeAsync(op);
});
await Check("expired interrupted operation needs fresh authenticated reconciliation scope",async()=>
{
    PrepareState();var op=Operation();var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);var writer=Writer(auth);
    await Denied(()=>writer.CommitAsync(op,p=>{if(p=="record-activation.json")throw new IOException("interrupt");}));
    f.Now=f.Now.AddMinutes(4);auth.AllowReconcile=false;try{await Denied(()=>writer.ResumeAsync(op));Assert(File.Exists(f.PathOf("pending.json")));auth.AllowReconcile=true;await writer.ResumeAsync(op);}finally{f.Now=f.Now.AddMinutes(-4);}
});
await Check("install operation requires fresh control state and authenticated install scope",async()=>
{
    using var fresh=await ProductionFixture.Create(node,repo,helper);var head=NativeJson.Read<InstalledActivation>(File.ReadAllBytes(fresh.PathOf("activation.json")));var web=NativeJson.Read<InstalledWeb>(File.ReadAllBytes(fresh.PathOf("web.json")));
    var op=new ProtectedOperation(1,"install",fresh.InstallationId,0,"",head.ReleaseDigest,head.DataId,5,5,head.MasterKeyDigest,head.PolicyDigest,head.PinDigest,web,null,fresh.Now.AddMinutes(2).ToUnixTimeSeconds(),Guid.NewGuid().ToString("N"));
    foreach(var name in new[]{"activation.json","epoch.json","owner-intent.json","web.json","bootstrap.json"})File.Delete(fresh.PathOf(name));
    var auth=new ApprovedOperations(fresh.InstallationId,()=>fresh.Now);auth.Approve(op);await new ProtectedStateTransactionWriter(fresh.Root,fresh.InstallationId,writerJob,auth,fresh.Control,()=>fresh.Now).CommitAsync(op);
    using var grant=await fresh.Resolve();Assert(NativeJson.Read<InstalledEpoch>(File.ReadAllBytes(fresh.PathOf("epoch.json"))).Epoch==1);
});
await Check("install cannot reset an authority root with existing committed history",async()=>
{
    PrepareState();var op=Operation("install") with{ExpectedEpoch=0,ExpectedActivationDigest=""};var before=new Dictionary<string,byte[]>();
    foreach(var name in new[]{"activation.json","epoch.json","owner-intent.json","web.json","bootstrap.json"}){before[name]=File.ReadAllBytes(f.PathOf(name));File.Delete(f.PathOf(name));}
    try{var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);await Denied(()=>Writer(auth).CommitAsync(op));}finally{foreach(var item in before)File.WriteAllBytes(f.PathOf(item.Key),item.Value);}
});
await Check("full partial pending record resumes only after reauthentication and old-state validation",async()=>
{
    PrepareState();var op=Operation();var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);var writer=Writer(auth);var before=f.Binding;
    await Denied(()=>writer.CommitAsync(op,p=>{if(p=="partial-pending.json")throw new IOException("interrupt");}));
    Assert(before==f.Binding&&!File.Exists(f.PathOf("pending.json")));await Denied(async()=>{using var grant=await f.Resolve();});await writer.ResumeAsync(op);using var accepted=await f.Resolve();
});
await Check("truncated pending record is preserved and cannot authorize any mutation",async()=>
{
    PrepareState();var op=Operation();var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);var writer=Writer(auth);var before=f.Binding;
    await Denied(()=>writer.CommitAsync(op,p=>{if(p=="partial-pending.json")throw new IOException("interrupt");}));
    var path=f.PathOf("pending.json.partial-"+op.Nonce);var complete=File.ReadAllBytes(path);File.WriteAllText(path,"{");await Denied(()=>writer.ResumeAsync(op));Assert(before==f.Binding&&File.ReadAllText(path)=="{");
    File.WriteAllBytes(path,complete);await writer.ResumeAsync(op);
});
await Check("committed receipt forbids replay over older control records",async()=>
{
    PrepareState();var op=Operation();var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);var writer=Writer(auth);
    await Denied(()=>writer.CommitAsync(op,p=>{if(p=="commit-receipt")throw new IOException("interrupt");}));
    var pending=NativeJson.Read<ProtectedPending>(File.ReadAllBytes(f.PathOf("pending.json")));
    File.WriteAllBytes(f.PathOf("activation.json"),Convert.FromBase64String(pending.Before["activation.json"]));await Denied(()=>writer.ResumeAsync(op));
    File.WriteAllBytes(f.PathOf("activation.json"),Convert.FromBase64String(pending.After["activation.json"]));await writer.ResumeAsync(op);
});
await Check("completed receipt still revalidates selected data before clearing pending",async()=>
{
    PrepareState();var op=Operation();var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);var writer=Writer(auth);
    await Denied(()=>writer.CommitAsync(op,p=>{if(p=="commit-receipt")throw new IOException("interrupt");}));
    var database=Path.Combine(f.Data,"app.sqlite");var original=File.ReadAllBytes(database);File.WriteAllText(database,"corrupt synthetic data");
    await Denied(()=>writer.ResumeAsync(op));Assert(File.Exists(f.PathOf("pending.json")));
    File.WriteAllBytes(database,original);await writer.ResumeAsync(op);using var grant=await f.Resolve();
});
await Check("unconfigured authentication boundary cannot publish protected state",async()=>
{
    PrepareState();var before=f.Binding;await Denied(()=>Writer(new UnconfiguredOwnerOperationAuthentication()).CommitAsync(Operation()));
    Assert(before==f.Binding&&!File.Exists(f.PathOf("pending.json")));
});
await Check("recovery commit is bound to actual backup receipt and verified restored plaintext",async()=>
{
    PrepareState();var op=Operation("recover");NativeBackupResult b;NativeRestoreResult restoredCandidate;
    using(var inspector=new NativeSqliteInspector(node,ProductionFixture.Hash(File.ReadAllBytes(node))))using(var recovery=await NativeRecoveryFixture.OpenAsync(f.Dataset,writerJob,inspector))
    {b=await recovery.BackupAsync();restoredCandidate=await recovery.RestoreToNewDirectoryAsync(b.BackupId,5,5);}
    op=op with{DataId=Guid.NewGuid().ToString("N"),Recovery=new(f.OriginalDataId,b.BackupId,restoredCandidate.OperationId,b.ReceiptDigest)};
    var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);await Writer(auth).CommitAsync(op);using var grant=await f.Resolve();
    Assert(NativeJson.Read<InstalledActivation>(File.ReadAllBytes(f.PathOf("activation.json"))).Phase=="committed");
});
await Check("production default still denies while real committer/channel is unconfigured",async()=>
{await Denied(()=>new InstalledActivationResolver().ResolveAsync(f.Binding,default));});
if(preparedPayload is not null)await Check("exact rebuilt full payload passes installed release and runtime admission without app launch",async()=>
{
    f.Reset();f.SelectPreparedPayload(preparedPayload);var admission=new VerificationOnlyAuthorization(f);var reports=new List<ServiceReport>();
    var timer=System.Diagnostics.Stopwatch.StartNew();var controller=new ServiceController(admission);
    var result=await controller.RunAsync(r=>{reports.Add(r);if(r.State==ServiceState.Running)controller.Control(ServiceDefinition.Stop);});
    if(result.Failure!=ServiceFailure.None||!reports.Any(r=>r.State==ServiceState.Running))throw new Exception("full startup path failed: "+result.Failure);
    Console.WriteLine("FULL_ADMISSION_SECONDS "+timer.Elapsed.TotalSeconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+" CHECKPOINTS "+reports.Count(r=>r.State==ServiceState.StartPending));
    Assert(reports.Count(r=>r.State==ServiceState.StartPending)>3);
});
File.WriteAllText(report,JsonSerializer.Serialize(new{kind="concrete-installed-adapters-synthetic-inputs",passed=results.Count-failures,failed=failures,
    productionEntryWired=true,actualAdminProvisioningTested=false,actualStoreWrites=true,actualReceiptProducer=true,scmInvoked=false,networkListenersOpened=false,
    productionTrustOrOwnerStateCreated=false,fullRebuiltPayloadInspected=preparedPayload is not null,fixtureRoot=f.Root,tests=results},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{results.Count-failures} passed; {failures} failed. No SCM or network listeners.");return failures==0?0:1;

internal sealed class PrivateControlFiles:IInstalledControlFiles
{public byte[] Read(string path,int bound,ICollection<IDisposable> held){var file=WindowsFileBoundary.OpenPrivateRead(path);held.Add(file);return file.Small(bound);}}
internal sealed class DirectAuthorization(ProductionFixture fixture):IServiceStartAuthorizer,IObservedAdmission
{
    private readonly InstalledActivationResolver resolver=fixture.CreateResolver();public TimeSpan AdmissionBudget=>resolver.AdmissionBudget;
    public event Action? ObservedWork{add=>resolver.ObservedWork+=value;remove=>resolver.ObservedWork-=value;}
    public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)=>Task.Run(()=>resolver.ResolveAsync(fixture.Binding,cancellation),cancellation);
}
internal sealed class VerificationOnlyAuthorization(ProductionFixture fixture):IServiceStartAuthorizer,IObservedAdmission
{
    private readonly InstalledActivationResolver resolver=fixture.CreateResolver();public TimeSpan AdmissionBudget=>resolver.AdmissionBudget;
    public event Action? ObservedWork{add=>resolver.ObservedWork+=value;remove=>resolver.ObservedWork-=value;}
    public async Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)
    {var actual=await Task.Run(()=>resolver.ResolveAsync(fixture.Binding,cancellation),cancellation);return new(actual.Release,new NoNetworkLauncher(),actual.Recovery,actual);}
    private sealed class NoNetworkLauncher:IPrivateAppLauncher
    {
        public Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken cancellation)=>Task.FromResult<IContainedAppSession>(new Session());
        public Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken cancellation)=>Task.CompletedTask;
        private sealed class Session:IContainedAppSession
        {
            private readonly TaskCompletionSource<int> exit=new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task RequestGracefulStopAsync(CancellationToken cancellation){exit.TrySetResult(0);return Task.CompletedTask;}
            public Task<int> WaitForRootExitAsync(CancellationToken cancellation)=>exit.Task.WaitAsync(cancellation);
            public Task WaitForTreeEmptyAsync(CancellationToken cancellation)=>Task.CompletedTask;
            public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation){exit.TrySetResult(1);return Task.CompletedTask;}
            public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
        }
    }
}
internal sealed class FileControl(ProductionFixture fixture):IChildControlFactory
{
    internal int Creates;internal bool HeldAfterClose;internal string? StopError;
    public IChildControl Create(){Creates++;HeldAfterClose=false;StopError=null;return new Channel(this,fixture);}
    private sealed class Channel(FileControl owner,ProductionFixture fixture):IChildControl
    {
        public string Endpoint=>"synthetic-control-no-network-listener";
        private async Task Wait(string name,CancellationToken cancellation)
        {while(!File.Exists(Path.Combine(fixture.Data,name)))await Task.Delay(10,cancellation);}
        public async Task<ChildReady> ReadyAsync(int root,CancellationToken cancellation)
        {await Wait("native-ready",cancellation);using var value=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.Data,"native-ready")));if(value.RootElement.GetProperty("pid").GetInt32()!=root||value.RootElement.GetProperty("PATH").GetString()!=""||!value.RootElement.GetProperty("clean").GetBoolean())throw new IOException("STORE_CHILD_ENV_OR_PID");return new((uint)root,"ready");}
        public async Task StopAsync(CancellationToken cancellation)
        {File.WriteAllText(Path.Combine(fixture.Data,"native-stop"),"stop");await Wait("native-closed",cancellation);try{using var wrong=WindowsFileBoundary.AcquireDataLease(fixture.Data);throw new IOException("EARLY_LEASE_RELEASE");}catch(Win32Exception e)when(e.NativeErrorCode==32){owner.HeldAfterClose=true;}catch(Exception e){owner.StopError=e.GetType().Name+":"+e.Message;throw;}File.WriteAllText(Path.Combine(fixture.Data,"native-exit"),"exit");}
        public void Dispose(){}
    }
}
internal sealed class ProductionFixture:IDisposable
{
    internal readonly string Root,Dataset,OriginalDataId=Guid.NewGuid().ToString("N"),InstallationId=Guid.NewGuid().ToString("N"),Payload,Node,Repo;
    internal string DataId,Phase="committed";internal long AnchorEpoch=1;internal string? AnchorDigest,OwnerRelease;internal long? Expiry;
    internal InstalledRecovery? Recovery;internal DateTimeOffset Now=DateTimeOffset.UtcNow;internal readonly FileControl Control;
    internal string Data=>Recovery is null?Path.Combine(Dataset,"data"):Path.Combine(Dataset,"restore-"+Recovery.OperationId,"completed","data");
    internal string Policy=>Path.Combine(Dataset,"update-policy");private string release="";
    private ProductionFixture(string repo)
    {
        Repo=repo;Root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());Directory.CreateDirectory(Path.Combine(Root,"datasets"));
        var source=SafeDirectory.CreatePrivateSession(Path.Combine(Root,"datasets"));Dataset=Path.Combine(Root,"datasets",OriginalDataId);
        if(!Path.GetFullPath(source).StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!Path.GetFullPath(Dataset).StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("TEST_MOVE_SCOPE");Directory.Move(source,Dataset);
        DataId=OriginalDataId;Payload=Path.Combine(Root,"payload-stage");Directory.CreateDirectory(Payload);Node=Path.Combine(Payload,"runtime/node/node.exe");Directory.CreateDirectory(Path.GetDirectoryName(Node)!);Control=new(this);
        Directory.CreateDirectory(Path.Combine(Root,"temporary"));Directory.CreateDirectory(Data);File.WriteAllText(Path.Combine(Dataset,"master-key"),Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));File.WriteAllText(Policy,"synthetic-policy");File.WriteAllText(Path.Combine(Dataset,"update-pin"),"synthetic-pin");
    }
    internal static async Task<ProductionFixture> Create(string node,string repo,string helper)
    {
        var f=new ProductionFixture(repo);File.Copy(node,f.Node);Copy(helper,Path.Combine(f.Payload,"app/native"));
        void Put(string name,string text){var p=Path.Combine(f.Payload,name);Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllText(p,text);}
        Put("runtime/ffmpeg/ffmpeg.exe","synthetic-ffmpeg-not-executed");Put("runtime/caddy/caddy.exe","synthetic-caddy-not-executed");
        Put("app/package.json","{\"type\":\"module\"}");Put("app/node_modules/tsx/package.json","{\"type\":\"module\",\"exports\":\"./index.mjs\"}");
        Put("app/node_modules/tsx/index.mjs","export * from "+JsonSerializer.Serialize(new Uri(Path.Combine(repo,"node_modules/tsx/dist/loader.mjs")).AbsoluteUri)+";");
        Put("legal/build-inputs.json","{\"databaseSchema\":5}");
        Put("app/src/main.ts",f.Worker());
        var records=Directory.GetFiles(f.Payload,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(p=>new{path=Path.GetRelativePath(f.Payload,p).Replace('\\','/'),size=new FileInfo(p).Length,sha256=Hash(File.ReadAllBytes(p))}).ToArray();
        var manifest=JsonSerializer.SerializeToUtf8Bytes(new{schema=1,kind="windows-runtime-candidate",target="win-x64",profile="full",authority="application-updater",
            launch=new{node="runtime/node/node.exe",entry="app/src/main.ts",helper="app/native/Yahad.Platform.exe",ffmpeg="runtime/ffmpeg/ffmpeg.exe",caddy="runtime/caddy/caddy.exe"},files=records});
        f.release=Hash(manifest);File.WriteAllBytes(Path.Combine(f.Payload,"bundle.json"),manifest);Directory.CreateDirectory(Path.Combine(f.Root,"payloads"));var target=Path.Combine(f.Root,"payloads",f.release);Directory.Move(f.Payload,target);
        f.actualPayload=target;
        await f.Seed(node);f.Publish();return f;
    }
    private string? actualPayload;internal string Package=>actualPayload??Payload;
    internal string PathOf(string name)=>Path.Combine(Root,name);
    internal void ClearSignals(){foreach(var name in new[]{"native-ready","native-stop","native-closed","native-exit"})if(File.Exists(Path.Combine(Data,name)))File.Delete(Path.Combine(Data,name));}
    internal void Reset(){Recovery=null;DataId=OriginalDataId;Phase="committed";AnchorEpoch=1;AnchorDigest=null;OwnerRelease=null;Expiry=null;if(File.Exists(PathOf("PENDING.JSON")))File.Delete(PathOf("PENDING.JSON"));File.WriteAllText(Policy,"synthetic-policy");File.WriteAllText(Path.Combine(Dataset,"update-pin"),"synthetic-pin");ClearSignals();Publish();}
    internal void ResetRecovery(NativeBackupResult b,NativeRestoreResult restore){Reset();DataId=Guid.NewGuid().ToString("N");Recovery=new(OriginalDataId,b.BackupId,restore.OperationId,b.ReceiptDigest);Phase="recovered";Publish();}
    internal void Publish()
    {
        string Write<T>(string name,T value){var bytes=JsonSerializer.SerializeToUtf8Bytes(value);File.WriteAllBytes(PathOf(name),bytes);return Hash(bytes);}
        var owner=Write("owner-intent.json",new InstalledOwnerIntent(1,InstallationId,1,Guid.NewGuid().ToString("N"),OwnerRelease??release,DataId,Recovery is not null?"recover":"activate","synthetic-owner",Now.AddSeconds(-1).ToUnixTimeSeconds(),Expiry));
        var web=Write("web.json",new InstalledWeb(1,InstallationId,"https://yahad.invalid",32000));
        var head=Write("activation.json",new InstalledActivation(1,InstallationId,1,release,DataId,5,5,Hash(File.ReadAllBytes(Path.Combine(Dataset,"master-key"))),Hash(File.ReadAllBytes(Policy)),Hash(File.ReadAllBytes(Path.Combine(Dataset,"update-pin"))),owner,web,Phase,Recovery));
        Write("epoch.json",new InstalledEpoch(1,InstallationId,AnchorEpoch,AnchorDigest??head));
    }
    internal InstallationBinding Binding=>new(InstallationId,Hash(File.ReadAllBytes(PathOf("activation.json"))));
    internal InstalledActivationResolver CreateResolver(JobProcessFaultInjection? fault=null)=>new(Root,new PrivateControlFiles(),Control,()=>Now,fault);
    internal Task<AuthorizedServiceStart> Resolve(CancellationToken cancellation=default)=>CreateResolver().ResolveAsync(Binding,cancellation);
    private async Task Seed(string node)
    {
        var config=ConfigScript();var script="import fs from 'node:fs';try{const{Store}=await import("+JsonSerializer.Serialize(new Uri(Path.Combine(Repo,"src/store.ts")).AbsoluteUri)+");"+config+"const s=new Store(config);s.close();}catch(e){fs.writeFileSync("+JsonSerializer.Serialize(Path.Combine(Root,"seed-error.json"))+",JSON.stringify({name:e.name,message:e.message}));process.exit(1)}";
        using var job=JobProcess.Start(node,["--import",new Uri(Path.Combine(Repo,"node_modules/tsx/dist/loader.mjs")).AbsoluteUri,"--input-type=module","-e",script],Repo,LaunchEnvironment.Clean(Root));using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(30));if(await job.WaitAsync(bound.Token)!=0)throw new IOException("REAL_STORE_SEED_FAILED "+(File.Exists(Path.Combine(Root,"seed-error.json"))?File.ReadAllText(Path.Combine(Root,"seed-error.json")):"loader or import failure"));await job.WaitForTreeEmptyAsync(bound.Token);
    }
    private string ConfigScript()=>"const config={dataDir:"+JsonSerializer.Serialize(Path.Combine(Dataset,"data"))+",masterKey:Buffer.from(fs.readFileSync("+JsonSerializer.Serialize(Path.Combine(Dataset,"master-key"))+",'utf8').trim(),'base64'),connector:'mock',realAllowed:false,host:'127.0.0.1',port:32000,origin:'https://yahad.invalid',secureCookie:true,maxMedia:16777216,maxStorage:2147483648,childIdleMs:900000,sessionMs:43200000,diagnosticsEnabled:false};";
    private string Worker()=>"import fs from 'node:fs';import path from 'node:path';import{Store}from "+JsonSerializer.Serialize(new Uri(Path.Combine(Repo,"src/store.ts")).AbsoluteUri)+";import{createPlatformFiles}from "+JsonSerializer.Serialize(new Uri(Path.Combine(Repo,"src/platform-files.ts")).AbsoluteUri)+";"+"const data=process.env.DATA_DIR;const release=await createPlatformFiles('win32',path.resolve('native/Yahad.Platform.exe')).acquireDataLock(data);const config={dataDir:data,masterKey:Buffer.from(fs.readFileSync(process.env.MASTER_KEY_FILE,'utf8').trim(),'base64'),connector:'mock',realAllowed:false,host:'127.0.0.1',port:32000,origin:'https://yahad.invalid',secureCookie:true,maxMedia:16777216,maxStorage:2147483648,childIdleMs:900000,sessionMs:43200000,diagnosticsEnabled:false};const store=new Store(config);store.db.exec('CREATE TABLE IF NOT EXISTS native_activation_probe(value INTEGER);INSERT INTO native_activation_probe VALUES(1)');fs.writeFileSync(path.join(data,'native-ready'),JSON.stringify({pid:process.pid,PATH:process.env.PATH,clean:!process.env.NODE_OPTIONS&&!process.env.DOTNET_STARTUP_HOOKS}));const wait=n=>new Promise(resolve=>{const timer=setInterval(()=>{if(fs.existsSync(path.join(data,n))){clearInterval(timer);resolve();}},10)});await wait('native-stop');store.close();await release();fs.writeFileSync(path.join(data,'native-closed'),'closed');await wait('native-exit');";
    internal void SelectPreparedPayload(string prepared)
    {
        var manifest=File.ReadAllBytes(Path.Combine(prepared,"bundle.json"));release=Hash(manifest);var target=Path.Combine(Root,"payloads",release);
        Directory.CreateDirectory(target);
        foreach(var path in Directory.GetFiles(prepared,"*",SearchOption.AllDirectories))
        {var destination=Path.Combine(target,Path.GetRelativePath(prepared,path));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(path,destination);}
        actualPayload=target;Publish();
    }
    internal VerifiedProcessSpec RawSpec(WindowsFileBoundary.DataLease lease)=>new(Path.Combine(Package,"runtime/node/node.exe"),Hash(File.ReadAllBytes(Path.Combine(Package,"runtime/node/node.exe"))),["--import","tsx","src/main.ts"],Path.Combine(Package,"app"),Path.Combine(Root,"temporary"),Path.Combine(Package,"runtime/ffmpeg/ffmpeg.exe"),new Dictionary<string,string>{{"DATA_DIR",Data},{"MASTER_KEY_FILE",Path.Combine(Dataset,"master-key")}},lease);
    internal static string Hash(byte[] value)=>Convert.ToHexStringLower(SHA256.HashData(value));
    private static void Copy(string source,string target){Directory.CreateDirectory(target);foreach(var file in Directory.GetFiles(source))File.Copy(file,Path.Combine(target,Path.GetFileName(file)));}
    public void Dispose(){} // synthetic data stays private; never included in source or runtime artifacts
}

internal sealed class StartFailureAuthorization(IPrivateAppLauncher launcher,StartReservation resource):IServiceStartAuthorizer,IAppRecoveryBarrier
{
    public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)=>Task.FromResult(new AuthorizedServiceStart(new("handoff-failure"),launcher,this,resource));
    public Task RequireStartAllowedAsync(AppRelease release,CancellationToken cancellation){cancellation.ThrowIfCancellationRequested();return Task.CompletedTask;}
}
internal sealed class StartReservation(WindowsFileBoundary.DataLease lease,JobProcessFaultInjection fault):IDisposable
{
    internal WindowsFileBoundary.DataLease Lease=>lease;internal bool Released;
    public void Dispose(){if(Released)return;if(!fault.RootExitObserved||!fault.TreeEmptyObserved)throw new Exception("reservation release before native exit observations");lease.Dispose();Released=true;}
}

internal sealed class ProbeAuthorization(ProductionFixture fixture,JobProcessFaultInjection fault):IServiceStartAuthorizer,IObservedAdmission
{
    private readonly InstalledActivationResolver resolver=fixture.CreateResolver(fault);public TimeSpan AdmissionBudget=>resolver.AdmissionBudget;
    public event Action? ObservedWork{add=>resolver.ObservedWork+=value;remove=>resolver.ObservedWork-=value;}
    public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)=>Task.Run(()=>resolver.ResolveAsync(fixture.Binding,cancellation),cancellation);
}

internal sealed class ApprovedOperations(string installation,Func<DateTimeOffset> clock):IOwnerOperationAuthentication
{
    private readonly Dictionary<string,long> approved=new(StringComparer.Ordinal);internal string? Wrong;internal bool AllowReconcile=true;
    internal void Approve(ProtectedOperation op)=>approved[InstalledActivationResolver.Hash(JsonSerializer.SerializeToUtf8Bytes(op))]=clock().AddSeconds(-1).ToUnixTimeSeconds();
    public Task<VerifiedOperationOwner> AuthenticateAsync(string digest,string scope,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();if(!approved.TryGetValue(digest,out var time)||scope=="reconcile"&&!AllowReconcile)throw new UnauthorizedAccessException("SYNTHETIC_AUTH_REFUSED");
        return Task.FromResult(new VerifiedOperationOwner("boundary-owner",Wrong=="principal-installation"?Guid.NewGuid().ToString("N"):installation,Wrong=="operation-digest"?new string('0',64):digest,Wrong=="scope"?"child":scope,time,time+3600));
    }
}

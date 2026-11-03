using Yahad.Authority;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;

if(args is ["--fixture-creation-cleanup-only",var fixtureRepo,var muxer])
{
    var scratch=OwnedFixtureDirectory.Create(Path.GetTempPath());var passed=0;var failed=0;
    try
    {
        var badNode=Path.Combine(scratch.Root,"not-node.exe");File.WriteAllBytes(badNode,[1,2,3]);
        var helperDirectory=Path.Combine(scratch.Root,"helper");Directory.CreateDirectory(helperDirectory);File.WriteAllText(Path.Combine(helperDirectory,"Yahad.Platform.exe"),"synthetic-not-executed");
        foreach(var scenario in new[]{"constructor","copy","seed"})
        {
            string? captured=null;var refused=false;
            try
            {
                using var unexpected=await ProductionFixture.Create(scenario=="seed"?muxer:badNode,fixtureRepo,scenario=="copy"?Path.Combine(scratch.Root,"missing-helper"):helperDirectory,
                    path=>{captured=path;if(scenario=="constructor")throw new IOException("TEST_CONSTRUCTOR_FAILURE");});
            }
            catch(Exception error){refused=true;Console.WriteLine("EXPECTED "+scenario+" "+error.GetType().Name);}
            if(refused&&captured is not null&&!Directory.Exists(captured)){passed++;Console.WriteLine("PASS "+scenario+" failure removes only owned fixture");}
            else{failed++;Console.WriteLine("FAIL "+scenario+" left fixture or unexpectedly succeeded");}
        }
    }
    finally{scratch.Cleanup(()=>true);}
    Console.WriteLine(JsonSerializer.Serialize(new{suite="fixture-creation-failure-cleanup",passed,failed,realServices=false}));return failed==0?0:1;
}

if(args is ["--store-schema-only",var schemaRepo])
{
    foreach(var expected in new[]{5,6,17})
        if(ProductionFixture.ParseSourceSchema($"// fixture\nexport const DATABASE_SCHEMA = {expected};\n")!=expected)throw new IOException("STORE_SCHEMA_PARSER");
    foreach(var invalid in new[]{"export const DATABASE_SCHEMA = 0;","export const DATABASE_SCHEMA = 6 + 1;","export const DATABASE_SCHEMA = 6;\nexport const DATABASE_SCHEMA = 7;","// export const DATABASE_SCHEMA = 6;","export const DATABASE_SCHEMA = 999999999999999999999;"})
    {
        var refused=false;try{ProductionFixture.ParseSourceSchema(invalid);}catch(IOException){refused=true;}
        if(!refused)throw new IOException("STORE_SCHEMA_PARSER_ADMITTED_INVALID_SOURCE");
    }
    var observed=ProductionFixture.ReadSourceSchema(schemaRepo);
    Console.WriteLine(JsonSerializer.Serialize(new{suite="production-store-schema-source-pure",ok=true,schema=observed,declaredSchemasTested=new[]{5,6,17},nativeOperations=false,storeOpened=false}));return 0;
}
var endpointOnly=args is ["--endpoint-only",..];if(endpointOnly)args=args[1..];bool endpointChecks=false;
string? preparedPayload=null;
if(args.Length==10&&args[^2]=="--prepared"){preparedPayload=args[^1];args=args[..^2];}
if(args is not ["--node",var node,"--repo",var repo,"--helper",var helper,"--report",var report])throw new IOException("PRIVATE_INPUTS_REQUIRED");
var results=new List<object>();var failures=0;var blocked=0;var fixtureBlocked=false;ProductionFixture? fixtureOwner=null;
void Assert(bool b,string reason="assertion failed"){if(!b)throw new Exception(reason);}
async Task Check(string name,Func<Task> run)
{
 if(endpointOnly&&!endpointChecks)return;
 if(fixtureBlocked){blocked++;results.Add(new{name,status="BLOCKED",reason="prior fixture failure; no further heavy cases"});Console.WriteLine("BLOCKED "+name);return;}
 try{await run();results.Add(new{name,status="PASS"});Console.WriteLine("PASS "+name);}
 catch(Exception failure)
 {
  failures++;fixtureBlocked=true;results.Add(new{name,status="FAIL",reason=failure.GetType().Name});Console.WriteLine("FAIL "+name+" "+failure);
  fixtureOwner?.RetainFailure(failure);throw new FixtureRunStopped();
 }
}
async Task Reject(Func<Task> run,string? code=null)
{try{await run();}catch(Exception e)when(e is IOException or Win32Exception or JsonException or CryptographicException or FormatException){if(code is not null&&e.Message!=code)throw new Exception("wrong refusal: "+e.Message);return;}throw new Exception("unexpected admission");}
var f=await ProductionFixture.Create(node,repo,helper);
fixtureOwner=f;
try{
await Check("production admin boundary rejects current-user fixture records",async()=>{await Reject(async()=>{using var grant=await new InstalledActivationResolver(f.Root,new AdminInstalledControlFiles(),f.Control,()=>f.Now).ResolveAsync(f.Binding,default);});});
await Check("production resolver refuses absent protected activation state",async()=>{var binding=f.Binding;var bytes=File.ReadAllBytes(f.PathOf("activation.json"));File.Delete(f.PathOf("activation.json"));try{await Reject(async()=>{using var grant=await new InstalledActivationResolver(f.Root,new PrivateControlFiles(),f.Control,()=>f.Now,connectorProfile:RuntimeConnectorProfile.Synthetic).ResolveAsync(binding,default);});}finally{File.WriteAllBytes(f.PathOf("activation.json"),bytes);}});
await Check("stale bootstrap digest cannot select a different protected activation",async()=>
{var binding=f.Binding with{ActivationReceiptDigest=new string('0',64)};await Reject(async()=>{using var grant=await new InstalledActivationResolver(f.Root,new PrivateControlFiles(),f.Control,()=>f.Now,connectorProfile:RuntimeConnectorProfile.Synthetic).ResolveAsync(binding,default);},"INSTALLED_ACTIVATION_ANCHOR");});
await Check($"same concrete resolver reads fixture capabilities and observes actual Store schema {f.Schema}",async()=>{using var grant=await f.Resolve();await grant.Recovery.RequireStartAllowedAsync(grant.Release,default);Assert(f.Control.Creates==0);});
foreach(var mutation in new[]{"epoch","head","policy","pin","pending","phase"})
await Check("protected-state binding refuses "+mutation,async()=>
{
    f.Reset();try
    {
        if(mutation=="epoch")f.AnchorEpoch=2;if(mutation=="head")f.AnchorDigest=new string('0',64);if(mutation=="phase")f.Phase="prepared";
        f.Publish();if(mutation=="policy")File.AppendAllText(f.Policy,"changed");if(mutation=="pin")File.AppendAllText(Path.Combine(f.Dataset,"update-pin"),"changed");if(mutation=="pending")File.WriteAllText(f.PathOf("PENDING.JSON"),"interrupted");
        await Reject(async()=>{using var grant=await f.Resolve();});
    }finally{f.Reset();}
});
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
    var launcher=f.CreateLauncher(spec);IContainedAppSession? session=null;
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
        await Reject(()=>{_ =f.StartJob(node,["-e","require('node:fs').writeFileSync(process.argv[1],'ran')",Path.Combine(f.Data,"native-ready")],f.Root,LaunchEnvironment.Clean(f.Root),(process,pid)=>
        {witness=System.Diagnostics.Process.GetProcessById(pid);_ =witness.SafeHandle;_ =lease.DuplicateTo(process);throw new IOException("INJECTED_CALLBACK_FAILURE");});return Task.CompletedTask;},"INJECTED_CALLBACK_FAILURE");
        Assert(witness is not null&&witness.WaitForExit(0)&&!File.Exists(Path.Combine(f.Data,"native-ready")));
    }
    finally{witness?.Dispose();}
});
NativeBackupResult? backup=null;NativeRestoreResult? restored=null;
await Check("actual recovery output is verified but cannot start before protected commit",async()=>
{
    f.Reset();f.ClearSignals();using var empty=f.StartJob(node,["-e",""],f.Root,LaunchEnvironment.Clean(f.Root));await empty.WaitAsync(default);await empty.WaitForTreeEmptyAsync(default);
    using(var inspector=new NativeSqliteInspector(node,ProductionFixture.Hash(File.ReadAllBytes(node))))
    using(var recovery=await NativeRecoveryFixture.OpenAsync(f.Dataset,empty.Process,inspector))
    {backup=await recovery.BackupAsync();restored=await recovery.RestoreToNewDirectoryAsync(backup.BackupId,f.Schema,f.Schema);}
    f.Recovery=new(f.OriginalDataId,backup.BackupId,restored.OperationId,backup.ReceiptDigest);f.DataId=Guid.NewGuid().ToString("N");f.Phase="recovered";f.Publish();
    await Reject(async()=>{using var grant=await f.Resolve();},"INSTALLED_RECOVERY_COMMIT_REQUIRED");Assert(restored.ObservedSchema==f.Schema);
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
using var writerJob=f.StartJob(node,["-e",""],f.Root,LaunchEnvironment.Clean(f.Root));await writerJob.WaitAsync(default);await writerJob.WaitForTreeEmptyAsync(default);
void PrepareState(){f.Reset();File.WriteAllBytes(f.PathOf("bootstrap.json"),JsonSerializer.SerializeToUtf8Bytes(new{schema=1,service=ServiceDefinition.Name,installationId=f.InstallationId,activationReceiptDigest=f.Binding.ActivationReceiptDigest}));}
ProtectedOperation Operation(string kind="activate")
{
    var head=NativeJson.Read<InstalledActivation>(File.ReadAllBytes(f.PathOf("activation.json")));var web=NativeJson.Read<InstalledWeb>(File.ReadAllBytes(f.PathOf("web.json")));
    return new(1,kind,f.InstallationId,head.Epoch,f.Binding.ActivationReceiptDigest,head.ReleaseDigest,head.DataId,head.MinimumSchema,head.MaximumSchema,head.MasterKeyDigest,head.PolicyDigest,head.PinDigest,web,null,f.Now.AddMinutes(2).ToUnixTimeSeconds(),Guid.NewGuid().ToString("N"));
}
async Task Denied(Func<Task> run){try{await run();}catch(Exception e)when(e is ServiceStartDenied or IOException or Win32Exception or UnauthorizedAccessException or JsonException or OperationCanceledException){return;}throw new Exception("transaction unexpectedly accepted");}
ProtectedStateTransactionWriter Writer(IOperationAuthentication auth,JobProcess? job=null)=>new(f.Root,f.InstallationId,job??writerJob.Process,auth,f.Control,()=>f.Now);
await Check("protected transaction publishes coherent records consumed by actual resolver",async()=>
{
    PrepareState();var op=Operation();var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);var binding=await Writer(auth).CommitAsync(op);
    Assert(binding==f.Binding);using var grant=await f.Resolve();
    Assert(NativeJson.Read<InstalledEpoch>(File.ReadAllBytes(f.PathOf("epoch.json"))).Epoch==2&&!File.Exists(f.PathOf("pending.json")));
});
await Check("transaction nonce is single-use even for a newly authenticated replay",async()=>
{
    PrepareState();var op=Operation();var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);var writer=Writer(auth);await writer.CommitAsync(op);await Denied(()=>writer.CommitAsync(op));
});
await Check("client cannot supply approval principal or grant scope fields",async()=>
{
    PrepareState();var document=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(Operation()))!.AsObject();document["Subject"]="client-principal";await Denied(()=>{NativeJson.Read<ProtectedOperation>(JsonSerializer.SerializeToUtf8Bytes(document));return Task.CompletedTask;});
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
    using var busy=f.StartJob(node,["-e","setInterval(()=>{},1000)"],f.Root,LaunchEnvironment.Clean(f.Root));
    try{using var bound=new CancellationTokenSource(150);await Denied(()=>Writer(auth,busy.Process).CommitAsync(op,cancellation:bound.Token));Assert(!File.Exists(f.PathOf("pending.json")));}
    finally{await busy.TerminateAndWaitForTreeEmptyAsync(default);}
});
await Check("actual selected-data lease blocks publication",async()=>
{
    PrepareState();var op=Operation();var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);using var lease=WindowsFileBoundary.AcquireDataLease(f.Data);await Denied(()=>Writer(auth).CommitAsync(op));Assert(!File.Exists(f.PathOf("pending.json")));
});
foreach(var point in new[]{"pending-published","nonce-reserved","record-web.json","partial-activation.json","record-activation.json","record-epoch.json","record-bootstrap.json","commit-receipt"})
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
    var fresh=await ProductionFixture.Create(node,repo,helper);Exception? freshFailure=null;try{var head=NativeJson.Read<InstalledActivation>(File.ReadAllBytes(fresh.PathOf("activation.json")));var web=NativeJson.Read<InstalledWeb>(File.ReadAllBytes(fresh.PathOf("web.json")));
    var op=new ProtectedOperation(1,"install",fresh.InstallationId,0,"",head.ReleaseDigest,head.DataId,fresh.Schema,fresh.Schema,head.MasterKeyDigest,head.PolicyDigest,head.PinDigest,web,null,fresh.Now.AddMinutes(2).ToUnixTimeSeconds(),Guid.NewGuid().ToString("N"));
    foreach(var name in new[]{"activation.json","epoch.json","web.json","bootstrap.json"})File.Delete(fresh.PathOf(name));
    var auth=new ApprovedOperations(fresh.InstallationId,()=>fresh.Now);auth.Approve(op);await new ProtectedStateTransactionWriter(fresh.Root,fresh.InstallationId,writerJob.Process,auth,fresh.Control,()=>fresh.Now).CommitAsync(op);
    using var grant=await fresh.Resolve();Assert(NativeJson.Read<InstalledEpoch>(File.ReadAllBytes(fresh.PathOf("epoch.json"))).Epoch==1);
    }catch(Exception failure){freshFailure=failure;fresh.RetainFailure(failure);}
    try{fresh.Dispose();}catch(Exception cleanup){if(freshFailure is not null)throw new AggregateException(freshFailure,cleanup);throw;}
    if(freshFailure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(freshFailure).Throw();
});
await Check("install cannot reset an authority root with existing committed history",async()=>
{
    PrepareState();var op=Operation("install") with{ExpectedEpoch=0,ExpectedActivationDigest=""};var before=new Dictionary<string,byte[]>();
    foreach(var name in new[]{"activation.json","epoch.json","web.json","bootstrap.json"}){before[name]=File.ReadAllBytes(f.PathOf(name));File.Delete(f.PathOf(name));}
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
    PrepareState();var before=f.Binding;await Denied(()=>Writer(new UnconfiguredOperationAuthentication()).CommitAsync(Operation()));
    Assert(before==f.Binding&&!File.Exists(f.PathOf("pending.json")));
});
await Check("recovery commit is bound to actual backup receipt and verified restored plaintext",async()=>
{
    PrepareState();var op=Operation("recover");NativeBackupResult b;NativeRestoreResult restoredCandidate;
    using(var inspector=new NativeSqliteInspector(node,ProductionFixture.Hash(File.ReadAllBytes(node))))using(var recovery=await NativeRecoveryFixture.OpenAsync(f.Dataset,writerJob.Process,inspector))
    {b=await recovery.BackupAsync();restoredCandidate=await recovery.RestoreToNewDirectoryAsync(b.BackupId,f.Schema,f.Schema);}
    op=op with{DataId=Guid.NewGuid().ToString("N"),Recovery=new(f.OriginalDataId,b.BackupId,restoredCandidate.OperationId,b.ReceiptDigest)};
    var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);await Writer(auth).CommitAsync(op);using var grant=await f.Resolve();
    Assert(NativeJson.Read<InstalledActivation>(File.ReadAllBytes(f.PathOf("activation.json"))).Phase=="committed");
});
await Check("production default still denies while real committer/channel is unconfigured",async()=>
{await Denied(()=>new InstalledActivationResolver().ResolveAsync(f.Binding,default));});

const string ForeignSid="S-1-5-21-100-101-102-1001",RuntimeSid="S-1-5-80-1-2-3-4-5",AuthoritySid="S-1-5-80-6-7-8-9-10";
var foreignPeer=new AuthorityPeer(ForeignSid,111,123,1,true);var runtimePeer=new AuthorityPeer(RuntimeSid,222,124,0,false);
await Check("actual role validator allows only the exact authority/runtime file class",()=>
{
 var roles=new InstalledFileRoles(new(AuthoritySid),new(RuntimeSid));
 var valid=new RawSecurityDescriptor($"O:{AuthoritySid}D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1301bf;;;{AuthoritySid})(A;;0x1200a9;;;{RuntimeSid})");roles.Validate(valid,InstalledFileRole.Control);
 foreach(var bad in new[]{new RawSecurityDescriptor($"O:{RuntimeSid}D:P(A;;FR;;;{RuntimeSid})"),new RawSecurityDescriptor($"O:{AuthoritySid}D:P(A;;FA;;;{RuntimeSid})"),new RawSecurityDescriptor($"O:{AuthoritySid}D:P(A;;FR;;;WD)")})
 {try{roles.Validate(bad,InstalledFileRole.Control);throw new Exception("role accepted");}catch(IOException){}}
 try{roles.Validate(valid,InstalledFileRole.Anchor);throw new Exception("mutable anchor accepted");}catch(IOException){}return Task.CompletedTask;
});
await Check("runtime-created inherited directories are accepted only in runtime-writable roles and stay mask-bounded",()=>
{
 var roles=new InstalledFileRoles(new(AuthoritySid),new(RuntimeSid));
 // Exactly what node's mkdir under the provisioned temporary root produces.
 var inherited=new RawSecurityDescriptor($"O:{RuntimeSid}D:(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)(A;OICIID;0x1200a9;;;{AuthoritySid})(A;OICIID;0x1301bf;;;{RuntimeSid})");
 roles.Validate(inherited,InstalledFileRole.Data);roles.Validate(inherited,InstalledFileRole.Lease);
 foreach(var role in new[]{InstalledFileRole.Control,InstalledFileRole.Anchor,InstalledFileRole.Payload,InstalledFileRole.MasterKey,InstalledFileRole.RecoveryEvidence})
 {try{roles.Validate(inherited,role);throw new Exception("unprotected "+role+" directory accepted");}catch(IOException){}}
 var widened=new RawSecurityDescriptor($"O:{RuntimeSid}D:(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)(A;OICIID;FA;;;{RuntimeSid})");
 try{roles.Validate(widened,InstalledFileRole.Data);throw new Exception("widened inherited data accepted");}catch(IOException){}
 var foreign=new RawSecurityDescriptor($"O:{RuntimeSid}D:(A;OICIID;FA;;;SY)(A;OICIID;0x1200a9;;;{ForeignSid})");
 try{roles.Validate(foreign,InstalledFileRole.Data);throw new Exception("foreign inherited data accepted");}catch(IOException){}return Task.CompletedTask;
});
await Check("bounded protocol refuses oversized duplicate and forged principal fields",async()=>
{
 await Denied(()=>{AuthorityProtocol.Request(new byte[32769]);return Task.CompletedTask;});
 foreach(var text in new[]{"{\"Format\":1,\"Format\":1}","{\"Format\":1,\"Id\":\""+Guid.NewGuid().ToString("N")+"\",\"Action\":\"install\",\"TargetDigest\":\""+new string('a',64)+"\",\"Mode\":null,\"Subject\":\"principal\"}"})await Denied(()=>{AuthorityProtocol.Request(System.Text.Encoding.UTF8.GetBytes(text));return Task.CompletedTask;});
});
await Check("revocation during publication is rechecked before further protected writes",async()=>
{
 PrepareState();var op=Operation();var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);
 await Denied(()=>Writer(auth).CommitAsync(op,p=>{if(p=="record-web.json")auth.Revoke(op);}));
 Assert(File.Exists(f.PathOf("pending.json")));await Denied(()=>f.Resolve());await Denied(()=>Writer(auth).ResumeAsync(op));
});
await Check("committed update retains actual recovery selection without authorizing another restore",async()=>
{
 PrepareState();NativeBackupResult saved;NativeRestoreResult recovered;
 using(var inspector=new NativeSqliteInspector(node,ProductionFixture.Hash(File.ReadAllBytes(node))))using(var recovery=await NativeRecoveryFixture.OpenAsync(f.Dataset,writerJob.Process,inspector))
 {saved=await recovery.BackupAsync();recovered=await recovery.RestoreToNewDirectoryAsync(saved.BackupId,f.Schema,f.Schema);}
 f.ResetRecovery(saved,recovered);f.Phase="committed";f.Publish();File.WriteAllBytes(f.PathOf("bootstrap.json"),JsonSerializer.SerializeToUtf8Bytes(new{schema=1,service=ServiceDefinition.Name,installationId=f.InstallationId,activationReceiptDigest=f.Binding.ActivationReceiptDigest}));
 f.ClearSignals();var controller=new ServiceController(new DirectAuthorization(f));var result=await controller.RunAsync(r=>{if(r.State==ServiceState.Running)controller.Control(ServiceDefinition.Stop);});Assert(result.Failure==ServiceFailure.None,"recovered routine startup failure="+result.Failure+" stop="+f.Control.StopError);
 var op=Operation() with{Recovery=f.Recovery};var auth=new ApprovedOperations(f.InstallationId,()=>f.Now);auth.Approve(op);
 var before=File.ReadAllBytes(Path.Combine(f.Data,"app.sqlite"));await Writer(auth).CommitAsync(op);using(var grant=await f.Resolve()){}
 Assert(before.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Data,"app.sqlite"))));
 var unapproved=Operation() with{Recovery=f.Recovery! with{ReceiptDigest=new string('0',64)}};
 await Denied(()=>Writer(auth).CommitAsync(unapproved));
 await Denied(()=>Writer(new ApprovedOperations(f.InstallationId,()=>f.Now)).CommitAsync(Operation()));
});
await Check("runtime launch gate wraps the actual service controller and mutable Store path",async()=>
{
 PrepareState();f.ClearSignals();var kernel=new FakeRuntimeKernel();var gate=new RuntimeAuthorityGate(kernel);gate.Admit(kernel.Generation);
 var controller=new ServiceController(new GatedRuntimeStartAuthorizer(new DirectAuthorization(f),gate,kernel.Generation));
 var result=await controller.RunAsync(r=>{if(r.State==ServiceState.Running)controller.Control(ServiceDefinition.Stop);});Assert(result.Failure==ServiceFailure.None&&f.Control.HeldAfterClose);
});
await Check("runtime gate denies launch before assignment and requires observed tree and host exit",async()=>
{
 var k=new FakeRuntimeKernel();var gate=new RuntimeAuthorityGate(k);await Denied(()=>{gate.RequireLaunch(k.Generation);return Task.CompletedTask;});gate.Admit(k.Generation);gate.RequireLaunch(k.Generation);
 await Denied(()=>{gate.RequireQuiescence();return Task.CompletedTask;});k.Exit=true;await Denied(()=>{gate.RequireQuiescence();return Task.CompletedTask;});k.Empty=true;gate.RequireQuiescence();
 await Denied(()=>{gate.Admit(k.Generation);return Task.CompletedTask;});k.Assigned=false;await Denied(()=>{gate.RequireLaunch(k.Generation);return Task.CompletedTask;});
});
await Check("runtime gate preserves uncertain state after assignment failure",async()=>
{
 var k=new FakeRuntimeKernel{RefuseAssignment=true};var gate=new RuntimeAuthorityGate(k);await Denied(()=>{gate.Admit(k.Generation);return Task.CompletedTask;});await Denied(()=>{gate.RequireLaunch(k.Generation);return Task.CompletedTask;});await Denied(()=>{gate.RequireQuiescence();return Task.CompletedTask;});
});
await Check("peer adapter reverts on exceptions and refuses failed identification or token reversion",async()=>
{
 using var noPipe=new Microsoft.Win32.SafeHandles.SafePipeHandle(IntPtr.Zero,false);
 var api=new FakePeerApi(foreignPeer){ThrowIdentity=true};await Denied(()=>{new WindowsAuthorityPeer(api,()=>{}).Read(noPipe);return Task.CompletedTask;});Assert(api.Reverts==1);
 api=new(foreignPeer){IdentifyAllowed=false};await Denied(()=>{new WindowsAuthorityPeer(api,()=>{}).Read(noPipe);return Task.CompletedTask;});Assert(api.Reverts==0);
 var fatal=false;api=new(foreignPeer){RevertAllowed=false};await Denied(()=>{new WindowsAuthorityPeer(api,()=>fatal=true).Read(noPipe);return Task.CompletedTask;});Assert(fatal);
});


await Check("shared typed job accounting reads active at 40 and ignores nonzero lifetime total",async()=>
{
 Assert(System.Runtime.InteropServices.Marshal.SizeOf<JobAccounting>()==48);
 Assert(System.Runtime.InteropServices.Marshal.OffsetOf<JobAccounting>(nameof(JobAccounting.TotalProcesses)).ToInt32()==36);
 Assert(System.Runtime.InteropServices.Marshal.OffsetOf<JobAccounting>(nameof(JobAccounting.ActiveProcesses)).ToInt32()==40);
 Assert(new JobAccounting{TotalProcesses=17,ActiveProcesses=0}.TreeEmpty);
 Assert(!new JobAccounting{TotalProcesses=17,ActiveProcesses=1}.TreeEmpty);
 using var job=f.StartJob(node,["-e",""],f.Root,LaunchEnvironment.Clean(f.Root));await job.WaitAsync(default);await job.WaitForTreeEmptyAsync(default);
 var actual=job.Process.ObserveAccounting();Assert(actual.TotalProcesses>0&&actual.ActiveProcesses==0&&actual.TreeEmpty);
});

endpointChecks=true;
int projectedRoles=0;
InstalledAuthorityComposition Composition(bool reset=true)
{
 if(reset)
 {
  PrepareState();Directory.CreateDirectory(f.PathOf("intake"));Directory.CreateDirectory(f.PathOf(".authority"));
  foreach(var pattern in new[]{"rpc-*.json","done-*.json","cancel-*.json"})foreach(var path in Directory.GetFiles(f.PathOf(".authority"),pattern))File.Delete(path);
  foreach(var path in Directory.GetFiles(f.PathOf("intake"),"mode.json.partial-*"))File.Delete(path);
  File.WriteAllBytes(f.PathOf("intake/mode.json"),JsonSerializer.SerializeToUtf8Bytes(new NativeUpdateMode(2,f.InstallationId,"notify",null,true,"system")));
 }
 var op=Operation();var anchor=new InstalledAuthorityAnchor(1,f.InstallationId,ServiceDefinition.Name,InstalledAuthorityReader.AuthorityService,op.PolicyDigest,op.PinDigest,ProductionFixture.Hash(File.ReadAllBytes(node)),new string('d',64));
 var bytes=JsonSerializer.SerializeToUtf8Bytes(anchor);File.WriteAllBytes(f.PathOf("installation.json"),bytes);
 return InstalledAuthorityComposition.PrivateFixture(f.Root,ProductionFixture.Hash(bytes),RuntimeSid,node,anchor.ProbeSha256!,(_,_)=>projectedRoles++);
}
byte[] Frame(string id,string method,object body)=>JsonSerializer.SerializeToUtf8Bytes(new AuthorityFrame(1,id,method,JsonSerializer.SerializeToElement(body)));
await Check("router rejects every peer but the one runtime service and client authority fields",async()=>
{
 var c=Composition();var frame=Frame(Guid.NewGuid().ToString("N"),"status",new{});
 foreach(var peer in new[]{foreignPeer,runtimePeer with{Sid=AuthoritySid},runtimePeer with{Interactive=true},runtimePeer with{Session=1},runtimePeer with{ProcessId=0},runtimePeer with{Created=0}})await Denied(()=>c.Router.DispatchAsync(peer,frame));
 Assert((await c.Router.DispatchAsync(runtimePeer,frame)).State=="observed");
 var id=Guid.NewGuid().ToString("N");await Denied(()=>c.Router.DispatchAsync(runtimePeer,Frame(id,"request",new{Format=1,Id=id,Action="check",TargetDigest=(string?)null,Mode=(string?)null,Subject=ForeignSid})));
});
await Check("same bounded connection parser works in memory and refuses oversized frame",async()=>
{
 var c=Composition();var bytes=Frame(Guid.NewGuid().ToString("N"),"status",new{});var encoded=new byte[bytes.Length+4];System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(encoded,bytes.Length);bytes.CopyTo(encoded,4);
 using var stream=new MemoryStream();stream.Write(encoded);stream.Position=0;await AuthorityEndpointConnection.ServeAsync(stream,c.Router,runtimePeer,default);
 stream.Position=encoded.Length;var header=new byte[4];stream.ReadExactly(header);var n=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);var reply=new byte[n];stream.ReadExactly(reply);Assert(NativeJson.Read<EndpointReply>(reply).State=="observed");
 using var bad=new MemoryStream(BitConverter.GetBytes(32769));await Denied(()=>AuthorityEndpointConnection.ServeAsync(bad,c.Router,runtimePeer,default));
});

await Check("synthetic installed-role projection is actually enforced at the composition boundary",async()=>
{
 var c=Composition();Assert(c.Files.SecretClass(f.PathOf("bootstrap.json"),SecretRole.SetupCapability)==InstalledFileRole.Control);Assert(c.Files.SecretClass(f.PathOf("installation.json"),SecretRole.SetupCapability)==InstalledFileRole.Anchor);Assert(c.Files.SecretClass(f.PathOf("datasets/anything/master-key"),SecretRole.MasterKey)==InstalledFileRole.MasterKey);var bytes=File.ReadAllBytes(f.PathOf("installation.json"));
 await Denied(()=>{InstalledAuthorityComposition.PrivateFixture(f.Root,ProductionFixture.Hash(bytes),RuntimeSid,node,ProductionFixture.Hash(File.ReadAllBytes(node)),(_,_)=>throw new IOException("synthetic role denied"));return Task.CompletedTask;});
 await Denied(()=>{InstalledAuthorityComposition.PrivateFixture(f.Root,new string('0',64),RuntimeSid,node,ProductionFixture.Hash(File.ReadAllBytes(node)),(_,_)=>{});return Task.CompletedTask;});
});

if(preparedPayload is not null)await Check("exact rebuilt full payload passes installed release and runtime admission without app launch",async()=>
{
    f.Reset();f.SelectPreparedPayload(preparedPayload);var admission=new VerificationOnlyAuthorization(f);var reports=new List<ServiceReport>();
    var timer=System.Diagnostics.Stopwatch.StartNew();var controller=new ServiceController(admission);
    var result=await controller.RunAsync(r=>{reports.Add(r);if(r.State==ServiceState.Running)controller.Control(ServiceDefinition.Stop);});
    if(result.Failure!=ServiceFailure.None||!reports.Any(r=>r.State==ServiceState.Running))throw new Exception("full startup path failed: "+result.Failure);
    Console.WriteLine("FULL_ADMISSION_SECONDS "+timer.Elapsed.TotalSeconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+" CHECKPOINTS "+reports.Count(r=>r.State==ServiceState.StartPending));
    Assert(reports.Count(r=>r.State==ServiceState.StartPending)>3);
});
}catch(FixtureRunStopped){}
finally{try{f.Dispose();}catch(Exception cleanup){failures++;results.Add(new{name="final fixture teardown",status="FAIL",reason=cleanup.GetType().Name});Console.WriteLine("FAIL final fixture teardown "+cleanup);}}
File.WriteAllText(report,JsonSerializer.Serialize(new{kind="concrete-installed-adapters-synthetic-inputs",passed=results.Count-failures-blocked,failed=failures,blocked,aborted=fixtureBlocked,
    productionEntryWired=true,productionAuthorityEntryEnabled=false,endpointOnly,actualAdminProvisioningTested=false,actualStoreWrites=true,actualReceiptProducer=true,scmInvoked=false,networkListenersOpened=false,
    productionTrustOrOwnerStateCreated=false,fullRebuiltPayloadInspected=preparedPayload is not null,fixtureRoot=f.Root,tests=results},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{results.Count-failures-blocked} passed; {failures} failed; {blocked} blocked. No SCM or network listeners.");return failures==0&&blocked==0?0:1;

internal sealed class PrivateControlFiles:IInstalledControlFiles
{public byte[] Read(string path,int bound,ICollection<IDisposable> held){var file=WindowsFileBoundary.OpenPrivateRead(path);held.Add(file);return file.Small(bound);}}
internal sealed class DirectAuthorization(ProductionFixture fixture):IServiceStartAuthorizer,IObservedAdmission
{
    private readonly FixtureProcesses.FixtureResolver resolver=fixture.CreateResolver();public TimeSpan AdmissionBudget=>resolver.AdmissionBudget;
    public event Action? ObservedWork{add=>resolver.ObservedWork+=value;remove=>resolver.ObservedWork-=value;}
    public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)=>Task.Run(()=>resolver.ResolveAsync(fixture.Binding,cancellation),cancellation);
}
internal sealed class VerificationOnlyAuthorization(ProductionFixture fixture):IServiceStartAuthorizer,IObservedAdmission
{
    private readonly FixtureProcesses.FixtureResolver resolver=fixture.CreateResolver();public TimeSpan AdmissionBudget=>resolver.AdmissionBudget;
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
    private sealed class Channel(FileControl owner,ProductionFixture fixture):IChildControl,IUpdateIdleSession
    {
        // Synthetic file-controlled Store worker, not installed/browser idle acceptance.
        public Task<bool> PrepareUpdateAsync(string id,CancellationToken cancellation){cancellation.ThrowIfCancellationRequested();return Task.FromResult(true);}
        public Task CancelUpdateAsync(string id,CancellationToken cancellation){cancellation.ThrowIfCancellationRequested();return Task.CompletedTask;}
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
    private readonly OwnedFixtureDirectory owned;
    private readonly FixtureProcesses processes=new();
    private bool disposed;
    private bool cleanupAttempted;private Exception? cleanupFailure;
    internal readonly string Root,Dataset,OriginalDataId=Guid.NewGuid().ToString("N"),InstallationId=Guid.NewGuid().ToString("N"),Payload,Node,Repo;
    internal string DataId,Phase="committed";internal long AnchorEpoch=1;internal string? AnchorDigest;
    internal int Schema {get;}
    internal InstalledRecovery? Recovery;internal DateTimeOffset Now=DateTimeOffset.UtcNow;internal readonly FileControl Control;
    internal string Data=>Recovery is null?Path.Combine(Dataset,"data"):Path.Combine(Dataset,"restore-"+Recovery.OperationId,"completed","data");
    internal string Policy=>Path.Combine(Dataset,"update-policy");private string release="";
    private ProductionFixture(string repo,Action<string>? allocated=null)
    {
        Repo=repo;Schema=ReadSourceSchema(repo);owned=OwnedFixtureDirectory.Create(Path.GetTempPath());Root=owned.Root;
        try{
        allocated?.Invoke(Root);
        Directory.CreateDirectory(Path.Combine(Root,"datasets"));
        var source=SafeDirectory.CreatePrivateSession(Path.Combine(Root,"datasets"));Dataset=Path.Combine(Root,"datasets",OriginalDataId);
        if(!Path.GetFullPath(source).StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!Path.GetFullPath(Dataset).StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("TEST_MOVE_SCOPE");Directory.Move(source,Dataset);
        DataId=OriginalDataId;Payload=Path.Combine(Root,"payload-stage");Directory.CreateDirectory(Payload);Node=Path.Combine(Payload,"runtime/node/node.exe");Directory.CreateDirectory(Path.GetDirectoryName(Node)!);Control=new(this);
        Directory.CreateDirectory(Path.Combine(Root,"temporary"));Directory.CreateDirectory(Data);File.WriteAllText(Path.Combine(Dataset,"master-key"),Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));File.WriteAllText(Policy,"synthetic-policy");File.WriteAllText(Path.Combine(Dataset,"update-pin"),"synthetic-pin");
        }catch(Exception failure){try{owned.Cleanup(()=>true);}catch(Exception cleanup){throw new AggregateException(failure,cleanup);}throw;}
    }
    internal static async Task<ProductionFixture> Create(string node,string repo,string helper,Action<string>? allocated=null)
    {
        var f=new ProductionFixture(repo,allocated);
        try{
        f.probe=node;f.probeDigest=Hash(File.ReadAllBytes(node));File.Copy(node,f.Node);Copy(helper,Path.Combine(f.Payload,"app/native"));
        void Put(string name,string text){var p=Path.Combine(f.Payload,name);Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllText(p,text);}
        Put("runtime/ffmpeg/ffmpeg.exe","synthetic-ffmpeg-not-executed");Put("runtime/caddy/caddy.exe","synthetic-caddy-not-executed");
        Put("app/package.json","{\"type\":\"module\"}");
        // Stands in for the server bundle: the package has no loader, so the entry
        // registers the repository tsx before importing the source Store worker.
        var register="const{register}=await import("+JsonSerializer.Serialize(new Uri(Path.Combine(repo,"node_modules/tsx/dist/esm/api/index.mjs")).AbsoluteUri)+");register();";
        Put("legal/build-inputs.json",JsonSerializer.Serialize(new{databaseSchema=f.Schema}));
        Put("app/server/worker.ts",f.Worker());Put("app/server/main.mjs",register+"await import('./worker.ts');");
        var records=Directory.GetFiles(f.Payload,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(p=>new{path=Path.GetRelativePath(f.Payload,p).Replace('\\','/'),size=new FileInfo(p).Length,sha256=Hash(File.ReadAllBytes(p))}).ToArray();
        var manifest=JsonSerializer.SerializeToUtf8Bytes(new{version="0.5.2",schema=1,kind="windows-runtime-candidate",target="win-x64",profile="full",authority="application-updater",
            launch=new{node="runtime/node/node.exe",entry="app/server/main.mjs",helper="app/native/Yahad.Platform.exe",ffmpeg="runtime/ffmpeg/ffmpeg.exe",caddy="runtime/caddy/caddy.exe"},files=records});
        f.release=Hash(manifest);File.WriteAllBytes(Path.Combine(f.Payload,"bundle.json"),manifest);Directory.CreateDirectory(Path.Combine(f.Root,"payloads"));var target=Path.Combine(f.Root,"payloads",f.release);Directory.Move(f.Payload,target);
        f.actualPayload=target;
        await f.Seed(node);f.Publish();return f;
        }catch(Exception failure){f.processes.Retain(failure);try{f.Dispose();}catch(Exception cleanup){throw new AggregateException(failure,cleanup);}throw;}
    }
    private string? actualPayload;internal string Package=>actualPayload??Payload;
    internal string PathOf(string name)=>Path.Combine(Root,name);
    internal void ClearSignals(){foreach(var name in new[]{"native-ready","native-stop","native-closed","native-exit"})if(File.Exists(Path.Combine(Data,name)))File.Delete(Path.Combine(Data,name));}
    internal void Reset(){Recovery=null;DataId=OriginalDataId;Phase="committed";AnchorEpoch=1;AnchorDigest=null;if(File.Exists(PathOf("PENDING.JSON")))File.Delete(PathOf("PENDING.JSON"));File.WriteAllText(Policy,"synthetic-policy");File.WriteAllText(Path.Combine(Dataset,"update-pin"),"synthetic-pin");ClearSignals();Publish();}
    internal void ResetRecovery(NativeBackupResult b,NativeRestoreResult restore){Reset();DataId=Guid.NewGuid().ToString("N");Recovery=new(OriginalDataId,b.BackupId,restore.OperationId,b.ReceiptDigest);Phase="recovered";Publish();}
    internal void Publish()
    {
        string Write<T>(string name,T value){var bytes=JsonSerializer.SerializeToUtf8Bytes(value);File.WriteAllBytes(PathOf(name),bytes);return Hash(bytes);}
        var web=Write("web.json",new InstalledWeb(1,InstallationId,"https://yahad.invalid",32000));
        var head=Write("activation.json",new InstalledActivation(1,InstallationId,1,release,DataId,Schema,Schema,Hash(File.ReadAllBytes(Path.Combine(Dataset,"master-key"))),Hash(File.ReadAllBytes(Policy)),Hash(File.ReadAllBytes(Path.Combine(Dataset,"update-pin"))),web,Phase,Recovery));
        Write("epoch.json",new InstalledEpoch(1,InstallationId,AnchorEpoch,AnchorDigest??head));
    }
    internal InstallationBinding Binding=>new(InstallationId,Hash(File.ReadAllBytes(PathOf("activation.json"))));
    internal FixtureProcesses.FixtureResolver CreateResolver(InstalledStateFiles? stateFiles=null)
        =>processes.Resolver(new(Root,new PrivateControlFiles(),Control,()=>Now,stateFiles:stateFiles??InstalledStateFiles.Private(Root,probe,probeDigest),processFactory:spec=>CreateLauncher(spec)));
    // Like an installed machine, admission reads SQLite through a fixed probe
    // outside the payload; the payload node is executed only by the runtime.
    private string probe="",probeDigest="";
    internal IPrivateAppLauncher CreateLauncher(VerifiedProcessSpec spec)
        =>processes.Launcher(new ServiceProcessLauncher(spec,Control));
    internal void RetainFailure(Exception failure)=>processes.Retain(failure);
    internal Task RetrySessionAsync(IContainedAppSession inner,CancellationToken cancellation)=>processes.RetrySessionAsync(inner,cancellation);
    internal FixtureProcesses.JobOwner StartJob(string image,IEnumerable<string> arguments,string directory,IReadOnlyDictionary<string,string> environment,
        Action<Microsoft.Win32.SafeHandles.SafeProcessHandle,int>? beforeResume=null)
        =>processes.StartJob(image,arguments,directory,environment,beforeResume);
    internal Task<AuthorizedServiceStart> Resolve(CancellationToken cancellation=default)=>CreateResolver().ResolveAsync(Binding,cancellation);
    private async Task Seed(string node)
    {
        var config=ConfigScript();var script="import fs from 'node:fs';try{const{Store}=await import("+JsonSerializer.Serialize(new Uri(Path.Combine(Repo,"src/store.ts")).AbsoluteUri)+");"+config+"const s=new Store(config);s.close();}catch(e){fs.writeFileSync("+JsonSerializer.Serialize(Path.Combine(Root,"seed-error.json"))+",JSON.stringify({name:e.name,message:e.message}));process.exit(1)}";
        using var seedOwner=processes.StartJob(node,["--import",new Uri(Path.Combine(Repo,"node_modules/tsx/dist/loader.mjs")).AbsoluteUri,"--input-type=module","-e",script],Repo,LaunchEnvironment.Clean(Root));var job=seedOwner.Process;
        using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(30));if(await job.WaitAsync(bound.Token)!=0)throw new IOException("REAL_STORE_SEED_FAILED "+(File.Exists(Path.Combine(Root,"seed-error.json"))?File.ReadAllText(Path.Combine(Root,"seed-error.json")):"loader or import failure"));await job.WaitForTreeEmptyAsync(bound.Token);
    }
    private string ConfigScript()=>"const config={dataDir:"+JsonSerializer.Serialize(Path.Combine(Dataset,"data"))+",masterKey:Buffer.from(fs.readFileSync("+JsonSerializer.Serialize(Path.Combine(Dataset,"master-key"))+",'utf8').trim(),'base64'),connector:'mock',realAllowed:false,host:'127.0.0.1',port:32000,origin:'https://yahad.invalid',secureCookie:true,maxMedia:16777216,maxStorage:2147483648,childIdleMs:900000,sessionMs:43200000,diagnosticsEnabled:false};";
    private string Worker()=>"import fs from 'node:fs';import path from 'node:path';import{Store}from "+JsonSerializer.Serialize(new Uri(Path.Combine(Repo,"src/store.ts")).AbsoluteUri)+";import{createPlatformFiles}from "+JsonSerializer.Serialize(new Uri(Path.Combine(Repo,"src/platform-files.ts")).AbsoluteUri)+";"+"const data=process.env.DATA_DIR;const release=await createPlatformFiles('win32',path.resolve('native/Yahad.Platform.exe')).acquireDataLock(data);const config={dataDir:data,masterKey:Buffer.from(fs.readFileSync(process.env.MASTER_KEY_FILE,'utf8').trim(),'base64'),connector:'mock',realAllowed:false,host:'127.0.0.1',port:32000,origin:'https://yahad.invalid',secureCookie:true,maxMedia:16777216,maxStorage:2147483648,childIdleMs:900000,sessionMs:43200000,diagnosticsEnabled:false};const store=new Store(config);store.db.exec('CREATE TABLE IF NOT EXISTS native_activation_probe(value INTEGER);INSERT INTO native_activation_probe VALUES(1)');fs.writeFileSync(path.join(data,'native-ready.partial'),JSON.stringify({pid:process.pid,PATH:process.env.PATH,clean:!process.env.NODE_OPTIONS&&!process.env.DOTNET_STARTUP_HOOKS}));fs.renameSync(path.join(data,'native-ready.partial'),path.join(data,'native-ready'));const wait=n=>new Promise(resolve=>{const timer=setInterval(()=>{if(fs.existsSync(path.join(data,n))){clearInterval(timer);resolve();}},10)});await wait('native-stop');store.close();await release();fs.writeFileSync(path.join(data,'native-closed'),'closed');await wait('native-exit');";
    internal void SelectPreparedPayload(string prepared)
    {
        var build=NativeJson.Read<JsonElement>(File.ReadAllBytes(Path.Combine(prepared,"legal/build-inputs.json")));
        if(build.GetProperty("databaseSchema").GetInt32()!=Schema)throw new IOException("STORE_FIXTURE_SCHEMA_MISMATCH");
        var manifest=File.ReadAllBytes(Path.Combine(prepared,"bundle.json"));release=Hash(manifest);var target=Path.Combine(Root,"payloads",release);
        Directory.CreateDirectory(target);
        foreach(var path in Directory.GetFiles(prepared,"*",SearchOption.AllDirectories))
        {var destination=Path.Combine(target,Path.GetRelativePath(prepared,path));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(path,destination);}
        actualPayload=target;Publish();
    }
    internal VerifiedProcessSpec RawSpec(WindowsFileBoundary.DataLease lease)=>new(Path.Combine(Package,"runtime/node/node.exe"),["server/main.mjs"],Path.Combine(Package,"app"),Path.Combine(Root,"temporary"),Path.Combine(Package,"runtime/ffmpeg/ffmpeg.exe"),new Dictionary<string,string>{{"DATA_DIR",Data},{"MASTER_KEY_FILE",Path.Combine(Dataset,"master-key")}},lease);
    internal static string Hash(byte[] value)=>Convert.ToHexStringLower(SHA256.HashData(value));
    internal static int ReadSourceSchema(string repo)
    {
        var path=Path.Combine(repo,"src/database-schema.ts");if(!File.Exists(path))path=Path.Combine(repo,"src/db.ts");
        var file=new FileInfo(path);if(!file.Exists||file.Length is <1 or >131072)throw new IOException("STORE_FIXTURE_SCHEMA_SOURCE");
        return ParseSourceSchema(File.ReadAllText(path));
    }
    internal static int ParseSourceSchema(string source)
    {
        var matches=System.Text.RegularExpressions.Regex.Matches(source,@"(?m)^\s*export\s+const\s+DATABASE_SCHEMA\s*=\s*([1-9][0-9]*)\s*;\s*$",System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if(matches.Count!=1||!int.TryParse(matches[0].Groups[1].Value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var schema)||schema is <1 or >100000)
            throw new IOException("STORE_FIXTURE_SCHEMA_SOURCE");
        return schema;
    }
    private static void Copy(string source,string target){Directory.CreateDirectory(target);foreach(var file in Directory.GetFiles(source))File.Copy(file,Path.Combine(target,Path.GetFileName(file)));}
    public void Dispose()
    {
        if(disposed)return;
        if(cleanupAttempted)throw new IOException("FIXTURE_RETAINED_AFTER_CLEANUP_FAILURE",cleanupFailure);
        cleanupAttempted=true;
        try{
        processes.CleanupAsync().GetAwaiter().GetResult();owned.Cleanup(()=>processes.Confirmed);disposed=true;
        }catch(Exception failure){cleanupFailure=failure;throw;}
    }
}

internal sealed class ApprovedOperations(string installation,Func<DateTimeOffset> clock):IOperationAuthentication
{
    private readonly Dictionary<string,long> approved=new(StringComparer.Ordinal);internal string? Wrong;internal bool AllowReconcile=true;
    private readonly HashSet<string> revoked=new(StringComparer.Ordinal);
    internal void Revoke(ProtectedOperation op)=>revoked.Add(InstalledActivationResolver.Hash(JsonSerializer.SerializeToUtf8Bytes(op)));
    public void RequireCurrent(VerifiedOperation approval){if(revoked.Contains(approval.OperationDigest))throw new UnauthorizedAccessException("SYNTHETIC_APPROVAL_REVOKED");}
    internal void Approve(ProtectedOperation op)=>approved[InstalledActivationResolver.Hash(JsonSerializer.SerializeToUtf8Bytes(op))]=clock().AddSeconds(-1).ToUnixTimeSeconds();
    public Task<VerifiedOperation> AuthenticateAsync(string digest,string scope,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();if(!approved.TryGetValue(digest,out var time)||scope=="reconcile"&&!AllowReconcile)throw new UnauthorizedAccessException("SYNTHETIC_AUTH_REFUSED");
        return Task.FromResult(new VerifiedOperation("boundary-approver",Wrong=="principal-installation"?Guid.NewGuid().ToString("N"):installation,Wrong=="operation-digest"?new string('0',64):digest,Wrong=="scope"?"child":scope,time,time+3600));
    }
}

internal sealed class FakeRuntimeKernel:IRuntimeGateKernel
{
 internal RuntimeGeneration Generation=new(Guid.NewGuid().ToString("N"),new string('a',64),Guid.NewGuid().ToString("N"),222,124);internal bool Assigned,Exit,Empty,RefuseAssignment;
 public RuntimeGeneration ObserveHost()=>Generation;public void AssignWaitingHost(RuntimeGeneration g){if(RefuseAssignment)throw new IOException("synthetic assignment refusal");Assigned=true;}
 public bool InOwnedJob(RuntimeGeneration g)=>Assigned;public bool HostExited(RuntimeGeneration g)=>Exit;public bool OwnedTreeEmpty()=>Empty;
}
internal sealed class FakePeerApi(AuthorityPeer peer):IAuthorityPeerApi
{
 internal bool ThrowIdentity,IdentifyAllowed=true,RevertAllowed=true;internal int Reverts;
 public int ClientPid(Microsoft.Win32.SafeHandles.SafePipeHandle p)=>peer.ProcessId;public IDisposable PinProcess(int p)=>new Noop();public long Created(int p)=>peer.Created;
 public bool Identify(Microsoft.Win32.SafeHandles.SafePipeHandle p)=>IdentifyAllowed;
 public AuthorityPeer ThreadIdentity(int p,long c){if(ThrowIdentity)throw new IOException("synthetic token failure");return peer;}
 public bool Revert(){Reverts++;return RevertAllowed;}private sealed class Noop:IDisposable{public void Dispose(){}}
}

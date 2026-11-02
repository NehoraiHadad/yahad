using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;

var failed=0;var results=new List<object>();
if(args is ["--own-token-observation-only"])
{
    // Read this test process only. No DACL writes, services or installed-data reads.
    try
    {
        using var session=new WindowsInstalledProcessObjectKernel().OpenCurrent(false);
        var observed=session.ObserveIdentity();
        using var control=System.Security.Principal.WindowsIdentity.GetCurrent();
        var expected=new System.Security.Principal.WindowsPrincipal(control).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        if(observed.Administrator!=expected||observed.CurrentPid!=Environment.ProcessId||observed.UserSid!=control.User?.Value)
            throw new IOException("OWN_TOKEN_OBSERVATION_MISMATCH");
        Console.WriteLine(JsonSerializer.Serialize(new{suite="own-token-observation-readonly",passed=1,failed=0,permissionsChanged=false,realServices=false}));return 0;
    }
    catch(Exception error)
    {Console.WriteLine(JsonSerializer.Serialize(new{suite="own-token-observation-readonly",passed=0,failed=1,type=error.GetType().Name,hresult=error.HResult,permissionsChanged=false,realServices=false}));return 1;}
}
async Task Check(string name,Func<PolicyFixture,Task> test)
{
    var fixture=new PolicyFixture();
    try{await test(fixture);Console.WriteLine("PASS "+name);results.Add(new{name,status="PASS"});}
    catch(Exception error){failed++;Console.WriteLine("FAIL "+name+" "+error);results.Add(new{name,status="FAIL",reason=error.Message});}
}
void Assert(bool condition){if(!condition)throw new Exception("ASSERTION_FAILED");}
async Task Reject(Func<Task> test,string code)
{try{await test();}catch(Exception error)when(error is IOException or UnauthorizedAccessException){if(error.Message!=code)throw new Exception("WRONG_REFUSAL "+error.Message);return;}throw new Exception("UNEXPECTED_ADMISSION");}
if(args is ["--updater-identities-only"])
{
    await UpdaterServiceIdentityTests.Run(Extra);await ProcessObjectAccessTests.Run(Extra);await OfflineMigrationRuntimeTests.Run(Extra);
    Console.WriteLine(JsonSerializer.Serialize(new{suite="updater-identities-synthetic",passed=results.Count-failed,failed,realServices=false,realPermissions=false}));return failed==0?0:1;
}
await ServiceStartupDiagnosticsTests.Run(async(name,test)=>
{
    try{await test();Console.WriteLine("PASS "+name);results.Add(new{name,status="PASS"});}
    catch(Exception error){failed++;Console.WriteLine("FAIL "+name+" "+error);results.Add(new{name,status="FAIL",reason=error.Message});}
});

await Check("Notify checks never reserve approval, stop runtime or write activation",async f=>
{
    var before=File.ReadAllBytes(f.Files.PathOf("activation.json"));
    for(var i=0;i<5;i++)Assert((await f.Request("check")).Phase=="available");
    Assert(f.Submits==0&&f.Quiesces==0&&f.Router.Execution is null);
    Assert(before.SequenceEqual(File.ReadAllBytes(f.Files.PathOf("activation.json"))));
    Assert(!Directory.EnumerateFiles(f.Files.PathOf("owner-ledger"),"approval-*.json").Any());
});
await Check("Notify revokes the existing automatic grant and repeated contraction is harmless",async f=>
{
    var grant=await f.Enroll();var candidate=f.Intake.Select();f.Ledger.AuthorizeRoutine(candidate.Catalog.Operation,candidate.Catalog.Facts,grant.Id);
    Assert((await f.Request("set_mode","notify")).Mode=="notify");
    await Reject(()=>f.Ledger.AuthenticateAsync(AuthorityProtocol.Digest(candidate.Catalog.Operation),"activate",default),"OWNER_DELEGATION_REVOKED");
    Assert(f.Ledger.RoutineStatus(grant.Id).State=="revoked");await f.Request("set_mode","notify");
    var next=await f.Enroll();Assert(next.Id!=grant.Id&&next.ExpiresUnix==grant.ExpiresUnix&&next.MaximumOperations==grant.MaximumOperations);
    Assert(f.Ledger.RoutineStatus(grant.Id).State=="revoked"&&f.Ledger.RoutineStatus(next.Id).State=="active");
});
await Check("Notify cancels pending enrollment and stale owner answer cannot enable Auto",async f=>
{
    await f.Request("set_mode","auto");var view=(await f.Status(true)).Confirmation!;
    await f.Request("set_mode","notify");await Reject(()=>f.Answer(view),"AUTHORITY_NO_CONFIRMATION");
    Assert((await f.Status()).Mode=="notify"&&f.Reopen().Phase=="idle");
});
await Check("fresh explicit enrollment retires its predecessor without extending scope",async f=>
{
    var first=await f.Enroll();var second=await f.Enroll();Assert(first.Id!=second.Id&&first.ExpiresUnix==second.ExpiresUnix&&first.Schema==second.Schema);
    Assert(f.Ledger.RoutineStatus(first.Id).State=="revoked"&&f.Ledger.RoutineStatus(second.Id).State=="active");
});
await Check("delegation filename cannot alias a different budget or revocation identity",async f=>
{
    var grant=await f.Enroll();var alias=Guid.NewGuid().ToString("N");
    File.Copy(f.Files.PathOf("owner-ledger/delegation-"+grant.Id+".json"),f.Files.PathOf("owner-ledger/delegation-"+alias+".json"));
    await Reject(()=>{f.Ledger.RoutineStatus(alias);return Task.CompletedTask;},"OWNER_DELEGATION_BINDING");
});
await Check("cancelled enrollment cannot contaminate the next exact-operation answer",async f=>
{
    await f.Request("set_mode","auto");var old=(await f.Status(true)).Confirmation!;await f.Cancel(old.RequestId);
    await f.Request("install");var next=(await f.Status(true)).Confirmation!;Assert(next.Scope=="activate");
    await f.Answer(next,approve:false);Assert((await f.Status()).Phase=="cancelled");
});
await Check("Notify cancels queued work; unfinished restart remains conservative",async f=>
{
    await f.Enroll();Func<Task>? pending=null;var complete=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    f.Schedule=work=>{pending=work;return complete.Task;};await f.Request("check");Assert(pending is not null);
    await f.Request("set_mode","notify");Assert(f.Reopen().Phase=="recovery_required");await pending!();complete.SetResult();
    Assert(f.Quiesces==0&&f.Router.Phase=="cancelled"&&f.Reopen().Phase=="idle");
});
foreach(var reason in new[]{"expiry","revocation"})await Check("queued "+reason+" cannot stop runtime or require transaction recovery",async f=>
{
    var grant=await f.Enroll();Func<Task>? pending=null;var complete=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    f.Schedule=work=>{pending=work;return complete.Task;};await f.Request("check");
    if(reason=="expiry")f.Now=f.Now.AddMinutes(11);else f.Ledger.DisableRoutine(grant.Id);
    await pending!();complete.SetResult();Assert(f.Quiesces==0&&f.Router.Phase=="cancelled"&&f.Reopen().Phase=="idle");
});
await Check("expired delegation reports renewal without stopping runtime or poisoning restart",async f=>
{
    var grant=await f.Enroll();f.Now=f.Now.AddDays(3);
    Assert((await f.Status()).Delegation?.State=="expired");
    var response=await f.Request("check");Assert(response.State=="approval_required"&&response.Phase=="available");
    Assert(f.Router.Execution is null&&f.Quiesces==0&&f.Reopen().Phase=="idle");
    Assert(f.Ledger.RoutineStatus(grant.Id).RemainingOperations==2);
    await Reject(()=>f.Request("set_mode","auto"),"OWNER_DELEGATION_LIMITS"); // Never extend absolute template expiry silently.
});
await Check("budget exhaustion remains visible and explicit install requests exact owner approval",async f=>
{
    var grant=await f.Enroll();for(var i=0;i<2;i++){var c=f.Intake.Select();f.Ledger.AuthorizeRoutine(c.Catalog.Operation,c.Catalog.Facts,grant.Id);}
    var response=await f.Request("check");Assert(response.State=="approval_required"&&response.Delegation?.State=="exhausted");
    Assert(response.Delegation!.RemainingOperations==0&&f.Router.Execution is null&&f.Quiesces==0);
    await f.Request("install");var view=(await f.Status(true)).Confirmation!;
    Assert(view.Scope=="activate"&&view.Delegation is null&&f.Ledger.RoutineStatus(grant.Id).RemainingOperations==0);
});
await Check("schema change requires exact approval and cannot expand automatic delegation",async f=>
{
    var grant=await f.Enroll();f.Stage("0.6.2",7);
    var response=await f.Request("check");Assert(response.State=="approval_required"&&f.Router.Execution is null);
    Assert(f.Ledger.RoutineStatus(grant.Id).RemainingOperations==2);
    await f.Request("install");Assert((await f.Status(true)).Confirmation?.Scope=="activate");
});
await Check("owner identity replay expiry and revoked grants fail closed",async f=>
{
    await f.Request("set_mode","auto");var view=(await f.Status(true)).Confirmation!;
    await Reject(()=>f.Answer(view,f.Owner with{Sid=f.Runtime.Sid}),"AUTHORITY_ENDPOINT_PEER");
    await f.Answer(view);await Reject(()=>f.Answer(view),"AUTHORITY_NO_CONFIRMATION");
    var grant=f.Intake.Mode().DelegationId!;f.Ledger.DisableRoutine(grant);
    Assert((await f.Request("check")).State=="approval_required");
    Assert((await f.Status()).Delegation?.State=="revoked"&&f.Quiesces==0);
});
await Check("busy ledger leaves mode request retryable and reports unknown delegation status",async f=>
{
    var grant=await f.Enroll();var before=Directory.GetFiles(f.Files.PathOf(".authority"),"rpc-*.json").Length;
    using(var held=f.Files.Lease(f.Files.PathOf("owner-ledger")))
    {
        await Reject(()=>f.Request("set_mode","notify"),"OWNER_LEDGER_BUSY");
        var response=await f.Status();Assert(response.Delegation?.State=="busy"&&response.Delegation.RemainingOperations is null);
        Assert(Directory.GetFiles(f.Files.PathOf(".authority"),"rpc-*.json").Length==before);
    }
    Assert(f.Reopen().Phase=="idle");await f.Request("set_mode","notify");Assert(f.Ledger.RoutineStatus(grant.Id).State=="revoked");
});
await Check("explicit owner reconciliation never renews an expired or revoked automatic grant",async f=>
{
    var grant=await f.Enroll();var candidate=f.Intake.Select();var digest=AuthorityProtocol.Digest(candidate.Catalog.Operation);
    f.Ledger.AuthorizeRoutine(candidate.Catalog.Operation,candidate.Catalog.Facts,grant.Id);var original=await f.Ledger.AuthenticateAsync(digest,"activate",default);
    f.Ledger.DisableRoutine(grant.Id);f.Now=f.Now.AddDays(3);
    var challenge=f.Ledger.Challenge(candidate.Catalog.Operation,true);f.Ledger.Approve(f.Owner,new OwnerAnswer(1,challenge.Id,challenge.Digest,true));
    var reconcile=await f.Ledger.AuthenticateAsync(digest,"reconcile",default);
    Assert(reconcile.ApprovedUnix==original.ApprovedUnix&&reconcile.Scope=="reconcile");f.Ledger.RequireCurrent(reconcile);
    await Reject(()=>f.Ledger.AuthenticateAsync(digest,"activate",default),"OWNER_APPROVAL_REVOKED_OR_EXPIRED");
    Assert(f.Ledger.RoutineStatus(grant.Id).RemainingOperations==1);
});
await Check("repeated busy checks spend no delegation budget and never stop the app",async f=>
{
    var grant=await f.Enroll();for(var i=0;i<120;i++){await f.Request("check");if(f.Router.Execution is{} work)await work;}
    Assert(f.Ledger.RoutineStatus(grant.Id).RemainingOperations==2&&f.Quiesces==0&&f.Router.Phase=="available");
});
await Check("manual approval cannot bypass a busy recording or draft fence",async f=>
{
    await f.Request("install");var confirmation=(await f.Status(true)).Confirmation!;await f.Answer(confirmation);
    Assert(f.Quiesces==0);await f.Cancel(confirmation.RequestId);if(f.Router.Execution is{} work)await work;Assert(f.Quiesces==0&&f.Router.Phase=="cancelled");
});
await Check("Notify keeps one exact manual approval queued until the same request becomes idle",async f=>
{
    var busy=true;var attempts=0;var waiting=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);f.Prepare=(_,_)=>{attempts++;waiting.TrySetResult();return Task.FromResult(!busy);};
    await f.Request("install");var confirmation=(await f.Status(true)).Confirmation!;await f.Answer(confirmation);
    await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert((await f.Status()).Mode=="notify"&&f.Quiesces==0);busy=false;
    await f.Router.Execution!;Assert(attempts>=2&&f.Submits==1&&f.Quiesces==1);
    Assert(Directory.EnumerateFiles(f.Files.PathOf("owner-ledger"),"challenge-*.json").Count()==1);
});
await Check("lost idle acknowledgement attempts confirmed cancellation before any stop",async f=>
{
    var attempted=0;var cancelled=0;f.Prepare=(_,_)=>{attempted++;throw new IOException("LOST_IDLE_REPLY");};f.CancelPreparation=(_,_)=>{cancelled++;return Task.CompletedTask;};f.Router=f.Reopen();
    await f.Enroll();await f.Request("check");if(f.Router.Execution is{} work)await work;
    Assert(attempted==1&&cancelled==1&&f.Quiesces==0&&f.Router.Phase=="recovery_required");
});
await Check("manual check queues fresh intake independently of the endpoint reply and never applies Notify",async f=>
{
    var permit=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
    f.Router.ConfigureSourceCheck(async(id,c)=>{calls++;await permit.Task.WaitAsync(c);f.Stage("0.6.2",6);await f.Router.CheckScheduledAsync(c,id);await f.Router.CompleteSourceCheckAsync(id,null);});
    var response=await f.Request("check");Assert(response.State=="queued"&&f.Router.SourceExecution is{IsCompleted:false});
    var replay=await f.Request("check",id:response.Id);Assert(replay.State=="pending");
    permit.SetResult();await f.Router.SourceExecution!;
    var status=await f.Status();Assert(calls==1&&status.Target?.Version=="0.6.2"&&f.Quiesces==0&&f.Submits==0&&status.LastRequest?.GetProperty("state").GetString()=="succeeded");
});
await Check("failed fresh intake check has a definite failure without stopping the app",async f=>
{
    f.Router.ConfigureSourceCheck((_,_)=>throw new IOException("OFFLINE_FIXTURE"));await f.Request("check");await f.Router.SourceExecution!;
    var status=await f.Status();Assert(status.Phase=="failed"&&status.LastRequest?.GetProperty("error").GetString()=="network"&&f.Quiesces==0);
});
await Check("source check cancellation is owned by service shutdown without an application stop",async f=>
{
    f.Router.ConfigureSourceCheck(async(_,c)=>await Task.Delay(Timeout.Infinite,c));await f.Request("check");
    await f.Router.StopAsync();Assert(f.Router.SourceExecution!.IsCompleted&&f.Quiesces==0);
});
async Task Extra(string name,Func<Task> test)
{
    try{await test();Console.WriteLine("PASS "+name);results.Add(new{name,status="PASS"});}
    catch(Exception error){failed++;Console.WriteLine("FAIL "+name+" "+error);results.Add(new{name,status="FAIL",reason=error.Message});}
}
await RuntimeAdmissionTests.Run(Extra);
await OfflineMigrationRuntimeTests.Run(Extra);
await ProcessObjectAccessTests.Run(Extra);
await UpdaterServiceIdentityTests.Run(Extra);
await OperationalProbeTests.Run(Extra);
await SystemCandidateIntakeTests.Run(Extra);
await FullSystemModeRouterTests.Run(Extra);
await SystemBootstrapHandoffTests.Run(Extra);
await HelperBaselineIntegrationTests.Run(Extra);
await InstallerReadinessTests.Run(Extra);
await Yahad.Production.Tests.FullSystemPolicyTests.Run(Extra);
await Yahad.Production.Tests.FullSystemCoordinatorTests.Run(Extra);
await Yahad.Production.Tests.FullSystemOwnerRecoveryTests.Run(Extra);
await Yahad.Production.Tests.FullSystemNativeBoundaryTests.Run(Extra);
await Yahad.Production.Tests.OfflineMigrationTests.Run(Extra);
await InstalledUpdaterLayoutTests.Run(Extra);
await OwnerRecoveryEndpointTests.Run(Extra);
await OwnerRecoveryCompletionTests.Run(Extra);
await HealthContextRegression.Run(Extra);
await UpdateSchedulerTests.Run(Extra);
await RecoveredUpdateScheduleTests.Run(Extra);
await NativeCandidateIntakeTests.Run(Extra);
await NativeIntakeRecoveryTests.Run(Extra);
await NativeIntakeMetadataTests.Run(Extra);
await Yahad.Production.Tests.InstalledRecoveryTests.Run(Extra);
if(args is ["--node",var nativeNode])
{
    var nodePath=Path.GetFullPath(nativeNode);using var node=File.OpenRead(nodePath);var nodeDigest=Convert.ToHexStringLower(SHA256.HashData(node));
    await Yahad.Production.Tests.InstalledRecoveryTests.RunNative(Extra,nodePath,nodeDigest);
    await Yahad.Production.Tests.OfflineMigrationTests.RunNative(Extra,nodePath,nodeDigest);
}
var report=new{suite="windows-updater-policy",passed=results.Count-failed,failed,privateSyntheticOnly=true,installedServicesTested=false,networkRequests=false,actualAppUpdates=false,results};
Console.WriteLine(JsonSerializer.Serialize(report));return failed==0?0:1;

internal sealed class PolicyFixture
{
    internal DateTimeOffset Now=new(2026,10,31,20,0,0,TimeSpan.Zero);
    internal InstalledStateFiles Files{get;}internal OwnerApprovalLedger Ledger{get;}internal InstalledIntakeSelection Intake{get;}internal AuthorityEndpointRouter Router{get;set;}
    internal AuthorityPeer Owner=new("S-1-5-21-100-101-102-1001",111,123,1,true),Runtime=new("S-1-5-80-1-2-3-4-5",222,124,0,false);
    private readonly string installation=Guid.NewGuid().ToString("N"),data=Guid.NewGuid().ToString("N"),digest=new('a',64);
    private InstalledAuthorityAnchor Anchor{get;}internal int Submits,Quiesces;internal Func<Func<Task>,Task>? Schedule;
    internal Func<string,CancellationToken,Task<bool>>? Prepare;internal Func<string,CancellationToken,Task>? CancelPreparation;
    internal PolicyFixture()
    {
        Files=InstalledStateFiles.Private(SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"yahad-updater-policy")));
        Directory.CreateDirectory(Files.PathOf("intake"));Directory.CreateDirectory(Files.PathOf("payloads"));
        Anchor=new(1,installation,ServiceDefinition.Name,InstalledAuthorityReader.AuthorityService,Owner.Sid,digest,digest,null);
        var web=new InstalledWeb(1,installation,"https://yahad.invalid",32000);var webDigest=Put("web.json",web);
        var current=Put("payloads/current.json",new{version="0.6.0"});Directory.CreateDirectory(Files.PathOf("payloads/"+current));File.Move(Files.PathOf("payloads/current.json"),Files.PathOf("payloads/"+current+"/bundle.json"));
        var head=new InstalledActivation(1,installation,1,current,data,6,6,digest,digest,digest,digest,webDigest,"committed",null);
        var activation=Put("activation.json",head);Put("epoch.json",new InstalledEpoch(1,installation,1,activation));
        Put("bootstrap.json",new{schema=1,service=ServiceDefinition.Name,installationId=installation,activationReceiptDigest=activation});
        Put("intake/mode.json",new NativeUpdateMode(1,installation,"notify",null,true));
        Put("intake/delegation-template.json",new RoutineDelegation(1,Guid.NewGuid().ToString("N"),installation,Owner.Sid,data,digest,digest,digest,"stable",web.Origin,null,"0.6.1","0.6.9",6,Now.AddDays(2).ToUnixTimeSeconds(),2,10000));
        Ledger=new(Files,installation,Owner.Sid,()=>Now);Intake=new(Files,Anchor,()=>Now);Stage("0.6.1",6);Router=Reopen();
    }
    internal AuthorityEndpointRouter Reopen()=>new(Files,Anchor,Runtime.Sid,Intake,Ledger,selected=>
    {
        Submits++;var gate=new RuntimeAuthorityGate(new UnusedKernel());
        return new(Runtime.Sid,new PrivateOperationCatalog(selected),Ledger,new ProtectedStateTransactionWriter(Files,installation,_=>throw new Exception("UNEXPECTED_COMMIT"),Ledger,new UnusedChannels(),()=>Now),gate);
    },_=>{Quiesces++;throw new Exception("UNEXPECTED_RUNTIME_STOP");},work=>Schedule is { } run?run(work):Task.Run(work),(id,c)=>Prepare?.Invoke(id,c)??Task.FromResult(false),(id,c)=>CancelPreparation?.Invoke(id,c)??Task.CompletedTask);
    internal void Stage(string version,int schema)
    {
        var manifest=JsonSerializer.SerializeToUtf8Bytes(new{version,target="win-x64",kind="windows-runtime-candidate",files=new[]{new{path="app/example",size=10}}});var id=Hash(manifest);
        Directory.CreateDirectory(Files.PathOf("payloads/"+id+"/legal"));File.WriteAllBytes(Files.PathOf("payloads/"+id+"/bundle.json"),manifest);
        Put("payloads/"+id+"/legal/build-inputs.json",new{databaseSchema=schema});
        var receipt=new IntakeReceipt(1,installation,id,version,"stable",10,schema,digest,digest);var bytes=JsonSerializer.SerializeToUtf8Bytes(receipt);var hash=Hash(bytes);
        File.WriteAllBytes(Files.PathOf("intake/receipt-"+hash+".json"),bytes);Put("intake/available.json",new IntakePointer(1,installation,hash));
    }
    internal Task<EndpointReply> Request(string action,string? mode=null,string? id=null)
    {id??=Guid.NewGuid().ToString("N");return Dispatch(false,id,"request",new AuthorityRequest(1,id,action,action=="install"?Intake.Select().Target:null,mode));}
    internal Task<EndpointReply> Status(bool owner=false)=>Dispatch(owner,Guid.NewGuid().ToString("N"),owner?"confirmation":"status",new{});
    internal Task<EndpointReply> Answer(OperationConfirmation view,AuthorityPeer? peer=null,bool approve=true)=>Dispatch(true,Guid.NewGuid().ToString("N"),"answer",new OwnerAnswer(1,view.Challenge,view.Digest,approve),peer);
    internal Task<EndpointReply> Cancel(string id)=>Dispatch(false,Guid.NewGuid().ToString("N"),"cancel",new{RequestId=id});
    internal async Task<RoutineDelegation> Enroll(){await Request("set_mode","auto");var view=(await Status(true)).Confirmation!;await Answer(view);return view.Delegation!;}
    private Task<EndpointReply> Dispatch(bool owner,string id,string method,object body,AuthorityPeer? peer=null)=>Router.DispatchAsync(owner?AuthorityEndpointRole.Owner:AuthorityEndpointRole.Runtime,peer??(owner?Owner:Runtime),JsonSerializer.SerializeToUtf8Bytes(new AuthorityFrame(1,id,method,JsonSerializer.SerializeToElement(body))));
    private string Put<T>(string name,T value){var bytes=JsonSerializer.SerializeToUtf8Bytes(value);File.WriteAllBytes(Files.PathOf(name),bytes);return Hash(bytes);}
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed class UnusedChannels:IChildControlFactory{public IChildControl Create()=>throw new Exception("UNEXPECTED_CHANNEL");}
    private sealed class UnusedKernel:IRuntimeGateKernel
    {
        public RuntimeGeneration ObserveHost()=>throw new Exception("UNEXPECTED_HOST");public void AssignWaitingHost(RuntimeGeneration g)=>throw new Exception("UNEXPECTED_ASSIGN");
        public bool InOwnedJob(RuntimeGeneration g)=>false;public bool HostExited(RuntimeGeneration g)=>false;public bool OwnedTreeEmpty()=>false;
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;

// Real router/policy with explicitly TRUSTED candidate facts. No archive,
// migration, service, process, real family database or network is exercised here.
internal static class FullSystemModeRouterTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("full Auto enable is published directly from the authenticated Runtime request",async()=>
        {
            var f=await Fixture.Create();var id=Guid.NewGuid().ToString("N");
            await Reject(()=>f.Request("set_mode","auto",peer:new AuthorityPeer("S-1-5-21-999-998-997-1001",44,55,1,true)));Assert(f.Intake.Mode().Mode=="notify");
            var reply=await f.Request("set_mode","auto",id);Assert(reply.State=="queued");
            var mode=f.Intake.Mode();var grant=f.Policy.CurrentGrant().Grant;
            Assert(mode.GrantKind=="system"&&mode.Mode=="auto"&&mode.DelegationId==grant.Id&&grant.EnrollmentId==f.Policy.Subject&&grant.ApprovedUnix==f.Now.ToUnixTimeSeconds());
            Assert(File.Exists(f.Files.PathOf(".authority/rpc-"+id+".json"))&&File.Exists(f.Files.PathOf(".authority/done-"+id+".json")));
            f.Now=f.Now.AddYears(1);_=f.Policy.CurrentGrant();
            var replay=await f.Request("set_mode","auto",id);Assert(replay.State=="pending"&&f.Policy.CurrentGrant().Grant.Id==grant.Id); // The latest request id stays current.
            await f.Request("set_mode","auto");Assert(f.Policy.CurrentGrant().Grant.Id==grant.Id); // An active grant is not reminted.
        });
        await check("full scheduled Notify never dispatches and Auto uses the same router hook",async()=>
        {
            var f=await Fixture.Create();var notify=await f.Router.CheckScheduledAsync(default);Assert(notify.Mode=="notify"&&notify.Phase=="available"&&f.Authorizations==0);
            await f.Request("set_mode","auto");var reply=await f.Router.CheckScheduledAsync(default);if(f.Router.Execution is {} task)await task;
            Assert(reply.State=="queued"&&f.Authorizations==1&&f.Last?.ManualApproval is null&&f.Capability is not null);
            await f.Request("set_mode","notify");Assert(f.Intake.Mode().GrantKind=="system"&&f.Intake.Mode().Mode=="notify");await Reject(()=>Task.Run(()=>f.Policy.RequireCurrent(f.Capability!)));
            var oldGrant=f.Capability!.GrantId;await f.Request("set_mode","auto");Assert(f.Policy.CurrentGrant().Grant.Id!=oldGrant);
        });
        await check("full scheduled check with an unusable automatic grant is observed without dispatch",async()=>
        {
            var f=await Fixture.Create();await f.Request("set_mode","auto");var grant=f.Policy.CurrentGrant().Grant.Id;
            File.WriteAllBytes(f.Files.PathOf("grants/revoked-system-grant-"+grant+".json"),JsonSerializer.SerializeToUtf8Bytes(new{Format=1,Id=grant}));
            var reply=await f.Router.CheckScheduledAsync(default);Assert(reply.State=="observed"&&reply.Phase=="available"&&f.Router.Execution is null&&f.Authorizations==0);
        });
        await check("full enable and revocation remain available during the separate background intake lease",async()=>
        {
            var f=await Fixture.Create();using var download=f.Files.Lease(f.Files.PathOf("intake"));
            await f.Request("set_mode","auto");_=f.Policy.CurrentGrant();
            await f.Request("set_mode","notify");Assert(f.Intake.Mode().Mode=="notify");await Reject(()=>Task.Run(()=>f.Policy.CurrentGrant()));
        });
        await check("Notify manual full install is the one-time approval and remains Notify",async()=>
        {
            var f=await Fixture.Create();var id=Guid.NewGuid().ToString("N");await f.Request("install",id:id);if(f.Router.Execution is {} task)await task;
            Assert(f.Authorizations==1&&f.Last?.ManualApproval is not null&&f.Intake.Mode().Mode=="notify"&&f.Intake.Mode().DelegationId is null);
            var replay=await f.Request("install",id:id);Assert((replay.State is "recorded" or "pending")&&f.Authorizations==1);
            var record=FullSystemOperationJournal.Read(f.Files,f.Installation,id);Assert(record.GrantKind=="manual"&&record.GrantId==id);
            var grant=NativeJson.Read<FullSystemManualGrant>(f.Files.Read(f.Files.PathOf("grants/system-manual-grant-"+id+".json"),8192));
            Assert(grant.EnrollmentId==f.Policy.Subject&&grant.ApprovedUnix==f.Now.ToUnixTimeSeconds()&&grant.ValidUntilUnix==grant.ApprovedUnix+600);
        });
        await check("Auto install with an unusable automatic grant falls back to the exact manual approval",async()=>
        {
            var f=await Fixture.Create();await f.Request("set_mode","auto");var grant=f.Policy.CurrentGrant().Grant.Id;
            File.WriteAllBytes(f.Files.PathOf("grants/revoked-system-grant-"+grant+".json"),JsonSerializer.SerializeToUtf8Bytes(new{Format=1,Id=grant}));
            await f.Request("install");if(f.Router.Execution is {} task)await task;Assert(f.Authorizations==1&&f.Last?.ManualApproval is not null&&f.Intake.Mode().Mode=="auto");
        });
        await check("updater handoff stays running until replacement reconciles the protected request",async()=>
        {
            var f=await Fixture.Create();await f.Request("set_mode","auto");f.CoordinatorPhase="restarting_updater";
            await f.Router.CheckScheduledAsync(default);if(f.Router.Execution is {} task)await task;var id=f.Last!.OperationId;
            Assert(!File.Exists(f.Files.PathOf(".authority/done-"+id+".json"))&&NativeRequestProjection.Read(f.Files,false)!.Value.GetProperty("state").GetString()=="running");
            f.RecreateRouter();f.CoordinatorPhase="healthy";await f.Router.CompleteFullSystemRequestAsync(id,"healthy");Assert(NativeRequestProjection.Read(f.Files,false)!.Value.GetProperty("state").GetString()=="succeeded");
            Assert(f.Router.FullSystemOperationId==id&&(await f.Status()).Phase=="healthy"&&f.Authorizations==1);
        });
        await check("fresh Router clears unadmitted recovery only after protected native cancellation and no effects",async()=>
        {
            var f=await Fixture.Create();var id=Guid.NewGuid().ToString("N");
            File.WriteAllBytes(f.Files.PathOf(".authority/rpc-"+id+".json"),JsonSerializer.SerializeToUtf8Bytes(new{Id=id,Action="install",Target=f.Receipt,Profile=FullSystemUpdatePolicy.Profile}));
            f.RecreateRouter();Assert((await f.Status()).Phase=="recovery_required");await Reject(()=>f.Router.CompleteUnadmittedFullRequestAsync(id));
            File.WriteAllBytes(f.Files.PathOf(".authority/done-"+id+".json"),JsonSerializer.SerializeToUtf8Bytes(new{Id=id,Phase="cancelled"}));
            Directory.CreateDirectory(f.Files.PathOf(".authority/updates/"+id));await Reject(()=>f.Router.CompleteUnadmittedFullRequestAsync(id));Assert((await f.Status()).Phase=="recovery_required");
            // A separate clean fixture proves success; preserve the rejected
            // fixture's evidence rather than removing an effects directory.
            var clean=await Fixture.Create();var other=Guid.NewGuid().ToString("N");
            File.WriteAllBytes(clean.Files.PathOf(".authority/rpc-"+other+".json"),JsonSerializer.SerializeToUtf8Bytes(new{Id=other,Action="check",Target=clean.Receipt,Profile=FullSystemUpdatePolicy.Profile}));
            clean.RecreateRouter();File.WriteAllBytes(clean.Files.PathOf(".authority/done-"+other+".json"),JsonSerializer.SerializeToUtf8Bytes(new{Id=other,Phase="cancelled"}));
            var capsule=new SystemBootstrapHelperCapsule(1,Guid.NewGuid().ToString("N"),new('a',64),Guid.NewGuid().ToString("N"),2,3,new('a',64),Path.Combine(InstalledUpdaterLayout.StateRoot,"datasets",Guid.NewGuid().ToString("N"),"data"),7,8,10,11,new NativeRuntimeTransfer(2,3,4,5,1,2),new NativeDataLeaseTransfer(2,3,[3],4),new NativeDataLeaseTransfer(2,3,[5],6));
            File.WriteAllBytes(clean.Files.PathOf(".authority/bootstrap-helper-active.json"),JsonSerializer.SerializeToUtf8Bytes(capsule));
            await clean.Router.CompleteUnadmittedFullRequestAsync(other);var completed=await clean.Status();var effectiveMode=clean.Intake.Mode().Mode;
            var diagnostics=JsonSerializer.Serialize(new{completed.Phase,OperationId=clean.Router.FullSystemOperationId,ExpectedOperationId=other,clean.Authorizations,Mode=effectiveMode,ObservedCoordinatorPhase=clean.CoordinatorPhase});
            AssertTagged(completed.Phase=="cancelled","UNADMITTED_PHASE",diagnostics);
            AssertTagged(clean.Router.FullSystemOperationId==other,"UNADMITTED_OPERATION",diagnostics);
            AssertTagged(clean.Authorizations==0,"UNADMITTED_AUTHORIZATIONS",diagnostics);
            AssertTagged(effectiveMode=="notify","UNADMITTED_MODE",diagnostics);
            File.WriteAllBytes(clean.Files.PathOf(".authority/bootstrap-helper-active.json"),JsonSerializer.SerializeToUtf8Bytes(new{malformed=true}));await Reject(()=>clean.Router.CompleteUnadmittedFullRequestAsync(other));
        });
        foreach(var reason in new[]{"target","schema"})await check("manual approval rejects changed "+reason+" before writing a manual grant",async()=>
        {
            var f=await Fixture.Create();var proposal=f.Control.ProposeInstall(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);
            if(reason=="schema")proposal=proposal with{TargetSchema=8};else f.Candidate=f.Candidate with{Version="0.6.2"};
            await Reject(()=>Task.Run(()=>f.Policy.AuthorizeManual(proposal)));
            Assert(!File.Exists(f.Files.PathOf("grants/system-manual-grant-"+proposal.OperationId+".json"))&&f.Intake.Mode().Mode=="notify");
        });
        await check("manual approval cannot be replayed with different approval bytes",async()=>
        {
            var f=await Fixture.Create();var proposal=f.Control.ProposeInstall(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);_=f.Policy.AuthorizeManual(proposal);
            f.Now=f.Now.AddSeconds(1);await Reject(()=>Task.Run(()=>f.Policy.AuthorizeManual(proposal)));
        });
        await check("manual expiry stops forward authority while bound repair cannot renew it",async()=>
        {
            var f=await Fixture.Create();var proposal=f.Control.ProposeInstall(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);
            var operation=f.Policy.AuthorizeManual(proposal);f.Policy.RequireCurrent(operation);f.Now=f.Now.AddMinutes(11);
            await Reject(()=>Task.Run(()=>f.Policy.RequireCurrent(operation)));var repair=f.Policy.RemintBootstrapRecovery(operation.OperationId);Assert(repair.OperationId==operation.OperationId&&repair.ManifestDigest==operation.ManifestDigest);
            await Reject(()=>Task.Run(()=>f.Policy.RequireCurrent(repair)));Assert(f.Intake.Mode().Mode=="notify");
        });
        await check("manual mixed-version proposal binds actual bootstrap and cannot downgrade it",async()=>
        {
            var f=await Fixture.Create();f.Observed=f.Observed with{BootstrapVersion="0.6.1"};
            var proposal=f.Control.ProposeInstall(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);Assert(proposal.FromVersion=="0.6.0"&&proposal.BootstrapVersion=="0.6.1");
            // Trusted candidate fixture publishes a subsequent compatible source.
            f.Candidate=f.Candidate with{Version="0.6.2"};proposal=proposal with{ToVersion="0.6.2"};
            var operation=f.Policy.AuthorizeManual(proposal);Assert(operation.ToVersion=="0.6.2"&&operation.BootstrapFromVersion=="0.6.1");
            var downgrade=proposal with{OperationId=Guid.NewGuid().ToString("N"),BootstrapVersion="0.6.3"};
            await Reject(()=>Task.Run(()=>f.Policy.AuthorizeManual(downgrade)));Assert(f.Intake.Mode().Mode=="notify");
        });
        await check("full mode cannot be interpreted without its immutable anchor pin",async()=>
        {
            var f=await Fixture.Create();var unpinned=new InstalledIntakeSelection(f.Files,f.Anchor with{FullSystemPolicyDigest=null});await Reject(()=>Task.Run(()=>unpinned.Mode()));
            Assert(f.Intake.Mode().GrantKind=="system");
        });
        foreach(var legacy in new[]{"format1","ordinary"})await check("mode without the Format-2 system grant kind is refused ("+legacy+")",async()=>
        {
            var f=await Fixture.Create();
            File.WriteAllBytes(f.Files.PathOf("intake/mode.json"),JsonSerializer.SerializeToUtf8Bytes(legacy=="format1"?new NativeUpdateMode(1,f.Installation,"notify",null,true):new NativeUpdateMode(2,f.Installation,"notify",null,true,"ordinary")));
            await Reject(()=>Task.Run(()=>f.Intake.Mode()));
        });
        await check("installer full enrollment plan is explicit and side-effect free",()=>
        {
            var installation=Guid.NewGuid().ToString("N");var plan=FullSystemInstallerEnrollment.Prepare(installation,2,"family","parent",new('a',64),new('b',64),DateTimeOffset.UtcNow,true);
            var enrollment=NativeJson.Read<FullSystemEnrollment>(plan.Enrollment);var grant=NativeJson.Read<FullSystemModeGrant>(plan.InitialGrant);var mode=NativeJson.Read<NativeUpdateMode>(plan.Mode);
            Assert(plan.PolicyDigest==Hash(plan.Enrollment)&&enrollment.InstallationId==installation&&grant.EnrollmentId==enrollment.Id&&grant.PolicyDigest==plan.PolicyDigest&&
                mode.Format==2&&mode.GrantKind=="system"&&mode.DelegationId==grant.Id&&plan.GrantPath=="grants/system-grant-"+grant.Id+".json");
            return Reject(()=>Task.FromResult(FullSystemInstallerEnrollment.Prepare(installation,1,"family","parent",new('a',64),new('b',64),DateTimeOffset.UtcNow,true)));
        });
        await check("manual check queues fresh intake independently of the endpoint reply and never applies Notify",async()=>
        {
            var f=await Fixture.Create();var pointer=f.Files.PathOf("intake/system-available.json");var held=f.Files.PathOf(".authority/system-available-held.json");File.Move(pointer,held);
            var permit=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
            f.Router.ConfigureSourceCheck(async(id,c)=>{calls++;await permit.Task.WaitAsync(c);File.Move(held,pointer);await f.Router.CheckScheduledAsync(c,id);await f.Router.CompleteSourceCheckAsync(id,null);});
            var response=await f.Request("check");Assert(response.State=="queued"&&f.Router.SourceExecution is{IsCompleted:false});
            var replay=await f.Request("check",id:response.Id);Assert(replay.State=="pending");
            permit.SetResult();await f.Router.SourceExecution!;
            var status=await f.Status();Assert(calls==1&&status.Target?.Version=="0.6.1"&&f.Authorizations==0&&status.LastRequest?.GetProperty("state").GetString()=="succeeded");
        });
        await check("failed fresh intake check has a definite failure without stopping the app",async()=>
        {
            var f=await Fixture.Create();f.Router.ConfigureSourceCheck((_,_)=>throw new IOException("OFFLINE_FIXTURE"));await f.Request("check");await f.Router.SourceExecution!;
            var status=await f.Status();Assert(status.Phase=="failed"&&status.LastRequest?.GetProperty("error").GetString()=="network"&&f.Authorizations==0);
        });
        await check("source check cancellation is owned by service shutdown without an application stop",async()=>
        {
            var f=await Fixture.Create();f.Router.ConfigureSourceCheck(async(_,c)=>await Task.Delay(Timeout.Infinite,c));await f.Request("check");
            await f.Router.StopAsync();Assert(f.Router.SourceExecution!.IsCompleted&&f.Authorizations==0);
        });
        foreach(var failure in new[]{"partial","malformed","missing","empty","invalid_delegation"})await check("installed composition marks "+failure+" mode recovery_required without defaulting Notify",async()=>
        {
            var f=await Fixture.Create();var mode=f.Files.PathOf("intake/mode.json");
            if(failure=="partial")File.WriteAllText(mode+".partial-"+Guid.NewGuid().ToString("N"),"interrupted");
            else if(failure=="missing")File.Move(mode,f.Files.PathOf(".authority/mode-before-test.json"));
            else if(failure=="invalid_delegation")File.WriteAllBytes(mode,JsonSerializer.SerializeToUtf8Bytes(new NativeUpdateMode(2,f.Installation,"auto","invalid",true,"system")));
            else File.WriteAllText(mode,failure=="empty"?"":"{");
            var composition=f.Composition((_,_)=>{});Assert(composition.Router.Phase=="recovery_required");
            await Reject(()=>composition.Router.DispatchAsync(f.Runtime,f.Frame("status",new{})));
        });
        foreach(var denied in new[]{"role","access"})await check("installed composition mode availability fallback propagates "+denied+" denial",async()=>
        {
            var f=await Fixture.Create();var refused=false;
            try
            {
                _=f.Composition((path,_)=>
                {if(path==f.Files.PathOf("intake/mode.json")){if(denied=="role")throw new IOException("INSTALLED_ROLE_ACCESS");throw new System.ComponentModel.Win32Exception(5);}});
            }
            catch(IOException error)when(error.Message=="INSTALLED_ROLE_ACCESS"){refused=true;}
            catch(System.ComponentModel.Win32Exception error)when(error.NativeErrorCode==5){refused=true;}
            Assert(refused);
        });
    }
    private static async Task Reject(Func<Task> work)
    {try{await work();}catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception){return;}throw new Exception("FULL_MODE_UNEXPECTED_AUTHORITY");}
    private static void Assert(bool condition){if(!condition)throw new Exception("FULL_MODE_ASSERTION");}
    private static void AssertTagged(bool condition,string tag,string diagnostics){if(!condition)throw new Exception("FULL_MODE_"+tag+" "+diagnostics);}
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed class Fixture:IProtectedSystemCandidateResolver
    {
        internal string Installation=Guid.NewGuid().ToString("N"),Receipt="";internal DateTimeOffset Now=DateTimeOffset.FromUnixTimeSeconds(1800000000);
        internal InstalledStateFiles Files=InstalledStateFiles.Private(SafeDirectory.CreatePrivateSession(Path.GetTempPath()));internal InstalledAuthorityAnchor Anchor=null!;
        internal FullSystemUpdatePolicy Policy=null!;internal FullSystemModeControl Control=null!;internal InstalledIntakeSelection Intake=null!;internal AuthorityEndpointRouter Router=null!;
        internal AuthorityPeer Runtime=new("S-1-5-80-100-101-102-103-104",56,78,0,false);
        internal FullSystemInstalledState Observed=null!;internal SystemCandidatePolicyFacts Candidate=null!;internal FullSystemDispatchRequest? Last;internal VerifiedSystemOperation? Capability;internal int Authorizations;internal string CoordinatorPhase="healthy";
        internal static async Task<Fixture> Create()
        {
            var f=new Fixture();foreach(var directory in new[]{"intake","grants",".authority","payloads"})Directory.CreateDirectory(f.Files.PathOf(directory));
            var plan=FullSystemInstallerEnrollment.Prepare(f.Installation,2,"family","parent",new('a',64),new('a',64),f.Now,false);
            File.WriteAllBytes(f.Files.PathOf(plan.EnrollmentPath),plan.Enrollment);File.WriteAllBytes(f.Files.PathOf(plan.GrantPath),plan.InitialGrant);File.WriteAllBytes(f.Files.PathOf("intake/mode.json"),plan.Mode);File.WriteAllBytes(f.Files.PathOf("intake/enrollment.json"),plan.OperatorBinding);
            f.Anchor=new(1,f.Installation,ServiceDefinition.Name,InstalledAuthorityReader.AuthorityService,new('a',64),new('a',64),null,plan.PolicyDigest);
            var manifest=JsonSerializer.SerializeToUtf8Bytes(new{version="0.6.0"});var release=Hash(manifest);Directory.CreateDirectory(f.Files.PathOf("payloads/"+release));File.WriteAllBytes(f.Files.PathOf("payloads/"+release+"/bundle.json"),manifest);
            var web=new InstalledWeb(1,f.Installation,"https://yahad.invalid",32000);var webDigest=f.Put("web.json",web);var data=Guid.NewGuid().ToString("N");var head=new InstalledActivation(1,f.Installation,1,release,data,6,6,new('a',64),new('a',64),new('a',64),webDigest,"committed",null);
            var activation=f.Put("activation.json",head);f.Put("epoch.json",new InstalledEpoch(1,f.Installation,1,activation));f.Put("bootstrap.json",new{schema=1,service=ServiceDefinition.Name,installationId=f.Installation,activationReceiptDigest=activation});f.Observed=new("0.6.0",1,activation,6);
            var api=SystemSourceRelease.Api;var source=await SystemSourceRelease.CheckAsync((uri,_,_)=>Task.FromResult(uri.AbsoluteUri.EndsWith("/releases/latest",StringComparison.Ordinal)?JsonSerializer.SerializeToUtf8Bytes(new{id=1,url=api+"/releases/1",tag_name="v0.6.1",draft=false,prerelease=false,assets=new[]{new{id=2,name="yahad-system-native-0.6.1.zip",state="uploaded",size=1,digest="sha256:"+new string('b',64),url=api+"/releases/assets/2"}}}):JsonSerializer.SerializeToUtf8Bytes(new{@ref="refs/tags/v0.6.1",url=api+"/git/refs/tags/v0.6.1",@object=new{type="commit",sha=new string('a',40),url=api+"/git/commits/"+new string('a',40)}})),default);
            var components=Enum.GetValues<SystemComponentKind>().Select(kind=>new SystemComponentDescriptor(kind,FixedSystemComponents.SlotPrefix(kind),FixedSystemComponents.EntryPoint(kind),new('d',64),new[]{new SystemComponentFile(FixedSystemComponents.EntryPoint(kind),1,new('d',64))})).ToArray();
            f.Candidate=new(new('e',64),new('f',64),source.Version,source.SourceCommit,7,new[]{6},components);
            var sourceReceipt=new SystemSourceReceipt(2,FullSystemUpdatePolicy.Profile,f.Installation,NativeCandidateIntake.Repository,source.ReleaseId,source.Tag,source.AssetId,source.AssetName,source.Bytes,source.ArchiveSha256,source.SourceCommit,source.SourceProofDigest,f.Candidate.ManifestDigest,f.Candidate.ApplicationBundleDigest,new('a',64),7,source.Version);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(sourceReceipt);f.Receipt=Hash(bytes);File.WriteAllBytes(f.Files.PathOf("intake/system-source-"+f.Receipt+".json"),bytes);f.Put("intake/system-available.json",new{Format=2,Profile=FullSystemUpdatePolicy.Profile,InstallationId=f.Installation,SourceReceiptDigest=f.Receipt,SystemManifestDigest=f.Candidate.ManifestDigest});
            f.Policy=FullSystemUpdatePolicy.Open(f.Files,f.Installation,plan.PolicyDigest,()=>f.Now,f);f.Control=new(f.Files,f.Anchor,f.Policy,f,()=>f.Now);f.Intake=new(f.Files,f.Anchor);
            f.RecreateRouter();return f;
        }
        internal void RecreateRouter()
        {
            Router=new(Files,Anchor,Runtime.Sid,Intake,work=>work());
            Router.ConfigureFullSystem(Control,()=>Observed,(request,token)=>{token.ThrowIfCancellationRequested();Last=request;Capability=request.ManualApproval is{} proposal?Policy.AuthorizeManual(proposal):Policy.Authorize(request.OperationId,request.SourceReceiptDigest,Observed);Authorizations++;return Task.CompletedTask;},()=>CoordinatorPhase,()=>false);
        }
        // Real private-root composition over this fixture's state with synthetic file-role metadata.
        internal InstalledAuthorityComposition Composition(Action<string,InstalledFileRole> roles)
        {
            var bytes=JsonSerializer.SerializeToUtf8Bytes(Anchor);File.WriteAllBytes(Files.PathOf("installation.json"),bytes);
            return InstalledAuthorityComposition.PrivateFixture(Files.Root,Hash(bytes),Runtime.Sid,"","",roles);
        }
        internal Task<EndpointReply> Request(string action,string? mode=null,string? id=null,AuthorityPeer? peer=null)
        {id??=Guid.NewGuid().ToString("N");return Send(id,"request",new AuthorityRequest(1,id,action,action=="install"?Receipt:null,mode),peer);}
        internal Task<EndpointReply> Status()=>Send(Guid.NewGuid().ToString("N"),"status",new{});
        internal byte[] Frame(string method,object body)=>JsonSerializer.SerializeToUtf8Bytes(new AuthorityFrame(1,Guid.NewGuid().ToString("N"),method,JsonSerializer.SerializeToElement(body)));
        private Task<EndpointReply> Send(string id,string method,object body,AuthorityPeer? peer=null)=>Router.DispatchAsync(peer??Runtime,JsonSerializer.SerializeToUtf8Bytes(new AuthorityFrame(1,id,method,JsonSerializer.SerializeToElement(body))));
        private string Put<T>(string path,T value){var bytes=JsonSerializer.SerializeToUtf8Bytes(value);File.WriteAllBytes(Files.PathOf(path),bytes);return Hash(bytes);}
        public SystemCandidatePolicyFacts RequireVerified(string digest){if(digest!=Receipt)throw new IOException("TRUSTED_FIXTURE_TARGET");return Candidate;}
    }
}

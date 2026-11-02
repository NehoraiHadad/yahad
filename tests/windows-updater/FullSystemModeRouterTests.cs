using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;

// Real router/ledger/policy with explicitly TRUSTED candidate facts. No archive,
// migration, service, process, real family database or network is exercised here.
internal static class FullSystemModeRouterTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("full Auto enable requires native Owner consent2 and preserves legacy limits",async()=>
        {
            var f=await Fixture.Create();var legacy=File.ReadAllBytes(f.Files.PathOf("intake/delegation-template.json"));
            await f.Request("set_mode","auto");var view=(await f.Status(true)).Confirmation!;Assert(view.Scope=="delegate-system"&&view.SystemConsent?.ConsentVersion==2&&view.Delegation is null);
            _=new OwnerConfirmationState(view,f.Installation,()=>f.Now);await Reject(()=>f.Answer(view,new AuthorityPeer("S-1-5-21-999-998-997-1001",44,55,1,true)));
            await f.Answer(view);var mode=f.Intake.Mode();Assert(mode.IsSystem&&mode.Mode=="auto"&&f.Policy.CurrentGrant().Grant.OwnerSid==f.Owner.Sid);
            f.Now=f.Now.AddYears(1);_=f.Policy.CurrentGrant();Assert(legacy.SequenceEqual(File.ReadAllBytes(f.Files.PathOf("intake/delegation-template.json")))&&!Directory.EnumerateFiles(f.Files.PathOf("owner-ledger"),"usage-*.json").Any());
            await Reject(()=>f.Answer(view));
        });
        await check("full scheduled Notify never dispatches and Auto uses the same router hook",async()=>
        {
            var f=await Fixture.Create();var notify=await f.Router.CheckScheduledAsync(default);Assert(notify.Mode=="notify"&&notify.Phase=="available"&&f.Authorizations==0);
            await f.Request("set_mode","auto");await f.Answer((await f.Status(true)).Confirmation!);var reply=await f.Router.CheckScheduledAsync(default);if(f.Router.Execution is {} task)await task;
            Assert(reply.State=="queued"&&f.Authorizations==1&&f.Last?.ManualApproval is null&&f.Capability is not null);
            await f.Request("set_mode","notify");Assert(f.Intake.Mode().IsSystem&&f.Intake.Mode().Mode=="notify");await Reject(()=>Task.Run(()=>f.Policy.RequireCurrent(f.Capability!)));
            var oldGrant=f.Capability!.GrantId;await f.Request("set_mode","auto");await f.Answer((await f.Status(true)).Confirmation!);Assert(f.Policy.CurrentGrant().Grant.Id!=oldGrant);
        });
        await check("full consent and revocation remain available during the separate background intake lease",async()=>
        {
            var f=await Fixture.Create();using var download=f.Files.Lease(f.Files.PathOf("intake"));
            await f.Request("set_mode","auto");await f.Answer((await f.Status(true)).Confirmation!);_=f.Policy.CurrentGrant();
            await f.Request("set_mode","notify");Assert(f.Intake.Mode().Mode=="notify");await Reject(()=>Task.Run(()=>f.Policy.CurrentGrant()));
        });
        await check("Notify manual full install requires exact Owner approval and remains Notify",async()=>
        {
            var f=await Fixture.Create();var id=Guid.NewGuid().ToString("N");await f.Request("install",id:id);Assert(f.Authorizations==0);var view=(await f.Status(true)).Confirmation!;Assert(view.Scope=="activate-system");
            await f.Answer(view);if(f.Router.Execution is {} task)await task;Assert(f.Authorizations==1&&f.Last?.ManualApproval is not null&&f.Intake.Mode().Mode=="notify"&&f.Intake.Mode().DelegationId is null);
            var replay=await f.Request("install",id:id);Assert((replay.State is "recorded" or "pending")&&f.Authorizations==1);await Reject(()=>f.Answer(view));
            Assert(FullSystemOperationJournal.Read(f.Files,f.Installation,id).GrantKind=="manual");
        });
        await check("Notify cancels pending full Auto consent and stale answer cannot enable it",async()=>
        {
            var f=await Fixture.Create();await f.Request("set_mode","auto");var view=(await f.Status(true)).Confirmation!;await f.Request("set_mode","notify");await Reject(()=>f.Answer(view));Assert(f.Intake.Mode().Mode=="notify");
        });
        await check("updater handoff stays running until replacement reconciles the protected request",async()=>
        {
            var f=await Fixture.Create();await f.Request("set_mode","auto");await f.Answer((await f.Status(true)).Confirmation!);f.CoordinatorPhase="restarting_updater";
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
        foreach(var reason in new[]{"target","state"})await check("manual approval rejects changed "+reason+" before Owner grant consumption",async()=>
        {
            var f=await Fixture.Create();await f.Request("install");var view=(await f.Status(true)).Confirmation!;
            if(reason=="state")f.Observed=f.Observed with{BootstrapEpoch=2};else f.Candidate=f.Candidate with{Version="0.6.2"};
            await Reject(()=>f.Answer(view));Assert(f.Authorizations==0&&f.Intake.Mode().Mode=="notify");
        });
        foreach(var reason in new[]{"scope","proposal","expiry"})await check("manual policy rejects "+reason+" mismatch without enabling Auto",async()=>
        {
            var f=await Fixture.Create();var proposal=f.Control.ProposeInstall(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);var digest=AuthorityProtocol.Digest(proposal);var scope=reason=="scope"?"delegate-system":"activate-system";
            var challenge=f.Ledger.SystemChallenge(digest,scope);f.Ledger.ApproveSystem(f.Owner,new(1,challenge.Id,digest,true),scope,digest);
            if(reason=="proposal")proposal=proposal with{TargetSchema=8};if(reason=="expiry")f.Now=f.Now.AddMinutes(11);
            await Reject(()=>f.Policy.AuthorizeManualAsync(proposal,f.Ledger,default));Assert(f.Intake.Mode().Mode=="notify");
        });
        foreach(var reason in new[]{"expiry","revoke"})await check("manual "+reason+" stops forward authority while bound repair cannot renew it",async()=>
        {
            var f=await Fixture.Create();var proposal=f.Control.ProposeInstall(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);var digest=AuthorityProtocol.Digest(proposal);var challenge=f.Ledger.SystemChallenge(digest,"activate-system");
            f.Ledger.ApproveSystem(f.Owner,new(1,challenge.Id,digest,true),"activate-system",digest);var operation=await f.Policy.AuthorizeManualAsync(proposal,f.Ledger,default);f.Policy.RequireCurrent(operation);
            if(reason=="expiry")f.Now=f.Now.AddMinutes(11);else f.Ledger.Revoke(f.Owner,digest,false);
            await Reject(()=>Task.Run(()=>f.Policy.RequireCurrent(operation)));var repair=f.Policy.RemintBootstrapRecovery(operation.OperationId);Assert(repair.OperationId==operation.OperationId&&repair.ManifestDigest==operation.ManifestDigest);
            await Reject(()=>Task.Run(()=>f.Policy.RequireCurrent(repair)));Assert(f.Intake.Mode().Mode=="notify");
        });
        await check("manual mixed-version proposal binds actual bootstrap and cannot downgrade it",async()=>
        {
            var f=await Fixture.Create();f.Observed=f.Observed with{BootstrapVersion="0.6.1"};
            var proposal=f.Control.ProposeInstall(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);Assert(proposal.FromVersion=="0.6.0"&&proposal.BootstrapVersion=="0.6.1");
            // Trusted candidate fixture publishes a subsequent compatible source.
            f.Candidate=f.Candidate with{Version="0.6.2"};proposal=proposal with{ToVersion="0.6.2"};
            var digest=AuthorityProtocol.Digest(proposal);var challenge=f.Ledger.SystemChallenge(digest,"activate-system");f.Ledger.ApproveSystem(f.Owner,new(1,challenge.Id,digest,true),"activate-system",digest);
            var operation=await f.Policy.AuthorizeManualAsync(proposal,f.Ledger,default);Assert(operation.ToVersion=="0.6.2"&&operation.BootstrapFromVersion=="0.6.1");
            var downgrade=proposal with{OperationId=Guid.NewGuid().ToString("N"),BootstrapVersion="0.6.3"};var changedDigest=AuthorityProtocol.Digest(downgrade);var changed=f.Ledger.SystemChallenge(changedDigest,"activate-system");f.Ledger.ApproveSystem(f.Owner,new(1,changed.Id,changedDigest,true),"activate-system",changedDigest);
            await Reject(()=>f.Policy.AuthorizeManualAsync(downgrade,f.Ledger,default));Assert(f.Intake.Mode().Mode=="notify");
        });
        await check("full mode cannot be interpreted without its immutable anchor pin",async()=>
        {
            var f=await Fixture.Create();var unpinned=new InstalledIntakeSelection(f.Files,f.Anchor with{FullSystemPolicyDigest=null},()=>f.Now);await Reject(()=>Task.Run(()=>unpinned.Mode()));
            Assert(f.Intake.Mode().GrantKind=="system");
        });
        await check("installer full enrollment plan is explicit, original-owner bound and side-effect free",()=>
        {
            var installation=Guid.NewGuid().ToString("N");const string owner="S-1-5-21-100-101-102-1001";var plan=FullSystemInstallerEnrollment.Prepare(installation,owner,2,"family","parent",new('a',64),new('b',64),DateTimeOffset.UtcNow,true);
            var enrollment=NativeJson.Read<FullSystemEnrollment>(plan.Enrollment);var grant=NativeJson.Read<FullSystemModeGrant>(plan.InitialGrant);var mode=NativeJson.Read<NativeUpdateMode>(plan.Mode);
            Assert(plan.PolicyDigest==Hash(plan.Enrollment)&&enrollment.OwnerSid==owner&&grant.PolicyDigest==plan.PolicyDigest&&mode.IsSystem&&mode.DelegationId==grant.Id);
            return Reject(()=>Task.FromResult(FullSystemInstallerEnrollment.Prepare(installation,owner,1,"family","parent",new('a',64),new('b',64),DateTimeOffset.UtcNow,true)));
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
        internal InstalledStateFiles Files=InstalledStateFiles.Private(SafeDirectory.CreatePrivateSession(Path.GetTempPath()));internal InstalledAuthorityAnchor Anchor=null!;internal OwnerApprovalLedger Ledger=null!;
        internal FullSystemUpdatePolicy Policy=null!;internal FullSystemModeControl Control=null!;internal InstalledIntakeSelection Intake=null!;internal AuthorityEndpointRouter Router=null!;
        internal AuthorityPeer Owner=new("S-1-5-21-100-101-102-1001",12,34,1,true),Runtime=new("S-1-5-80-100-101-102-103-104",56,78,0,false);
        internal FullSystemInstalledState Observed=null!;internal SystemCandidatePolicyFacts Candidate=null!;internal FullSystemDispatchRequest? Last;internal VerifiedSystemOperation? Capability;internal int Authorizations;internal string CoordinatorPhase="healthy";
        internal static async Task<Fixture> Create()
        {
            var f=new Fixture();foreach(var directory in new[]{"intake","owner-ledger",".authority","payloads"})Directory.CreateDirectory(f.Files.PathOf(directory));
            var plan=FullSystemInstallerEnrollment.Prepare(f.Installation,f.Owner.Sid,2,"family","parent",new('a',64),new('a',64),f.Now,false);
            File.WriteAllBytes(f.Files.PathOf(plan.EnrollmentPath),plan.Enrollment);File.WriteAllBytes(f.Files.PathOf(plan.GrantPath),plan.InitialGrant);File.WriteAllBytes(f.Files.PathOf("intake/mode.json"),plan.Mode);File.WriteAllBytes(f.Files.PathOf("intake/enrollment.json"),plan.OperatorBinding);
            f.Anchor=new(1,f.Installation,ServiceDefinition.Name,InstalledAuthorityReader.AuthorityService,f.Owner.Sid,new('a',64),new('a',64),null,plan.PolicyDigest);
            var manifest=JsonSerializer.SerializeToUtf8Bytes(new{version="0.6.0"});var release=Hash(manifest);Directory.CreateDirectory(f.Files.PathOf("payloads/"+release));File.WriteAllBytes(f.Files.PathOf("payloads/"+release+"/bundle.json"),manifest);
            var web=new InstalledWeb(1,f.Installation,"https://yahad.invalid",32000);var webDigest=f.Put("web.json",web);var data=Guid.NewGuid().ToString("N");var head=new InstalledActivation(1,f.Installation,1,release,data,6,6,new('a',64),new('a',64),new('a',64),new('a',64),webDigest,"committed",null);
            var activation=f.Put("activation.json",head);f.Put("epoch.json",new InstalledEpoch(1,f.Installation,1,activation));f.Put("bootstrap.json",new{schema=1,service=ServiceDefinition.Name,installationId=f.Installation,activationReceiptDigest=activation});f.Observed=new("0.6.0",1,activation,6);
            f.Put("intake/delegation-template.json",new RoutineDelegation(1,Guid.NewGuid().ToString("N"),f.Installation,f.Owner.Sid,data,new('a',64),new('a',64),new('a',64),"stable",web.Origin,null,"0.6.1","0.6.9",6,f.Now.AddDays(90).ToUnixTimeSeconds(),100,2147483648));
            var api=SystemSourceRelease.Api;var source=await SystemSourceRelease.CheckAsync((uri,_,_)=>Task.FromResult(uri.AbsoluteUri.EndsWith("/releases/latest",StringComparison.Ordinal)?JsonSerializer.SerializeToUtf8Bytes(new{id=1,url=api+"/releases/1",tag_name="v0.6.1",draft=false,prerelease=false,assets=new[]{new{id=2,name="yahad-system-native-0.6.1.zip",state="uploaded",size=1,digest="sha256:"+new string('b',64),url=api+"/releases/assets/2"}}}):JsonSerializer.SerializeToUtf8Bytes(new{@ref="refs/tags/v0.6.1",url=api+"/git/refs/tags/v0.6.1",@object=new{type="commit",sha=new string('a',40),url=api+"/git/commits/"+new string('a',40)}})),default);
            var components=Enum.GetValues<SystemComponentKind>().Select(kind=>new SystemComponentDescriptor(kind,FixedSystemComponents.SlotPrefix(kind),FixedSystemComponents.EntryPoint(kind),new('d',64),new[]{new SystemComponentFile(FixedSystemComponents.EntryPoint(kind),1,new('d',64))})).ToArray();
            f.Candidate=new(new('e',64),new('f',64),source.Version,source.SourceCommit,7,new[]{6},components);
            var sourceReceipt=new SystemSourceReceipt(2,FullSystemUpdatePolicy.Profile,f.Installation,NativeCandidateIntake.Repository,source.ReleaseId,source.Tag,source.AssetId,source.AssetName,source.Bytes,source.ArchiveSha256,source.SourceCommit,source.SourceProofDigest,f.Candidate.ManifestDigest,f.Candidate.ApplicationBundleDigest,new('a',64),7,source.Version);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(sourceReceipt);f.Receipt=Hash(bytes);File.WriteAllBytes(f.Files.PathOf("intake/system-source-"+f.Receipt+".json"),bytes);f.Put("intake/system-available.json",new{Format=2,Profile=FullSystemUpdatePolicy.Profile,InstallationId=f.Installation,SourceReceiptDigest=f.Receipt,SystemManifestDigest=f.Candidate.ManifestDigest});
            f.Ledger=new(f.Files,f.Installation,f.Owner.Sid,()=>f.Now);f.Policy=FullSystemUpdatePolicy.Open(f.Files,f.Installation,f.Owner.Sid,plan.PolicyDigest,()=>f.Now,f);f.Control=new(f.Files,f.Anchor,f.Ledger,f.Policy,f,()=>f.Now);f.Intake=new(f.Files,f.Anchor,()=>f.Now);
            f.RecreateRouter();return f;
        }
        internal void RecreateRouter()
        {
            Router=new(Files,Anchor,Runtime.Sid,Intake,Ledger,_=>throw new Exception("LEGACY_PIPELINE_ENTERED"),_=>throw new Exception("UNEXPECTED_RUNTIME_STOP"),work=>work());
            Router.ConfigureFullSystem(Control,()=>Observed,async(request,token)=>{Last=request;Capability=request.ManualApproval is{} proposal?await Policy.AuthorizeManualAsync(proposal,Ledger,token):Policy.Authorize(request.OperationId,request.SourceReceiptDigest,Observed);Authorizations++;},()=>CoordinatorPhase,()=>false);
        }
        internal Task<EndpointReply> Request(string action,string? mode=null,string? id=null)
        {id??=Guid.NewGuid().ToString("N");return Send(false,id,"request",new AuthorityRequest(1,id,action,action=="install"?Receipt:null,mode));}
        internal Task<EndpointReply> Status(bool owner=false)=>Send(owner,Guid.NewGuid().ToString("N"),owner?"confirmation":"status",new{});
        internal Task<EndpointReply> Answer(OperationConfirmation view,AuthorityPeer? peer=null)=>Send(true,Guid.NewGuid().ToString("N"),"answer",new OwnerAnswer(1,view.Challenge,view.Digest,true),peer);
        private Task<EndpointReply> Send(bool owner,string id,string method,object body,AuthorityPeer? peer=null)=>Router.DispatchAsync(owner?AuthorityEndpointRole.Owner:AuthorityEndpointRole.Runtime,peer??(owner?Owner:Runtime),JsonSerializer.SerializeToUtf8Bytes(new AuthorityFrame(1,id,method,JsonSerializer.SerializeToElement(body))));
        private string Put<T>(string path,T value){var bytes=JsonSerializer.SerializeToUtf8Bytes(value);File.WriteAllBytes(Files.PathOf(path),bytes);return Hash(bytes);}
        public SystemCandidatePolicyFacts RequireVerified(string digest){if(digest!=Receipt)throw new IOException("TRUSTED_FIXTURE_TARGET");return Candidate;}
    }
}

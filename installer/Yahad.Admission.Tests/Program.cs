using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yahad.Install;
using Yahad.Service;

if(args is not ["--node",var node,"--report",var report])throw new IOException("PRIVATE_TOOL_AND_REPORT_REQUIRED");
node=Path.GetFullPath(node);var nodeHash=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(node)));
var results=new List<object>();var failed=0;
void Assert(bool value,string reason="assertion failed"){if(!value)throw new Exception(reason);}
async Task Check(string name,Func<Task> test){try{await test();Console.WriteLine("PASS "+name);results.Add(new{name,status="PASS"});}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+" "+e);results.Add(new{name,status="FAIL",reason=e.GetType().Name});}}
async Task Reject(Func<Task> action,string? expected=null)
{
    try{await action();}catch(Exception e)when(e is ServiceStartDenied or IOException or Win32Exception or JsonException or CryptographicException or FormatException or OperationCanceledException or ObjectDisposedException)
    {if(expected is not null&&e.Message!=expected)throw new Exception("wrong refusal: "+e.Message);return;}
    throw new Exception("admission unexpectedly granted");
}
async Task<Fixture> Make(int schema=5)=>await Fixture.Create(node,nodeHash,schema);

await Check("valid signed fixture links exact release and data to service lifecycle while holding actual lease",async()=>
{
    using var f=await Make();var before=f.DataHash();var controller=new ServiceController(new ResolveAuthorizer(f));var reports=new List<ServiceReport>();
    var result=await controller.RunAsync(r=>{reports.Add(r);if(r.State==ServiceState.Running){Assert(f.Factory.View!.ReleaseId=="fixture-1"&&f.Factory.View.ObservedSchema==5&&f.Factory.View.DataDirectory==f.State);try{using var denied=WindowsFileBoundary.AcquireDataLease(f.State);throw new Exception("second writer admitted");}catch(Win32Exception){}controller.Control(ServiceDefinition.Stop);}});
    Assert(result.Failure==ServiceFailure.None&&f.Factory.Starts==1&&before==f.DataHash());using var released=WindowsFileBoundary.AcquireDataLease(f.State);
});
foreach(var phase in new[]{"prepared","publish-intent","candidate-start-intent","recovery-pending","unknown"})
await Check("signed interrupted phase refuses: "+phase,async()=>
{using var f=await Make();f.Phase=phase;f.Publish();await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_INCOMPLETE_INTENT");Assert(f.Factory.Starts==0);});
await Check("stale signed epoch fails the independently supplied fixture floor",async()=>
{using var f=await Make();f.MinimumEpoch=2;await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_HEAD_IDENTITY");});
await Check("exact head digest anchor rejects a different valid signed current head",async()=>
{using var f=await Make();var old=f.Binding;f.Epoch=2;f.Publish();await Reject(async()=>{using var grant=await f.Resolve(old);},"ADMISSION_HEAD_ANCHOR");});
await Check("foreign installation identity refuses",async()=>
{using var f=await Make();await Reject(async()=>{using var grant=await f.Resolve(f.Binding with{InstallationId=Guid.NewGuid().ToString("N")});},"ADMISSION_HEAD_IDENTITY");});
foreach(var name in new[]{"release.json","selection.json","intent.json","owner.json"})
await Check("changed signed document bytes refuse: "+name,async()=>
{using var f=await Make();var path=Path.Combine(f.Root,name);var bytes=File.ReadAllBytes(path);bytes[^10]^=1;File.WriteAllBytes(path,bytes);await Reject(async()=>{using var grant=await f.Resolve();});Assert(f.Factory.Starts==0);});
await Check("unknown pinned key identity cannot be selected from the envelope",async()=>
{using var f=await Make();f.RewriteEnvelope("head.json",json=>json["KeyId"]="other");await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_UNKNOWN_SIGNER");});
await Check("signature purpose separation rejects a control-key head signed for owner purpose",async()=>
{using var f=await Make();var head=f.ReadPayload<AdmissionHead>("head.json");File.WriteAllBytes(Path.Combine(f.Root,"head.json"),f.Sign(f.ControlKey,"control","owner",head));await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_SIGNATURE");});
await Check("correctly signed duplicate payload fields still refuse",async()=>
{
    using var f=await Make();var current=f.ReadPayload<AdmissionHead>("head.json");var text=JsonSerializer.Serialize(current).Replace("\"Format\":1","\"Format\":1,\"Format\":1");
    var payload=Encoding.UTF8.GetBytes(text);var envelope=new FixtureSignedEnvelope("control",Convert.ToBase64String(payload),Convert.ToBase64String(f.ControlKey.SignData(FixtureDocumentVerifier.Message("head",payload),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    File.WriteAllBytes(Path.Combine(f.Root,"head.json"),JsonSerializer.SerializeToUtf8Bytes(envelope));await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_DUPLICATE_FIELD");
});
await Check("observation output must be an existing pinned private directory",async()=>
{
    using var f=await Make();Directory.Move(Path.Combine(f.Root,"observations"),Path.Combine(f.Root,"old-observations"));File.WriteAllText(Path.Combine(f.Root,"observations"),"not a directory");
    await Reject(async()=>{using var grant=await f.Resolve();});Assert(f.Factory.Starts==0);
});
await Check("expired owner authorization refuses before launch",async()=>
{using var f=await Make();f.Expiry=f.Now.ToUnixTimeSeconds()-1;f.Publish();await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_OWNER_EXPIRED");});
await Check("missing owner authorization refuses",async()=>
{using var f=await Make();File.Delete(Path.Combine(f.Root,"owner.json"));await Reject(async()=>{using var grant=await f.Resolve();});});
await Check("owner approval for a different intent nonce refuses",async()=>
{using var f=await Make();f.OwnerNonce=Guid.NewGuid().ToString("N");f.Publish();await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_OWNER_SCOPE");});
foreach(var name in new[]{"update-policy","update-pin"})
await Check("observed policy/pin drift refuses: "+name,async()=>
{using var f=await Make();File.WriteAllText(Path.Combine(f.Root,name),"changed");await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_POLICY_DRIFT");});
await Check("data master-key identity mismatch refuses",async()=>
{using var f=await Make();File.WriteAllText(f.MasterKey,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_KEY_IDENTITY");});
await Check("selected data identity cannot be retargeted to another data set",async()=>
{using var f=await Make();f.IdentityDataId=Guid.NewGuid().ToString("N");f.Publish();await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_KEY_IDENTITY");});
await Check("payload bytes and complete member closure are observed",async()=>
{using var f=await Make();File.AppendAllText(Path.Combine(f.Root,"payload/worker.mjs"),"changed");await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_RELEASE_BYTES");});
await Check("extra payload file refuses even when signed member hashes still match",async()=>
{using var f=await Make();File.WriteAllText(Path.Combine(f.Root,"payload/extra.mjs"),"unselected");await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_RELEASE_MEMBERS");});
await Check("signed schema range cannot replace actual SQLite observation",async()=>
{using var f=await Make(6);await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_SCHEMA_INCOMPATIBLE");Assert(f.Factory.Starts==0);});
await Check("WAL-mode clean database is inspected immutably without creating source sidecars",async()=>
{using var f=await Make();await f.Node("const{DatabaseSync}=require('node:sqlite');const d=new DatabaseSync(process.argv[1]);d.exec('PRAGMA journal_mode=WAL');d.close();",[Path.Combine(f.State,"app.sqlite")]);var before=f.DataHash();using var grant=await f.Resolve();Assert(before==f.DataHash()&&!Directory.GetFiles(f.State,"app.sqlite-*").Any());});
await Check("actual crash-left committed WAL is refused and never checkpointed or deleted",async()=>
{
    using var f=await Make();const string script="const{DatabaseSync}=require('node:sqlite'),fs=require('node:fs');const d=new DatabaseSync(process.argv[1]);d.exec('PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; UPDATE schema_version SET version=6');fs.writeFileSync(process.argv[2],'ready');setInterval(()=>{},1000);";
    using(var writer=JobProcess.Start(node,["-e",script,Path.Combine(f.State,"app.sqlite"),Path.Combine(f.Root,"wal-ready")],f.Root,LaunchEnvironment.Clean(f.Root)))
    {using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(!File.Exists(Path.Combine(f.Root,"wal-ready")))await Task.Delay(10,bound.Token);await writer.TerminateAndWaitForTreeEmptyAsync(bound.Token);}
    Assert(new FileInfo(Path.Combine(f.State,"app.sqlite-wal")).Length>0);var before=f.DataHash();await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_WAL_RECONCILIATION_REQUIRED");Assert(before==f.DataHash());
});
await Check("actual busy job blocks admission without a Boolean override",async()=>
{
    using var f=await Make();using var busy=JobProcess.Start(node,["-e","setInterval(()=>{},1000)"],f.Root,LaunchEnvironment.Clean(f.Root));using var cancel=new CancellationTokenSource(150);
    await Reject(async()=>{using var grant=await f.Resolver(busy).ResolveAsync(f.Binding,cancel.Token);});await busy.TerminateAndWaitForTreeEmptyAsync(default);
});
await Check("actual competing data lease blocks admission",async()=>
{using var f=await Make();using var busy=WindowsFileBoundary.AcquireDataLease(f.State);await Reject(async()=>{using var grant=await f.Resolve();});});
await Check("owner expiration is rechecked immediately before lifecycle start",async()=>
{using var f=await Make();using var grant=await f.Resolve();f.Now=f.Now.AddMinutes(6);await Reject(()=>grant.Recovery.RequireStartAllowedAsync(grant.Release,default),"ADMISSION_OWNER_EXPIRED");Assert(f.Factory.Starts==0);});
await Check("reservation pins data bytes and rejects a late new member",async()=>
{
    using var f=await Make();using var grant=await f.Resolve();try{File.WriteAllText(Path.Combine(f.State,"app.sqlite"),"replace");throw new Exception("pinned DB overwritten");}catch(IOException){}
    File.WriteAllText(Path.Combine(f.State,"late-file"),"late");await Reject(()=>grant.Recovery.RequireStartAllowedAsync(grant.Release,default),"ADMISSION_DATA_MEMBERS_CHANGED");
});
foreach(var name in new[]{"PENDING.JSON","HEAD.JSON.PARTIAL.crash","Selection.Json.Partial"})
await Check("case-insensitive interrupted artifact refuses: "+name,async()=>
{using var f=await Make();File.WriteAllText(Path.Combine(f.Root,name),"interrupted");await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_PENDING_ARTIFACT");});
await Check("late extra payload file refuses again immediately before start",async()=>
{using var f=await Make();using var grant=await f.Resolve();File.WriteAllText(Path.Combine(f.Root,"payload/late.mjs"),"late");await Reject(()=>grant.Recovery.RequireStartAllowedAsync(grant.Release,default),"ADMISSION_RELEASE_MEMBERS");});
await Check("released reservation cannot become a later start grant",async()=>
{using var f=await Make();var grant=await f.Resolve();grant.Dispose();await Reject(()=>grant.Recovery.RequireStartAllowedAsync(grant.Release,default));});
await Check("recovered data requires complete receipt and matching actual tree/schema/key/policy",async()=>
{using var f=await Make();f.Phase="recovered";f.Publish();using var grant=await f.Resolve();Assert(f.Factory.View!.ObservedSchema==5);});
await Check("a signed recovery receipt cannot hide changed recovered data",async()=>
{using var f=await Make();f.Phase="recovered";f.Publish();File.AppendAllText(Path.Combine(f.State,"media/file"),"changed");await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_RECOVERY_UNVERIFIED");});
await Check("incomplete signed recovery record refuses",async()=>
{using var f=await Make();f.Phase="recovered";f.RecoveryPhase="restoring";f.Publish();await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_RECOVERY_UNVERIFIED");});
await Check("missing recovery owner scope refuses",async()=>
{using var f=await Make();f.Phase="recovered";f.OwnerOperation="activate";f.Publish();await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_OWNER_SCOPE");});
await Check("actual interrupted publisher leaves durable pending evidence that blocks old selection",async()=>
{
    using var f=await Make();File.WriteAllBytes(Path.Combine(f.Root,"staged-intent.json"),f.Sign(f.ControlKey,"control","intent",new{phase="prepared"}));
    const string script="const fs=require('node:fs'),p=require('node:path');const root=process.argv[1],fd=fs.openSync(p.join(root,'pending.json'),'wx');fs.writeSync(fd,fs.readFileSync(p.join(root,'staged-intent.json')));fs.fsyncSync(fd);fs.closeSync(fd);fs.writeFileSync(p.join(root,'publisher-ready'),'ready');setInterval(()=>{},1000);";
    using(var publisher=JobProcess.Start(node,["-e",script,f.Root],f.Root,LaunchEnvironment.Clean(f.Root)))
    {using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(!File.Exists(Path.Combine(f.Root,"publisher-ready")))await Task.Delay(10,bound.Token);await publisher.TerminateAndWaitForTreeEmptyAsync(bound.Token);}
    var before=f.DataHash();await Reject(async()=>{using var grant=await f.Resolve();},"ADMISSION_PENDING_ARTIFACT");Assert(before==f.DataHash()&&File.Exists(Path.Combine(f.Root,"pending.json")));
});
await Check("new verified recovered data selection leaves previous data bytes untouched",async()=>
{
    using var f=await Make();var old=f.State;var before=f.DataHash();f.DataId=Guid.NewGuid().ToString("N");await f.Seed(5);f.Phase="recovered";f.Publish();using var grant=await f.Resolve();
    Assert(f.Factory.View!.DataDirectory!=old&&before==Fixture.TreeHash(old));
});
await Check("production entry still has no fixture trust or activation wiring",async()=>
{await Reject(()=>new UnconfiguredActivationResolver().ResolveAsync(new(new string('a',32),new string('b',64)),default));});
File.WriteAllText(report,JsonSerializer.Serialize(new{kind="signed-fixture-admission-only",passed=results.Count-failed,failed,productionWired=false,
    signingPrivateKeysPersisted=false,syntheticMasterKeysInPrivateFixtures=true,scmInvoked=false,listenersOpened=false,activeDataReplaced=false,tests=results},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{results.Count-failed} passed; {failed} failed. Test-only signatures; no production admission.");return failed==0?0:1;

internal sealed class ResolveAuthorizer(Fixture fixture):IServiceStartAuthorizer
{public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)=>fixture.Resolve(cancellation:cancellation);}
internal sealed class DummyFactory:IReadOnlyAdmissionLauncherFactory
{
    internal ReadOnlyAdmissionView? View;internal int Starts;
    public IPrivateAppLauncher Create(ReadOnlyAdmissionView view){View=view;return new Dummy(this);}
    private sealed class Dummy(DummyFactory owner):IPrivateAppLauncher
    {
        public Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken cancellation){owner.Starts++;return Task.FromResult<IContainedAppSession>(new Session());}
        public Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken cancellation)=>Task.CompletedTask;
    }
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
internal sealed class Fixture:IDisposable
{
    internal readonly ECDsa ReleaseKey=ECDsa.Create(ECCurve.NamedCurves.nistP256),ControlKey=ECDsa.Create(ECCurve.NamedCurves.nistP256),OwnerKey=ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FixtureDocumentVerifier releases,control,owners;private readonly string node,nodeHash;private JobProcess empty=null!;
    internal readonly string Root,Installation=Guid.NewGuid().ToString("N"),Nonce=Guid.NewGuid().ToString("N");
    internal string DataId=Guid.NewGuid().ToString("N"),Phase="committed",RecoveryPhase="complete";internal string? IdentityDataId,OwnerNonce,OwnerOperation;
    internal long Epoch=1,MinimumEpoch=1,Expiry;internal DateTimeOffset Now=DateTimeOffset.UtcNow;internal readonly DummyFactory Factory=new();
    internal string State=>Path.Combine(Root,"data",DataId,"state");internal string MasterKey=>Path.Combine(Root,"data",DataId,"master-key");
    private Fixture(string node,string hash)
    {
        this.node=node;nodeHash=hash;Root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());Directory.CreateDirectory(Path.Combine(Root,"payload"));Directory.CreateDirectory(Path.Combine(Root,"observations"));
        File.WriteAllText(Path.Combine(Root,"payload/worker.mjs"),"// synthetic read-only admission payload");File.WriteAllText(Path.Combine(Root,"update-policy"),"fixture-policy");File.WriteAllText(Path.Combine(Root,"update-pin"),"fixture-pin");
        releases=new("release",ReleaseKey.ExportSubjectPublicKeyInfo());control=new("control",ControlKey.ExportSubjectPublicKeyInfo());owners=new("owner",OwnerKey.ExportSubjectPublicKeyInfo());Expiry=Now.AddMinutes(5).ToUnixTimeSeconds();
    }
    internal static async Task<Fixture> Create(string node,string hash,int schema)
    {var f=new Fixture(node,hash);await f.Seed(schema);f.empty=JobProcess.Start(node,["-e",""],f.Root,LaunchEnvironment.Clean(f.Root));await f.empty.WaitAsync(default);await f.empty.WaitForTreeEmptyAsync(default);f.Publish();return f;}
    internal async Task Seed(int schema)
    {
        Directory.CreateDirectory(Path.Combine(State,"media"));File.WriteAllText(MasterKey,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));File.WriteAllText(Path.Combine(State,"media/file"),"synthetic data");
        await Node("const{DatabaseSync}=require('node:sqlite');const d=new DatabaseSync(process.argv[1]);d.exec('CREATE TABLE schema_version(version INTEGER);INSERT INTO schema_version VALUES('+Number(process.argv[2])+');CREATE TABLE synthetic_data(id INTEGER PRIMARY KEY,value TEXT);INSERT INTO synthetic_data VALUES(1,\\'fixture\\')');d.close();",[Path.Combine(State,"app.sqlite"),schema.ToString()]);
    }
    internal async Task Node(string script,string[] arguments)
    {using var job=JobProcess.Start(node,new[]{"-e",script}.Concat(arguments),Root,LaunchEnvironment.Clean(Root));if(await job.WaitAsync(default)!=0)throw new IOException("FIXTURE_NODE_FAILED");await job.WaitForTreeEmptyAsync(default);}
    internal byte[] Sign<T>(ECDsa key,string id,string purpose,T value)
    {var payload=JsonSerializer.SerializeToUtf8Bytes(value);return JsonSerializer.SerializeToUtf8Bytes(new FixtureSignedEnvelope(id,Convert.ToBase64String(payload),Convert.ToBase64String(key.SignData(FixtureDocumentVerifier.Message(purpose,payload),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));}
    internal T ReadPayload<T>(string name){using var json=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root,name)));return JsonSerializer.Deserialize<T>(Convert.FromBase64String(json.RootElement.GetProperty("Payload").GetString()!))!;}
    internal void RewriteEnvelope(string name,Action<JsonObject> change){var path=Path.Combine(Root,name);var json=JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();change(json);File.WriteAllText(path,json.ToJsonString());}
    internal void Publish()
    {
        string Write<T>(string name,ECDsa signer,string id,string purpose,T value){var bytes=Sign(signer,id,purpose,value);File.WriteAllBytes(Path.Combine(Root,name),bytes);return Hash(bytes);}
        var policy=Hash(File.ReadAllBytes(Path.Combine(Root,"update-policy")));var pin=Hash(File.ReadAllBytes(Path.Combine(Root,"update-pin")));var key=Hash(File.ReadAllBytes(MasterKey));
        var identity=Write("data/"+DataId+"/identity.json",ControlKey,"control","data-identity",new AdmissionDataIdentity(1,Installation,IdentityDataId??DataId,key));
        var entry=File.ReadAllBytes(Path.Combine(Root,"payload/worker.mjs"));var release=Write("release.json",ReleaseKey,"release","release",new AdmissionRelease(1,"fixture-1",5,5,nodeHash,"worker.mjs",[new("worker.mjs",entry.Length,Hash(entry))]));
        var selection=Write("selection.json",ControlKey,"control","selection",new AdmissionSelection(1,Installation,Epoch,"fixture-1",DataId,identity));
        var intent=Write("intent.json",ControlKey,"control","intent",new AdmissionIntent(1,Installation,Epoch,Nonce,Phase,release,selection,DataId,policy,pin));
        var owner=Write("owner.json",OwnerKey,"owner","owner",new AdmissionOwner(1,Installation,Epoch,OwnerNonce??Nonce,OwnerOperation??(Phase=="recovered"?"recover":"activate"),release,selection,DataId,policy,pin,Expiry));
        string? recovery=null;if(Phase=="recovered")
        {
            var tree=DataHash();var backup=Write("backup.json",ControlKey,"control","backup",new AdmissionBackup(1,Installation,DataId,Guid.NewGuid().ToString("N"),5,tree,key,policy,pin));
            recovery=Write("recovery.json",ControlKey,"control","recovery",new AdmissionRecovery(1,Installation,Epoch,RecoveryPhase,release,selection,DataId,owner,backup,5,tree));
        }
        Write("head.json",ControlKey,"control","head",new AdmissionHead(1,Installation,Epoch,release,selection,intent,owner,recovery,policy,pin));
    }
    internal InstallationBinding Binding=>new(Installation,Hash(File.ReadAllBytes(Path.Combine(Root,"head.json"))));
    internal SignedFixtureAdmissionResolver Resolver(JobProcess? job=null)=>new(Root,job??empty,node,releases,control,owners,MinimumEpoch,Factory,()=>Now);
    internal Task<AuthorizedServiceStart> Resolve(InstallationBinding? binding=null,CancellationToken cancellation=default)=>Resolver().ResolveAsync(binding??Binding,cancellation);
    internal string DataHash()=>TreeHash(State);
    internal static string TreeHash(string directory)
    {
        using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(var path in Directory.GetFiles(directory,"*",SearchOption.AllDirectories).Where(p=>Path.GetFileName(p)!="process.lock").OrderBy(p=>Path.GetRelativePath(directory,p).Replace('\\','/'),StringComparer.Ordinal))
        {var name=Path.GetRelativePath(directory,path).Replace('\\','/');using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var content=new MemoryStream();input.CopyTo(content);var bytes=content.ToArray();digest.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{name}\0{bytes.Length}\0{Hash(bytes)}\n")));}
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Dispose(){empty?.Dispose();releases.Dispose();control.Dispose();owners.Dispose();ReleaseKey.Dispose();ControlKey.Dispose();OwnerKey.Dispose();}
}

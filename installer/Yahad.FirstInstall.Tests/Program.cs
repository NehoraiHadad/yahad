using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yahad.Install;
using Yahad.Service;
using Yahad.Setup;

if(args is ["--elevation-framing-only"])
{
    var result=await ElevationFramingRegression.RunAsync();Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));return result.Ok?0:1;
}
if(args is not [var node,var repo,var helper,var output])throw new IOException("TEST_INPUTS_REQUIRED");
Directory.CreateDirectory(output);var cases=new List<object>();int failures=0;
void Assert(bool condition,string code="assertion failed"){if(!condition)throw new Exception(code);}
async Task Check(string name,Func<Task> run){try{await run();cases.Add(new{name,status="PASS"});Console.WriteLine("PASS "+name);}catch(Exception e){failures++;cases.Add(new{name,status="FAIL",reason=e.GetType().Name});Console.WriteLine("FAIL "+name+" "+e);}}
async Task Refuse(Func<Task> run){try{await run();}catch(Exception e)when(e is IOException or UnauthorizedAccessException or OperationCanceledException or Win32Exception or JsonException){return;}throw new Exception("unexpected admission");}
var owner=new OwnerProcess("S-1-5-21-1-2-3-1001",111,123,new string('a',64));
await Check("single installer path performs prepare publish service readiness then browser handoff",async()=>
{var ops=new FakeHost();var phases=new List<string>();var flow=new InstallFlow(ops);var result=await flow.RunAsync(owner,false,p=>phases.Add(p.Phase),default);Assert(result.Ok&&ops.Calls.SequenceEqual(new[]{"prepare","publish","ready"})&&phases.Last()=="ready"&&result.SetupToken!.Length==43);await Refuse(()=>flow.RunAsync(owner,false,_=>{},default));});
foreach(var step in new[]{"prepare","publish","ready"})await Check("failed "+step+" never reports ready or removes data",async()=>
{var ops=new FakeHost{Failure=step};var phases=new List<string>();await Refuse(()=>new InstallFlow(ops).RunAsync(owner,false,p=>phases.Add(p.Phase),default));Assert(!phases.Contains("ready")&&!ops.Calls.Contains("uninstall"));});
await Check("cancel before service publication performs no subsequent host operation",async()=>
{using var cancel=new CancellationTokenSource();var ops=new FakeHost{AfterPrepare=cancel.Cancel};await Refuse(()=>new InstallFlow(ops).RunAsync(owner,false,_=>{},cancel.Token));Assert(ops.Calls.SequenceEqual(new[]{"prepare"}));});
await Check("invalid owner fields cannot reach a host operation",async()=>
{var ops=new FakeHost();await Refuse(()=>new InstallFlow(ops).RunAsync(owner with{Sid="S-1-5-18"},false,_=>{},default));Assert(ops.Calls.Count==0);});
foreach(var url in new[]{"https://evil.invalid/","http://192.168.1.2:47123/","http://127.0.0.1:47123/?token=x","http://user@127.0.0.1:47123/"})await Check("browser handoff refuses "+url,async()=>
{var ops=new FakeHost{Result=new(true,"READY",url,new string('a',43))};await Refuse(()=>new InstallFlow(ops).RunAsync(owner,false,_=>{},default));});
await Check("failed health result never becomes browser-ready",async()=>
{var phases=new List<string>();var ops=new FakeHost{Result=new(false,"SERVER_NOT_READY")};var result=await new InstallFlow(ops).RunAsync(owner,false,p=>phases.Add(p.Phase),default);Assert(!result.Ok&&!phases.Contains("ready"));});
await Check("uninstall only calls the owned uninstall adapter and reports data preservation",async()=>
{var ops=new FakeHost();var result=await new InstallFlow(ops).RunAsync(owner,true,_=>{},default);Assert(result.Ok&&result.DataPreserved&&ops.Calls.SequenceEqual(new[]{"uninstall"}));});
foreach(var path in new[]{"../personal.txt","payload/../../secret","payload/C:/bad","payload/CON","payload/x\\y","host/trailing. ","host//duplicate"})await Check("embedded extraction refuses unsafe path "+path,()=>
{try{InstallPackage.Name(path);}catch(IOException){return Task.CompletedTask;}throw new Exception("unsafe member accepted");});
await Check("real package reader verifies and extracts exact offline payload bytes",()=>
{
 var fixture=Package();using var package=new InstallPackage(new MemoryStream(fixture.Zip),fixture.Manifest);var root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());package.Extract(root,_=>{},default);
 Assert(File.ReadAllText(Path.Combine(root,"payload/app/src/main.ts"))=="synthetic-app-not-executed");return Task.CompletedTask;
});
await Check("corrupt package digest and duplicate zip entry refuse before extraction",async()=>
{
 var fixture=Package();var manifest=NativeJson.Read<InstallPackageManifest>(fixture.Manifest);await Refuse(()=>{using var p=new InstallPackage(new MemoryStream(fixture.Zip),JsonSerializer.SerializeToUtf8Bytes(manifest with{ZipSha256=new string('0',64)}));return Task.CompletedTask;});
 fixture=Package(duplicate:true);await Refuse(()=>{using var p=new InstallPackage(new MemoryStream(fixture.Zip),fixture.Manifest);return Task.CompletedTask;});
});
await Check("wrong member hash fails without overwriting preexisting files",async()=>
{
 var fixture=Package();var document=NativeJson.Read<InstallPackageManifest>(fixture.Manifest);var records=document.Files.Select((x,i)=>i==0?x with{Sha256=new string('0',64)}:x).ToArray();using var p=new InstallPackage(new MemoryStream(fixture.Zip),JsonSerializer.SerializeToUtf8Bytes(document with{Files=records}));
 var root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());await Refuse(()=>{p.Extract(root,_=>{},default);return Task.CompletedTask;});Assert(!File.Exists(Path.Combine(root,"payload/app/src/main.ts")));
});
await Check("initial data capability is admin-owned read/delete only, never service-writable",()=>
{
 var sid=new System.Security.Principal.SecurityIdentifier("S-1-5-80-1-2-3-4-5");var good=new RawSecurityDescriptor($"O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1300a9;;;{sid.Value})");WindowsFileBoundary.ValidateInitialSeedAcl(good,sid);
 foreach(var bad in new[]{new RawSecurityDescriptor($"O:BAD:P(A;;FA;;;BA)(A;;FA;;;{sid.Value})"),new RawSecurityDescriptor($"O:{sid.Value}D:P(A;;FR;;;{sid.Value})")})
 {try{WindowsFileBoundary.ValidateInitialSeedAcl(bad,sid);throw new Exception("seed accepted");}catch(IOException){}}
 return Task.CompletedTask;
});
await Check("user installer enables existing real connector; only explicit test profile uses mock",()=>
{
 var production=RuntimeConnectorSettings.For(RuntimeConnectorProfile.Production);Assert(production.Connector=="baileys"&&production.Approval=="I_APPROVE_UNOFFICIAL_LINK");
 var synthetic=RuntimeConnectorSettings.For(RuntimeConnectorProfile.Synthetic);Assert(synthetic.Connector=="mock"&&synthetic.Approval=="no");
 try{RuntimeConnectorSettings.For((RuntimeConnectorProfile)99);throw new Exception("unknown profile accepted");}catch(IOException){}
 return Task.CompletedTask;
});
await Check("preserved reinstall binds exact owner package activation and keys without rewriting data",async()=>
{
 var fixture=Package();var manifest=NativeJson.Read<InstallPackageManifest>(fixture.Manifest);
 var id=Guid.NewGuid().ToString("N");var dataId=Guid.NewGuid().ToString("N");var records=new Dictionary<string,byte[]>();
 string Put<T>(string name,T value){var bytes=JsonSerializer.SerializeToUtf8Bytes(value);records[name]=bytes;return Convert.ToHexStringLower(SHA256.HashData(bytes));}
 string Raw(string name,string value){records[name]=Encoding.UTF8.GetBytes(value);return Convert.ToHexStringLower(SHA256.HashData(records[name]));}
 var key=Raw("datasets/"+dataId+"/master-key","synthetic-key");var policy=Raw("datasets/"+dataId+"/update-policy","preserved-policy");var pin=Raw("datasets/"+dataId+"/update-pin","preserved-pin");
 var intent=Put("owner-intent.json",new InstalledOwnerIntent(1,id,1,Guid.NewGuid().ToString("N"),manifest.PayloadDigest,dataId,"activate",owner.Sid,1,null));
 var web=Put("web.json",new InstalledWeb(1,id,"http://127.0.0.1:47123",47123));
 var anchor=Put("activation.json",new InstalledActivation(1,id,1,manifest.PayloadDigest,dataId,5,5,key,policy,pin,intent,web,"committed",null));
 Put("epoch.json",new InstalledEpoch(1,id,1,anchor));Put("bootstrap.json",new{installationId=id,activationReceiptDigest=anchor});
 Put("installation.json",new FirstInstallReceipt(2,id,dataId,manifest.PayloadDigest,"0.5.3",new string('a',64),47123,manifest.ZipSha256,"committed",new string('b',64)));
 var state=new InstallerState(1,id,dataId,manifest.ZipSha256,manifest.PayloadDigest,manifest.SourceCommit,owner.Sid,47123,"uninstalled",manifest.Files,"S-1-5-80-1-2-3-4-5",owner.ImageSha256);
 var before=records.ToDictionary(x=>x.Key,x=>Convert.ToHexStringLower(SHA256.HashData(x.Value)));
 void Verify(InstallerState value,OwnerProcess principal)=>PreservedInstallation.Require(value,manifest,principal,name=>records[name]);
 Verify(state,owner);Verify(state with{Phase="reinstalling"},owner);
 Assert(before.All(x=>x.Value==Convert.ToHexStringLower(SHA256.HashData(records[x.Key]))));
 foreach(var bad in new[]{state with{Phase="preparing"},state with{PackageDigest=new string('0',64)},state with{SourceCommit=new string('0',40)},state with{SetupSha256=new string('0',64)},state with{DataId=Guid.NewGuid().ToString("N")}})await Refuse(()=>{Verify(bad,owner);return Task.CompletedTask;});
 await Refuse(()=>{Verify(state,owner with{Sid="S-1-5-21-9-8-7-1001"});return Task.CompletedTask;});
 var old=records["epoch.json"];Put("epoch.json",new InstalledEpoch(1,id,2,anchor));await Refuse(()=>{Verify(state,owner);return Task.CompletedTask;});records["epoch.json"]=old;
 records["datasets/"+dataId+"/master-key"]=Encoding.UTF8.GetBytes("changed");await Refuse(()=>{Verify(state,owner);return Task.CompletedTask;});
 Assert(!records.ContainsKey("initial-seed.json")&&!records.ContainsKey("initial-retry.json"));
});
await Check("reinstall cached payload verification rejects changed missing and additional files",async()=>
{
 var root=Path.Combine(SafeDirectory.CreatePrivateSession(Path.GetTempPath()),"payload");Directory.CreateDirectory(root);var bytes=Encoding.UTF8.GetBytes("pinned-payload");var file=Path.Combine(root,"node.exe");File.WriteAllBytes(file,bytes);
 var records=new[]{new InstallMember("payload/node.exe",bytes.Length,Convert.ToHexStringLower(SHA256.HashData(bytes)))};
 PreservedInstallation.VerifyDirectory(root,records,"payload/");
 File.WriteAllText(Path.Combine(root,"unexpected"),"keep");await Refuse(()=>{PreservedInstallation.VerifyDirectory(root,records,"payload/");return Task.CompletedTask;});File.Delete(Path.Combine(root,"unexpected"));
 File.WriteAllText(file,"changed");await Refuse(()=>{PreservedInstallation.VerifyDirectory(root,records,"payload/");return Task.CompletedTask;});File.Delete(file);
 await Refuse(()=>{PreservedInstallation.VerifyDirectory(root,records,"payload/");return Task.CompletedTask;});
});
await Check("elevated progress frame completes while the calling UI context is synchronously blocked",async()=>
{var result=await ElevationFramingRegression.RunAsync();Assert(result.Ok,"ELEVATION_PROGRESS_CONTEXT_DEADLOCK");});
await Check("removal defaults to keeping data and full removal requires exact explicit confirmation",()=>
{
 var choice=new UninstallChoice();Assert(choice.Mode==UninstallMode.KeepData&&choice.CanBegin);
 choice.Select(UninstallMode.DeleteData);Assert(!choice.CanBegin);
 foreach(var text in new[]{"","מחק","DELETE","מחיקה "}){choice.Confirmation=text;Assert(!choice.CanBegin);}
 choice.Confirmation="מחיקה";Assert(choice.Confirmed()==UninstallMode.DeleteData);
 choice.Select(UninstallMode.KeepData);choice.Select(UninstallMode.DeleteData);Assert(!choice.CanBegin&&choice.Confirmation=="");return Task.CompletedTask;
});
await Check("authenticated flow carries full removal mode and refuses unknown or switched intent",async()=>
{
 var ops=new FakeHost();var result=await new InstallFlow(ops).RunAsync(owner,true,_=>{},default,UninstallMode.DeleteData);Assert(result.Ok&&!result.DataPreserved&&ops.LastRemoval==UninstallMode.DeleteData);
 var invalid=new FakeHost();await Refuse(()=>new InstallFlow(invalid).RunAsync(owner,true,_=>{},default,(UninstallMode)99));Assert(invalid.Calls.Count==0);
 await Refuse(()=>{UninstallChoice.RequireSameIntent("removing_keep_data",UninstallMode.DeleteData);return Task.CompletedTask;});
 await Refuse(()=>{UninstallChoice.RequireSameIntent("removing_all_data",UninstallMode.KeepData);return Task.CompletedTask;});UninstallChoice.RequireSameIntent("removing_all_data",UninstallMode.DeleteData);
 var serviceSid="S-1-5-80-123-456-789-123-456";
 WindowsFileBoundary.ValidateInstallerMetadataAcl(new System.Security.AccessControl.RawSecurityDescriptor(NativeInstallFiles.Descriptor(serviceSid)));
 await Refuse(()=>{WindowsFileBoundary.ValidateInstallerMetadataAcl(new System.Security.AccessControl.RawSecurityDescriptor(NativeInstallFiles.Descriptor(serviceSid,ownerSid:owner.Sid)));return Task.CompletedTask;});
 var s=new InstallerState(1,"install","data","package","payload","source","owner",47123,"committed",[new("host/file",5,"hash")],"service","setup");
 Assert(UninstallChoice.SameInstallation(s,s with{Phase="removing_all_data",Files=[..s.Files]}));
 foreach(var other in new[]{s with{Port=47124},s with{PayloadDigest="changed"},s with{SetupSha256="changed"},s with{Files=[new("host/file",6,"hash")]}})Assert(!UninstallChoice.SameInstallation(s,other));
 Assert(!InstallLocations.RemovalStateFile.StartsWith(InstallLocations.Binaries+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase));
});
await Check("full removal deletes only the inspected private synthetic tree and keeps external backup",()=>
{RemovalTests.Complete();return Task.CompletedTask;});
await Check("locked or hardlinked synthetic data fails before deletion",()=>
{RemovalTests.LockedAndHardlink();return Task.CompletedTask;});
await Check("interrupted synthetic purge retains outside intent and resumes exact remaining tree",()=>
{RemovalTests.Interrupted();return Task.CompletedTask;});
using var f=await ProductionFixture.Create(node,repo,helper);
await Check("real Store first startup creates schema5 only after one-use authorization",async()=>
{
 var original=Path.Combine(f.Data,"app.sqlite");File.Move(original,f.PathOf("original-seeded.sqlite"));f.ClearSignals();var head=NativeJson.Read<InstalledActivation>(File.ReadAllBytes(f.PathOf("activation.json")));var consumed=0;
 var fresh=new FreshDataAuthorization(f.InstallationId,f.OriginalDataId,head.ReleaseDigest,()=>{if(++consumed!=1)throw new IOException("seed replay");});
 var resolver=new InstalledActivationResolver(f.Root,new PrivateControlFiles(),f.Control,()=>f.Now,connectorProfile:RuntimeConnectorProfile.Synthetic,fresh:fresh);
 var controller=new ServiceController(new ResolverStart(resolver,()=>f.Binding));var result=await controller.RunAsync(r=>{if(r.State==ServiceState.Running)controller.Control(ServiceDefinition.Stop);});
 Assert(result.Failure==ServiceFailure.None&&consumed==1&&File.Exists(original));var schema=await FrozenAdmissionSchema.ReadAsync(node,original,f.PathOf("temporary"),default);Assert(schema==5);
 File.Move(original,f.PathOf("new-seeded.sqlite"));f.ClearSignals();await Refuse(()=>f.Resolve());File.Move(f.PathOf("new-seeded.sqlite"),original);
});
await Check("existing verified database retires a leftover initial grant without reinitializing",async()=>
{
 f.ClearSignals();var head=NativeJson.Read<InstalledActivation>(File.ReadAllBytes(f.PathOf("activation.json")));var count=0;
 var resolver=new InstalledActivationResolver(f.Root,new PrivateControlFiles(),f.Control,()=>f.Now,connectorProfile:RuntimeConnectorProfile.Synthetic,fresh:new(f.InstallationId,f.OriginalDataId,head.ReleaseDigest,()=>count++));
 var controller=new ServiceController(new ResolverStart(resolver,()=>f.Binding));var result=await controller.RunAsync(r=>{if(r.State==ServiceState.Running)controller.Control(ServiceDefinition.Stop);});Assert(result.Failure==ServiceFailure.None&&count==1);
});
await Check("missing database plus unrelated data refuses fresh initialization and keeps files",async()=>
{
 f.ClearSignals();var db=Path.Combine(f.Data,"app.sqlite");File.Move(db,f.PathOf("held-db.sqlite"));var sentinel=Path.Combine(f.Data,"family-marker");File.WriteAllText(sentinel,"preserve");var count=0;var head=NativeJson.Read<InstalledActivation>(File.ReadAllBytes(f.PathOf("activation.json")));
 try{var resolver=new InstalledActivationResolver(f.Root,new PrivateControlFiles(),f.Control,()=>f.Now,connectorProfile:RuntimeConnectorProfile.Synthetic,fresh:new(f.InstallationId,f.OriginalDataId,head.ReleaseDigest,()=>count++));await Refuse(()=>resolver.ResolveAsync(f.Binding,default));Assert(count==0&&File.ReadAllText(sentinel)=="preserve"&&!File.Exists(db));}
 finally{File.Delete(sentinel);File.Move(f.PathOf("held-db.sqlite"),db);}
});
// Exercise the same two-file authorization used by the production resolver.
FirstInstallReceipt CreateInitialGrants()
{
 var head=NativeJson.Read<InstalledActivation>(File.ReadAllBytes(f.PathOf("activation.json")));
 byte[] Grant()=>JsonSerializer.SerializeToUtf8Bytes(new FirstInstallSeed(1,f.InstallationId,f.OriginalDataId,head.ReleaseDigest,Guid.NewGuid().ToString("N")));
 var seed=Grant();var retry=Grant();File.WriteAllBytes(f.PathOf("initial-seed.json"),seed);File.WriteAllBytes(f.PathOf("initial-retry.json"),retry);
 return new(2,f.InstallationId,f.OriginalDataId,head.ReleaseDigest,"0.5.3",ProductionFixture.Hash(seed),32000,new string('b',64),"committed",ProductionFixture.Hash(retry));
}
FreshDataAuthorization? ReadInitialGrants(FirstInstallReceipt receipt)
{
 byte[] Read(string path){using var file=WindowsFileBoundary.OpenPrivateRead(path);return file.Small(4096);}
 void Consume(string path,string digest){if(ProductionFixture.Hash(Read(path))!=digest)throw new IOException("PRIVATE_GRANT_CHANGED");File.Delete(path);}
 return FirstInstallInitialization.Read(f.Root,receipt,Read,Consume);
}
foreach(var failure in new[]{"handoff","resume","early-crash"})await Check("initial "+failure+" retains durable retry grant and a quiescent retry succeeds",async()=>
{
 f.ClearSignals();var db=Path.Combine(f.Data,"app.sqlite");var saved=f.PathOf("before-"+failure+".sqlite");File.Move(db,saved);var receipt=CreateInitialGrants();
 var fresh=ReadInitialGrants(receipt)!;JobProcessFaultInjection? fault=null;
 if(failure=="handoff"){var original=fresh.Begin;fresh=fresh with{Begin=()=>{original?.Invoke();throw new IOException("INJECTED_BEFORE_DESCRIPTOR");}};}
 if(failure=="resume")fault=new(){FailResumeThread=true};
 if(failure=="early-crash")File.WriteAllText(f.PathOf("crash-before-store"),"synthetic");
 var resolver=new InstalledActivationResolver(f.Root,new PrivateControlFiles(),f.Control,()=>f.Now,connectorProfile:RuntimeConnectorProfile.Synthetic,fresh:fresh,startFault:fault);
 var reports=new List<ServiceReport>();var controller=new ServiceController(new ResolverStart(resolver,()=>f.Binding),lifecycleDeadline:TimeSpan.FromSeconds(8));
 var failed=await controller.RunAsync(reports.Add);Assert(failed.Failure!=ServiceFailure.None&&failed.QuiescenceConfirmed&&!reports.Any(x=>x.State==ServiceState.Running));
 Assert(!File.Exists(db)&&!File.Exists(f.PathOf("initial-seed.json"))&&File.Exists(f.PathOf("initial-retry.json")));
 using(var observed=WindowsFileBoundary.AcquireDataLease(f.Data)){} // actual empty-job lease reacquisition, not a UI boolean
 if(failure=="early-crash")File.Delete(f.PathOf("crash-before-store"));
 f.ClearSignals();var retry=ReadInitialGrants(receipt);Assert(retry is not null);
 var next=new ServiceController(new ResolverStart(new InstalledActivationResolver(f.Root,new PrivateControlFiles(),f.Control,()=>f.Now,connectorProfile:RuntimeConnectorProfile.Synthetic,fresh:retry),()=>f.Binding));
 var success=await next.RunAsync(report=>{if(report.State==ServiceState.Running)next.Control(ServiceDefinition.Stop);});
 Assert(success.Failure==ServiceFailure.None&&File.Exists(db)&&!File.Exists(f.PathOf("initial-retry.json")));
 // Once observed ready, deleting even every DB file cannot re-open first setup.
 f.ClearSignals();var initialized=f.PathOf("after-"+failure+".sqlite");File.Move(db,initialized);Assert(ReadInitialGrants(receipt) is null);await Refuse(()=>f.Resolve());Assert(!File.Exists(db));File.Move(initialized,db);
});
await Check("uncertain first-start cleanup retains both retry evidence and exclusive lease until observed reconciliation",async()=>
{
 f.ClearSignals();var db=Path.Combine(f.Data,"app.sqlite");File.Move(db,f.PathOf("before-uncertain.sqlite"));var receipt=CreateInitialGrants();
 var fault=new JobProcessFaultInjection{FailAfterHandoff=true,TreeObservationUnavailable=true};
 var resolver=new InstalledActivationResolver(f.Root,new PrivateControlFiles(),f.Control,()=>f.Now,connectorProfile:RuntimeConnectorProfile.Synthetic,fresh:ReadInitialGrants(receipt),startFault:fault);
 var reports=new List<ServiceReport>();var controller=new ServiceController(new ResolverStart(resolver,()=>f.Binding),lifecycleDeadline:TimeSpan.FromSeconds(2));
 var result=await controller.RunAsync(reports.Add);Assert(!result.QuiescenceConfirmed&&controller.CleanupReconciliationRequired&&!reports.Any(x=>x.State==ServiceState.Stopped)&&File.Exists(f.PathOf("initial-retry.json"))&&!File.Exists(db));
 try{using var wrong=WindowsFileBoundary.AcquireDataLease(f.Data);throw new Exception("early lease release");}catch(Win32Exception ex)when(ex.NativeErrorCode==32){}
 fault.TreeObservationUnavailable=false;await controller.RetryAdmissionCleanupAsync(default);Assert(!controller.CleanupReconciliationRequired);
 using(var observed=WindowsFileBoundary.AcquireDataLease(f.Data)){}
 var retry=new ServiceController(new ResolverStart(new InstalledActivationResolver(f.Root,new PrivateControlFiles(),f.Control,()=>f.Now,connectorProfile:RuntimeConnectorProfile.Synthetic,fresh:ReadInitialGrants(receipt)),()=>f.Binding));
 var recovered=await retry.RunAsync(report=>{if(report.State==ServiceState.Running)retry.Control(ServiceDefinition.Stop);});Assert(recovered.Failure==ServiceFailure.None&&!File.Exists(f.PathOf("initial-retry.json")));
});
await Check("verified DB from an interrupted attempt retires grants before retry and never resets data",async()=>
{
 f.ClearSignals();var receipt=CreateInitialGrants();var schema=await FrozenAdmissionSchema.ReadAsync(node,Path.Combine(f.Data,"app.sqlite"),f.PathOf("temporary"),default);Assert(schema==5);
 var before=File.ReadAllBytes(Path.Combine(f.Data,"app.sqlite"));var grant=await new InstalledActivationResolver(f.Root,new PrivateControlFiles(),f.Control,()=>f.Now,connectorProfile:RuntimeConnectorProfile.Synthetic,fresh:ReadInitialGrants(receipt)).ResolveAsync(f.Binding,default);
 try{await grant.Recovery.RequireStartAllowedAsync(grant.Release,default);Assert(File.Exists(f.PathOf("initial-retry.json")));}finally{grant.Dispose();}
 var controller=new ServiceController(new ResolverStart(new InstalledActivationResolver(f.Root,new PrivateControlFiles(),f.Control,()=>f.Now,connectorProfile:RuntimeConnectorProfile.Synthetic,fresh:ReadInitialGrants(receipt)),()=>f.Binding));
 var result=await controller.RunAsync(report=>{if(report.State==ServiceState.Running)controller.Control(ServiceDefinition.Stop);});Assert(result.Failure==ServiceFailure.None&&!File.Exists(f.PathOf("initial-seed.json"))&&!File.Exists(f.PathOf("initial-retry.json")));
});
await Check("inconsistent or corrupt initial grant state fails closed and creates no replacement",async()=>
{
 f.ClearSignals();var receipt=CreateInitialGrants();File.Delete(f.PathOf("initial-retry.json"));await Refuse(()=>{ReadInitialGrants(receipt);return Task.CompletedTask;});Assert(!File.Exists(f.PathOf("initial-retry.json")));File.Delete(f.PathOf("initial-seed.json"));
 receipt=CreateInitialGrants();File.WriteAllText(f.PathOf("initial-retry.json"),"corrupt");await Refuse(()=>{ReadInitialGrants(receipt);return Task.CompletedTask;});File.Delete(f.PathOf("initial-seed.json"));File.Delete(f.PathOf("initial-retry.json"));
});
await Check("SQLite handles a crash-left WAL under actual exclusive lease without deleting live data",async()=>
{
 f.ClearSignals();var signal=f.PathOf("wal-ready");var code="const fs=require('node:fs'),{DatabaseSync}=require('node:sqlite');const db=new DatabaseSync(process.argv[1]);db.exec('PRAGMA journal_mode=WAL;CREATE TABLE IF NOT EXISTS first_install_wal(value INTEGER);INSERT INTO first_install_wal VALUES(73)');fs.writeFileSync(process.argv[2],'ready');setInterval(()=>{},1000)";
 using(var writer=JobProcess.Start(node,["-e",code,Path.Combine(f.Data,"app.sqlite"),signal],f.Root,LaunchEnvironment.Clean(f.Root)))
 {using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(!File.Exists(signal))await Task.Delay(20,limit.Token);await writer.TerminateAndWaitForTreeEmptyAsync(limit.Token);}
 Assert(File.Exists(Path.Combine(f.Data,"app.sqlite-wal")));using var lease=WindowsFileBoundary.AcquireDataLease(f.Data);var schema=await FirstInstallWal.ReconcileAsync(node,Path.Combine(f.Data,"app.sqlite"),f.PathOf("temporary"),default);Assert(schema==5&&!File.Exists(Path.Combine(f.Data,"app.sqlite-wal")));
});
await Check("native RTL window renders at desktop and minimum size without showing or installing",async()=>
{
 var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var thread=new Thread(()=>
 {try{var app=new App();app.InitializeComponent();foreach(var size in new[]{new Size(980,730),new Size(760,620)})
  {var window=new MainWindow();var root=(FrameworkElement)window.Content;root.Width=size.Width;root.Height=size.Height;root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout();var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,"setup-"+size.Width+".png"));png.Save(file);Assert(root.FlowDirection==FlowDirection.RightToLeft);}
  foreach(var size in new[]{new Size(980,730),new Size(760,620)})foreach(var full in new[]{false,true})
  {
   var window=new MainWindow(true);var keep=(System.Windows.Controls.RadioButton)window.FindName("KeepOption");var remove=(System.Windows.Controls.RadioButton)window.FindName("DeleteOption");var input=(System.Windows.Controls.TextBox)window.FindName("ConfirmationInput");var primary=(System.Windows.Controls.Button)window.FindName("Primary");
   Assert(keep.IsChecked==true&&primary.IsEnabled);if(full){remove.IsChecked=true;Assert(!primary.IsEnabled);input.Text="מחיקה";Assert(primary.IsEnabled);input.Text="";Assert(!primary.IsEnabled);}
   var root=(FrameworkElement)window.Content;root.Width=size.Width;root.Height=size.Height;root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout();var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,"uninstall-"+(full?"delete":"keep")+"-"+size.Width+".png"));png.Save(file);Assert(root.FlowDirection==FlowDirection.RightToLeft);
   if(full)
   {
    input.Text=UninstallChoice.ConfirmationPhrase;var scroll=(System.Windows.Controls.ScrollViewer)window.FindName("ContentScroll");scroll.ScrollToEnd();root.UpdateLayout();
    Assert(primary.IsEnabled&&primary.TabIndex>input.TabIndex);var position=input.TransformToAncestor(scroll).Transform(new Point(0,0));Assert(position.Y>=0&&position.Y+input.ActualHeight<=scroll.ActualHeight);
    var confirmed=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);confirmed.Render(root);var ready=new PngBitmapEncoder();ready.Frames.Add(BitmapFrame.Create(confirmed));using var final=File.Create(Path.Combine(output,"uninstall-confirmed-"+size.Width+".png"));ready.Save(final);
    keep.IsChecked=true;remove.IsChecked=true;Assert(input.Text==""&&!primary.IsEnabled);
   }
  }
  completed.SetResult();}catch(Exception e){completed.SetException(e);}});thread.SetApartmentState(ApartmentState.STA);thread.Start();await completed.Task;thread.Join();
});
File.WriteAllText(Path.Combine(output,"tests.json"),JsonSerializer.Serialize(new{kind="first-install-synthetic-host-operations",passed=cases.Count-failures,failed=failures,nativeScmInvoked=false,hostAclProvisioned=false,setupCapabilityProvisioned=false,networkListenersOpened=false,offscreenUiOnly=true,tests=cases},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{cases.Count-failures} passed; {failures} failed. No native host installation, service, trust or listener.");return failures==0?0:1;

(byte[] Zip,byte[] Manifest) Package(bool duplicate=false)
{
 var names=new[]{"host/Yahad.ServiceHost.exe","host/Yahad.ServiceHost.runtimeconfig.json","payload/bundle.json","payload/runtime/node/node.exe","payload/runtime/ffmpeg/ffmpeg.exe","payload/runtime/caddy/caddy.exe","payload/app/src/main.ts","payload/app/dist/index.html","payload/app/native/Yahad.Platform.exe","payload/legal/build-inputs.json"};
 var records=new List<InstallMember>();using var data=new MemoryStream();string digest="";
 using(var zip=new ZipArchive(data,ZipArchiveMode.Create,true))foreach(var name in names)
 {var bytes=Encoding.UTF8.GetBytes(name=="payload/bundle.json"?"{\"version\":\"0.5.3\"}":name=="payload/app/src/main.ts"?"synthetic-app-not-executed":"synthetic");if(name=="payload/bundle.json")digest=Convert.ToHexStringLower(SHA256.HashData(bytes));var entry=zip.CreateEntry(name);using(var stream=entry.Open())stream.Write(bytes);records.Add(new(name,bytes.Length,Convert.ToHexStringLower(SHA256.HashData(bytes))));if(duplicate&&name==names[0]){using var stream=zip.CreateEntry(name).Open();stream.Write(bytes);}}
 var archive=data.ToArray();return(archive,JsonSerializer.SerializeToUtf8Bytes(new InstallPackageManifest(1,"0.5.3",new string('a',40),Convert.ToHexStringLower(SHA256.HashData(archive)),digest,records.ToArray())));
}
internal sealed class FakeHost:ISetupHostOperations
{
 internal UninstallMode? LastRemoval;internal readonly List<string> Calls=[];internal string? Failure;internal Action? AfterPrepare;internal SetupResult Result=new(true,"READY","http://127.0.0.1:47123/",new string('a',43));
 private Task Call(string name,CancellationToken c){c.ThrowIfCancellationRequested();Calls.Add(name);if(Failure==name)throw new IOException("injected");return Task.CompletedTask;}
 public async Task PrepareAsync(OwnerProcess o,Action<SetupProgress> p,CancellationToken c){await Call("prepare",c);AfterPrepare?.Invoke();}
 public Task PublishServiceAsync(OwnerProcess o,Action<SetupProgress> p,CancellationToken c)=>Call("publish",c);
 public async Task<SetupResult> StartAndObserveAsync(Action<SetupProgress> p,CancellationToken c){await Call("ready",c);return Result;}
 public async Task<SetupResult> UninstallAsync(OwnerProcess o,Action<SetupProgress> p,CancellationToken c,UninstallMode mode=UninstallMode.KeepData){LastRemoval=mode;await Call("uninstall",c);return new(true,"REMOVED",DataPreserved:mode==UninstallMode.KeepData);}
}
internal sealed class ResolverStart(InstalledActivationResolver resolver,Func<InstallationBinding> binding):IServiceStartAuthorizer,IObservedAdmission
{
 public TimeSpan AdmissionBudget=>resolver.AdmissionBudget;public event Action? ObservedWork{add=>resolver.ObservedWork+=value;remove=>resolver.ObservedWork-=value;}
 public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)=>resolver.ResolveAsync(binding(),cancellation);
}

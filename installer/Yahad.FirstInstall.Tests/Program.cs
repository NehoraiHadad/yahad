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
using var f=await ProductionFixture.Create(node,repo,helper);
await Check("real Store first startup creates schema5 only after one-use authorization",async()=>
{
 var original=Path.Combine(f.Data,"app.sqlite");File.Move(original,f.PathOf("original-seeded.sqlite"));f.ClearSignals();var head=NativeJson.Read<InstalledActivation>(File.ReadAllBytes(f.PathOf("activation.json")));var consumed=0;
 var fresh=new FreshDataAuthorization(f.InstallationId,f.OriginalDataId,head.ReleaseDigest,()=>{if(++consumed!=1)throw new IOException("seed replay");});
 var resolver=new InstalledActivationResolver(f.Root,new PrivateControlFiles(),f.Control,()=>f.Now,fresh:fresh);
 var controller=new ServiceController(new ResolverStart(resolver,()=>f.Binding));var result=await controller.RunAsync(r=>{if(r.State==ServiceState.Running)controller.Control(ServiceDefinition.Stop);});
 Assert(result.Failure==ServiceFailure.None&&consumed==1&&File.Exists(original));var schema=await FrozenAdmissionSchema.ReadAsync(node,original,f.PathOf("temporary"),default);Assert(schema==5);
 File.Move(original,f.PathOf("new-seeded.sqlite"));f.ClearSignals();await Refuse(()=>f.Resolve());File.Move(f.PathOf("new-seeded.sqlite"),original);
});
await Check("ordinary restart never consumes a fresh-data capability",async()=>
{
 f.ClearSignals();var head=NativeJson.Read<InstalledActivation>(File.ReadAllBytes(f.PathOf("activation.json")));var count=0;
 var resolver=new InstalledActivationResolver(f.Root,new PrivateControlFiles(),f.Control,()=>f.Now,fresh:new(f.InstallationId,f.OriginalDataId,head.ReleaseDigest,()=>count++));
 var controller=new ServiceController(new ResolverStart(resolver,()=>f.Binding));var result=await controller.RunAsync(r=>{if(r.State==ServiceState.Running)controller.Control(ServiceDefinition.Stop);});Assert(result.Failure==ServiceFailure.None&&count==0);
});
await Check("missing database plus unrelated data refuses fresh initialization and keeps files",async()=>
{
 f.ClearSignals();var db=Path.Combine(f.Data,"app.sqlite");File.Move(db,f.PathOf("held-db.sqlite"));var sentinel=Path.Combine(f.Data,"family-marker");File.WriteAllText(sentinel,"preserve");var count=0;var head=NativeJson.Read<InstalledActivation>(File.ReadAllBytes(f.PathOf("activation.json")));
 try{var resolver=new InstalledActivationResolver(f.Root,new PrivateControlFiles(),f.Control,()=>f.Now,fresh:new(f.InstallationId,f.OriginalDataId,head.ReleaseDigest,()=>count++));await Refuse(()=>resolver.ResolveAsync(f.Binding,default));Assert(count==0&&File.ReadAllText(sentinel)=="preserve"&&!File.Exists(db));}
 finally{File.Delete(sentinel);File.Move(f.PathOf("held-db.sqlite"),db);}
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
 internal readonly List<string> Calls=[];internal string? Failure;internal Action? AfterPrepare;internal SetupResult Result=new(true,"READY","http://127.0.0.1:47123/",new string('a',43));
 private Task Call(string name,CancellationToken c){c.ThrowIfCancellationRequested();Calls.Add(name);if(Failure==name)throw new IOException("injected");return Task.CompletedTask;}
 public async Task PrepareAsync(OwnerProcess o,Action<SetupProgress> p,CancellationToken c){await Call("prepare",c);AfterPrepare?.Invoke();}
 public Task PublishServiceAsync(OwnerProcess o,Action<SetupProgress> p,CancellationToken c)=>Call("publish",c);
 public async Task<SetupResult> StartAndObserveAsync(Action<SetupProgress> p,CancellationToken c){await Call("ready",c);return Result;}
 public async Task<SetupResult> UninstallAsync(OwnerProcess o,Action<SetupProgress> p,CancellationToken c){await Call("uninstall",c);return new(true,"REMOVED",DataPreserved:true);}
}
internal sealed class ResolverStart(InstalledActivationResolver resolver,Func<InstallationBinding> binding):IServiceStartAuthorizer,IObservedAdmission
{
 public TimeSpan AdmissionBudget=>resolver.AdmissionBudget;public event Action? ObservedWork{add=>resolver.ObservedWork+=value;remove=>resolver.ObservedWork-=value;}
 public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)=>resolver.ResolveAsync(binding(),cancellation);
}

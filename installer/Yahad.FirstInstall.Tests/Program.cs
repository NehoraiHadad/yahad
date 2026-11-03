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

await using var storeCompilerLifetime=StoreFixtureModules.OwnInvocation();

if(args is ["--packaged-native-start-only",var preparedRuntime,var startupReport])
    return await PackagedNativeStartupProbe.Run(preparedRuntime,startupReport);
if(args is ["--startup-budget-only"])
    return await PackagedNativeStartupProbe.CheckBudget();

if(args is ["--fixture-native-launch-race",var fixtureNode])
{
    await FixtureProcessesTests.NativeLaunchRace(fixtureNode);
    Console.WriteLine(JsonSerializer.Serialize(new{suite="fixture-owned-native-launch-race",passed=1,failed=0,realServices=false}));return 0;
}

if(args is ["--fixture-process-owner-only"])
{
    var passed=0;var failed=0;
    async Task Observe(string name,Func<Task> run){try{await run();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+" "+error);}}
    await FixtureProcessesTests.RunAsync(Observe);await Observe("shared compiler root survives both users then disappears",StoreFixtureModules.TestSharedLifetime);
    Console.WriteLine(JsonSerializer.Serialize(new{suite="fixture-process-and-cache-lifetime",passed,failed,realServices=false}));return failed==0?0:1;
}

if(args is ["--fixture-teardown-only"])
{
    var passed=0;var failed=0;await OwnedFixtureDirectoryTests.RunAsync(async(name,run)=>
    {try{await run();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+" "+error);}});
    await FixtureProcessesTests.RunAsync(async(name,run)=>
    {try{await run();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+" "+error);}});
    Console.WriteLine(JsonSerializer.Serialize(new{suite="fixture-teardown-owned-roots",passed,failed,realServices=false,globalSweep=false}));return failed==0?0:1;
}

if(args is ["--network-profile-readonly",var selectedProfileAdapter])
{
    try
    {
        var category=NativeNetworkProfile.ReadCategory(selectedProfileAdapter);
        NativeNetworkProfile.RequirePrivateCategory(category);
        Console.WriteLine(JsonSerializer.Serialize(new{suite="selected-network-profile-readonly",ok=true,category,networkChanged=false,servicesChanged=false}));return 0;
    }
    catch(Exception failure)
    {Console.WriteLine(JsonSerializer.Serialize(new{suite="selected-network-profile-readonly",ok=false,reason=failure.GetType().Name,hresult=failure.HResult,networkChanged=false,servicesChanged=false}));return 1;}
}

if(args is ["--store-import-trace-only",var traceNode,var traceRepo,var traceOutput])
    return await ImportTraceProbe.RunAsync(traceNode,traceRepo,traceOutput);
if(args is ["--compiler-trace-only",var compilerNode,var compilerRepo,var compilerOutput])
    return await ImportTraceProbe.RunAsync(compilerNode,compilerRepo,compilerOutput,true);
if(args is ["--existing-link-guards-only",var hardFixture,var junctionFixture,var targetFixture,var linkOutput])
    return ExistingLinkGuardsProbe.Run(hardFixture,junctionFixture,targetFixture,linkOutput);

if(args is ["--uninstall-recovery-only",var recoveryOutput])
{
    Directory.CreateDirectory(recoveryOutput);var failed=0;var observations=new List<object>();
    async Task Observe(string name,Func<Task> run)
    {
        try{await run();observations.Add(new{name,status="PASS"});Console.WriteLine("PASS "+name);}
        catch(Exception error){failed++;observations.Add(new{name,status="FAIL",reason=error.GetType().Name});Console.WriteLine("FAIL "+name+" "+error);}
    }
    await FailureDiagnosticTests.RunAsync(Observe);
    await Observe("owned synthetic removal keeps external backup",()=>{RemovalTests.Complete();return Task.CompletedTask;});
    await Observe("locked and hardlinked synthetic removal refuses",()=>{RemovalTests.LockedAndHardlink();return Task.CompletedTask;});
    await Observe("interrupted synthetic purge retains same-choice intent",()=>{RemovalTests.Interrupted();return Task.CompletedTask;});
    var report=JsonSerializer.Serialize(new{suite="uninstall-recovery-synthetic",passed=observations.Count-failed,failed,realScmInvoked=false,liveReceiptModified=false,uacRequested=false,cases=observations},new JsonSerializerOptions{WriteIndented=true});
    File.WriteAllText(Path.Combine(recoveryOutput,"tests.json"),report);Console.WriteLine(report);return failed==0?0:1;
}
if(args is ["--failure-diagnostics-only"])
{
    var failed=0;
    await FailureDiagnosticTests.RunAsync(async(name,run)=>
    {
        try{await run();Console.WriteLine("PASS "+name);}
        catch(Exception failure){failed++;Console.WriteLine("FAIL "+name+" "+failure);}
    });
    return failed==0?0:1;
}
if(args is ["--full-installer-atomic-only"])
{
    FullInstallerAtomicTests.Run();Console.WriteLine(JsonSerializer.Serialize(new{suite="full-installer-atomic-private-files",passed=4,failed=0,nativeScmOperations=false,realFamilyData=false}));return 0;
}
if(args is ["--full-installer-ui-only",var fullUiOutput])
{
    await FullInstallerUiTests.Run(fullUiOutput);Console.WriteLine(JsonSerializer.Serialize(new{suite="full-installer-ui-synthetic",passed=2,failed=0,nativeOperations=false}));return 0;
}
if(args is ["--startup-failure-only"])
{
    var passed=0;var failed=0;await ServiceStartupFailureTests.RunAsync(async(name,run)=>
    {try{await run();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+" "+error);}});
    Console.WriteLine(JsonSerializer.Serialize(new{suite="service-startup-failure-pure",passed,failed,nativeOperations=false,eventLogWrites=false}));return failed==0?0:1;
}
if(args is ["--full-installer-only"])
{
    var passed=0;var failed=0;await FullInstallerIntegrationTests.Run(async(name,run)=>
    {try{await run();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+" "+error);}});
    await ServiceStartupFailureTests.RunAsync(async(name,run)=>
    {try{await run();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+" "+error);}});
    await UpgradeTests.RunAsync(async(name,run)=>
    {try{await run();passed++;Console.WriteLine("PASS "+name);}catch(Exception error){failed++;Console.WriteLine("FAIL "+name+" "+error);}});
    Console.WriteLine(JsonSerializer.Serialize(new{suite="full-installer-integration-pure",passed,failed,nativeOperations=false}));return failed==0?0:1;
}
if(args is ["--version-only",var versionRepo])
{
    var observations=new List<object>();var failed=0;
    await VersionIdentityTests.RunAsync(async(name,run)=>
    {
        try{await run();observations.Add(new{name,status="PASS"});Console.WriteLine("PASS "+name);}
        catch(Exception failure){failed++;observations.Add(new{name,status="FAIL",reason=failure.GetType().Name});Console.WriteLine("FAIL "+name+" "+failure);}
    },Package,versionRepo);
    Console.WriteLine(JsonSerializer.Serialize(new{suite="application-version-pure",ok=failed==0,version=ApplicationRelease.Version,failed,nativeOperations=false,cases=observations}));return failed==0?0:1;
}
if(args is ["--store-schema-only",var schemaRepo])
{
    foreach(var expected in new[]{5,6,17})
        if(StoreFixture.ParseSourceSchema($"// fixture\nexport const DATABASE_SCHEMA = {expected};\n")!=expected)throw new IOException("STORE_SCHEMA_PARSER");
    foreach(var invalid in new[]{"export const DATABASE_SCHEMA = 0;","export const DATABASE_SCHEMA = 6 + 1;","export const DATABASE_SCHEMA = 6;\nexport const DATABASE_SCHEMA = 7;","// export const DATABASE_SCHEMA = 6;","export const DATABASE_SCHEMA = 999999999999999999999;"})
    {
        var refused=false;try{StoreFixture.ParseSourceSchema(invalid);}catch(IOException){refused=true;}
        if(!refused)throw new IOException("STORE_SCHEMA_PARSER_ADMITTED_INVALID_SOURCE");
    }
    var observed=StoreFixture.ReadSourceSchema(schemaRepo);
    Console.WriteLine(JsonSerializer.Serialize(new{suite="store-schema-source-pure",ok=true,schema=observed,declaredSchemasTested=new[]{5,6,17},nativeOperations=false,storeOpened=false}));return 0;
}
if(args is ["--lan-integration-only"])
{
    var observations=new List<object>();var failed=0;
    await LanIntegrationTests.RunAsync(async(name,run)=>
    {
        try{await run();observations.Add(new{name,status="PASS"});Console.WriteLine("PASS "+name);}
        catch(Exception failure){failed++;observations.Add(new{name,status="FAIL",reason=failure.GetType().Name});Console.WriteLine("FAIL "+name+" "+failure);}
    });
    Console.WriteLine(JsonSerializer.Serialize(new{suite="lan-integration-pure",ok=failed==0,failed,nativeOperations=false,cases=observations}));return failed==0?0:1;
}
if(args is ["--lan-runtime-only"])
{
    var observations=new List<object>();var failed=0;
    await LanRuntimeTests.RunAsync(async(name,run)=>
    {
        try{await run();observations.Add(new{name,status="PASS"});Console.WriteLine("PASS "+name);}
        catch(Exception failure){failed++;observations.Add(new{name,status="FAIL",reason=failure.GetType().Name});Console.WriteLine("FAIL "+name+" "+failure);}
    });
    Console.WriteLine(JsonSerializer.Serialize(new{suite="lan-runtime-pure",ok=failed==0,failed,nativeOperations=false,cases=observations}));return failed==0?0:1;
}
var storeOnly=args.Length==5&&args[0]=="--store-integration-only";
if(storeOnly)args=args.Skip(1).ToArray();
if(args is not [var node,var repo,var helper,var output])throw new IOException("TEST_INPUTS_REQUIRED");
Directory.CreateDirectory(output);var cases=new List<object>();int failures=0;int blockedStore=0;bool preparationBlocked=false;
void Assert(bool condition,string code="assertion failed"){if(!condition)throw new Exception(code);}
async Task Check(string name,Func<Task> run){try{await run();cases.Add(new{name,status="PASS"});Console.WriteLine("PASS "+name);}catch(Exception e){failures++;cases.Add(new{name,status="FAIL",reason=e.GetType().Name});Console.WriteLine("FAIL "+name+" "+e);}}
async Task Refuse(Func<Task> run){try{await run();}catch(Exception e)when(e is IOException or UnauthorizedAccessException or OperationCanceledException or Win32Exception or JsonException){return;}throw new Exception("unexpected admission");}
if(!storeOnly)
{
await FailureDiagnosticTests.RunAsync(Check);
var owner=new SetupOwner("S-1-5-21-1-2-3-1001",new string('a',64));
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
 Assert(File.ReadAllText(Path.Combine(root,"payload/app/server/main.mjs"))=="synthetic-app-not-executed");return Task.CompletedTask;
});
await Check("corrupt package digest and duplicate zip entry refuse before extraction",async()=>
{
 var fixture=Package();var manifest=NativeJson.Read<InstallPackageManifest>(fixture.Manifest);await Refuse(()=>{using var p=new InstallPackage(new MemoryStream(fixture.Zip),JsonSerializer.SerializeToUtf8Bytes(manifest with{ZipSha256=new string('0',64)}));return Task.CompletedTask;});
 fixture=Package(duplicate:true);await Refuse(()=>{using var p=new InstallPackage(new MemoryStream(fixture.Zip),fixture.Manifest);return Task.CompletedTask;});
});
await Check("wrong member hash fails without overwriting preexisting files",async()=>
{
 var fixture=Package();var document=NativeJson.Read<InstallPackageManifest>(fixture.Manifest);var records=document.Files.Select((x,i)=>i==0?x with{Sha256=new string('0',64)}:x).ToArray();using var p=new InstallPackage(new MemoryStream(fixture.Zip),JsonSerializer.SerializeToUtf8Bytes(document with{Files=records}));
 var root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());await Refuse(()=>{p.Extract(root,_=>{},default);return Task.CompletedTask;});Assert(!File.Exists(Path.Combine(root,"payload/app/server/main.mjs")));
});
await Check("user installer enables existing real connector; only explicit test profile uses mock",()=>
{
 var production=RuntimeConnectorSettings.For(RuntimeConnectorProfile.Production);Assert(production.Connector=="baileys"&&production.Approval=="I_APPROVE_UNOFFICIAL_LINK");
 var synthetic=RuntimeConnectorSettings.For(RuntimeConnectorProfile.Synthetic);Assert(synthetic.Connector=="mock"&&synthetic.Approval=="no");
 try{RuntimeConnectorSettings.For((RuntimeConnectorProfile)99);throw new Exception("unknown profile accepted");}catch(IOException){}
 return Task.CompletedTask;
});
await Check("removal defaults to keeping data and full removal requires exact explicit confirmation",()=>
{
 var choice=new UninstallChoice();Assert(choice.Mode==UninstallMode.KeepData&&choice.CanBegin);
 choice.Select(UninstallMode.DeleteData);Assert(!choice.CanBegin);
 foreach(var text in new[]{"","מחק","DELETE","מחיקה "}){choice.Confirmation=text;Assert(!choice.CanBegin);}
 choice.Confirmation="מחיקה";Assert(choice.Confirmed()==UninstallMode.DeleteData);
 choice.Select(UninstallMode.KeepData);choice.Select(UninstallMode.DeleteData);Assert(!choice.CanBegin&&choice.Confirmation=="");return Task.CompletedTask;
});
await Check("authenticated flow carries full removal mode and refuses unknown intent",async()=>
{
 var ops=new FakeHost();var result=await new InstallFlow(ops).RunAsync(owner,true,_=>{},default,UninstallMode.DeleteData);Assert(result.Ok&&!result.DataPreserved&&ops.LastRemoval==UninstallMode.DeleteData);
 var invalid=new FakeHost();await Refuse(()=>new InstallFlow(invalid).RunAsync(owner,true,_=>{},default,(UninstallMode)99));Assert(invalid.Calls.Count==0);
 var serviceSid="S-1-5-80-123-456-789-123-456";
 WindowsFileBoundary.ValidateInstallerMetadataAcl(new System.Security.AccessControl.RawSecurityDescriptor(NativeInstallFiles.Descriptor(serviceSid)));
 await Refuse(()=>{WindowsFileBoundary.ValidateInstallerMetadataAcl(new System.Security.AccessControl.RawSecurityDescriptor(NativeInstallFiles.Descriptor(serviceSid,ownerSid:owner.Sid)));return Task.CompletedTask;});
 var s=FullInstallerIntegrationTests.State();
 Assert(UninstallChoice.SameInstallation(s,s with{Phase="removing_all_data",Files=[..s.Files]}));
 foreach(var other in new[]{s with{Port=47124},s with{PayloadDigest=new string('f',64)},s with{SetupSha256=new string('f',64)},s with{Files=[s.Files[0] with{Size=6},..s.Files[1..]]}})Assert(!UninstallChoice.SameInstallation(s,other));
 Assert(!InstallLocations.RemovalStateFile.StartsWith(InstallLocations.Binaries+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase));
});
await Check("full removal deletes only the inspected private synthetic tree and keeps external backup",()=>
{RemovalTests.Complete();return Task.CompletedTask;});
await Check("locked or hardlinked synthetic data fails before deletion",()=>
{RemovalTests.LockedAndHardlink();return Task.CompletedTask;});
await Check("interrupted synthetic purge retains outside intent and resumes exact remaining tree",()=>
{RemovalTests.Interrupted();return Task.CompletedTask;});
}
async Task StoreCheck(string name,Func<StoreFixture,Task> run)
{
 if(preparationBlocked){blockedStore++;cases.Add(new{name,status="BLOCKED",reason="fixture preparation failed"});Console.WriteLine("BLOCKED "+name);return;}
 await Check(name,async()=>
 {
  StoreFixture fixture;
  try{fixture=await StoreFixture.Create(node,repo,helper);}
  catch{preparationBlocked=true;throw;}
  Exception? bodyFailure=null;
  try{await run(fixture);}catch(Exception failure){bodyFailure=failure;}
  try{fixture.Dispose();}catch(Exception cleanup){preparationBlocked=true;if(bodyFailure is not null)throw new AggregateException(bodyFailure,cleanup);throw;}
  if(bodyFailure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(bodyFailure).Throw();
 });
}
await StoreIntegrationTests.RunAsync(StoreCheck,node);
if(!storeOnly)
await Check("native RTL window renders at desktop and minimum size without showing or installing",async()=>
{
 var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var thread=new Thread(()=>
 {try{var app=new App();app.InitializeComponent();foreach(var size in new[]{new Size(980,730),new Size(760,620)})
  {var window=new MainWindow();var root=(FrameworkElement)window.Content;root.Width=size.Width;root.Height=size.Height;root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout();var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,"setup-"+size.Width+".png"));png.Save(file);Assert(root.FlowDirection==FlowDirection.RightToLeft);}
  foreach(var size in new[]{new Size(980,730),new Size(760,620)})foreach(var full in new[]{false,true})
  {
   var window=new MainWindow(true);var keep=(System.Windows.Controls.RadioButton)window.FindName("KeepOption");var remove=(System.Windows.Controls.RadioButton)window.FindName("DeleteOption");var input=(System.Windows.Controls.TextBox)window.FindName("ConfirmationInput");var primary=(System.Windows.Controls.Button)window.FindName("Primary");
   Assert(((System.Windows.Controls.TextBlock)window.FindName("VersionBadge")).Text==ApplicationRelease.Version+" · Windows");
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
File.WriteAllText(Path.Combine(output,"tests.json"),JsonSerializer.Serialize(new{kind=storeOnly?"first-install-synthetic-store-only":"first-install-synthetic-host-operations",passed=cases.Count-failures-blockedStore,failed=failures,blocked=blockedStore,nativeScmInvoked=false,hostAclProvisioned=false,setupCapabilityProvisioned=false,networkListenersOpened=false,offscreenUiOnly=!storeOnly,tests=cases},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{cases.Count-failures-blockedStore} passed; {failures} failed; {blockedStore} blocked. No native host installation, service, trust or listener.");return failures==0&&blockedStore==0?0:1;

(byte[] Zip,byte[] Manifest) Package(bool duplicate=false)
{
 var names=new[]{"host/Yahad.ServiceHost.exe","host/Yahad.ServiceHost.runtimeconfig.json","payload/bundle.json","payload/runtime/node/node.exe","payload/runtime/ffmpeg/ffmpeg.exe","payload/runtime/caddy/caddy.exe","payload/app/server/main.mjs","payload/app/dist/index.html","payload/app/native/Yahad.Platform.exe","payload/legal/build-inputs.json"};
 var records=new List<InstallMember>();using var data=new MemoryStream();string digest="";
 using(var zip=new ZipArchive(data,ZipArchiveMode.Create,true))foreach(var name in names)
 {var bytes=Encoding.UTF8.GetBytes(name=="payload/bundle.json"?JsonSerializer.Serialize(new{version=ApplicationRelease.Version}):name=="payload/app/server/main.mjs"?"synthetic-app-not-executed":"synthetic");if(name=="payload/bundle.json")digest=Convert.ToHexStringLower(SHA256.HashData(bytes));var entry=zip.CreateEntry(name);using(var stream=entry.Open())stream.Write(bytes);records.Add(new(name,bytes.Length,Convert.ToHexStringLower(SHA256.HashData(bytes))));if(duplicate&&name==names[0]){using var stream=zip.CreateEntry(name).Open();stream.Write(bytes);}}
 var archive=data.ToArray();return(archive,JsonSerializer.SerializeToUtf8Bytes(new InstallPackageManifest(1,ApplicationRelease.Version,ApplicationRelease.SourceCommit,Convert.ToHexStringLower(SHA256.HashData(archive)),digest,records.ToArray())));
}
internal sealed class FakeHost:ISetupHostOperations
{
 internal UninstallMode? LastRemoval;internal readonly List<string> Calls=[];internal string? Failure;internal Action? AfterPrepare;internal SetupResult Result=new(true,"READY","http://127.0.0.1:47123/",new string('a',43));
 private Task Call(string name,CancellationToken c){c.ThrowIfCancellationRequested();Calls.Add(name);if(Failure==name)throw new IOException("injected");return Task.CompletedTask;}
 public async Task PrepareAsync(SetupOwner o,Action<SetupProgress> p,CancellationToken c){await Call("prepare",c);AfterPrepare?.Invoke();}
 public Task PublishServiceAsync(SetupOwner o,Action<SetupProgress> p,CancellationToken c)=>Call("publish",c);
 public async Task<SetupResult> StartAndObserveAsync(Action<SetupProgress> p,CancellationToken c){await Call("ready",c);return Result;}
 public async Task<SetupResult> UninstallAsync(SetupOwner o,Action<SetupProgress> p,CancellationToken c,UninstallMode mode=UninstallMode.KeepData){LastRemoval=mode;await Call("uninstall",c);return new(true,"REMOVED",DataPreserved:mode==UninstallMode.KeepData);}
}

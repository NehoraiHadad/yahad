using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yahad.Install;

if(args is ["--exit"])return 0;
if(args is ["--wait"]){await Task.Delay(Timeout.Infinite);return 0;}
if(args is ["--backup-worker",var backupWorkerRoot,var backupWorkerNode,var backupWorkerHash])
{
    using var app=await Helpers.EmptyJob(backupWorkerRoot);
    using var probe=new NativeSqliteInspector(backupWorkerNode,backupWorkerHash);
    using var recovery=await NativeRecoveryFixture.OpenAsync(backupWorkerRoot,app,probe);
    await recovery.BackupAsync(p=>
    {
        if(p!="probe-copy-complete")return;
        using(var ready=new FileStream(Path.Combine(backupWorkerRoot,"crash-ready"),FileMode.CreateNew,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough))
        {ready.WriteByte(1);ready.Flush(true);}Thread.Sleep(Timeout.Infinite);
    });return 0;
}
if(args is ["--worker",var workerRoot,var workerNode,var workerHash,var workerBackup,var workerOperation,var point])
{
    using var app=await Helpers.EmptyJob(workerRoot);
    using var probe=new NativeSqliteInspector(workerNode,workerHash);
    using var recovery=await NativeRecoveryFixture.OpenAsync(workerRoot,app,probe);
    await recovery.RestoreToNewDirectoryAsync(workerBackup,5,5,workerOperation,p=>
    {
        if(p!=point)return;
        using(var ready=new FileStream(Path.Combine(workerRoot,"crash-ready"),FileMode.CreateNew,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough))
        {ready.WriteByte(1);ready.Flush(true);}
        Thread.Sleep(Timeout.Infinite);
    });
    return 0;
}
if(args.Length is <3 or >4||args[0]!="--run")throw new IOException("EXPLICIT_PRIVATE_NODE_AND_REPORT_REQUIRED");
var node=Path.GetFullPath(args[1]);var report=args[2];var appRoot=args.Length==4?Path.GetFullPath(args[3]):null;
node=Path.GetFullPath(node);var nodeHash=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(node)));
var parent=SafeDirectory.CreatePrivateSession(Path.GetTempPath());
var results=new List<object>();var failed=0;
void Assert(bool value,string message="assertion failed"){if(!value)throw new Exception(message);}
async Task Reject(Func<Task> action)
{
    try{await action();}catch(Exception e)when(e is IOException or Win32Exception or CryptographicException or JsonException or FormatException or OperationCanceledException){return;}
    throw new Exception("expected refusal");
}
async Task Check(string name,Func<Task> test)
{
    try{await test();Console.WriteLine("PASS "+name);results.Add(new{name,status="PASS"});}
    catch(Exception error){failed++;Console.WriteLine("FAIL "+name+" "+error);results.Add(new{name,status="FAIL",reason=error.GetType().Name});}
}
async Task<Fixture> Make(int schema=5)=>await Fixture.Create(parent,node,nodeHash,schema);
string Operation(Fixture f)=>Path.GetFileName(Directory.GetDirectories(f.Root,"restore-*").Single())[8..];
string Completed(Fixture f,string operation)=>Path.Combine(f.Root,"restore-"+operation,"completed");
IOException DiskFull()=>new("INJECTED_DISK_FULL",unchecked((int)0x80070070));

await Check("inspector exception cannot leave plaintext in backup artifacts",async()=>
{
    using var f=await Make(0);using var recovery=await f.Open();var before=f.DataDigest();
    try{await recovery.BackupAsync();throw new Exception("invalid SQLite observation accepted");}
    catch(IOException error)when(error.Message=="RECOVERY_SQLITE_REFUSED"){}
    Assert(!Directory.GetDirectories(f.Root,"backup-*").Any()&&!Directory.EnumerateFileSystemEntries(Path.Combine(f.Root,".recovery-probes")).Any()&&before==f.DataDigest());
});
await Check("cancellation after private SQLite copy never creates a backup artifact",async()=>
{
    using var f=await Make();using var recovery=await f.Open();using var cancel=new CancellationTokenSource();var copied=false;
    try{await recovery.BackupAsync(p=>{if(p=="probe-copy-complete"){copied=true;cancel.Cancel();}},cancel.Token);throw new Exception("cancellation ignored");}
    catch(OperationCanceledException){}
    Assert(copied&&!Directory.GetDirectories(f.Root,"backup-*").Any()&&!Directory.EnumerateFileSystemEntries(Path.Combine(f.Root,".recovery-probes")).Any());
});
await Check("killed backup worker leaves only private owned residue and admission reconciles it",async()=>
{
    using var f=await Make();var before=f.DataDigest();
    using(var worker=Helpers.Job(f.Root,["--backup-worker",f.Root,node,nodeHash]))
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(35));
        while(!File.Exists(Path.Combine(f.Root,"crash-ready")))await Task.Delay(25,deadline.Token);
        Assert(!Directory.GetDirectories(f.Root,"backup-*").Any());
        await worker.TerminateAndWaitForTreeEmptyAsync(deadline.Token);
    }
    var scratch=Directory.GetDirectories(Path.Combine(f.Root,".recovery-probes")).Single();
    var database=Path.Combine(scratch,"app.sqlite");Assert(File.ReadAllBytes(database).AsSpan(0,16).SequenceEqual(Encoding.ASCII.GetBytes("SQLite format 3\0")));
    using(var locked=new FileStream(database,FileMode.Open,FileAccess.Read,FileShare.None))await Reject(async()=>{using var refused=await f.Open();});
    Assert(File.Exists(database));var owner=Path.Combine(scratch,"owner.json");var original=File.ReadAllBytes(owner);var bad=(byte[])original.Clone();bad[bad.Length/2]^=1;File.WriteAllBytes(owner,bad);
    await Reject(async()=>{using var refused=await f.Open();});Assert(File.Exists(database));File.WriteAllBytes(owner,original);
    using var recovery=await f.Open();Assert(recovery.ReconciledProbeResidues==1&&!Directory.Exists(scratch)&&before==f.DataDigest());
    var backup=await recovery.BackupAsync();Assert(Directory.GetFiles(Path.Combine(f.Root,"backup-"+backup.BackupId)).All(p=>p.EndsWith(".bin")||Path.GetFileName(p)=="receipt.json"));
});
await Check("metadata-only interrupted probe creation cleans safely but unowned payload stays quarantined",async()=>
{
    using var f=await Make();var catalog=Path.Combine(f.Root,".recovery-probes");Directory.CreateDirectory(catalog);
    var empty=SafeDirectory.CreatePrivateSession(catalog);
    using(var recovery=await f.Open())Assert(recovery.ReconciledProbeResidues==1&&!Directory.Exists(empty));
    var unowned=SafeDirectory.CreatePrivateSession(catalog);var database=Path.Combine(unowned,"app.sqlite");File.Copy(Path.Combine(f.Root,"data/app.sqlite"),database);
    await Reject(async()=>{using var refused=await f.Open();});Assert(File.Exists(database)&&!Directory.GetDirectories(f.Root,"backup-*").Any());
});
await Check("receipt codec exact encoded capacity and one-byte overflow share the intake bound",()=>
{
    var maximum=RecoveryReceiptEncoding.MaximumPayloadBytes;var bytes=RecoveryReceiptEncoding.Serialize(new byte[maximum],new string('0',64));
    Assert(bytes.Length==RecoveryReceiptEncoding.EncodedSize(maximum)&&bytes.Length<=RecoveryReceiptEncoding.MaximumBytes);
    using(var json=JsonDocument.Parse(bytes))Assert(Convert.FromBase64String(json.RootElement.GetProperty("Payload").GetString()!).Length==maximum);
    var directory=SafeDirectory.CreatePrivateSession(parent);var file=Path.Combine(directory,"receipt.json");
    var atLimit=new byte[RecoveryReceiptEncoding.MaximumBytes];bytes.CopyTo(atLimit,0);Array.Fill(atLimit,(byte)' ',bytes.Length,atLimit.Length-bytes.Length);File.WriteAllBytes(file,atLimit);
    using(var read=WindowsFileBoundary.OpenPrivateRead(file))Assert(read.Small(RecoveryReceiptEncoding.MaximumBytes).Length==atLimit.Length);
    File.WriteAllBytes(file,new byte[RecoveryReceiptEncoding.MaximumBytes+1]);
    try{using var read=WindowsFileBoundary.OpenPrivateRead(file);read.Small(RecoveryReceiptEncoding.MaximumBytes);throw new Exception("intake overflow accepted");}catch(IOException error)when(error.Message=="RECOVERY_FILE_BOUND"){}
    try{RecoveryReceiptEncoding.Serialize(new byte[maximum+1],new string('0',64));throw new Exception("preflight overflow accepted");}catch(IOException error)when(error.Message=="RECOVERY_RECEIPT_BOUND"){}
    return Task.CompletedTask;
});
await Check("10000-record long-path receipt is rejected by metadata preflight",()=>
{
    var placeholder=new string('0',64);var files=Enumerable.Range(0,10000).Select(i=>new RecoveryFile("data/"+new string('a',189)+"/"+i.ToString("D5"),0,placeholder,0,placeholder)).ToArray();
    Assert(files.All(f=>f.Name.Length==200));
    try{RecoveryReceiptEncoding.Preflight(new(1,new string('a',32),placeholder,5,files));throw new Exception("oversized manifest accepted");}catch(IOException error)when(error.Message=="RECOVERY_RECEIPT_BOUND"){}
    var shortFiles=files.Select((f,i)=>f with{Name="data/f"+i.ToString("D5")}).ToArray();
    Assert(RecoveryReceiptEncoding.Preflight(new(1,new string('a',32),placeholder,5,shortFiles))<=RecoveryReceiptEncoding.MaximumBytes);
    return Task.CompletedTask;
});
await Check("200-character source member passes but 201 refuses before encryption",async()=>
{
    using var f=await Make();using var recovery=await f.Open();File.WriteAllBytes(Path.Combine(f.Root,"data/media",new string('a',189)),[1]);
    var valid=await recovery.BackupAsync();var count=Directory.GetDirectories(f.Root,"backup-*").Length;
    File.WriteAllBytes(Path.Combine(f.Root,"data/media",new string('b',190)),[2]);var encrypted=false;
    try{await recovery.BackupAsync(p=>{if(p=="backup-chunk-written")encrypted=true;});throw new Exception("long member accepted");}catch(IOException error)when(error.Message=="RECOVERY_MEMBER_PATH"){}
    Assert(!encrypted&&count==Directory.GetDirectories(f.Root,"backup-*").Length);
});
await Check("10000 payload records plus receipt pass the real 10001-file artifact verifier",async()=>
{
    using var f=await Make();using var recovery=await f.Open();var backup=await recovery.BackupAsync();var directory=Path.Combine(f.Root,"backup-"+backup.BackupId);
    var envelope=JsonNode.Parse(File.ReadAllText(Path.Combine(directory,"receipt.json")))!;
    var manifest=JsonNode.Parse(Convert.FromBase64String(envelope["Payload"]!.GetValue<string>()))!;
    var originals=manifest["Files"]!.AsArray();var retained=new Dictionary<string,(JsonNode Record,byte[] Cipher)>();
    foreach(var name in new[]{"data/app.sqlite","identity/master-key","policy/update-policy","policy/update-pin"})
    {var index=originals.Select((item,i)=>(item,i)).Single(p=>p.item!["Name"]!.GetValue<string>()==name).i;retained[name]=(originals[index]!.DeepClone(),File.ReadAllBytes(Path.Combine(directory,index.ToString("D5")+".bin")));}
    var records=new JsonArray();var empty=Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()));
    records.Add(retained["data/app.sqlite"].Record.DeepClone());
    for(var i=1;i<=9996;i++)records.Add(JsonSerializer.SerializeToNode(new RecoveryFile("data/file-"+i.ToString("D5"),0,empty,0,empty)));
    foreach(var name in new[]{"identity/master-key","policy/update-policy","policy/update-pin"})records.Add(retained[name].Record.DeepClone());
    for(var i=0;i<records.Count;i++)File.WriteAllBytes(Path.Combine(directory,i.ToString("D5")+".bin"),retained.TryGetValue(records[i]!["Name"]!.GetValue<string>(),out var original)?original.Cipher:Array.Empty<byte>());
    f.RewriteReceipt(backup.BackupId,json=>json["Files"]=records);
    Assert(Directory.GetFiles(directory).Length==10001);
    try{await recovery.RestoreToNewDirectoryAsync(backup.BackupId,6,6);throw new Exception("unexpected schema acceptance");}
    catch(IOException error)when(error.Message=="RECOVERY_SCHEMA_INCOMPATIBLE"){} // reached AFTER real artifact membership verification
    File.WriteAllBytes(Path.Combine(directory,"10000.bin"),[]);
    try{await recovery.RestoreToNewDirectoryAsync(backup.BackupId,6,6);throw new Exception("extra artifact member accepted");}
    catch(IOException error)when(error.Message=="RECOVERY_FILE_COUNT"){}
});
await Check("actual nonempty job refuses offline boundary",async()=>
{
    using var f=await Make();using var live=Helpers.Job(f.Root,["--wait"]);using var cancel=new CancellationTokenSource(150);
    await Reject(async()=>{using var ignored=await NativeRecoveryFixture.OpenAsync(f.Root,live,f.Probe,cancel.Token);});
    Assert(!Directory.EnumerateDirectories(f.Root,"backup-*").Any());await live.TerminateAndWaitForTreeEmptyAsync(default);
});
await Check("actual exclusive data lease prevents backup admission",async()=>
{
    using var f=await Make();using var lease=WindowsFileBoundary.AcquireDataLease(Path.Combine(f.Root,"data"));
    await Reject(async()=>{using var ignored=await f.Open();});
});
await Check("encrypted complete snapshot and new-directory restore preserve all source bytes",async()=>
{
    using var f=await Make();using var recovery=await f.Open();var original=f.DataDigest();var backup=await recovery.BackupAsync();
    Assert(backup.ObservedSchema==5);var restored=await recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5);
    Assert(restored.ObservedSchema==5&&!restored.ReconciledPublication&&f.DataDigest()==original);
    var signed=JsonNode.Parse(File.ReadAllText(Path.Combine(f.Root,"restore-"+restored.OperationId,"00001.json")))!;
    var observed=JsonNode.Parse(Convert.FromBase64String(signed["Payload"]!.GetValue<string>()))!;
    Assert(observed["ObservedJobRootProcessId"]!.GetValue<int>()==f.AppPid&&Guid.TryParseExact(observed["LeaseGeneration"]!.GetValue<string>(),"N",out _));
    Assert(File.ReadAllBytes(Path.Combine(restored.RestoredDirectory,"data/app.sqlite")).SequenceEqual(File.ReadAllBytes(Path.Combine(f.Root,"data/app.sqlite"))));
    Assert(File.ReadAllText(Path.Combine(restored.RestoredDirectory,"identity/master-key"))==File.ReadAllText(Path.Combine(f.Root,"master-key")));
    Assert(File.ReadAllBytes(Path.Combine(restored.RestoredDirectory,"data/media/תמונה.bin")).Length==2*1024*1024+17);
    var rawKey=Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(f.Root,"master-key")));
    foreach(var path in Directory.GetFiles(Path.Combine(f.Root,"backup-"+backup.BackupId),"*",SearchOption.AllDirectories))Assert(File.ReadAllBytes(path).AsSpan().IndexOf(rawKey)<0&&!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("synthetic-private-message"));
    var repeated=await recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5,restored.OperationId);Assert(repeated.ReconciledPublication&&repeated.RestoredDirectory==restored.RestoredDirectory);
});
if(appRoot is not null)
await Check("actual schema-5 Store preserves family login session grants encrypted history media and auth",async()=>
{
    using var f=await Make();File.Delete(Path.Combine(f.Root,"data/app.sqlite"));
    await Helpers.Store(node,appRoot,f.Root,null);
    using var recovery=await f.Open();var before=f.DataDigest();var backup=await recovery.BackupAsync();
    var restored=await recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5);
    using(var restoredLease=WindowsFileBoundary.AcquireDataLease(Path.Combine(restored.RestoredDirectory,"data")))
        await Helpers.Store(node,appRoot,f.Root,restored.RestoredDirectory);
    Assert(f.DataDigest()==before);
});
await Check("future observed SQLite schema refuses older target before restore writes",async()=>
{
    using var f=await Make(6);using var recovery=await f.Open();var backup=await recovery.BackupAsync();Assert(backup.ObservedSchema==6);
    await Reject(()=>recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5));Assert(!Directory.EnumerateDirectories(f.Root,"restore-*").Any());
});
await Check("actual SQLite probe defeats an authenticated false schema claim",async()=>
{
    using var f=await Make();NativeBackupResult backup;using(var recovery=await f.Open())backup=await recovery.BackupAsync();
    f.RewriteReceipt(backup.BackupId,json=>json["Schema"]=4);
    using var retry=await f.Open();try{await retry.RestoreToNewDirectoryAsync(backup.BackupId,4,4);throw new Exception("false schema accepted");}
    catch(IOException error)when(error.Message=="RECOVERY_SCHEMA_INCOMPATIBLE"){}
    Assert(!Directory.EnumerateDirectories(f.Root,"restore-*").Any(p=>Directory.Exists(Path.Combine(p,"completed"))));
});
await Check("backup rejects SQLite sidecars instead of guessing a checkpoint",async()=>
{
    using var f=await Make();using var recovery=await f.Open();File.WriteAllText(Path.Combine(f.Root,"data/app.sqlite-wal"),"uncheckpointed synthetic WAL");
    await Reject(()=>recovery.BackupAsync());Assert(!Directory.EnumerateDirectories(f.Root,"backup-*").Any());
});
await Check("corrupted SQLite is not accepted as a backup",async()=>
{
    using var f=await Make();File.WriteAllText(Path.Combine(f.Root,"data/app.sqlite"),"not SQLite");using var recovery=await f.Open();
    await Reject(()=>recovery.BackupAsync());Assert(!Directory.EnumerateFiles(f.Root,"receipt.json",SearchOption.AllDirectories).Any());
});
foreach(var file in new[]{"master-key","update-policy","update-pin"})
await Check("changed "+file+" refuses old backup identity or policy",async()=>
{
    using var f=await Make();NativeBackupResult backup;using(var recovery=await f.Open())backup=await recovery.BackupAsync();
    File.WriteAllText(Path.Combine(f.Root,file),file=="master-key"?Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)):"changed-policy");
    using var retry=await f.Open();await Reject(()=>retry.RestoreToNewDirectoryAsync(backup.BackupId,5,5));Assert(!Directory.EnumerateDirectories(f.Root,"restore-*").Any());
});
foreach(var kind in new[]{"receipt","cipher"})
await Check("corrupt "+kind+" refuses before publishing restored data",async()=>
{
    using var f=await Make();using var recovery=await f.Open();var backup=await recovery.BackupAsync();
    var path=Path.Combine(f.Root,"backup-"+backup.BackupId,kind=="receipt"?"receipt.json":"00000.bin");var bytes=File.ReadAllBytes(path);bytes[bytes.Length/2]^=1;File.WriteAllBytes(path,bytes);
    await Reject(()=>recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5));Assert(!Directory.EnumerateDirectories(f.Root,"restore-*").Any());
});
await Check("authenticated traversal member is refused",async()=>
{
    using var f=await Make();NativeBackupResult backup;using(var recovery=await f.Open())backup=await recovery.BackupAsync();
    f.RewriteReceipt(backup.BackupId,json=>json["Files"]![0]!["Name"]="../escape");
    using var retry=await f.Open();await Reject(()=>retry.RestoreToNewDirectoryAsync(backup.BackupId,5,5));Assert(!File.Exists(Path.Combine(f.Root,"escape")));
});
await Check("actual locked source file leaves active data unchanged",async()=>
{
    using var f=await Make();using var recovery=await f.Open();var before=f.DataDigest();
    using(var locked=new FileStream(Path.Combine(f.Root,"data/media/תמונה.bin"),FileMode.Open,FileAccess.Read,FileShare.None))await Reject(()=>recovery.BackupAsync());
    Assert(f.DataDigest()==before);
});
await Check("hardlink alias created after admission still refuses backup",async()=>
{
    using var f=await Make();using var recovery=await f.Open();
    if(!Helpers.CreateHardLinkW(@"\\?\"+Path.GetFullPath(Path.Combine(f.Root,"data/media/alias")),@"\\?\"+Path.GetFullPath(Path.Combine(f.Root,"data/media/תמונה.bin")),IntPtr.Zero))throw new Win32Exception();
    await Reject(()=>recovery.BackupAsync());Assert(!Directory.EnumerateFiles(f.Root,"receipt.json",SearchOption.AllDirectories).Any());
});
foreach(var restore in new[]{false,true})
await Check("cancellation between encrypted chunks refuses "+(restore?"restore publication":"backup receipt"),async()=>
{
    using var f=await Make();using var recovery=await f.Open();using var cancel=new CancellationTokenSource();var triggered=false;
    if(restore){var backup=await recovery.BackupAsync();await Reject(()=>recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5,fault:p=>{if(p=="restore-chunk-written"){triggered=true;cancel.Cancel();}},cancellation:cancel.Token));Assert(!Directory.EnumerateDirectories(f.Root,"restore-*").Any(p=>Directory.Exists(Path.Combine(p,"completed"))));}
    else{await Reject(()=>recovery.BackupAsync(p=>{if(p=="backup-chunk-written"){triggered=true;cancel.Cancel();}},cancel.Token));Assert(!Directory.EnumerateFiles(f.Root,"receipt.json",SearchOption.AllDirectories).Any());}
    Assert(triggered);
});
await Check("injected backup disk-full never publishes a receipt",async()=>
{
    using var f=await Make();using var recovery=await f.Open();var before=f.DataDigest();var triggered=false;
    await Reject(()=>recovery.BackupAsync(p=>{if(p=="backup-chunk-written"){triggered=true;throw DiskFull();}}));
    Assert(triggered&&f.DataDigest()==before&&!Directory.EnumerateFiles(f.Root,"receipt.json",SearchOption.AllDirectories).Any());
});
await Check("partial receipt flush remains uncommitted and cannot restore",async()=>
{
    using var f=await Make();using var recovery=await f.Open();
    await Reject(()=>recovery.BackupAsync(p=>{if(p=="before-durable-flush")throw DiskFull();}));
    var backup=Path.GetFileName(Directory.GetDirectories(f.Root,"backup-*").Single())[7..];
    await Reject(()=>recovery.RestoreToNewDirectoryAsync(backup,5,5));Assert(!Directory.EnumerateDirectories(f.Root,"restore-*").Any());
});
await Check("injected restore disk-full quarantines partial attempt then retries into fresh directory",async()=>
{
    using var f=await Make();using var recovery=await f.Open();var backup=await recovery.BackupAsync();var before=f.DataDigest();var triggered=false;
    await Reject(()=>recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5,fault:p=>{if(p=="restore-chunk-written"){triggered=true;throw DiskFull();}}));Assert(triggered);
    var operation=Operation(f);Assert(!Directory.Exists(Completed(f,operation)));var result=await recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5,operation);
    Assert(f.DataDigest()==before&&Directory.Exists(result.RestoredDirectory)&&Directory.GetDirectories(Path.Combine(f.Root,"restore-"+operation),"staging-*").Length==1);
});
await Check("three interrupted extraction attempts quarantine further retries",async()=>
{
    using var f=await Make();using var recovery=await f.Open();var backup=await recovery.BackupAsync();string? operation=null;
    for(var attempt=0;attempt<3;attempt++)
    {
        var triggered=false;await Reject(()=>recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5,operation,fault:p=>{if(p=="restore-chunk-written"){triggered=true;throw DiskFull();}}));
        Assert(triggered);operation=Operation(f);
    }
    try{await recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5,operation);throw new Exception("unbounded retry");}
    catch(IOException error)when(error.Message=="RECOVERY_ATTEMPTS_QUARANTINED"){}
    Assert(!Directory.Exists(Completed(f,operation!)));
});
await Check("journal tampering refuses automatic retry",async()=>
{
    using var f=await Make();using var recovery=await f.Open();var backup=await recovery.BackupAsync();
    await Reject(()=>recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5,fault:p=>{if(p=="journal-planned")throw DiskFull();}));
    var operation=Operation(f);var journal=Path.Combine(f.Root,"restore-"+operation,"00001.json");var bytes=File.ReadAllBytes(journal);bytes[^5]^=1;File.WriteAllBytes(journal,bytes);
    await Reject(()=>recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5,operation));Assert(!Directory.Exists(Completed(f,operation)));
});
await Check("retry refuses changed receipt or schema policy",async()=>
{
    using var f=await Make();using var recovery=await f.Open();var backup=await recovery.BackupAsync();
    await Reject(()=>recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5,fault:p=>{if(p=="journal-planned")throw DiskFull();}));
    var operation=Operation(f);await Reject(()=>recovery.RestoreToNewDirectoryAsync(backup.BackupId,4,5,operation));Assert(!Directory.Exists(Completed(f,operation)));
});
await Check("existing unexpected completed directory is preserved and refused",async()=>
{
    using var f=await Make();using var recovery=await f.Open();var backup=await recovery.BackupAsync();
    await Reject(()=>recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5,fault:p=>{if(p=="journal-planned")throw DiskFull();}));
    var operation=Operation(f);Directory.CreateDirectory(Completed(f,operation));var marker=Path.Combine(Completed(f,operation),"keep");File.WriteAllText(marker,"do not replace");
    await Reject(()=>recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5,operation));Assert(File.ReadAllText(marker)=="do not replace");
});
foreach(var crashPoint in new[]{"restore-chunk-written","after-restore-publication"})
await Check("actual worker termination at "+crashPoint+" reconciles without touching live DB",async()=>
{
    using var f=await Make();NativeBackupResult backup;string operation;var before=f.DataDigest();
    using(var recovery=await f.Open())
    {
        backup=await recovery.BackupAsync();await Reject(()=>recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5,fault:p=>{if(p=="journal-planned")throw DiskFull();}));operation=Operation(f);
    }
    using(var worker=Helpers.Job(f.Root,["--worker",f.Root,node,nodeHash,backup.BackupId,operation,crashPoint]))
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(35));
        while(!File.Exists(Path.Combine(f.Root,"crash-ready")))await Task.Delay(25,deadline.Token);
        await worker.TerminateAndWaitForTreeEmptyAsync(deadline.Token);
    }
    using var retry=await f.Open();var result=await retry.RestoreToNewDirectoryAsync(backup.BackupId,5,5,operation);
    Assert(f.DataDigest()==before&&Directory.Exists(result.RestoredDirectory));
    Assert(result.ReconciledPublication==(crashPoint=="after-restore-publication"));
});
await Check("completed output deletion is refused rather than replaying writes",async()=>
{
    using var f=await Make();using var recovery=await f.Open();var backup=await recovery.BackupAsync();var result=await recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5);
    Directory.Move(result.RestoredDirectory,result.RestoredDirectory+"-quarantined");await Reject(()=>recovery.RestoreToNewDirectoryAsync(backup.BackupId,5,5,result.OperationId));
});

File.WriteAllText(report,JsonSerializer.Serialize(new{kind="synthetic-native-backup-restore",nodeSha256=nodeHash,actualStoreIntegration=appRoot is not null,passed=results.Count-failed,failed,tests=results},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{results.Count-failed} passed; {failed} failed. Fixture: {parent}");return failed==0?0:1;

internal static class Helpers
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]public static extern bool CreateHardLinkW(string link,string existing,IntPtr security);
    public static async Task Store(string node,string packaged,string fixture,string? restored)
    {
        const string script="""
            (async()=>{
            const fs=require('node:fs'),path=require('node:path'),{pathToFileURL}=require('node:url'),assert=require('node:assert/strict');
            const packaged=process.argv[1],fixture=process.argv[2],restored=process.argv[3];
            const {Store}=await import(pathToFileURL(path.join(packaged,'app/src/store.ts')));
            const {verifyPassword}=await import(pathToFileURL(path.join(packaged,'app/src/crypto.ts')));
            const masterKey=Buffer.from(fs.readFileSync(path.join(restored||fixture,restored?'identity/master-key':'master-key'),'utf8'),'base64');
            const store=new Store({dataDir:path.join(restored||fixture,'data'),masterKey,connector:'mock',realAllowed:false,host:'127.0.0.1',port:3000,origin:'http://127.0.0.1:3000',secureCookie:false,maxMedia:1048576,maxStorage:8388608,childIdleMs:900000,sessionMs:3600000});
            try{
            if(!restored){
             const family=await store.createFamily('Synthetic backup family',{name:'Parent',password:'fixture-parent-password'},{name:'Child',password:'fixture-child-password'});
             const group='120363001@g.us';store.syncGroups(family.familyId,[{id:group,title:'Synthetic backup chat'}]);store.grant(family.familyId,family.childId,group,true);
             store.authSet(family.familyId,'fixture-auth','synthetic-local-auth');
             const user=store.user(store.one('SELECT * FROM users WHERE id=?',family.childId)),session=store.createSession(user);
             store.putMessage(family.familyId,{id:'fixture-message',groupId:group,sender:'fixture',fromMe:false,time:1,kind:'image',text:'synthetic encrypted history',mediaId:'fixture-media',mime:'image/jpeg',status:'delivered',reactions:{}});
             store.putMedia(family.familyId,group,'fixture-media','fixture-message',Buffer.from('synthetic encrypted media'));
             fs.writeFileSync(path.join(fixture,'store-fixture-identities.json'),JSON.stringify({family,group,session}),{flag:'wx',mode:0o600});
            }else{
             const{family,group,session}=JSON.parse(fs.readFileSync(path.join(fixture,'store-fixture-identities.json')));
             const parent=store.one('SELECT * FROM users WHERE id=?',family.parentId);assert.equal(await verifyPassword('fixture-parent-password',parent.password),true);
             assert.equal(store.session(session.sid).user.id,family.childId);assert.equal(store.allowed(store.user(store.one('SELECT * FROM users WHERE id=?',family.childId)),group),true);
             assert.equal(store.authGet(family.familyId,'fixture-auth'),'synthetic-local-auth');assert.equal(store.getMessage(family.familyId,group,'fixture-message').text,'synthetic encrypted history');
             assert.equal(store.getMedia(family.familyId,group,'fixture-media').bytes.toString(),'synthetic encrypted media');assert.equal(store.one('SELECT version FROM schema_version').version,5);
            }}finally{store.close();masterKey.fill(0);}
            })().catch(()=>{process.exitCode=2;});
            """;
        var loader=new Uri(Path.Combine(packaged,"app/node_modules/tsx/dist/loader.mjs")).AbsoluteUri;
        using var job=JobProcess.Start(node,["--import",loader,"-e",script,packaged,fixture,restored??""],fixture,LaunchEnvironment.Clean(fixture));
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try{if(await job.WaitAsync(deadline.Token)!=0)throw new IOException("REAL_STORE_FIXTURE_REFUSED");await job.WaitForTreeEmptyAsync(deadline.Token);}
        catch{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(10));await job.TerminateAndWaitForTreeEmptyAsync(cleanup.Token);throw;}
    }
    public static JobProcess Job(string directory,IEnumerable<string> args)
    {
        var environment=LaunchEnvironment.Clean(directory);
        environment["DOTNET_ROOT"]=Environment.GetEnvironmentVariable("DOTNET_ROOT")??throw new IOException("PRIVATE_TEST_DOTNET_ROOT_REQUIRED");
        return JobProcess.Start(Environment.ProcessPath!,args,directory,environment);
    }
    public static async Task<JobProcess> EmptyJob(string directory)
    {var job=Job(directory,["--exit"]);await job.WaitAsync(default);await job.WaitForTreeEmptyAsync(default);return job;}
}
internal sealed class Fixture(string root,JobProcess app,NativeSqliteInspector probe) : IDisposable
{
    public string Root=>root;
    public int AppPid=>app.ProcessId;
    public NativeSqliteInspector Probe=>probe;
    public Task<NativeRecoveryFixture> Open()=>NativeRecoveryFixture.OpenAsync(root,app,probe);
    public static async Task<Fixture> Create(string parent,string node,string hash,int schema)
    {
        var root=SafeDirectory.CreatePrivateSession(parent);Directory.CreateDirectory(Path.Combine(root,"data/media"));
        File.WriteAllText(Path.Combine(root,"master-key"),Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        File.WriteAllText(Path.Combine(root,"update-policy"),"{\"channel\":\"fixture-only\",\"autoUpdate\":false}");
        File.WriteAllText(Path.Combine(root,"update-pin"),"{\"release\":\"fixture-previous\"}");
        File.WriteAllBytes(Path.Combine(root,"data/media/תמונה.bin"),RandomNumberGenerator.GetBytes(2*1024*1024+17));File.WriteAllBytes(Path.Combine(root,"data/media/empty"),[]);
        const string seed="const{DatabaseSync}=require('node:sqlite');const db=new DatabaseSync(process.argv[1]);db.exec('PRAGMA journal_mode=DELETE; CREATE TABLE schema_version(version INTEGER NOT NULL); INSERT INTO schema_version VALUES('+Number(process.argv[2])+'); CREATE TABLE synthetic_credentials(id INTEGER PRIMARY KEY,body BLOB NOT NULL)');db.prepare('INSERT INTO synthetic_credentials VALUES(1,?)').run(Buffer.from('synthetic-private-message'));db.close();";
        using(var job=JobProcess.Start(node,["-e",seed,Path.Combine(root,"data/app.sqlite"),schema.ToString()],root,LaunchEnvironment.Clean(root)))
        {if(await job.WaitAsync(default)!=0)throw new IOException("FIXTURE_SQLITE_SEED_FAILED");await job.WaitForTreeEmptyAsync(default);}
        return new(root,await Helpers.EmptyJob(root),new NativeSqliteInspector(node,hash));
    }
    public string DataDigest()
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(var path in Directory.GetFiles(Path.Combine(root,"data"),"*",SearchOption.AllDirectories).Where(p=>Path.GetFileName(p)!="process.lock").Order(StringComparer.Ordinal))hash.AppendData(File.ReadAllBytes(path));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    public void RewriteReceipt(string id,Action<JsonObject> modify)
    {
        var path=Path.Combine(root,"backup-"+id,"receipt.json");var envelope=JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var payload=JsonNode.Parse(Convert.FromBase64String(envelope["Payload"]!.GetValue<string>()))!.AsObject();modify(payload);
        var encoded=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload));envelope["Payload"]=encoded;
        var raw=Convert.FromBase64String(File.ReadAllText(Path.Combine(root,"master-key")));var auth=HKDF.DeriveKey(HashAlgorithmName.SHA256,raw,32,info:Encoding.UTF8.GetBytes("yahad-fixture-backup-authentication-v1"));
        try{envelope["Mac"]=Convert.ToHexStringLower(HMACSHA256.HashData(auth,Encoding.UTF8.GetBytes("backup:"+encoded)));File.WriteAllText(path,envelope.ToJsonString());}
        finally{CryptographicOperations.ZeroMemory(raw);CryptographicOperations.ZeroMemory(auth);}
    }
    public void Dispose(){probe.Dispose();app.Dispose();}
}

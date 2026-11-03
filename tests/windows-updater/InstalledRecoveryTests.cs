using System.Text.Json;
using System.Security.Cryptography;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Production.Tests;

internal static class InstalledRecoveryTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("Installed journal retains every ordered activation checkpoint across restart",()=>
        {
            var f=new Fixture();var plan=f.Plan();var backup=f.Backup();
            using(var journal=new InstalledUpdateJournal(f.Files,f.Installation))
            {
                journal.Begin(plan);journal.Append(UpdateCheckpoint.Quiesced);journal.Append(UpdateCheckpoint.BackupVerified,backup);
                journal.Append(UpdateCheckpoint.ActivationIntent);journal.Append(UpdateCheckpoint.CandidateStartIntent);
            }
            using(var journal=new InstalledUpdateJournal(f.Files,f.Installation))
            {
                var state=journal.Resume(plan.OperationId);Assert(state.Checkpoint==UpdateCheckpoint.CandidateStartIntent&&state.BackupId==backup.BackupId);
                journal.Append(UpdateCheckpoint.Healthy);Assert(journal.RecoveryDecisionEvidence().Checkpoint==UpdateCheckpoint.Healthy);
            }
            return Task.CompletedTask;
        });
        await check("Installed journal refuses activation before verified backup and schema mismatch",()=>
        {
            var f=new Fixture();using var journal=new InstalledUpdateJournal(f.Files,f.Installation);journal.Begin(f.Plan());
            Reject(()=>journal.Append(UpdateCheckpoint.ActivationIntent),"UPDATE_JOURNAL_TRANSITION");journal.Append(UpdateCheckpoint.Quiesced);
            Reject(()=>journal.Append(UpdateCheckpoint.BackupVerified,f.Backup() with{ObservedSchema=7}),"UPDATE_JOURNAL_BACKUP_SCHEMA");
            Reject(()=>journal.Append(UpdateCheckpoint.BackupVerified),"UPDATE_JOURNAL_BACKUP_REQUIRED");return Task.CompletedTask;
        });
        await check("Installed journal forbids payload-only completion after candidate start intent",()=>
        {
            var f=new Fixture();using var journal=new InstalledUpdateJournal(f.Files,f.Installation);journal.Begin(f.Plan());
            journal.Append(UpdateCheckpoint.Quiesced);journal.Append(UpdateCheckpoint.BackupVerified,f.Backup());journal.Append(UpdateCheckpoint.ActivationIntent);
            journal.Append(UpdateCheckpoint.CandidateStartIntent);journal.Append(UpdateCheckpoint.RecoveryRequired);journal.Append(UpdateCheckpoint.RollbackIntent);
            Reject(()=>journal.Append(UpdateCheckpoint.RollbackComplete),"UPDATE_JOURNAL_NEW_DATA_RESTORE_REQUIRED");
            journal.Append(UpdateCheckpoint.RollbackComplete,restoreOperationId:Guid.NewGuid().ToString("N"));return Task.CompletedTask;
        });
        await check("Interrupted owner restore retains its exact operation through recovery restart",()=>
        {
            var f=new Fixture();var plan=f.Plan();var restore=Guid.NewGuid().ToString("N");
            using(var journal=new InstalledUpdateJournal(f.Files,f.Installation))
            {
                journal.Begin(plan);journal.Append(UpdateCheckpoint.Quiesced);journal.Append(UpdateCheckpoint.BackupVerified,f.Backup());journal.Append(UpdateCheckpoint.ActivationIntent);
                journal.Append(UpdateCheckpoint.CandidateStartIntent);journal.Append(UpdateCheckpoint.RollbackIntent,restoreOperationId:restore);journal.Append(UpdateCheckpoint.RecoveryRequired);
            }
            using(var journal=new InstalledUpdateJournal(f.Files,f.Installation))
            {Assert(journal.Resume(plan.OperationId).RestoreOperationId==restore);Reject(()=>journal.Append(UpdateCheckpoint.RollbackIntent,restoreOperationId:Guid.NewGuid().ToString("N")),"UPDATE_JOURNAL_BINDING");journal.Append(UpdateCheckpoint.RollbackIntent,restoreOperationId:restore);}
            return Task.CompletedTask;
        });
        await check("Installed journal exclusive operation lease blocks concurrent updater",()=>
        {
            var f=new Fixture();using var first=new InstalledUpdateJournal(f.Files,f.Installation);
            var rejected=false;try{using var second=new InstalledUpdateJournal(f.Files,f.Installation);}catch(Exception e)when(e is IOException or System.ComponentModel.Win32Exception){rejected=true;}
            Assert(rejected);return Task.CompletedTask;
        });
        await check("Previous-only restart remains unfinished until observed health and needs no missing backup",()=>
        {
            var f=new Fixture();var plan=f.Plan();
            using(var journal=new InstalledUpdateJournal(f.Files,f.Installation)){journal.Begin(plan);journal.Append(UpdateCheckpoint.RecoveryRequired);journal.Append(UpdateCheckpoint.PreviousStartIntent);}
            using(var journal=new InstalledUpdateJournal(f.Files,f.Installation))
            {
                Reject(journal.RequireNoUnfinished,"UPDATE_JOURNAL_UNFINISHED_OPERATION");journal.Resume(plan.OperationId);
                var facts=new RecoveryFacts(true,true,plan.PreviousRelease,6,plan.PolicyDigest,plan.PinDigest,null,false,true);
                Assert(UpdateRecovery.Decide(journal.RecoveryDecisionEvidence(),facts).Action==RecoveryAction.StartPreviousUnchanged);
                Assert(UpdateRecovery.Decide(journal.RecoveryDecisionEvidence(),facts with{ObservedSchema=7}).Action==RecoveryAction.Refuse);
                journal.Append(UpdateCheckpoint.Aborted);
            }
            using(var journal=new InstalledUpdateJournal(f.Files,f.Installation))journal.RequireNoUnfinished();return Task.CompletedTask;
        });
        await check("Restored runtime start intent cannot report recovery complete before health",()=>
        {
            var f=new Fixture();var plan=f.Plan();var restore=Guid.NewGuid().ToString("N");
            using(var journal=new InstalledUpdateJournal(f.Files,f.Installation))
            {
                journal.Begin(plan);journal.Append(UpdateCheckpoint.Quiesced);journal.Append(UpdateCheckpoint.BackupVerified,f.Backup());journal.Append(UpdateCheckpoint.ActivationIntent);journal.Append(UpdateCheckpoint.CandidateStartIntent);
                journal.Append(UpdateCheckpoint.RollbackIntent,restoreOperationId:restore);journal.Append(UpdateCheckpoint.RollbackStartIntent);
                var facts=new RecoveryFacts(true,true,plan.PreviousRelease,6,plan.PolicyDigest,plan.PinDigest,journal.Current!.BackupReceiptDigest,true,true);
                Assert(UpdateRecovery.Decide(journal.RecoveryDecisionEvidence(),facts).Action==RecoveryAction.Refuse);
            }
            using(var journal=new InstalledUpdateJournal(f.Files,f.Installation))
            {Reject(journal.RequireNoUnfinished,"UPDATE_JOURNAL_UNFINISHED_OPERATION");journal.Resume(plan.OperationId);journal.Append(UpdateCheckpoint.RollbackComplete);}
            return Task.CompletedTask;
        });
        await check("Installed journal rejects chain tampering and interrupted partial publication",()=>
        {
            var f=new Fixture();var plan=f.Plan();
            using(var journal=new InstalledUpdateJournal(f.Files,f.Installation)){journal.Begin(plan);journal.Append(UpdateCheckpoint.Quiesced);}
            var path=f.Files.PathOf(".authority/updates/"+plan.OperationId+"/00002.json");var record=JsonSerializer.Deserialize<InstalledUpdateRecord>(File.ReadAllBytes(path))!;
            File.WriteAllBytes(path,JsonSerializer.SerializeToUtf8Bytes(record with{PreviousDigest=new string('f',64)}));
            using(var journal=new InstalledUpdateJournal(f.Files,f.Installation))Reject(()=>journal.Resume(plan.OperationId),"UPDATE_JOURNAL_CHAIN");
            var g=new Fixture();var pending=g.Plan();using(var journal=new InstalledUpdateJournal(g.Files,g.Installation))
            {journal.Begin(pending);try{journal.Append(UpdateCheckpoint.Quiesced,fault:stage=>{if(stage=="before-update-journal-flush")throw new IOException("CRASH");});}catch(IOException error)when(error.Message=="CRASH"){} }
            using(var journal=new InstalledUpdateJournal(g.Files,g.Installation))Reject(()=>journal.Resume(pending.OperationId),"UPDATE_JOURNAL_RECONCILIATION_REQUIRED");
            return Task.CompletedTask;
        });
        await check("Restore staging is scoped to generated new output and leaves live data roles unchanged",()=>
        {
            var roles=new List<InstalledFileRole>();var f=new Fixture((_,role)=>roles.Add(role));
            var source=f.Files.PathOf("datasets/"+f.Data);Directory.CreateDirectory(source);Directory.CreateDirectory(Path.Combine(source,"data"));
            var operation=Guid.NewGuid().ToString("N");var scope=Path.Combine(source,"restore-"+operation);var view=f.Files.ForRecoveryStaging(scope);
            var completed=Path.Combine(scope,"completed");Directory.CreateDirectory(Path.Combine(completed,"data"));Directory.CreateDirectory(Path.Combine(completed,"identity"));
            File.WriteAllText(Path.Combine(completed,"data","app.sqlite"),"synthetic");File.WriteAllText(Path.Combine(completed,"identity","master-key"),"synthetic");
            Assert(view.Role(Path.Combine(completed,"data","app.sqlite"))==InstalledFileRole.RecoveryEvidence);
            Assert(f.Files.Role(Path.Combine(completed,"data","app.sqlite"))==InstalledFileRole.Data);
            Assert(f.Files.Role(Path.Combine(completed,"identity","master-key"))==InstalledFileRole.MasterKey);
            Assert(view.Role(Path.Combine(source,"data","app.sqlite"))==InstalledFileRole.Data);
            Reject(()=>f.Files.ForRecoveryStaging(source),"RECOVERY_STAGING_SCOPE");
            Reject(()=>view.PromoteRecoveryDirectory(Path.Combine(source,"data")),"RECOVERY_PUBLICATION_SCOPE");
            view.PromoteRecoveryDirectory(completed);Assert(roles.Contains(InstalledFileRole.Data)&&roles.Contains(InstalledFileRole.MasterKey));return Task.CompletedTask;
        });
        await check("Installed journal quarantines a release after three completed failed recoveries",()=>
        {
            var f=new Fixture();
            for(var i=0;i<3;i++)using(var journal=new InstalledUpdateJournal(f.Files,f.Installation))
            {journal.Begin(f.Plan());journal.Append(UpdateCheckpoint.Quiesced);journal.Append(UpdateCheckpoint.BackupVerified,f.Backup());journal.Append(UpdateCheckpoint.ActivationIntent);journal.Append(UpdateCheckpoint.RollbackIntent);journal.Append(UpdateCheckpoint.RollbackComplete);}
            using(var journal=new InstalledUpdateJournal(f.Files,f.Installation))Reject(()=>journal.Begin(f.Plan()),"UPDATE_RELEASE_QUARANTINED");return Task.CompletedTask;
        });
    }
    internal static async Task RunNative(Func<string,Func<Task>,Task> check,string nodePath,string nodeDigest)
    {
        if(!Path.IsPathFullyQualified(nodePath))throw new IOException("RECOVERY_TEST_NODE_PATH");
        using(var node=File.OpenRead(nodePath))if(Convert.ToHexStringLower(SHA256.HashData(node))!=nodeDigest)throw new IOException("RECOVERY_TEST_NODE_DIGEST");
        await check("Actual projected installed WAL database restores a new directory without changing source",async()=>
        {
            var seen=new List<InstalledFileRole>();var f=new Fixture((_,role)=>seen.Add(role),"ir");
            var dataset=f.Files.PathOf("datasets/"+f.Data);Directory.CreateDirectory(Path.Combine(dataset,"data","media"));
            File.WriteAllText(Path.Combine(dataset,"master-key"),Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            File.WriteAllText(Path.Combine(dataset,"update-policy"),"synthetic-protected-policy");File.WriteAllText(Path.Combine(dataset,"update-pin"),"synthetic-protected-pin");
            File.WriteAllText(Path.Combine(dataset,"data","media","draft.bin"),"synthetic private bytes");
            using var writer=JobProcess.Start(nodePath,["-e","const{DatabaseSync}=require('node:sqlite');const db=new DatabaseSync(process.argv[1]);db.exec('PRAGMA journal_mode=WAL;CREATE TABLE schema_version(version INTEGER NOT NULL);INSERT INTO schema_version VALUES(6);CREATE TABLE messages(id INTEGER PRIMARY KEY,body TEXT);INSERT INTO messages VALUES(1,123)');db.close();",Path.Combine(dataset,"data","app.sqlite")],dataset,LaunchEnvironment.Clean(dataset));
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));if(await writer.WaitAsync(deadline.Token)!=0)throw new IOException("RECOVERY_TEST_WRITER");await writer.WaitForTreeEmptyAsync(deadline.Token);
            var source=Path.Combine(dataset,"data","app.sqlite");var before=Hash(File.ReadAllBytes(source));
            using var inspector=new NativeSqliteInspector(nodePath,nodeDigest);
            using var recovery=await NativeRecoveryFixture.OpenInstalledAsync(f.Files,f.Data,writer,inspector,cancellation:deadline.Token);
            var legacyProbePath=Path.Combine(f.Files.ProbeTemporary,"recovery-"+Hash(System.Text.Encoding.UTF8.GetBytes(Path.Combine(dataset,"data").ToUpperInvariant())),"yahad-fixture-"+new string('0',32),"app.sqlite");
            var probePath=Path.Combine(f.Files.ProbeTemporary,"recovery-"+f.Data,"yahad-fixture-"+new string('0',32),"app.sqlite");
            Console.WriteLine("OBSERVED installed WAL probe legacy database/WAL lengths "+legacyProbePath.Length+"/"+(legacyProbePath.Length+4)+"; selected database/WAL lengths "+probePath.Length+"/"+(probePath.Length+4));
            if(legacyProbePath.Length+4>=260)Assert(probePath.Length+4<260);
            var backup=await recovery.BackupAsync(cancellation:deadline.Token);Assert(backup.ObservedSchema==6);
            var restored=await recovery.RestoreToNewDirectoryAsync(backup.BackupId,6,6,cancellation:deadline.Token);
            var crashed=false;try{await recovery.PublishInstalledRestoreAsync(backup.BackupId,restored.OperationId,6,6,
                fault:stage=>{if(stage=="recovery-role-published"&&!crashed){crashed=true;throw new IOException("SYNTHETIC_ROLE_CRASH");}},cancellation:deadline.Token);}
            catch(IOException failure)when(failure.Message=="SYNTHETIC_ROLE_CRASH"){}
            Assert(crashed&&Hash(File.ReadAllBytes(source))==before);
            var published=await recovery.PublishInstalledRestoreAsync(backup.BackupId,restored.OperationId,6,6,cancellation:deadline.Token);
            Assert(published.RestoredDirectory!=dataset&&Hash(File.ReadAllBytes(source))==before);
            Assert(Hash(File.ReadAllBytes(Path.Combine(published.RestoredDirectory,"data","app.sqlite")))==before);
            Assert(File.ReadAllText(Path.Combine(published.RestoredDirectory,"data","media","draft.bin"))=="synthetic private bytes");
            Assert(seen.Contains(InstalledFileRole.Data)&&seen.Contains(InstalledFileRole.MasterKey)&&seen.Contains(InstalledFileRole.RecoveryEvidence));
            var retained=new List<IDisposable>();try
            {
                Assert(NativeRecoveryAdmission.VerifyCompleted(dataset,Path.Combine(dataset,"data"),backup.BackupId,restored.OperationId,backup.ReceiptDigest,published.RestoredDirectory,
                    Path.Combine(published.RestoredDirectory,"identity","master-key"),Path.Combine(published.RestoredDirectory,"policy","update-policy"),Path.Combine(published.RestoredDirectory,"policy","update-pin"),retained,f.Files)==6);
            }
            finally{foreach(var resource in retained)resource.Dispose();}
        });
        await check("Actual retained generation and duplicated lease survive original owner disposal",async()=>
        {
            var f=new Fixture(prefix:"ir");var dataset=f.Files.PathOf("datasets/"+f.Data);var data=Path.Combine(dataset,"data");Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(dataset,"master-key"),Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));File.WriteAllText(Path.Combine(dataset,"update-policy"),"policy");File.WriteAllText(Path.Combine(dataset,"update-pin"),"pin");
            var writer=JobProcess.Start(nodePath,["-e","const{DatabaseSync}=require('node:sqlite');const db=new DatabaseSync(process.argv[1]);db.exec('CREATE TABLE schema_version(version INTEGER);INSERT INTO schema_version VALUES(6)');db.close();",Path.Combine(data,"app.sqlite")],dataset,LaunchEnvironment.Clean(dataset));
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));if(await writer.WaitAsync(deadline.Token)!=0)throw new IOException("RECOVERY_TEST_WRITER");await writer.WaitForTreeEmptyAsync(deadline.Token);
            using var observed=writer.AcquireRecoveryQuiescence();writer.Dispose();await observed.RequireAsync(deadline.Token);
            using var inspector=new NativeSqliteInspector(nodePath,nodeDigest);var recovery=await NativeRecoveryFixture.OpenInstalledAsync(f.Files,f.Data,observed,inspector,cancellation:deadline.Token);
            using var duplicate=recovery.DuplicateDataLease(data);var wrong=f.Files.PathOf("wrong-data");Directory.CreateDirectory(wrong);
            Reject(()=>recovery.DuplicateDataLease(wrong),"NATIVE_LEASE_DIRECTORY_BINDING");recovery.Dispose();
            var refused=false;try{using var contender=f.Files.Lease(data);}catch(Exception error)when(error is IOException or System.ComponentModel.Win32Exception){refused=true;}Assert(refused);
            duplicate.Dispose();using var next=f.Files.Lease(data);
        });
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed class UnusedChannels:IChildControlFactory{public IChildControl Create()=>throw new IOException("RECOVERY_TEST_UNEXPECTED_CHANNEL");}
    private static void Assert(bool condition){if(!condition)throw new IOException("RECOVERY_ASSERTION");}
    private static void Reject(Action action,string code){try{action();}catch(IOException e)when(e.Message==code){return;}throw new IOException("RECOVERY_EXPECTED_"+code);}
    private sealed class Fixture
    {
        internal readonly string Installation=Guid.NewGuid().ToString("N"),Data=Guid.NewGuid().ToString("N");internal readonly InstalledStateFiles Files;
        internal Fixture(Action<string,InstalledFileRole>? projection=null,string prefix="yahad-installed-recovery",string probe="",string probeDigest="")=>Files=InstalledStateFiles.Private(SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),prefix)),probe,probeDigest,projection);
        internal InstalledUpdatePlan Plan()=>new(1,Installation,Guid.NewGuid().ToString("N"),Data,new('a',64),new('b',64),new('c',64),6,6,6,6,6,new('d',64),new('e',64));
        internal NativeBackupResult Backup()=>new(Guid.NewGuid().ToString("N"),new('f',64),6);
    }
}

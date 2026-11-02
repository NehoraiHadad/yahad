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
        await check("Actual owner restore retries partial promotion and failed commit with exact output and lease",async()=>
        {
            var f=new Fixture(prefix:"tr",probe:nodePath,probeDigest:nodeDigest);var now=DateTimeOffset.UtcNow;DateTimeOffset Clock()=>now;
            var dataset=f.Files.PathOf("datasets/"+f.Data);var data=Path.Combine(dataset,"data");Directory.CreateDirectory(data);
            string Put(string relative,object value){var bytes=JsonSerializer.SerializeToUtf8Bytes(value);var path=f.Files.PathOf(relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,bytes);return Hash(bytes);}
            var key=System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));File.WriteAllBytes(Path.Combine(dataset,"master-key"),key);
            var policy=System.Text.Encoding.UTF8.GetBytes("policy");var pin=System.Text.Encoding.UTF8.GetBytes("pin");File.WriteAllBytes(Path.Combine(dataset,"update-policy"),policy);File.WriteAllBytes(Path.Combine(dataset,"update-pin"),pin);
            string Payload(string version)
            {var bytes=JsonSerializer.SerializeToUtf8Bytes(new{version,files=new[]{new{path="runtime/node/node.exe",size=new FileInfo(nodePath).Length,sha256=nodeDigest}}});var hash=Hash(bytes);Directory.CreateDirectory(f.Files.PathOf("payloads/"+hash));File.WriteAllBytes(f.Files.PathOf("payloads/"+hash+"/bundle.json"),bytes);return hash;}
            var previous=Payload("0.6.0");var candidate=Payload("0.6.1");var web=new InstalledWeb(1,f.Installation,"https://yahad.invalid",32000);var webDigest=Put("web.json",web);
            var head=new InstalledActivation(1,f.Installation,1,previous,f.Data,6,6,Hash(key),Hash(policy),Hash(pin),new('d',64),webDigest,"committed",null);
            void Publish(InstalledActivation current)
            {
                var owner=Put("owner-intent.json",new InstalledOwnerIntent(1,f.Installation,current.Epoch,Guid.NewGuid().ToString("N"),current.ReleaseDigest,current.DataId,current.Recovery is null?"activate":"recover","S-1-5-21-1-2-3-1001",now.ToUnixTimeSeconds(),null));
                var hash=Put("activation.json",current with{OwnerIntentDigest=owner});Put("epoch.json",new InstalledEpoch(1,f.Installation,current.Epoch,hash));Put("bootstrap.json",new{schema=1,service=ServiceDefinition.Name,installationId=f.Installation,activationReceiptDigest=hash});
            }
            Publish(head);var anchor=new InstalledAuthorityAnchor(1,f.Installation,ServiceDefinition.Name,InstalledAuthorityReader.AuthorityService,"S-1-5-21-1-2-3-1001",Hash(policy),Hash(pin));
            using var original=JobProcess.Start(nodePath,["-e","const{DatabaseSync}=require('node:sqlite');const db=new DatabaseSync(process.argv[1]);db.exec('CREATE TABLE schema_version(version INTEGER);INSERT INTO schema_version VALUES(6);CREATE TABLE messages(value INTEGER);INSERT INTO messages VALUES(1)');db.close();",Path.Combine(data,"app.sqlite")],dataset,LaunchEnvironment.Clean(dataset));
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));if(await original.WaitAsync(deadline.Token)!=0)throw new IOException("RECOVERY_TEST_WRITER");await original.WaitForTreeEmptyAsync(deadline.Token);
            var operation=new ProtectedOperation(1,"activate",f.Installation,1,Hash(f.Files.Read(f.Files.PathOf("activation.json"),16384)),candidate,f.Data,6,6,Hash(key),Hash(policy),Hash(pin),web,null,now.AddMinutes(10).ToUnixTimeSeconds(),Guid.NewGuid().ToString("N"));
            var catalog=new CatalogOperation(1,operation,new("0.6.0","0.6.1","stable",web.Origin,1,Hash(policy),Hash(pin)));
            var update=InstalledUpdateTransaction.Begin(f.Files,anchor,catalog,Guid.NewGuid().ToString("N"),original.AcquireRecoveryQuiescence(),Clock);
            await update.BackupAfterQuiescenceAsync(deadline.Token);update.ActivationIntent();Publish(head with{Epoch=2,ReleaseDigest=candidate});
            Assert(!update.CanRestartPreviousUnchanged);
            Reject(()=>update.PreparePreviousRestart(Clock),"UPDATE_PREVIOUS_RESTART_CANDIDATE_MAY_HAVE_RUN");
            update.CandidateStartIntent();
            using var later=JobProcess.Start(nodePath,["-e","const{DatabaseSync}=require('node:sqlite');const db=new DatabaseSync(process.argv[1]);db.exec('INSERT INTO messages VALUES(2)');db.close();",Path.Combine(data,"app.sqlite")],dataset,LaunchEnvironment.Clean(dataset));
            if(await later.WaitAsync(deadline.Token)!=0)throw new IOException("RECOVERY_TEST_LATER_WRITER");await later.WaitForTreeEmptyAsync(deadline.Token);var newerBytes=Hash(File.ReadAllBytes(Path.Combine(data,"app.sqlite")));
            update.Failure();var operationId=update.Evidence.Plan.OperationId;
            update=await InstalledUpdateTransaction.ResumeAsync(f.Files,anchor,operationId,later.AcquireRecoveryQuiescence(),Clock,deadline.Token);
            var ledger=new OwnerApprovalLedger(f.Files,f.Installation,anchor.OwnerSid,Clock);var owner=new AuthorityPeer(anchor.OwnerSid,123,1,1,true);
            CatalogOperation Approve(){var selected=update.PrepareProtectedRecover(Clock);var challenge=ledger.Challenge(selected.Operation);ledger.Approve(owner,new(1,challenge.Id,challenge.Digest,true));return selected;}
            var approved=Approve();var promotionFailed=false;
            try{await update.RestoreNewAsync(ledger,approved,deadline.Token,stage=>{if(stage=="recovery-role-published"&&!promotionFailed){promotionFailed=true;throw new IOException("SYNTHETIC_TRANSACTION_PROMOTION_CRASH");}});}
            catch(IOException failure)when(failure.Message=="SYNTHETIC_TRANSACTION_PROMOTION_CRASH"){}
            Assert(promotionFailed&&Hash(File.ReadAllBytes(Path.Combine(data,"app.sqlite")))==newerBytes);
            update.Failure();update=await InstalledUpdateTransaction.ResumeAsync(f.Files,anchor,operationId,later.AcquireRecoveryQuiescence(),Clock,deadline.Token);
            approved=Approve();var first=await update.RestoreNewAsync(ledger,approved,deadline.Token);var restoredData=Path.Combine(first.RestoredDirectory,"data");
            // This fixture's prior manifest deliberately omits the native bundle
            // contract. The actual writer borrows the real held data lease, then
            // refuses that candidate during preflight before publishing head.
            var commit=new ProtectedStateTransactionWriter(f.Files,f.Installation,later.WaitForTreeEmptyAsync,ledger,new UnusedChannels(),Clock);
            commit.UseCommitDataLease(update.DuplicateLease);var refusedCommit=false;
            try{await commit.CommitAsync(approved.Operation,cancellation:deadline.Token);}catch(KeyNotFoundException){refusedCommit=true;}
            Assert(refusedCommit&&Hash(f.Files.Read(f.Files.PathOf("activation.json"),16384))==approved.Operation.ExpectedActivationDigest);
            update.Failure();update=await InstalledUpdateTransaction.ResumeAsync(f.Files,anchor,operationId,later.AcquireRecoveryQuiescence(),Clock,deadline.Token);
            approved=Approve();var second=await update.RestoreNewAsync(ledger,approved,deadline.Token);
            Assert(first.OperationId==second.OperationId&&first.RestoredDirectory==second.RestoredDirectory&&Hash(File.ReadAllBytes(Path.Combine(data,"app.sqlite")))==newerBytes);
            var blocked=false;try{using var other=f.Files.Lease(restoredData);}catch(Exception e)when(e is IOException or System.ComponentModel.Win32Exception){blocked=true;}Assert(blocked);
            // Private control fixture stands in for the separately tested writer;
            // this assertion targets retry ownership, not installed acceptance.
            var recovered=approved.Operation;Publish(head with{Epoch=3,DataId=recovered.DataId,Recovery=recovered.Recovery});
            var binding=WindowsInstallationReader.Parse(f.Files.Read(f.Files.PathOf("bootstrap.json"),1024));update.ReleaseForRollbackStart(binding);
            using var verify=JobProcess.Start(nodePath,["-e","const{DatabaseSync}=require('node:sqlite');const db=new DatabaseSync(process.argv[1],{readOnly:true});if(db.prepare('SELECT COUNT(*) AS n FROM messages').get().n!==1)process.exitCode=2;db.close();",Path.Combine(restoredData,"app.sqlite")],dataset,LaunchEnvironment.Clean(dataset));
            if(await verify.WaitAsync(deadline.Token)!=0)throw new IOException("RECOVERY_TEST_RESTORED_BYTES");await verify.WaitForTreeEmptyAsync(deadline.Token);update.MarkRollbackComplete(binding);
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

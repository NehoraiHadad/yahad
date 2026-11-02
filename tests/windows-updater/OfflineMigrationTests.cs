using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Production.Tests;

internal static class OfflineMigrationTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("read-only update journal inspection shares validation without taking the held serial lease",()=>
        {
            var f=new JournalFixture();using var owner=new InstalledUpdateJournal(f.Files,f.Installation);var plan=f.Plan();owner.Begin(plan);
            Assert(InstalledUpdateJournal.Inspect(f.Files,f.Installation,plan.OperationId).Checkpoint==UpdateCheckpoint.Prepared);
            var path=f.Files.PathOf(".authority/updates/"+plan.OperationId+"/00001.json");var bad=NativeJson.Read<InstalledUpdateRecord>(File.ReadAllBytes(path)) with{Checkpoint=UpdateCheckpoint.Healthy};File.WriteAllBytes(path,JsonSerializer.SerializeToUtf8Bytes(bad));Reject(()=>InstalledUpdateJournal.Inspect(f.Files,f.Installation,plan.OperationId));
            var missing=new JournalFixture();
            string[] Snapshot()=>Directory.EnumerateFileSystemEntries(missing.Files.Root,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path=>Path.GetRelativePath(missing.Files.Root,path)+(Directory.Exists(path)?"/directory":"/file/"+Hash(File.ReadAllBytes(path)))).ToArray();
            var before=Snapshot();Reject(()=>InstalledUpdateJournal.Inspect(missing.Files,missing.Installation,Guid.NewGuid().ToString("N")));Assert(before.SequenceEqual(Snapshot(),StringComparer.Ordinal));return Task.CompletedTask;
        });
        await check("migration request has exactly thirteen fixed protocol fields",()=>
        {
            var s=Spec();s.Validate();var json=JsonSerializer.SerializeToElement(MigrationChildRequest.From(s));
            Assert(json.EnumerateObject().Count()==13&&json.GetProperty("dataDirectory").GetString()==s.DataDirectory&&json.GetProperty("entryDigest").GetString()==s.Plan.EntryDigest);
            Reject(()=>(s with{DataDirectory=s.SourceDataDirectory}).Validate());Reject(()=>(s with{EntryPath=Path.Combine(s.PayloadRoot,"app","src","index.ts")}).Validate());
            Reject(()=>(s with{TemporaryDirectory=Path.Combine(s.ControlRoot,"temporary")}).Validate());return Task.CompletedTask;
        });
        await check("migration rejects extra capabilities, loopback, other user, and changed entry pins",()=>
        {
            var s=Spec();var observed=new MigrationSandboxObservation(123,1,s.ExpectedRuntimeSid,s.ExpectedAppContainerSid,[],false,s.Plan.EntryDigest,s.NodeDigest);
            NativeOfflineMigration.RequireObserved(s,observed);
            foreach(var invalid in new[]{observed with{CapabilitySids=["S-1-15-3-1"]},observed with{LoopbackExempt=true},observed with{RuntimeSid=s.ExpectedAuthoritySid},observed with{AppContainerSid="S-1-15-2-1"},observed with{EntryDigest=new('f',64)},observed with{ProcessId=0}})Reject(()=>NativeOfflineMigration.RequireObserved(s,invalid));
            return Task.CompletedTask;
        });
        await check("full compatible journal requires cloned data and probation before effects release",()=>
        {
            var f=new JournalFixture();using var j=new InstalledUpdateJournal(f.Files,f.Installation);j.Begin(f.Plan());j.Append(UpdateCheckpoint.Quiesced);j.Append(UpdateCheckpoint.BackupVerified,f.Backup);
            Reject(()=>j.Append(UpdateCheckpoint.ActivationIntent));var attempt=f.Attempt(1);j.Append(UpdateCheckpoint.CloneIntent,migrationAttempt:attempt);j.Append(UpdateCheckpoint.CloneVerified);j.Append(UpdateCheckpoint.ActivationIntent);j.Append(UpdateCheckpoint.CandidateStartIntent);
            Reject(()=>j.Append(UpdateCheckpoint.Healthy));Reject(()=>j.Append(UpdateCheckpoint.EffectsReleaseIntent));j.Append(UpdateCheckpoint.ProbationHealthy);j.Append(UpdateCheckpoint.EffectsReleaseIntent);
            Assert(j.EffectsMayHaveBeenReleased);j.Append(UpdateCheckpoint.RecoveryRequired);Assert(j.EffectsMayHaveBeenReleased);j.Append(UpdateCheckpoint.CurrentAvailabilityStartIntent);j.Append(UpdateCheckpoint.Healthy);return Task.CompletedTask;
        });
        await check("failed migration attempts retain backup and require fresh copy identities with a three-attempt bound",()=>
        {
            var f=new JournalFixture();using var j=new InstalledUpdateJournal(f.Files,f.Installation);j.Begin(f.Plan() with{MigrationTargetSchema=7,CandidateMinimumSchema=7,CandidateMaximumSchema=7});j.Append(UpdateCheckpoint.Quiesced);j.Append(UpdateCheckpoint.BackupVerified,f.Backup);
            var first=f.Attempt(1);j.Append(UpdateCheckpoint.MigrationIntent,migrationAttempt:first);j.Append(UpdateCheckpoint.RecoveryRequired);Reject(()=>j.Append(UpdateCheckpoint.MigrationIntent,migrationAttempt:first));
            Reject(()=>j.Append(UpdateCheckpoint.MigrationIntent,migrationAttempt:f.Attempt(2) with{CloneOperationId=first.CloneOperationId}));
            j.Append(UpdateCheckpoint.MigrationIntent,migrationAttempt:f.Attempt(2));j.Append(UpdateCheckpoint.RecoveryRequired);j.Append(UpdateCheckpoint.MigrationIntent,migrationAttempt:f.Attempt(3));j.Append(UpdateCheckpoint.RecoveryRequired);
            Reject(()=>j.Append(UpdateCheckpoint.MigrationIntent,migrationAttempt:f.Attempt(4)));Assert(j.Current!.BackupReceiptDigest==f.Backup.ReceiptDigest);return Task.CompletedTask;
        });
        await check("legacy journal cannot acquire migration or full rollback capability",()=>
        {
            var f=new JournalFixture();using var j=new InstalledUpdateJournal(f.Files,f.Installation);
            Reject(()=>j.Begin(f.Plan() with{FullSystemPolicyDigest=null,MigrationTargetSchema=7,CandidateMinimumSchema=7,CandidateMaximumSchema=7}));
            j.Begin(f.Plan() with{FullSystemPolicyDigest=null});j.Append(UpdateCheckpoint.Quiesced);j.Append(UpdateCheckpoint.BackupVerified,f.Backup);Reject(()=>j.Append(UpdateCheckpoint.FullRollbackStartIntent));Reject(()=>j.Append(UpdateCheckpoint.CurrentAvailabilityStartIntent));return Task.CompletedTask;
        });
        await check("pre-start full cancellations do not quarantine a release; three failed candidate starts do",()=>
        {
            foreach(var started in new[]{false,true})
            {
                var f=new JournalFixture();var plan=f.Plan();
                for(var n=0;n<3;n++)
                {
                    using var j=new InstalledUpdateJournal(f.Files,f.Installation);j.Begin(plan with{OperationId=Guid.NewGuid().ToString("N")});j.Append(UpdateCheckpoint.Quiesced);j.Append(UpdateCheckpoint.BackupVerified,f.Backup);
                    if(started){j.Append(UpdateCheckpoint.CloneIntent,migrationAttempt:f.Attempt(1));j.Append(UpdateCheckpoint.CloneVerified);j.Append(UpdateCheckpoint.ActivationIntent);j.Append(UpdateCheckpoint.CandidateStartIntent);}
                    j.Append(UpdateCheckpoint.FullRollbackStartIntent);j.Append(UpdateCheckpoint.FullRollbackComplete);
                }
                using var next=new InstalledUpdateJournal(f.Files,f.Installation);if(started)Reject(()=>next.Begin(plan));else next.Begin(plan);
            }
            return Task.CompletedTask;
        });
        await check("full rollback preserves current data after an effects intent and rejects old data selection",()=>
        {
            var f=new FullSystemPolicyTests.Fixture();var now=f.Now;var original=Head(f.Installation);var before=JsonSerializer.SerializeToUtf8Bytes(original);f.Observed=f.Observed with{ActivationDigest=Hash(before)};
            var policy=f.Open();var system=policy.Authorize(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);using var journal=new InstalledUpdateJournal(f.Files,f.Installation);
            journal.Begin(new(1,f.Installation,system.OperationId,original.DataId,Hash(before),original.ReleaseDigest,system.ApplicationBundleDigest,6,6,6,6,6,original.PolicyDigest,original.PinDigest,original.MasterKeyDigest,FullSystemPolicyDigest:system.PolicyDigest));
            journal.Append(UpdateCheckpoint.Quiesced);var backup=new NativeBackupResult(Guid.NewGuid().ToString("N"),new('d',64),6);journal.Append(UpdateCheckpoint.BackupVerified,backup);
            var attempt=new MigrationAttempt(1,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"));journal.Append(UpdateCheckpoint.CloneIntent,migrationAttempt:attempt);journal.Append(UpdateCheckpoint.CloneVerified);journal.Append(UpdateCheckpoint.ActivationIntent);journal.Append(UpdateCheckpoint.CandidateStartIntent);journal.Append(UpdateCheckpoint.ProbationHealthy);journal.Append(UpdateCheckpoint.EffectsReleaseIntent);
            var current=original with{Epoch=2,ReleaseDigest=system.ApplicationBundleDigest,DataId=attempt.NewDataId,Recovery=new(original.DataId,backup.BackupId,attempt.CloneOperationId,backup.ReceiptDigest)};var currentBytes=JsonSerializer.SerializeToUtf8Bytes(current);
            var op=new ProtectedOperation(1,"activate",f.Installation,2,Hash(currentBytes),original.ReleaseDigest,current.DataId,6,6,original.MasterKeyDigest,original.PolicyDigest,original.PinDigest,new(1,f.Installation,"https://yahad.invalid",32000),current.Recovery,now.AddMinutes(10).ToUnixTimeSeconds(),Guid.NewGuid().ToString("N"));
            var auth=new FullSystemSelectionAuthentication(policy,system,journal,before,currentBytes,op,()=>f.Now);auth.RequireSelection(op,current);
            Reject(()=>new FullSystemSelectionAuthentication(policy,system,journal,before,currentBytes,op with{DataId=original.DataId,Recovery=null},()=>f.Now));
            policy.DisableCurrent();auth.RequireSelection(op,current);return Task.CompletedTask;
        });
    }
    private static InstalledActivation Head(string installation)=>new(1,installation,1,new('b',64),Guid.NewGuid().ToString("N"),6,6,new('c',64),new('d',64),new('e',64),new('f',64),new('a',64),"committed",null);
    internal static async Task RunNative(Func<string,Func<Task>,Task> check,string nodePath,string nodeDigest)
    {
        foreach(var effectsReleased in new[]{false,true})
        await check("private current availability preserves later writes after revoke; effects intent="+effectsReleased,async()=>
        {
            // Native files, backup, lease, SQLite and admission are real private
            // fixtures. Bootstrap publication/control writer and runtime ACK are
            // synthetic; this is not an installed SCM acceptance claim.
            var f=new FullSystemPolicyTests.Fixture();var files=InstalledStateFiles.Private(f.Files.Root,nodePath,nodeDigest);var dataId=Guid.NewGuid().ToString("N");var dataset=files.PathOf("datasets/"+dataId);var data=Path.Combine(dataset,"data");Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(dataset,"master-key"),Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));File.WriteAllText(Path.Combine(dataset,"update-policy"),"policy");File.WriteAllText(Path.Combine(dataset,"update-pin"),"pin");
            var web=new InstalledWeb(1,f.Installation,"https://yahad.invalid",32000);f.Put("web.json",web);var before=Head(f.Installation) with{DataId=dataId,MasterKeyDigest=Hash(File.ReadAllBytes(Path.Combine(dataset,"master-key"))),PolicyDigest=Hash(File.ReadAllBytes(Path.Combine(dataset,"update-policy"))),PinDigest=Hash(File.ReadAllBytes(Path.Combine(dataset,"update-pin"))),WebDigest=Hash(JsonSerializer.SerializeToUtf8Bytes(web))};
            byte[] Publish(InstalledActivation h)
            {
                var owner=new InstalledOwnerIntent(1,f.Installation,h.Epoch,Guid.NewGuid().ToString("N"),h.ReleaseDigest,h.DataId,h.Recovery is null?"activate":"recover",f.Owner,f.Now.ToUnixTimeSeconds(),null);f.Put("owner-intent.json",owner);h=h with{OwnerIntentDigest=Hash(JsonSerializer.SerializeToUtf8Bytes(owner))};var raw=JsonSerializer.SerializeToUtf8Bytes(h);f.Put("activation.json",h);f.Put("epoch.json",new InstalledEpoch(1,f.Installation,h.Epoch,Hash(raw)));f.Put("bootstrap.json",new{schema=1,service=ServiceDefinition.Name,installationId=f.Installation,activationReceiptDigest=Hash(raw)});return raw;
            }
            var previous=Publish(before);before=NativeJson.Read<InstalledActivation>(previous);f.Observed=f.Observed with{ActivationDigest=Hash(previous)};
            var bundle=JsonSerializer.SerializeToUtf8Bytes(new{version="0.6.1"});var bundleDigest=Hash(bundle);Directory.CreateDirectory(files.PathOf("payloads/"+bundleDigest));File.WriteAllBytes(files.PathOf("payloads/"+bundleDigest+"/bundle.json"),bundle);f.Candidate=f.Candidate with{ApplicationBundleDigest=bundleDigest};
            for(var i=0;i<f.Components.Length;i++)if(f.Components[i].Kind==SystemComponentKind.Probe){var c=f.Components[i];f.Components[i]=new(c.Kind,c.SlotRelativeDirectory,c.EntryPoint,nodeDigest,[new(c.EntryPoint,new FileInfo(nodePath).Length,nodeDigest)]);}
            var policy=f.Open();var system=policy.Authorize(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);var anchor=new InstalledAuthorityAnchor(1,f.Installation,ServiceDefinition.Name,InstalledAuthorityReader.AuthorityService,f.Owner,before.PolicyDigest,before.PinDigest);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(90));using var writer=JobProcess.Start(nodePath,["-e","const{DatabaseSync}=require('node:sqlite');const db=new DatabaseSync(process.argv[1]);db.exec('CREATE TABLE schema_version(version INTEGER);INSERT INTO schema_version VALUES(6)');db.close();",Path.Combine(data,"app.sqlite")],dataset,LaunchEnvironment.Clean(dataset));Assert(await writer.WaitAsync(deadline.Token)==0);await writer.WaitForTreeEmptyAsync(deadline.Token);using var stopped=writer.AcquireRecoveryQuiescence();
            using var probe=new NativeSqliteInspector(nodePath,nodeDigest);using var source=await NativeRecoveryFixture.OpenInstalledAsync(files,dataId,stopped.Duplicate(),probe,cancellation:deadline.Token);var backup=await source.BackupAsync(cancellation:deadline.Token);var clone=await source.RestoreToNewDirectoryAsync(backup.BackupId,6,6,cancellation:deadline.Token);await source.PublishInstalledRestoreAsync(backup.BackupId,clone.OperationId,6,6,cancellation:deadline.Token);
            files.EnsureDirectory(files.PathOf(".authority"));files.EnsureDirectory(files.PathOf(".authority/update-serial"));using var serial=files.Lease(files.PathOf(".authority/update-serial"));using var sourceLease=source.DuplicateDataLease(data);
            using var bootstrap=new SystemBootstrapJournal(files,system,serial);var snapshot=new SystemBootstrapSnapshot(1,f.Installation,1,"0.6.0",new('a',64),new('a',64),[new(SystemComponentKind.Probe,FixedSystemComponents.EntryPoint(SystemComponentKind.Probe),nodeDigest,[])]);bootstrap.Append(SystemBootstrapPhase.Prepared,snapshot);bootstrap.Append(SystemBootstrapPhase.CustodyConfirmed,snapshot);
            foreach(var kind in new[]{SystemComponentKind.RuntimeHost,SystemComponentKind.Owner,SystemComponentKind.Probe,SystemComponentKind.Authority}){bootstrap.Append(SystemBootstrapPhase.ComponentIntent,snapshot,kind);bootstrap.Append(SystemBootstrapPhase.ComponentPublished,snapshot,kind);}
            foreach(var phase in new[]{SystemBootstrapPhase.AuthorityStartIntent,SystemBootstrapPhase.AuthorityHealthy,SystemBootstrapPhase.RuntimeVerified,SystemBootstrapPhase.AwaitingHelperExit,SystemBootstrapPhase.HelperIntent,SystemBootstrapPhase.HelperPublished,SystemBootstrapPhase.Complete})bootstrap.Append(phase,snapshot);
            var attempt=new MigrationAttempt(1,Guid.NewGuid().ToString("N"),clone.OperationId,Guid.NewGuid().ToString("N"));var recoverySelection=new InstalledRecovery(dataId,backup.BackupId,clone.OperationId,backup.ReceiptDigest);var selected=new ProtectedOperation(1,"recover",f.Installation,1,Hash(previous),bundleDigest,attempt.NewDataId,6,6,before.MasterKeyDigest,before.PolicyDigest,before.PinDigest,web,recoverySelection,f.Now.AddMinutes(10).ToUnixTimeSeconds(),attempt.OperationId);f.Put(".authority/full-data-operation-"+system.OperationId+".json",selected);
            using(var journal=new InstalledUpdateJournal(files,f.Installation,serial))
            {journal.Begin(new(1,f.Installation,system.OperationId,dataId,Hash(previous),before.ReleaseDigest,bundleDigest,6,6,6,6,6,before.PolicyDigest,before.PinDigest,before.MasterKeyDigest,FullSystemPolicyDigest:system.PolicyDigest));journal.Append(UpdateCheckpoint.Quiesced);journal.Append(UpdateCheckpoint.BackupVerified,backup);journal.Append(UpdateCheckpoint.CloneIntent,migrationAttempt:attempt);journal.Append(UpdateCheckpoint.CloneVerified);journal.Append(UpdateCheckpoint.ActivationIntent);journal.Append(UpdateCheckpoint.CandidateStartIntent);journal.Append(UpdateCheckpoint.ProbationHealthy);if(effectsReleased)journal.Append(UpdateCheckpoint.EffectsReleaseIntent);}
            var current=Publish(before with{Epoch=2,ReleaseDigest=bundleDigest,DataId=attempt.NewDataId,Recovery=recoverySelection});var binding=new InstallationBinding(f.Installation,Hash(current));var currentData=Path.Combine(clone.RestoredDirectory,"data");File.WriteAllText(Path.Combine(currentData,"later-draft"),"preserve after effects");var originalHash=Hash(File.ReadAllBytes(Path.Combine(data,"app.sqlite")));
            policy.DisableCurrent();var transaction=await InstalledDataMigrationTransaction.ResumeForRecoveryAsync(files,anchor,policy,policy.RemintBootstrapRecovery(system.OperationId),bootstrap,stopped,serial,sourceLease,()=>f.Now,previous,deadline.Token);using var currentLease=files.Lease(currentData);f.Put(".authority/full-current-restart-"+system.OperationId+"-01.json",new{Format=1,InstallationId=f.Installation,SystemOperationId=system.OperationId,Attempt=1,Activation=current,Binding=binding});
            if(!effectsReleased)
            {var rejected=false;try{await transaction.PrepareCurrentAvailabilityRestartAsync(binding,stopped,currentLease,deadline.Token);}catch(IOException e)when(e.Message=="CURRENT_RESTART_EFFECTS_REQUIRED"){rejected=true;}Assert(rejected&&transaction.Evidence.Checkpoint==UpdateCheckpoint.ProbationHealthy);transaction.Failure();return;}
            await transaction.PrepareCurrentAvailabilityRestartAsync(binding,stopped,currentLease,deadline.Token);var runtime=new SyntheticProbation(true);Reject(()=>transaction.CompleteCurrentAvailabilityRestart(runtime,binding));currentLease.Dispose();await runtime.StartCommittedAsync(binding,new(bundleDigest),deadline.Token);transaction.CompleteCurrentAvailabilityRestart(runtime,binding);
            Assert(runtime.OrdinaryStarted&&!runtime.Released&&transaction.Evidence.Checkpoint==UpdateCheckpoint.Healthy&&File.ReadAllText(Path.Combine(currentData,"later-draft"))=="preserve after effects"&&Hash(File.ReadAllBytes(Path.Combine(data,"app.sqlite")))==originalHash);
        });
        await check("failed full transaction resumes reminted capability with actual retained source lease",async()=>
        {
            var f=new FullSystemPolicyTests.Fixture();var files=InstalledStateFiles.Private(f.Files.Root,nodePath,nodeDigest);var id=Guid.NewGuid().ToString("N");var dataset=files.PathOf("datasets/"+id);var data=Path.Combine(dataset,"data");Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(dataset,"master-key"),Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));File.WriteAllText(Path.Combine(dataset,"update-policy"),"policy");File.WriteAllText(Path.Combine(dataset,"update-pin"),"pin");
            var web=new InstalledWeb(1,f.Installation,"https://yahad.invalid",32000);var ownerIntent=new InstalledOwnerIntent(1,f.Installation,1,Guid.NewGuid().ToString("N"),new('b',64),id,"activate",f.Owner,f.Now.ToUnixTimeSeconds(),null);f.Put("web.json",web);f.Put("owner-intent.json",ownerIntent);
            var original=Head(f.Installation) with{DataId=id,MasterKeyDigest=Hash(File.ReadAllBytes(Path.Combine(dataset,"master-key"))),PolicyDigest=Hash(File.ReadAllBytes(Path.Combine(dataset,"update-policy"))),PinDigest=Hash(File.ReadAllBytes(Path.Combine(dataset,"update-pin"))),OwnerIntentDigest=Hash(JsonSerializer.SerializeToUtf8Bytes(ownerIntent)),WebDigest=Hash(JsonSerializer.SerializeToUtf8Bytes(web))};
            var bytes=JsonSerializer.SerializeToUtf8Bytes(original);f.Put("activation.json",original);f.Put("epoch.json",new InstalledEpoch(1,f.Installation,1,Hash(bytes)));f.Put("bootstrap.json",new{schema=1,service=ServiceDefinition.Name,installationId=f.Installation,activationReceiptDigest=Hash(bytes)});f.Observed=f.Observed with{ActivationDigest=Hash(bytes)};
            var bundle=JsonSerializer.SerializeToUtf8Bytes(new{version="0.6.1"});var bundleDigest=Hash(bundle);Directory.CreateDirectory(files.PathOf("payloads/"+bundleDigest));File.WriteAllBytes(files.PathOf("payloads/"+bundleDigest+"/bundle.json"),bundle);f.Candidate=f.Candidate with{ApplicationBundleDigest=bundleDigest};
            var policy=f.Open();var system=policy.Authorize(Guid.NewGuid().ToString("N"),f.Receipt,f.Observed);var anchor=new InstalledAuthorityAnchor(1,f.Installation,ServiceDefinition.Name,InstalledAuthorityReader.AuthorityService,f.Owner,original.PolicyDigest,original.PinDigest);
            using var writer=JobProcess.Start(nodePath,["-e","const{DatabaseSync}=require('node:sqlite');const db=new DatabaseSync(process.argv[1]);db.exec('CREATE TABLE schema_version(version INTEGER);INSERT INTO schema_version VALUES(6)');db.close();",Path.Combine(data,"app.sqlite")],dataset,LaunchEnvironment.Clean(dataset));using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));Assert(await writer.WaitAsync(deadline.Token)==0);await writer.WaitForTreeEmptyAsync(deadline.Token);
            using var stopped=writer.AcquireRecoveryQuiescence();files.EnsureDirectory(files.PathOf(".authority"));files.EnsureDirectory(files.PathOf(".authority/update-serial"));using var serial=files.Lease(files.PathOf(".authority/update-serial"));using var source=files.Lease(data);
            using var bootstrap=new SystemBootstrapJournal(files,system,serial);var snapshot=new SystemBootstrapSnapshot(1,f.Installation,1,"0.6.0",new('a',64),new('a',64),[new(SystemComponentKind.Probe,FixedSystemComponents.EntryPoint(SystemComponentKind.Probe),nodeDigest,[])]);bootstrap.Append(SystemBootstrapPhase.Prepared,snapshot);
            var transaction=InstalledDataMigrationTransaction.Begin(files,anchor,policy,system,bootstrap,stopped,serial,source,()=>f.Now);source.Dispose();transaction.Failure();
            var reminted=policy.RemintBootstrapRecovery(system.OperationId);Assert(!ReferenceEquals(system,reminted));using var sourceProof=transaction.DuplicateSourceLease();
            var resumed=await InstalledDataMigrationTransaction.ResumeForRecoveryAsync(files,anchor,policy,reminted,bootstrap,stopped,serial,sourceProof,()=>f.Now,bytes,deadline.Token);Assert(ReferenceEquals(transaction,resumed));
            var blocked=false;try{using var contender=files.Lease(data);}catch(Exception error)when(error is IOException or System.ComponentModel.Win32Exception){blocked=true;}Assert(blocked);
            var lifecycle=new SyntheticProbation();var result=await resumed.ReconcileAsync(lifecycle,new UnusedChannels(),stopped,deadline.Token);Assert(result.Result==FullDataRecoveryResult.RestoredPrevious&&lifecycle.Released&&resumed.Evidence.Checkpoint==UpdateCheckpoint.Aborted);
            // Crash after terminal data evidence but before native/RPC done:
            // retain later writes, demand a fresh native boot, never append a
            // new terminal transition or reissue the old effects command.
            File.WriteAllText(Path.Combine(data,"later-draft"),"retain");var terminalFiles=Directory.GetFiles(files.PathOf(".authority/updates/"+system.OperationId)).Length;var terminalBinding=new InstallationBinding(f.Installation,Hash(bytes));
            f.Put(".authority/full-terminal-restart-"+system.OperationId+"-01.json",new{Format=1,InstallationId=f.Installation,SystemOperationId=system.OperationId,Attempt=1,Activation=bytes,Binding=terminalBinding});
            policy.DisableCurrent();await resumed.PrepareTerminalRestartAsync(terminalBinding,stopped,sourceProof,deadline.Token);var ordinary=new SyntheticProbation(true);Reject(()=>resumed.CompleteTerminalRestart(ordinary,terminalBinding));
            await ordinary.StartCommittedAsync(terminalBinding,new(original.ReleaseDigest),deadline.Token);resumed.CompleteTerminalRestart(ordinary,terminalBinding);
            Assert(ordinary.OrdinaryStarted&&!ordinary.Released&&File.ReadAllText(Path.Combine(data,"later-draft"))=="retain"&&Directory.GetFiles(files.PathOf(".authority/updates/"+system.OperationId)).Length==terminalFiles);
            Reject(()=>resumed.CompleteTerminalRestart(ordinary,terminalBinding));
            var sameMarkerRejected=false;try{await resumed.PrepareTerminalRestartAsync(terminalBinding,stopped,sourceProof,deadline.Token);}catch(IOException e)when(e.Message=="TERMINAL_RESTART_ATTEMPT_BOUND"){sameMarkerRejected=true;}Assert(sameMarkerRejected);
            // NativeDone may still fail after this fresh health ACK. A NEW
            // bounded intent and actual stopped generation permit availability
            // retry; neither an old completion nor the same marker suffices.
            f.Put(".authority/full-terminal-restart-"+system.OperationId+"-02.json",new{Format=1,InstallationId=f.Installation,SystemOperationId=system.OperationId,Attempt=2,Activation=bytes,Binding=terminalBinding});
            using var retryHost=JobProcess.Start(nodePath,["-e","process.exit(0)"],dataset,LaunchEnvironment.Clean(dataset));Assert(await retryHost.WaitAsync(deadline.Token)==0);await retryHost.WaitForTreeEmptyAsync(deadline.Token);using var retryStopped=retryHost.AcquireRecoveryQuiescence();
            await resumed.PrepareTerminalRestartAsync(terminalBinding,retryStopped,sourceProof,deadline.Token);var retryRuntime=new SyntheticProbation(true);Reject(()=>resumed.CompleteTerminalRestart(retryRuntime,terminalBinding));await retryRuntime.StartCommittedAsync(terminalBinding,new(original.ReleaseDigest),deadline.Token);resumed.CompleteTerminalRestart(retryRuntime,terminalBinding);
            Assert(!retryRuntime.Released&&File.ReadAllText(Path.Combine(data,"later-draft"))=="retain"&&Directory.GetFiles(files.PathOf(".authority/updates/"+system.OperationId)).Length==terminalFiles);
        });
        await check("private SQLite migration uses verified backup and new copy; source bytes survive target changes",async()=>
        {
            var s=Spec(nodePath,nodeDigest);var root=s.ControlRoot;var files=InstalledStateFiles.Private(root,nodePath,nodeDigest);var dataset=Path.GetDirectoryName(s.SourceDataDirectory)!;
            Directory.CreateDirectory(s.SourceDataDirectory);File.WriteAllText(Path.Combine(dataset,"master-key"),Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));File.WriteAllText(Path.Combine(dataset,"update-policy"),"policy");File.WriteAllText(Path.Combine(dataset,"update-pin"),"pin");
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var writer=JobProcess.Start(nodePath,["-e","const{DatabaseSync}=require('node:sqlite');const db=new DatabaseSync(process.argv[1]);db.exec('CREATE TABLE schema_version(version INTEGER);INSERT INTO schema_version VALUES(6);CREATE TABLE drafts(body TEXT)');db.prepare('INSERT INTO drafts VALUES(?)').run('keep');db.close();",Path.Combine(s.SourceDataDirectory,"app.sqlite")],dataset,LaunchEnvironment.Clean(dataset));
            Assert(await writer.WaitAsync(deadline.Token)==0);await writer.WaitForTreeEmptyAsync(deadline.Token);var original=Hash(File.ReadAllBytes(Path.Combine(s.SourceDataDirectory,"app.sqlite")));
            using var probe=new NativeSqliteInspector(nodePath,nodeDigest);using var recovery=await NativeRecoveryFixture.OpenInstalledAsync(files,s.Plan.SourceDataId,writer,probe,cancellation:deadline.Token);
            var backup=await recovery.BackupAsync(cancellation:deadline.Token);var clone=await recovery.RestorePlannedToNewDirectoryAsync(backup.BackupId,6,6,s.Plan.CloneOperationId,cancellation:deadline.Token);await recovery.PublishInstalledRestoreAsync(backup.BackupId,clone.OperationId,6,6,cancellation:deadline.Token);
            s=s with{Plan=s.Plan with{BackupId=backup.BackupId,BackupReceiptDigest=backup.ReceiptDigest,MasterKeyDigest=Hash(File.ReadAllBytes(Path.Combine(dataset,"master-key"))),PolicyDigest=Hash(File.ReadAllBytes(Path.Combine(dataset,"update-policy"))),PinDigest=Hash(File.ReadAllBytes(Path.Combine(dataset,"update-pin")))}};
            Directory.CreateDirectory(Path.GetDirectoryName(s.ResultFile)!);using var cloneLease=files.Lease(s.DataDirectory);
            using var migrated=JobProcess.Start(nodePath,["-e","const{DatabaseSync}=require('node:sqlite');const db=new DatabaseSync(process.argv[1]);db.exec('ALTER TABLE drafts ADD COLUMN retained INTEGER DEFAULT 1;UPDATE schema_version SET version=7');db.close();",Path.Combine(s.DataDirectory,"app.sqlite")],dataset,LaunchEnvironment.Clean(dataset));Assert(await migrated.WaitAsync(deadline.Token)==0);await migrated.WaitForTreeEmptyAsync(deadline.Token);
            File.WriteAllBytes(s.ResultFile,JsonSerializer.SerializeToUtf8Bytes(new MigrationChildResult(1,s.Plan.OperationId,s.Nonce,6,7,s.Plan.PayloadDigest)));
            // Synthetic token evidence is deliberate; this test validates the
            // actual copy, SQLite probe and receipt engine, not AppContainer.
            var actor=new MigrationSandboxObservation(123,1,s.ExpectedRuntimeSid,s.ExpectedAppContainerSid,[],false,s.Plan.EntryDigest,s.NodeDigest);
            var verified=await NativeMigrationAdmission.VerifyAndPublishAsync(files,recovery,s,actor,deadline.Token);Assert(verified.ObservedSchema==7);_=NativeMigrationAdmission.VerifyCompleted(files,s,verified.ReceiptDigest);
            Assert(Hash(File.ReadAllBytes(Path.Combine(s.SourceDataDirectory,"app.sqlite")))==original);
            // A second update backs up the CURRENT restored dataset. Its
            // authenticated source path is the first clone, never the original
            // dataset path. Keep the original and both new copies intact.
            using(var secondRecovery=await NativeRecoveryFixture.OpenInstalledAsync(files,s.Plan.SourceDataId,migrated.AcquireRecoveryQuiescence(),probe,s.Plan.CloneOperationId,deadline.Token,cloneLease))
            {
                var currentHash=Hash(File.ReadAllBytes(Path.Combine(s.DataDirectory,"app.sqlite")));var secondBackup=await secondRecovery.BackupAsync(cancellation:deadline.Token);Assert(secondBackup.ObservedSchema==7);
                var second=await secondRecovery.RestoreToNewDirectoryAsync(secondBackup.BackupId,7,7,cancellation:deadline.Token);await secondRecovery.PublishInstalledRestoreAsync(secondBackup.BackupId,second.OperationId,7,7,cancellation:deadline.Token);
                var selection=new InstalledRecovery(s.Plan.SourceDataId,secondBackup.BackupId,second.OperationId,secondBackup.ReceiptDigest,s.Plan.CloneOperationId);Assert(selection.SourceDirectory(root)==s.DataDirectory);
                var pins=new List<IDisposable>();try{Assert(NativeRecoveryAdmission.VerifyCompleted(dataset,selection.SourceDirectory(root),secondBackup.BackupId,second.OperationId,secondBackup.ReceiptDigest,second.RestoredDirectory,Path.Combine(second.RestoredDirectory,"identity","master-key"),Path.Combine(second.RestoredDirectory,"policy","update-policy"),Path.Combine(second.RestoredDirectory,"policy","update-pin"),pins,files)==7);}finally{foreach(var retained in pins)retained.Dispose();}
                Assert(Hash(File.ReadAllBytes(Path.Combine(second.RestoredDirectory,"data","app.sqlite")))==currentHash&&Hash(File.ReadAllBytes(Path.Combine(s.DataDirectory,"app.sqlite")))==currentHash&&Hash(File.ReadAllBytes(Path.Combine(s.SourceDataDirectory,"app.sqlite")))==original);
            }
            File.WriteAllText(Path.Combine(s.DataDirectory,"later-draft"),"preserve later writes");Reject(()=>NativeMigrationAdmission.VerifyCompleted(files,s,verified.ReceiptDigest));_=NativeMigrationAdmission.VerifyCommittedEvidence(files,s,verified.ReceiptDigest);
            File.AppendAllText(Path.Combine(s.CloneRoot,"policy","update-policy"),"tamper");Reject(()=>NativeMigrationAdmission.VerifyCommittedEvidence(files,s,verified.ReceiptDigest));
        });
    }
    private static VerifiedMigrationSpec Spec(string? node=null,string? digest=null)
    {
        var root=SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"fm"));var id=Guid.NewGuid().ToString("N");var data=Guid.NewGuid().ToString("N");var clone=Guid.NewGuid().ToString("N");
        var p=new OfflineMigrationPlan(1,Guid.NewGuid().ToString("N"),id,data,clone,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),new('a',64),6,7,new('b',64),new('c',64),new('d',64),new('e',64),new('f',64));
        var work=Path.Combine(root,"migration-staging",id);var payload=Path.Combine(work,"payload");var cloneRoot=Path.Combine(root,"datasets",data,"restore-"+clone,"completed");
        return new(p,Path.Combine(payload,"runtime","node","node.exe"),digest??new('a',64),Path.Combine(payload,"app","src","native-migrate-v1.ts"),payload,cloneRoot,Path.Combine(cloneRoot,"data"),Path.Combine(work,"work","request.json"),Path.Combine(work,"work","result.json"),Path.Combine(work,"temp"),"S-1-5-80-1","S-1-15-2-123",Guid.NewGuid().ToString("N"),Path.Combine(root,"datasets",data,"data"),root,"S-1-5-80-2");
    }
    private sealed class JournalFixture
    {
        internal readonly string Installation=Guid.NewGuid().ToString("N");internal readonly InstalledStateFiles Files=InstalledStateFiles.Private(SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"fj")));
        internal readonly NativeBackupResult Backup=new(Guid.NewGuid().ToString("N"),new('a',64),6);
        internal InstalledUpdatePlan Plan()=>new(1,Installation,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),new('a',64),new('b',64),new('c',64),6,6,6,6,6,new('d',64),new('e',64),FullSystemPolicyDigest:new('f',64));
        internal MigrationAttempt Attempt(int number)=>new(number,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"));
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed class UnusedChannels:IChildControlFactory{public IChildControl Create()=>throw new IOException("MIGRATION_UNEXPECTED_CHANNEL");}
    private sealed class SyntheticProbation(bool allowOrdinary=false):IAuthorityProbationLifecycle
    {
        public string Phase{get;private set;}="stopped";public bool CleanupReconciliationRequired=>false;internal bool Released,OrdinaryStarted;
        public Task StartProbationAsync(ProbationRuntimeBinding binding,CancellationToken cancellation){Phase="healthy";return Task.CompletedTask;}
        public Task StartCommittedAsync(InstallationBinding binding,AppRelease release,CancellationToken cancellation=default){if(!allowOrdinary)throw new IOException("MIGRATION_UNFENCED_START");OrdinaryStarted=true;Phase="healthy";return Task.CompletedTask;}
        public Task StopAsync(CancellationToken cancellation=default){Phase="stopped";return Task.CompletedTask;}
        public Task RetryCleanupAsync(CancellationToken cancellation=default)=>Task.CompletedTask;
        public Task ReleaseEffectsAsync(ProbationRuntimeBinding binding,string id,CancellationToken cancellation){Released=true;return Task.CompletedTask;}
    }
    private static void Assert(bool value){if(!value)throw new IOException("OFFLINE_MIGRATION_ASSERT");}
    private static void Reject(Action action){try{action();}catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){return;}throw new InvalidOperationException("OFFLINE_MIGRATION_EXPECTED_REFUSAL");}
}

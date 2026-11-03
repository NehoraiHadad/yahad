using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Production.Tests;

// Update journal and full-system selection contracts that remain after the
// offline migration flow was removed: a schema bump is an ordinary clone of the
// backup that the candidate Store migrates on start.
internal static class UpdateJournalTests
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
        await check("full compatible journal requires cloned data and probation before effects release",()=>
        {
            var f=new JournalFixture();using var j=new InstalledUpdateJournal(f.Files,f.Installation);j.Begin(f.Plan());j.Append(UpdateCheckpoint.Quiesced);j.Append(UpdateCheckpoint.BackupVerified,f.Backup);
            Reject(()=>j.Append(UpdateCheckpoint.ActivationIntent));var attempt=f.Attempt(1);j.Append(UpdateCheckpoint.CloneIntent,cloneAttempt:attempt);j.Append(UpdateCheckpoint.CloneVerified);j.Append(UpdateCheckpoint.ActivationIntent);j.Append(UpdateCheckpoint.CandidateStartIntent);
            Reject(()=>j.Append(UpdateCheckpoint.Healthy));Reject(()=>j.Append(UpdateCheckpoint.EffectsReleaseIntent));j.Append(UpdateCheckpoint.ProbationHealthy);j.Append(UpdateCheckpoint.EffectsReleaseIntent);
            Assert(j.EffectsMayHaveBeenReleased);j.Append(UpdateCheckpoint.RecoveryRequired);Assert(j.EffectsMayHaveBeenReleased);j.Append(UpdateCheckpoint.CurrentAvailabilityStartIntent);j.Append(UpdateCheckpoint.Healthy);return Task.CompletedTask;
        });
        await check("failed clone attempts retain backup and require fresh copy identities with a three-attempt bound",()=>
        {
            var f=new JournalFixture();using var j=new InstalledUpdateJournal(f.Files,f.Installation);j.Begin(f.Plan());j.Append(UpdateCheckpoint.Quiesced);j.Append(UpdateCheckpoint.BackupVerified,f.Backup);
            var first=f.Attempt(1);j.Append(UpdateCheckpoint.CloneIntent,cloneAttempt:first);j.Append(UpdateCheckpoint.RecoveryRequired);Reject(()=>j.Append(UpdateCheckpoint.CloneIntent,cloneAttempt:first));
            Reject(()=>j.Append(UpdateCheckpoint.CloneIntent,cloneAttempt:f.Attempt(2) with{CloneOperationId=first.CloneOperationId}));
            j.Append(UpdateCheckpoint.CloneIntent,cloneAttempt:f.Attempt(2));j.Append(UpdateCheckpoint.RecoveryRequired);j.Append(UpdateCheckpoint.CloneIntent,cloneAttempt:f.Attempt(3));j.Append(UpdateCheckpoint.RecoveryRequired);
            Reject(()=>j.Append(UpdateCheckpoint.CloneIntent,cloneAttempt:f.Attempt(4)));Assert(j.Current!.BackupReceiptDigest==f.Backup.ReceiptDigest);return Task.CompletedTask;
        });
        await check("legacy journal cannot acquire full rollback capability",()=>
        {
            var f=new JournalFixture();using var j=new InstalledUpdateJournal(f.Files,f.Installation);
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
                    if(started){j.Append(UpdateCheckpoint.CloneIntent,cloneAttempt:f.Attempt(1));j.Append(UpdateCheckpoint.CloneVerified);j.Append(UpdateCheckpoint.ActivationIntent);j.Append(UpdateCheckpoint.CandidateStartIntent);}
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
            var attempt=new CloneAttempt(1,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"));journal.Append(UpdateCheckpoint.CloneIntent,cloneAttempt:attempt);journal.Append(UpdateCheckpoint.CloneVerified);journal.Append(UpdateCheckpoint.ActivationIntent);journal.Append(UpdateCheckpoint.CandidateStartIntent);journal.Append(UpdateCheckpoint.ProbationHealthy);journal.Append(UpdateCheckpoint.EffectsReleaseIntent);
            var current=original with{Epoch=2,ReleaseDigest=system.ApplicationBundleDigest,DataId=attempt.NewDataId,Recovery=new(original.DataId,backup.BackupId,attempt.CloneOperationId,backup.ReceiptDigest)};var currentBytes=JsonSerializer.SerializeToUtf8Bytes(current);
            var op=new ProtectedOperation(1,"activate",f.Installation,2,Hash(currentBytes),original.ReleaseDigest,current.DataId,6,6,original.MasterKeyDigest,original.PolicyDigest,original.PinDigest,new(1,f.Installation,"https://yahad.invalid",32000),current.Recovery,now.AddMinutes(10).ToUnixTimeSeconds(),Guid.NewGuid().ToString("N"));
            var auth=new FullSystemSelectionAuthentication(policy,system,journal,before,currentBytes,op,()=>f.Now);auth.RequireSelection(op,current);
            Reject(()=>new FullSystemSelectionAuthentication(policy,system,journal,before,currentBytes,op with{DataId=original.DataId,Recovery=null},()=>f.Now));
            policy.DisableCurrent();auth.RequireSelection(op,current);return Task.CompletedTask;
        });
    }
    private static InstalledActivation Head(string installation)=>new(1,installation,1,new('b',64),Guid.NewGuid().ToString("N"),6,6,new('c',64),new('d',64),new('e',64),new('a',64),"committed",null);
    private sealed class JournalFixture
    {
        internal readonly string Installation=Guid.NewGuid().ToString("N");internal readonly InstalledStateFiles Files=InstalledStateFiles.Private(SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"fj")));
        internal readonly NativeBackupResult Backup=new(Guid.NewGuid().ToString("N"),new('a',64),6);
        internal InstalledUpdatePlan Plan()=>new(1,Installation,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),new('a',64),new('b',64),new('c',64),6,6,6,6,6,new('d',64),new('e',64),FullSystemPolicyDigest:new('f',64));
        internal CloneAttempt Attempt(int number)=>new(number,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"));
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Assert(bool value){if(!value)throw new IOException("UPDATE_JOURNAL_TEST_ASSERT");}
    private static void Reject(Action action){try{action();}catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){return;}throw new InvalidOperationException("UPDATE_JOURNAL_TEST_EXPECTED_REFUSAL");}
}

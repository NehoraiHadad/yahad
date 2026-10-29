using Yahad.Install;

internal static class UpdateRecoveryTests
{
    private static readonly string Policy = new('a',64), Pin = new('b',64), Receipt = new('c',64);
    private static UpdateJournal Journal(UpdateCheckpoint phase) => new(phase,"old","new",4,4,4,4,5,Policy,Pin,Receipt);
    private static RecoveryFacts Facts() => new(true,true,"new",5,Policy,Pin,Receipt,true,false);
    public static async Task Run(Func<string,Func<Task>,Task> check)
    {
        void Assert(bool value) { if (!value) throw new Exception("assertion failed"); }
        await check("recovery cannot restore while app tree lives or exclusive data lease is absent",()=>
        {
            foreach(var facts in new[]{Facts() with {EntireProcessTreeEmpty=false,ExplicitOwnerRollback=true},Facts() with {ExclusiveDataLeaseOwned=false,ExplicitOwnerRollback=true}})
            { var plan=UpdateRecovery.Decide(Journal(UpdateCheckpoint.CandidateStartIntent),facts);Assert(plan.Action==RecoveryAction.WaitForQuiescence&&!plan.DatabaseRestore); }
            return Task.CompletedTask;
        });
        await check("policy or pin drift prevents recovery without silently restoring old settings",()=>
        {
            foreach(var facts in new[]{Facts() with {PolicyDigest=new('d',64),ExplicitOwnerRollback=true},Facts() with {PinDigest=new('e',64),ExplicitOwnerRollback=true}})
                Assert(UpdateRecovery.Decide(Journal(UpdateCheckpoint.RecoveryRequired),facts).Action==RecoveryAction.Refuse);
            return Task.CompletedTask;
        });
        await check("preactivation crash resumes unchanged old payload only with compatible observed schema",()=>
        {
            var facts=Facts() with {ActiveRelease="old",ObservedSchema=4};
            foreach(var phase in new[]{UpdateCheckpoint.Prepared,UpdateCheckpoint.Quiesced,UpdateCheckpoint.BackupVerified})
                Assert(UpdateRecovery.Decide(Journal(phase),facts).Action==RecoveryAction.StartPreviousUnchanged);
            Assert(UpdateRecovery.Decide(Journal(UpdateCheckpoint.Prepared),facts with {ObservedSchema=5}).Action==RecoveryAction.Refuse);
            return Task.CompletedTask;
        });
        await check("possible new schema writes never trigger automatic downgrade",()=>
        {
            foreach(var phase in new[]{UpdateCheckpoint.ActivationIntent,UpdateCheckpoint.CandidateStartIntent,UpdateCheckpoint.RecoveryRequired})
            { var plan=UpdateRecovery.Decide(Journal(phase),Facts());Assert(plan.Action==RecoveryAction.Refuse&&!plan.DatabaseRestore); }
            return Task.CompletedTask;
        });
        await check("explicit recovery requires matching complete backup receipt and compatible backup schema",()=>
        {
            foreach(var facts in new[]{Facts() with {ExplicitOwnerRollback=true,VerifiedBackupReceiptDigest=new('f',64)},Facts() with {ExplicitOwnerRollback=true,BackupIncludesDataKeysAndPolicy=false}})
                Assert(UpdateRecovery.Decide(Journal(UpdateCheckpoint.CandidateStartIntent),facts).Action==RecoveryAction.Refuse);
            Assert(UpdateRecovery.Decide(Journal(UpdateCheckpoint.CandidateStartIntent) with {SchemaBefore=5},Facts() with {ExplicitOwnerRollback=true}).Action==RecoveryAction.Refuse);
            return Task.CompletedTask;
        });
        await check("before candidate start owner can request payload-only rollback without database overwrite",()=>
        {
            var plan=UpdateRecovery.Decide(Journal(UpdateCheckpoint.ActivationIntent),Facts() with {ObservedSchema=4,ExplicitOwnerRollback=true});
            Assert(plan.Action==RecoveryAction.RestorePreviousPayloadOnly&&!plan.DatabaseRestore&&plan.RequiresDurableRollbackIntent);
            return Task.CompletedTask;
        });
        await check("after candidate start explicit recovery plans offline verified restore before old app",()=>
        {
            var plan=UpdateRecovery.Decide(Journal(UpdateCheckpoint.CandidateStartIntent),Facts() with {ExplicitOwnerRollback=true});
            Assert(plan.Action==RecoveryAction.RestoreVerifiedBackupOffline&&plan.DatabaseRestore&&plan.RequiresDurableRollbackIntent&&plan.Release=="old");
            return Task.CompletedTask;
        });
        await check("interrupted restore requires reconciliation instead of repeating uncertain writes",()=>
        {
            var plan=UpdateRecovery.Decide(Journal(UpdateCheckpoint.RollbackIntent),Facts() with {ExplicitOwnerRollback=true});
            Assert(plan.Action==RecoveryAction.ReconcileInterruptedRestore&&!plan.DatabaseRestore);
            return Task.CompletedTask;
        });
        await check("healthy candidate restarts only when active pointer and schema agree",()=>
        {
            Assert(UpdateRecovery.Decide(Journal(UpdateCheckpoint.Healthy),Facts()).Action==RecoveryAction.StartCandidate);
            Assert(UpdateRecovery.Decide(Journal(UpdateCheckpoint.Healthy),Facts() with {ObservedSchema=6}).Action==RecoveryAction.Refuse);
            Assert(UpdateRecovery.Decide(Journal(UpdateCheckpoint.Healthy),Facts() with {ActiveRelease="old",ObservedSchema=4}).Action==RecoveryAction.Refuse);
            return Task.CompletedTask;
        });
        await check("completed rollback needs verified receipt and observed previous pointer and schema",()=>
        {
            var facts=Facts() with {ActiveRelease="old",ObservedSchema=4};
            Assert(UpdateRecovery.Decide(Journal(UpdateCheckpoint.RollbackComplete),facts).Action==RecoveryAction.StartPreviousUnchanged);
            Assert(UpdateRecovery.Decide(Journal(UpdateCheckpoint.RollbackComplete),facts with {VerifiedBackupReceiptDigest=null}).Action==RecoveryAction.Refuse);
            return Task.CompletedTask;
        });
        await check("invalid journal identifiers and unrecognized phases never produce recovery actions",()=>
        {
            foreach(var journal in new[]{Journal((UpdateCheckpoint)999),Journal(UpdateCheckpoint.Prepared) with {PreviousRelease="../escape"},Journal(UpdateCheckpoint.Prepared) with {PreviousMinimumSchema=5,PreviousMaximumSchema=4}})
                Assert(UpdateRecovery.Decide(journal,Facts()).Action==RecoveryAction.Refuse);
            return Task.CompletedTask;
        });
    }
}

using Yahad.Authority;
using Yahad.Service;
namespace Yahad.Production.Tests;

// These are protected-policy + orchestration tests with a synthetic kernel.
// They do not constitute SCM/helper/AppContainer/ACL or installed acceptance.
internal static class FullSystemCoordinatorTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("two successive synthetic full updates reuse one permanent consent without UAC or budget renewal",async()=>
        {
            var f=new Fixture();await f.Run();Assert(f.Coordinator.Phase=="healthy"&&f.Kernel.Starts==1&&f.Kernel.Releases==1);
            f.PolicyFixture.Candidate=f.PolicyFixture.Candidate with{Version="0.6.2",ManifestDigest=new('b',64),ApplicationBundleDigest=new('c',64)};
            await f.Run();Assert(f.Coordinator.Phase=="healthy"&&f.Kernel.Starts==2&&f.Kernel.Releases==2&&f.PolicyFixture.Observed.Version=="0.6.2");
            Assert(f.Kernel.Acquisitions==2&&Directory.GetFiles(f.PolicyFixture.Files.PathOf("owner-ledger"),"usage-*").Length==0);
        });
        await check("Notify never acquires, stops, backs up or applies a scheduled full update",async()=>
        {
            var f=new Fixture();f.PolicyFixture.Put("intake/mode.json",new{Format=2,InstallationId=f.PolicyFixture.Installation,Mode="notify",DelegationId=(string?)null,ModeControlAllowed=true,GrantKind="system"});
            await Reject(f.Run);Assert(f.Kernel.Acquisitions==0&&f.Kernel.Starts==0&&f.Kernel.Releases==0);
        });
        await check("revocation during full backup prevents bootstrap publication and reconciles its exact source",async()=>
        {
            var f=new Fixture();f.Kernel.AfterBackup=()=>f.Policy.DisableCurrent();await Reject(f.Run);
            Assert(f.Kernel.Bootstraps==0&&f.Kernel.Reconciles==1&&f.Kernel.Releases==0&&f.Coordinator.Phase=="previous_restored");
        });
        await check("an acquired source observation failure restores only an unadmitted unchanged source",async()=>
        {
            var f=new Fixture();f.Kernel.FailObservation=true;await Reject(f.Run);
            Assert(f.Kernel.UnadmittedRestores==1&&f.Kernel.Backups==0&&f.Kernel.Starts==0&&f.Coordinator.Phase=="previous_restored");
        });
        await check("old Authority returns only after bootstrap custody transfer and never starts candidate effects",async()=>
        {
            var f=new Fixture();f.Kernel.Handoff=true;await f.Run();
            Assert(f.Coordinator.Phase=="restarting_updater"&&f.Kernel.Bootstraps==1&&f.Kernel.Starts==0&&f.Kernel.Releases==0&&f.Kernel.Disposals==1);
        });
        await check("unknown effects outcome is reconciled without replaying release or authorizing old-data restore",async()=>
        {
            var f=new Fixture();f.Kernel.FailRelease=true;await Reject(f.Run);
            Assert(f.Kernel.Releases==1&&f.Kernel.Reconciles==1&&f.Kernel.SawEffectsIntent&&f.Kernel.Retentions==1&&f.Coordinator.Phase=="recovery_required");
            await f.Coordinator.ResumeAsync(f.LastOperation,CancellationToken.None);
            Assert(f.Kernel.Releases==1&&f.Kernel.Reconciles==2&&f.Kernel.SawEffectsIntent&&f.Coordinator.Phase=="recovery_required");
        });
        await check("failed disposal retains exact ownership and blocks new work until the same operation reconciles",async()=>
        {
            var f=new Fixture();f.Kernel.FailDisposal=true;await Reject(f.Run);var original=f.LastOperation;
            Assert(f.Kernel.Retentions==1&&f.Coordinator.Phase=="recovery_required");f.Kernel.FailDisposal=false;
            await Reject(f.Run);Assert(f.Kernel.Acquisitions==1&&f.Coordinator.Phase=="recovery_required");
            f.Kernel.HealthyReconcile=true;await f.Coordinator.ResumeAsync(original,CancellationToken.None);Assert(f.Coordinator.Phase=="healthy");
            f.PolicyFixture.Candidate=f.PolicyFixture.Candidate with{Version="0.6.2"};using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await f.Run(timeout.Token);Assert(f.Coordinator.Phase=="healthy"&&f.Kernel.Acquisitions==2);
        });
    }
    private static async Task Reject(Func<Task> action){try{await action();}catch(Exception e)when(e is IOException or UnauthorizedAccessException){return;}throw new IOException("FULL_COORDINATOR_EXPECTED_FAILURE");}
    private static void Assert(bool condition){if(!condition)throw new IOException("FULL_COORDINATOR_ASSERTION");}
    private sealed class Fixture
    {
        internal readonly FullSystemPolicyTests.Fixture PolicyFixture=new();internal readonly FullSystemUpdatePolicy Policy;
        internal readonly Kernel Kernel;internal readonly FullSystemUpdateCoordinator Coordinator;internal string LastOperation="";
        internal Fixture(){Policy=PolicyFixture.Open();Kernel=new(PolicyFixture);Coordinator=new(Policy,new OwnerApprovalLedger(PolicyFixture.Files,PolicyFixture.Installation,PolicyFixture.Owner,()=>PolicyFixture.Now),Kernel);}
        internal Task Run()=>Run(CancellationToken.None);
        internal Task Run(CancellationToken token){LastOperation=Guid.NewGuid().ToString("N");return Coordinator.ExecuteAsync(new(LastOperation,PolicyFixture.Receipt,null),token);}
    }
    private sealed class Kernel(FullSystemPolicyTests.Fixture fixture):IFullSystemExecutionKernel
    {
        internal int Acquisitions,Backups,Bootstraps,Starts,Releases,Reconciles,UnadmittedRestores,Retentions,Disposals;
        internal bool FailObservation,Handoff,FailRelease,FailDisposal,SawEffectsIntent,HealthyReconcile;internal Action? AfterBackup;
        private Session? current;
        public Task RequireNoOtherUnfinishedAsync(string id,CancellationToken c){c.ThrowIfCancellationRequested();return Task.CompletedTask;}
        public Task<IFullSystemExecutionSession?> TryAcquireIdleAsync(string id,CancellationToken c)
        {Acquisitions++;return Task.FromResult<IFullSystemExecutionSession?>(current=new(this,fixture));}
        public Task<IFullSystemExecutionSession> ReacquireForRecoveryAsync(VerifiedSystemOperation op,CancellationToken c)=>Task.FromResult<IFullSystemExecutionSession>(current??throw new IOException("SYNTHETIC_NO_SESSION"));
        private sealed class Session(Kernel owner,FullSystemPolicyTests.Fixture fixture):IFullSystemExecutionSession
        {
            public bool CandidateStartWasRecorded{get;private set;}public bool EffectsMayHaveBeenReleased{get;private set;}
            public Task<FullSystemInstalledState> ObserveSourceAsync(CancellationToken c)=>owner.FailObservation?throw new IOException("SYNTHETIC_OBSERVATION"):Task.FromResult(fixture.Observed);
            public Task PrepareBootstrapAndBackupAsync(VerifiedSystemOperation op,CancellationToken c){owner.Backups++;owner.AfterBackup?.Invoke();return Task.CompletedTask;}
            public Task<FullSystemCompletion> ReplaceBootstrapAsync(VerifiedSystemOperation op,CancellationToken c){owner.Bootstraps++;return Task.FromResult(owner.Handoff?FullSystemCompletion.HandedToReplacementAuthority:FullSystemCompletion.Healthy);}
            public Task PrepareCandidateDataAsync(VerifiedSystemOperation op,CancellationToken c)=>Task.CompletedTask;
            public Task CommitAndStartProbationAsync(VerifiedSystemOperation op,CancellationToken c){owner.Starts++;CandidateStartWasRecorded=true;return Task.CompletedTask;}
            public Task ReleaseEffectsAsync(VerifiedSystemOperation op,CancellationToken c)
            {
                owner.Releases++;EffectsMayHaveBeenReleased=true;if(owner.FailRelease)throw new IOException("SYNTHETIC_LOST_ACK");
                fixture.Observed=new(op.ToVersion,op.ExpectedBootstrapEpoch+1,new('d',64),op.TargetSchema);return Task.CompletedTask;
            }
            public Task<FullSystemCompletion> ReconcileAsync(VerifiedSystemOperation op,CancellationToken c)
            {owner.Reconciles++;owner.SawEffectsIntent=EffectsMayHaveBeenReleased;return Task.FromResult(owner.HealthyReconcile?FullSystemCompletion.Healthy:EffectsMayHaveBeenReleased?FullSystemCompletion.RecoveryRequired:FullSystemCompletion.RestoredPrevious);}
            public Task<FullSystemCompletion> RestoreUnadmittedSourceAsync(CancellationToken c){owner.UnadmittedRestores++;return Task.FromResult(FullSystemCompletion.RestoredPrevious);}
            public void RetainUnresolvedOwnership()=>owner.Retentions++;
            public ValueTask DisposeAsync(){owner.Disposals++;if(owner.FailDisposal)throw new IOException("SYNTHETIC_DISPOSAL");return ValueTask.CompletedTask;}
        }
    }
}

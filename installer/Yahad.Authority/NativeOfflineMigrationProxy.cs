using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

// Launches no candidate under Authority. The existing authenticated SCM host
// is first assigned to its actual owned job, then admits exactly this offline
// purpose. Its descriptor was published by prepareSandbox through Control role.
internal sealed class NativeOfflineMigrationProxy(IInstallationReader installation,
    Func<IRetainedScmMigrationSession> create,TimeSpan? startupDeadline=null):IMigrationProcessFactory
{
    internal NativeOfflineMigrationProxy(IInstallationReader installation):this(installation,()=>new NativeScmRuntimeSession()){}
    private Session? retained;
    private readonly TimeSpan startup=ValidateStartup(startupDeadline??TimeSpan.FromMinutes(5));
    private static TimeSpan ValidateStartup(TimeSpan value)=>value>TimeSpan.Zero&&value<=TimeSpan.FromMinutes(5)?value:throw new ArgumentOutOfRangeException(nameof(startupDeadline));
    public async Task<IMigrationChildSession> StartAsync(VerifiedMigrationSpec spec,CancellationToken cancellation)
    {
        spec.Validate();cancellation.ThrowIfCancellationRequested();
        if(retained is not null)throw new IOException("MIGRATION_PROXY_REPLAY");var binding=installation.Read();
        if(binding.InstallationId!=spec.Plan.InstallationId)throw new UnauthorizedAccessException("MIGRATION_PROXY_INSTALLATION");
        var session=new Session(create(),spec);retained=session;
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(startup);
            await session.StartAsync(binding,deadline.Token);
            if(installation.Read()!=binding)throw new UnauthorizedAccessException("MIGRATION_PROXY_BOOTSTRAP_CHANGED");return session;
        }
        catch(Exception failure)
        {
            try{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await session.TerminateAndWaitForTreeEmptyAsync(cleanup.Token);await session.DisposeAsync();}
            catch{session.StartFailure=failure;return session;}
            throw;
        }
    }
    private sealed class Session(IRetainedScmMigrationSession runtime,VerifiedMigrationSpec spec):IMigrationChildSession
    {
        private RuntimeAdmissionBinding? admission;private RuntimeAuthorityGate? gate;private MigrationSandboxObservation? observed;
        private Task<int>? completion;private bool disposed,rootExited,treeEmpty,terminated;private int? exitCode;internal Exception? StartFailure;
        internal async Task StartAsync(InstallationBinding binding,CancellationToken cancellation)
        {
            admission=await runtime.StartWaitingAsync(new(binding,spec.Plan.PayloadDigest,spec.Plan.OperationId,"offline-migrate"),cancellation);admission.Validate();
            if(admission.Binding!=binding||admission.ReleaseDigest!=spec.Plan.PayloadDigest||admission.Nonce!=spec.Plan.OperationId)
                throw new UnauthorizedAccessException("MIGRATION_PROXY_ADMISSION");runtime.RequireAuthenticatedCurrent(admission);
            gate=new(runtime.Kernel);gate.Admit(new(admission.Binding.InstallationId,admission.ReleaseDigest,admission.Nonce,admission.ProcessId,admission.Created));
            runtime.RequireAuthenticatedCurrent(admission);await runtime.ReleaseLaunchAsync(admission,cancellation);
            observed=await runtime.WaitMigrationStartedAsync(admission,cancellation);NativeMigrationSandbox.RequireObservation(spec,observed);
            await runtime.AcknowledgeMigrationStartedAsync(admission,cancellation);
        }
        public MigrationSandboxObservation Observe()
        {
            ObjectDisposedException.ThrowIf(disposed,this);if(StartFailure is not null)throw new IOException("MIGRATION_PROXY_START_CLEANUP_UNCONFIRMED",StartFailure);
            return observed??throw new IOException("MIGRATION_PROXY_OBSERVATION_UNAVAILABLE");
        }
        public Task RequestGracefulStopAsync(CancellationToken cancellation)=>runtime.StopAsync(cancellation);
        public async Task<int> WaitForRootExitAsync(CancellationToken cancellation)
        {
            // Admission may have failed, but cleanup must still be able to
            // observe the retained native host's exit. It never converts the
            // failed operation into success: return a failure code after exit.
            if(exitCode is { } code)return code;
            if(terminated&&rootExited)return -1;
            if(StartFailure is not null||admission is null)
            {await runtime.WaitForExitAsync(cancellation);rootExited=true;return -1;}
            // One reader owns the final frame. Cancellation cannot start a
            // second read against a partially consumed transport frame.
            completion??=ReadCompletionAsync();return await completion.WaitAsync(cancellation);
        }
        private async Task<int> ReadCompletionAsync()
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var code=await runtime.WaitMigrationCompletedAsync(admission!,deadline.Token);
            await runtime.WaitForExitAsync(deadline.Token);rootExited=true;exitCode=code;return code;
        }
        public async Task WaitForTreeEmptyAsync(CancellationToken cancellation)
        {
            await runtime.WaitForExitAsync(cancellation);rootExited=true;
            // Native RequireQuiescence checks the original host handle and the
            // ENTIRE actual Authority-owned job; Runtime's report is insufficient.
            runtime.RequireQuiescence();treeEmpty=true;
        }
        public async Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation)
        {await runtime.StopAsync(cancellation);runtime.RequireQuiescence();terminated=true;rootExited=true;treeEmpty=true;}
        public ValueTask DisposeAsync()
        {
            if(disposed)return ValueTask.CompletedTask;
            if(!rootExited||!treeEmpty)throw new IOException("MIGRATION_PROXY_QUIESCENCE_UNCONFIRMED");runtime.RequireQuiescence();runtime.Dispose();disposed=true;return ValueTask.CompletedTask;
        }
    }
}

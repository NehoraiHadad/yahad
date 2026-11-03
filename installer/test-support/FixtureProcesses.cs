using System.IO;
using Yahad.Install;
using Yahad.Service;

// Test-owned handles only. Never discovers or terminates processes by name/PID.
internal sealed class FixtureProcesses
{
    private readonly List<Session> sessions=[];
    private readonly List<JobProcess> jobs=[];
    private readonly List<ProbeOwnershipException> probes=[];
    private readonly List<CancellationTokenSource> operations=[];
    private readonly List<GrantOwner> grants=[];
    private readonly object gate=new();
    private bool closing,confirmed;
    private int starting,resolving;
    internal bool Confirmed=>confirmed;
    internal IPrivateAppLauncher Launcher(IPrivateAppLauncher inner)=>new LauncherOwner(this,inner);
    internal JobOwner StartJob(string image,IEnumerable<string> arguments,string directory,IReadOnlyDictionary<string,string> environment,
        Action<Microsoft.Win32.SafeHandles.SafeProcessHandle,int>? beforeResume=null)
    {
        lock(gate){if(closing)throw new IOException("FIXTURE_ALREADY_CLOSING");starting++;}
        try{var job=JobProcess.Start(image,arguments,directory,environment,beforeResume);lock(gate)jobs.Add(job);return new(job);}
        finally{lock(gate)starting--;}
    }
    internal Task RetrySessionAsync(IContainedAppSession inner,CancellationToken cancellation)
        =>(sessions.SingleOrDefault(session=>ReferenceEquals(session.Inner,inner))??throw new IOException("FIXTURE_SESSION_NOT_OWNED")).CleanupAsync(cancellation);
    internal FixtureResolver Resolver(InstalledActivationResolver inner)=>new(this,inner);
    internal void Retain(Exception error)
    {
        if(error is ProbeOwnershipException probe)lock(gate){if(!probes.Contains(probe))probes.Add(probe);}
        if(error is AggregateException aggregate){foreach(var cause in aggregate.InnerExceptions)Retain(cause);}
        else if(error.InnerException is {} inner)Retain(inner);
    }
    internal async Task CleanupAsync()
    {
        if(confirmed)return;
        CancellationTokenSource[] pending;lock(gate){closing=true;pending=operations.ToArray();}
        foreach(var operation in pending)try{operation.Cancel();}catch(ObjectDisposedException){}
        using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while(Volatile.Read(ref starting)!=0||Volatile.Read(ref resolving)!=0)await Task.Delay(10,bound.Token);
        foreach(var session in sessions)await session.CleanupAsync(bound.Token);
        foreach(var probe in probes)await probe.RetryCleanupAsync(bound.Token);
        foreach(var job in jobs){await job.TerminateAndWaitForTreeEmptyAsync(bound.Token);job.Dispose();}
        foreach(var grant in grants)grant.Dispose();
        confirmed=true;
    }
    internal sealed class FixtureResolver(FixtureProcesses owner,InstalledActivationResolver inner):IInstalledActivationResolver,IObservedAdmission
    {
        public TimeSpan AdmissionBudget=>inner.AdmissionBudget;
        public event Action? ObservedWork{add=>inner.ObservedWork+=value;remove=>inner.ObservedWork-=value;}
        public async Task<AuthorizedServiceStart> ResolveAsync(InstallationBinding binding,CancellationToken cancellation)
        {
            using var operation=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            lock(owner.gate){if(owner.closing)throw new IOException("FIXTURE_ALREADY_CLOSING");owner.resolving++;owner.operations.Add(operation);}
            try
            {
                var original=await inner.ResolveAsync(binding,operation.Token);var grant=new GrantOwner(original);
                lock(owner.gate)owner.grants.Add(grant);
                return new(original.Release,original.Launcher,original.Recovery,grant);
            }
            catch(Exception error){owner.Retain(error);throw;}
            finally{lock(owner.gate){owner.operations.Remove(operation);owner.resolving--;}}
        }
    }
    private sealed class GrantOwner(AuthorizedServiceStart original):IDisposable
    {private bool disposed;public void Dispose(){if(disposed)return;original.Dispose();disposed=true;}}
    internal sealed class JobOwner(JobProcess job):IDisposable
    {
        internal JobProcess Process=>job;
        internal int ProcessId=>job.ProcessId;
        internal Task<int> WaitAsync(CancellationToken c)=>job.WaitAsync(c);
        internal Task WaitForTreeEmptyAsync(CancellationToken c)=>job.WaitForTreeEmptyAsync(c);
        internal Task TerminateAndWaitForTreeEmptyAsync(CancellationToken c)=>job.TerminateAndWaitForTreeEmptyAsync(c);
        // The owning FixtureProcesses terminates and disposes every started job at cleanup.
        public void Dispose(){}
    }
    private sealed class LauncherOwner(FixtureProcesses owner,IPrivateAppLauncher inner):IPrivateAppLauncher
    {
        public async Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken cancellation)
        {
            lock(owner.gate){if(owner.closing)throw new IOException("FIXTURE_ALREADY_CLOSING");owner.starting++;}
            try{var session=new Session(await inner.StartAsync(release,cancellation));lock(owner.gate)owner.sessions.Add(session);return session;}
            finally{lock(owner.gate)owner.starting--;}
        }
        public Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken cancellation)
            =>inner.WaitForHealthyAsync(((Session)session).Inner,cancellation);
    }
    private sealed class Session(IContainedAppSession inner):IContainedAppSession,IUpdateIdleSession
    {
        internal IContainedAppSession Inner=>inner;
        private bool quiescent,disposed;
        public Task RequestGracefulStopAsync(CancellationToken c)=>inner.RequestGracefulStopAsync(c);
        public Task<int> WaitForRootExitAsync(CancellationToken c)=>inner.WaitForRootExitAsync(c);
        public async Task WaitForTreeEmptyAsync(CancellationToken c){await inner.WaitForTreeEmptyAsync(c);quiescent=true;}
        public async Task TerminateAndWaitForTreeEmptyAsync(CancellationToken c){if(quiescent)return;await inner.TerminateAndWaitForTreeEmptyAsync(c);quiescent=true;}
        public Task<bool> PrepareUpdateAsync(string id,CancellationToken c)=>inner is IUpdateIdleSession idle?idle.PrepareUpdateAsync(id,c):Task.FromResult(false);
        public Task CancelUpdateAsync(string id,CancellationToken c)=>inner is IUpdateIdleSession idle?idle.CancelUpdateAsync(id,c):Task.CompletedTask;
        public async ValueTask DisposeAsync(){if(disposed)return;if(!quiescent)throw new IOException("FIXTURE_SESSION_QUIESCENCE_UNCONFIRMED");await inner.DisposeAsync();disposed=true;}
        internal async Task CleanupAsync(CancellationToken c){if(disposed)return;await TerminateAndWaitForTreeEmptyAsync(c);await DisposeAsync();}
    }
}

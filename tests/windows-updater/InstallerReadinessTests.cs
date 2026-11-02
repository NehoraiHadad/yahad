using System.Text.Json;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;

internal static class InstallerReadinessTests
{
    private static readonly InstallationBinding Binding=new(new string('a',32),new string('c',64));
    private static readonly AppRelease Release=new(new string('d',64));
    private static readonly string RuntimeNonce=new('e',32);
    private static InstallerReadinessRequest Request()=>new(1,Binding.InstallationId,new string('b',32),Binding.ActivationReceiptDigest);
    private static InstallerReadinessObservation Healthy()=>new(1,Binding.InstallationId,Request().Nonce,Binding.ActivationReceiptDigest,Release.Id,55,66,true,77,88,RuntimeNonce);
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("installer readiness accepts only the exact healthy generation and roundtrips its bounded fields",()=>
        {
            var observed=JsonSerializer.Deserialize<InstallerReadinessObservation>(JsonSerializer.SerializeToUtf8Bytes(Healthy()))!;
            Require(observed);Assert(observed==Healthy());return Task.CompletedTask;
        });
        var mutations=new Dictionary<string,InstallerReadinessObservation>
        {
            ["format"]=Healthy() with{Format=2},["installation"]=Healthy() with{InstallationId=new string('f',32)},
            ["challenge"]=Healthy() with{Nonce=new string('f',32)},["activation"]=Healthy() with{ActivationDigest=new string('f',64)},
            ["release"]=Healthy() with{ReleaseDigest=new string('f',64)},["unhealthy"]=Healthy() with{RuntimeHealthy=false},
            ["Authority PID"]=Healthy() with{AuthorityPid=56},["same Authority PID reused"]=Healthy() with{AuthorityCreated=67},
            ["Runtime PID"]=Healthy() with{RuntimePid=78},["same Runtime PID reused"]=Healthy() with{RuntimeCreated=89},
            ["missing runtime nonce"]=Healthy() with{RuntimeNonce=null},["noncanonical runtime nonce"]=Healthy() with{RuntimeNonce=new string('E',32)},
            ["runtime nonce GUID format"]=Healthy() with{RuntimeNonce=Guid.NewGuid().ToString("D")},["invalid runtime nonce"]=Healthy() with{RuntimeNonce=new string('g',32)}
        };
        foreach(var mutation in mutations)await check("installer readiness rejects "+mutation.Key,()=>
        {Denied(()=>Require(mutation.Value));return Task.CompletedTask;});
        await check("installer readiness rejects missing independently observed native generations",()=>
        {
            foreach(var value in new[]{(0,66L,77,88L),(55,0L,77,88L),(55,66L,0,88L),(55,66L,77,0L)})
                Denied(()=>InstallerReadiness.RequireObserved(Request(),Healthy(),Release.Id,value.Item1,value.Item2,value.Item3,value.Item4));
            return Task.CompletedTask;
        });
        await check("installer readiness validates its fresh request independently",()=>
        {
            foreach(var request in new[]{Request() with{Format=2},Request() with{Nonce=new string('B',32)},Request() with{ActivationDigest="invalid"},Request() with{InstallationId=new string('f',32)}})
                Denied(()=>InstallerReadiness.Require(request,Binding.InstallationId));
            return Task.CompletedTask;
        });
        await check("installer readiness lifecycle observes the actual admitted binding and release",async()=>
        {
            var session=new Session();var life=Lifecycle(session);
            Assert(life.ObserveInstallerReadiness(Binding,Release) is null);
            await life.StartCommittedAsync(Binding,Release);
            try
            {
                Assert(life.ObserveInstallerReadiness(Binding,Release)==session.Admission);
                Denied(()=>life.ObserveInstallerReadiness(Binding with{ActivationReceiptDigest=new string('f',64)},Release));
                Denied(()=>life.ObserveInstallerReadiness(Binding,new(new string('f',64))));
            }
            finally{await life.StopAsync();life.ReleaseStoppedResources();}
            Assert(life.ObserveInstallerReadiness(Binding,Release) is null&&session.Disposed);
        });
        await check("installer readiness lifecycle reauthenticates the retained generation before observation",async()=>
        {
            var session=new Session();var life=Lifecycle(session);await life.StartCommittedAsync(Binding,Release);
            try
            {
                session.IdentityCurrent=false;Denied(()=>life.ObserveInstallerReadiness(Binding,Release));session.IdentityCurrent=true;
                session.CreatedDelta=1;Denied(()=>life.ObserveInstallerReadiness(Binding,Release));session.CreatedDelta=0;
                session.Assigned=false;Denied(()=>life.ObserveInstallerReadiness(Binding,Release));session.Assigned=true;
                Assert(life.ObserveInstallerReadiness(Binding,Release)==session.Admission);
            }
            finally{session.IdentityCurrent=true;session.CreatedDelta=0;session.Assigned=true;await life.StopAsync();life.ReleaseStoppedResources();}
        });
        foreach(var uncertain in new[]{false,true})await check("forged HTTP ready cannot replace "+(uncertain?"recovery-required":"failed")+" Runtime admission",async()=>
        {
            var session=new Session{FailReady=true,CleanupUnavailable=uncertain};var life=Lifecycle(session);
            await DeniedAsync(()=>life.StartCommittedAsync(Binding,Release));
            Assert(life.Phase==(uncertain?"recovery_required":"failed")&&life.ObserveInstallerReadiness(Binding,Release) is null);
            // Even plausible HTTP JSON and positive-looking process fields are
            // insufficient when the actual retained Runtime is not healthy.
            var forgedHttp=JsonSerializer.Deserialize<JsonElement>("{\"ok\":true,\"state\":\"ready\"}");
            Assert(forgedHttp.GetProperty("ok").GetBoolean());Denied(()=>Require(Healthy() with{RuntimeHealthy=false}));
            if(uncertain){Assert(!session.Disposed);session.CleanupUnavailable=false;await life.RetryCleanupAsync();}
            Assert(session.Disposed&&life.ObserveInstallerReadiness(Binding,Release) is null);
        });
        await check("consumed installer challenge preserves prior-generation evidence across ordinary reboot and changed activation",async()=>
        {
            var f=new PublicationFixture();var request=f.Challenge();var session=new Session();var life=Lifecycle(session);
            await life.StartCommittedAsync(f.Binding,Release);
            try
            {
                InstallerReadiness.Publish(f.Files,f.Anchor,life);var observed=f.Read(request);
                using var process=System.Diagnostics.Process.GetCurrentProcess();
                InstallerReadiness.RequireObserved(request,observed,Release.Id,process.Id,process.StartTime.ToFileTimeUtc(),77,88);
                Assert(observed.AuthorityCreated==process.StartTime.ToFileTimeUtc());
                // A previous actual generation would have different process
                // fields. Existing completed bytes must never be rewritten.
                var prior=observed with{AuthorityPid=observed.AuthorityPid+1,AuthorityCreated=observed.AuthorityCreated+1,RuntimeCreated=89};
                var bytes=JsonSerializer.SerializeToUtf8Bytes(prior);File.WriteAllBytes(f.Output(request),bytes);
                session.IdentityCurrent=false;InstallerReadiness.Publish(f.Files,f.Anchor,life);
                Assert(File.ReadAllBytes(f.Output(request)).AsSpan().SequenceEqual(bytes));
                f.SetActivation(2,new(new string('f',64)));InstallerReadiness.Publish(f.Files,f.Anchor,life);
                Assert(File.ReadAllBytes(f.Output(request)).AsSpan().SequenceEqual(bytes));
            }
            finally{session.IdentityCurrent=true;await life.StopAsync();life.ReleaseStoppedResources();}
        });
        await check("stale unconsumed installer challenge cannot attest a later activation while a fresh nonce can",async()=>
        {
            var f=new PublicationFixture();var stale=f.Challenge();var old=f.Binding;
            var next=new AppRelease(new string('f',64));f.SetActivation(2,next);
            var session=new Session();var life=Lifecycle(session);await life.StartCommittedAsync(f.Binding,next);
            try
            {
                InstallerReadiness.Publish(f.Files,f.Anchor,life);Assert(!File.Exists(f.Output(stale)));
                var fresh=f.Challenge();Assert(fresh.Nonce!=stale.Nonce&&fresh.ActivationDigest!=old.ActivationReceiptDigest);
                InstallerReadiness.Publish(f.Files,f.Anchor,life);var observed=f.Read(fresh);
                using var process=System.Diagnostics.Process.GetCurrentProcess();
                InstallerReadiness.RequireObserved(fresh,observed,next.Id,process.Id,process.StartTime.ToFileTimeUtc(),77,88);
                Denied(()=>InstallerReadiness.RequireObserved(stale,observed,next.Id,process.Id,process.StartTime.ToFileTimeUtc(),77,88));
                Assert(!File.Exists(f.Output(stale)));
            }
            finally{await life.StopAsync();life.ReleaseStoppedResources();}
        });
        await check("private installer publication reports actual unready Runtime despite plausible HTTP success",()=>
        {
            var f=new PublicationFixture();var request=f.Challenge();var life=Lifecycle(new Session());
            InstallerReadiness.Publish(f.Files,f.Anchor,life);var observed=f.Read(request);
            Assert(!observed.RuntimeHealthy&&observed.RuntimePid==0&&observed.RuntimeCreated==0&&observed.RuntimeNonce is null);
            using var process=System.Diagnostics.Process.GetCurrentProcess();
            Denied(()=>InstallerReadiness.RequireObserved(request,observed,Release.Id,process.Id,process.StartTime.ToFileTimeUtc(),77,88));
            return Task.CompletedTask;
        });
    }
    private static void Require(InstallerReadinessObservation observed)=>InstallerReadiness.RequireObserved(Request(),observed,Release.Id,55,66,77,88);
    private static void Assert(bool value){if(!value)throw new Exception("INSTALLER_READINESS_ASSERTION");}
    private static void Denied(Action action)
    {try{action();}catch(Exception failure)when(failure is IOException or UnauthorizedAccessException){return;}throw new Exception("INSTALLER_READINESS_UNEXPECTED_SUCCESS");}
    private static async Task DeniedAsync(Func<Task> action)
    {try{await action();}catch(Exception failure)when(failure is IOException or UnauthorizedAccessException){return;}throw new Exception("INSTALLER_READINESS_UNEXPECTED_SUCCESS");}
    private static WindowsScmRuntimeLifecycle Lifecycle(Session session)=>new(()=>session,TimeSpan.FromSeconds(1),TimeSpan.FromMilliseconds(100),()=>RuntimeNonce);
    private sealed class PublicationFixture
    {
        internal InstalledStateFiles Files{get;}
        internal InstalledAuthorityAnchor Anchor{get;}
        internal InstallationBinding Binding=null!;
        internal PublicationFixture()
        {
            var root=SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"yahad-installer-readiness"));Files=InstalledStateFiles.Private(root);
            Files.EnsureDirectory(Files.PathOf(".authority"));
            Anchor=new(1,Guid.NewGuid().ToString("N"),InstalledUpdaterLayout.RuntimeServiceName,InstalledUpdaterLayout.AuthorityServiceName,
                "S-1-5-21-1-2-3-1001",new string('1',64),new string('2',64));
            SetActivation(1,Release);
        }
        internal void SetActivation(long epoch,AppRelease release)
        {
            var activation=new InstalledActivation(1,Anchor.InstallationId,epoch,release.Id,new string('3',32),6,6,
                new string('4',64),new string('5',64),new string('6',64),new string('7',64),new string('8',64),"committed",null);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(activation);File.WriteAllBytes(Files.PathOf("activation.json"),bytes);
            Binding=new(Anchor.InstallationId,InstalledActivationResolver.Hash(bytes));
        }
        internal InstallerReadinessRequest Challenge()
        {
            var value=new InstallerReadinessRequest(1,Anchor.InstallationId,Guid.NewGuid().ToString("N"),Binding.ActivationReceiptDigest);
            File.WriteAllBytes(Files.PathOf(InstallerReadiness.RequestFile),JsonSerializer.SerializeToUtf8Bytes(value));return value;
        }
        internal string Output(InstallerReadinessRequest request)=>Files.PathOf(".authority/install-ready-"+request.Nonce+".json");
        internal InstallerReadinessObservation Read(InstallerReadinessRequest request)=>NativeJson.Read<InstallerReadinessObservation>(Files.Read(Output(request),2048));
    }
    // Same retained-session contract as RuntimeAdmissionTests, kept local
    // because that fixture is private. No native services, tokens or jobs run.
    private sealed class Session:IRetainedScmRuntimeSession,IRuntimeGateKernel
    {
        internal bool FailReady,CleanupUnavailable,Disposed,Assigned,IdentityCurrent=true;
        internal long CreatedDelta;internal RuntimeAdmissionBinding Admission=null!;private bool exited;
        private readonly TaskCompletionSource exit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IRuntimeGateKernel Kernel=>this;
        public Task<RuntimeAdmissionBinding> StartWaitingAsync(RuntimeStartIntent intent,CancellationToken cancellation)
        {cancellation.ThrowIfCancellationRequested();Admission=new(intent.Binding,intent.ReleaseDigest,intent.Nonce,77,88);return Task.FromResult(Admission);}
        public void RequireAuthenticatedCurrent(RuntimeAdmissionBinding admission)
        {if(!IdentityCurrent||admission!=Admission)throw new UnauthorizedAccessException("SYNTHETIC_RUNTIME_IDENTITY");}
        public Task ReleaseLaunchAsync(RuntimeAdmissionBinding admission,CancellationToken cancellation)
        {RequireAuthenticatedCurrent(admission);Assert(Assigned);return Task.CompletedTask;}
        public Task<RuntimeAdmissionBinding> WaitReadyAsync(CancellationToken cancellation)
        {cancellation.ThrowIfCancellationRequested();if(FailReady)throw new IOException("SYNTHETIC_RUNTIME_NOT_READY");return Task.FromResult(Admission);}
        public Task WaitForExitAsync(CancellationToken cancellation)=>exit.Task.WaitAsync(cancellation);
        public Task StopAsync(CancellationToken cancellation){exited=true;exit.TrySetResult();return Task.CompletedTask;}
        public void RequireQuiescence(){if(!exited||CleanupUnavailable)throw new IOException("SYNTHETIC_RUNTIME_TREE_UNCONFIRMED");}
        public void Dispose(){RequireQuiescence();Disposed=true;}
        public RuntimeGeneration ObserveHost()=>new(Admission.Binding.InstallationId,Admission.ReleaseDigest,Admission.Nonce,Admission.ProcessId,Admission.Created+CreatedDelta);
        public void AssignWaitingHost(RuntimeGeneration generation){Assigned=true;}
        public bool InOwnedJob(RuntimeGeneration generation)=>Assigned;
        public bool HostExited(RuntimeGeneration generation)=>exited;
        public bool OwnedTreeEmpty()=>exited&&!CleanupUnavailable;
    }
}

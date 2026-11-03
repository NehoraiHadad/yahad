using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Yahad.Install;
using Yahad.Service;
using Yahad.Authority;

// In-memory native adapters only. No token, AppContainer profile, ACL, pipe or
// installed SCM operation is created by these cases.
internal static class OfflineMigrationRuntimeTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("initial full enrollment creates only the fixed zero-capability current-Runtime profile",()=>
        {
            var kernel=new ProfileKernel();var sid=InstalledMigrationProfile.EnsureForCurrentRuntime(new('a',32),new('b',64),kernel);
            Assert(sid==kernel.Sid&&kernel.CreatedName==NativeMigrationSandbox.ProfileName(new('a',32))&&kernel.Capabilities==0&&kernel.Creates==1&&kernel.ScopeReads==1);
            Assert(kernel.Order.SequenceEqual(new[]{"identity","derive","loopback","identity","create","scope","identity","derive","loopback"}));return Task.CompletedTask;
        });
        await check("existing migration profile requires matching derived SID and current-user folder evidence",()=>
        {
            var kernel=new ProfileKernel{CreateResult=InstalledMigrationProfile.AlreadyExists,CreatedSid=null};
            Assert(InstalledMigrationProfile.EnsureForCurrentRuntime(new('a',32),new('b',64),kernel)==kernel.Sid&&kernel.Creates==1&&kernel.ScopeReads==1);return Task.CompletedTask;
        });
        foreach(var fault in new[]{"installation","pin","uppercase-pin"})
            await check("migration profile rejects invalid "+fault+" before querying or creating native state",async()=>
            {
                var kernel=new ProfileKernel();await Reject(()=>{_ =InstalledMigrationProfile.EnsureForCurrentRuntime(fault=="installation"?"../other":new('a',32),fault=="pin"?"":fault=="uppercase-pin"?new('B',64):new('b',64),kernel);return Task.CompletedTask;});
                Assert(kernel.Order.Count==0&&kernel.Creates==0);
            });
        foreach(var fault in new[]{"process","token","admin","privilege","interactive","thread"})
            await check("migration profile cannot create under a spoofed "+fault+" identity",async()=>
            {
                var kernel=new ProfileKernel();var identity=kernel.Runtime;
                kernel.Runtime=fault switch
                {
                    "process"=>identity with{ProcessSid="S-1-5-18"},"token"=>identity with{Token=identity.Token with{UserSid="S-1-5-18"}},
                    "admin"=>identity with{Token=identity.Token with{Administrator=true}},"privilege"=>identity with{Token=identity.Token with{Privileges=["SeChangeNotifyPrivilege","SeDebugPrivilege"]}},
                    "interactive"=>identity with{Token=identity.Token with{ServiceLogon=false}},"thread"=>identity with{ThreadTokenPresent=true},_=>throw new Exception()
                };
                await Reject(()=>{_ =InstalledMigrationProfile.EnsureForCurrentRuntime(new('a',32),new('b',64),kernel);return Task.CompletedTask;});Assert(kernel.Creates==0&&kernel.ScopeReads==0);
            });
        foreach(var denied in new[]{InstalledMigrationProfile.AccessDenied,unchecked((int)0x80004005)})
            await check("migration profile native creation failure has no alternate-account retry "+denied,async()=>
            {
                var kernel=new ProfileKernel{CreateResult=denied,CreatedSid=null};
                try{_ =InstalledMigrationProfile.EnsureForCurrentRuntime(new('a',32),new('b',64),kernel);throw new Exception("PROFILE_FAILURE_IGNORED");}
                catch(IOException error){Assert(error.Message==(denied==InstalledMigrationProfile.AccessDenied?"MIGRATION_PROFILE_CREATE_DENIED":"MIGRATION_PROFILE_CREATE_FAILED"));}
                Assert(kernel.Creates==1&&kernel.ScopeReads==0);await Task.CompletedTask;
            });
        foreach(var sid in new string?[]{null,"S-1-15-2-9-8-7-6-5-4-3"})
            await check("migration profile success cannot accept a missing or substituted native SID "+sid,async()=>
            {
                var kernel=new ProfileKernel{CreatedSid=sid};await Reject(()=>{_ =InstalledMigrationProfile.EnsureForCurrentRuntime(new('a',32),new('b',64),kernel);return Task.CompletedTask;});
                Assert(kernel.Creates==1&&kernel.ScopeReads==0);
            });
        foreach(var fault in new[]{"user-profile-missing","container-missing","reparse","other-user","outside-user-root","system-profile","relative"})
            await check("existing migration profile refuses "+fault+" scope without repairing it",async()=>
            {
                var kernel=new ProfileKernel{CreateResult=InstalledMigrationProfile.AlreadyExists,CreatedSid=null};var scope=kernel.Scope;
                var system=Path.Combine(Path.GetPathRoot(scope.ProfileRoot)!,"Windows","System32","config","systemprofile");
                kernel.Scope=fault switch
                {
                    "user-profile-missing"=>scope with{ProfileExists=false},"container-missing"=>scope with{ContainerExists=false},"reparse"=>scope with{ReparseFree=false},
                    "other-user"=>scope with{UserSid="S-1-5-18"},"outside-user-root"=>scope with{ContainerRoot=Path.Combine(Path.GetTempPath(),"another-profile","container")},
                    "system-profile"=>scope with{ProfileRoot=system,ContainerRoot=Path.Combine(system,"AppData","Local","Packages","container")},"relative"=>scope with{ProfileRoot="relative"},_=>throw new Exception()
                };
                await Reject(()=>{_ =InstalledMigrationProfile.EnsureForCurrentRuntime(new('a',32),new('b',64),kernel);return Task.CompletedTask;});Assert(kernel.Creates==1&&kernel.ScopeReads==1);
            });
        foreach(var changedAt in new[]{2,3})
            await check("migration profile rejects changed Runtime identity at boundary "+changedAt,async()=>
            {
                var kernel=new ProfileKernel{ChangeIdentityAt=changedAt};await Reject(()=>{_ =InstalledMigrationProfile.EnsureForCurrentRuntime(new('a',32),new('b',64),kernel);return Task.CompletedTask;});
                Assert(kernel.Creates==(changedAt==2?0:1));
            });
        foreach(var changedAt in new[]{1,2})
            await check("migration profile rejects loopback exemption at boundary "+changedAt,async()=>
            {
                var kernel=new ProfileKernel{ExemptAt=changedAt};await Reject(()=>{_ =InstalledMigrationProfile.EnsureForCurrentRuntime(new('a',32),new('b',64),kernel);return Task.CompletedTask;});
                Assert(kernel.Creates==(changedAt==1?0:1));
            });
        await check("migration profile cannot derive the shared all-applications SID",async()=>
        {
            var kernel=new ProfileKernel{Sid="S-1-15-2-1"};await Reject(()=>{_ =InstalledMigrationProfile.EnsureForCurrentRuntime(new('a',32),new('b',64),kernel);return Task.CompletedTask;});Assert(kernel.Creates==0);
        });
        await check("migration profile derivation must remain fixed after native creation",async()=>
        {
            var kernel=new ProfileKernel{ChangeDerivedSid=true};await Reject(()=>{_ =InstalledMigrationProfile.EnsureForCurrentRuntime(new('a',32),new('b',64),kernel);return Task.CompletedTask;});Assert(kernel.Creates==1);
        });
        foreach(var when in new[]{"during-release","after-unknown"})
            await check("original host exit "+when+" retains cleanup then permits fresh offline proof without release retry",async()=>
            {
                var binding=Binding();var session=new ProbationRecoveryRuntime(binding){ExitDuringRelease=when=="during-release"};var life=new WindowsScmRuntimeLifecycle(()=>session);
                await life.StartProbationAsync(binding);await Reject(()=>life.ReleaseEffectsAsync(binding,new('1',32),default));if(when=="after-unknown")session.NaturalExit();
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(1));while(life.Phase!="recovery_required")await Task.Delay(1,deadline.Token);
                Assert(life.OwnsRuntimeResources&&!session.Disposed&&session.ReleaseCalls==1);await Reject(()=>life.ReleaseEffectsAsync(binding,new('2',32),default));
                await life.RetryCleanupAsync();Assert(session.Disposed&&!life.OwnsRuntimeResources&&life.Phase=="failed"&&session.ReleaseCalls==1);
                var fresh=new ProbationRecoveryRuntime(binding){AllowOffline=true};var offline=new WindowsScmRuntimeLifecycle(()=>fresh);
                await offline.PrepareOfflineRecoveryAsync(binding.Binding,new(binding.ReleaseDigest));offline.RetainedGate.RequireQuiescence();RequireFakeCapture(offline,fresh);
                Assert(fresh.RecoveryRetains==1&&fresh.ReleaseCalls==0&&fresh.Exited&&fresh.Empty&&offline.Phase=="offline_recovery_ready");offline.ReleaseStoppedResources();Assert(fresh.Disposed);
            });
        await check("lost effects ACK busy reply never permits a forced stop or release retry",async()=>
        {
            var binding=Binding();var session=new ProbationRecoveryRuntime(binding);var life=new WindowsScmRuntimeLifecycle(()=>session);
            await life.StartProbationAsync(binding);await Reject(()=>life.ReleaseEffectsAsync(binding,new('1',32),default));
            Assert(life.Phase=="effects_outcome_unknown"&&session.ReleaseCalls==1);RequireFakeCapture(life,session);
            Assert(!await life.PrepareUpdateAsync(new('2',32),default));await Reject(()=>life.StopAsync());
            Assert(life.Phase=="effects_outcome_unknown"&&session.Stops==0&&!session.Exited);
            await Reject(()=>life.ReleaseEffectsAsync(binding,new('3',32),default));Assert(session.ReleaseCalls==1);
            await FinishUnknown(life,session);
        });
        await check("lost effects ACK idle contracts only the same generation then proves actual stopped job",async()=>
        {
            var binding=Binding();var session=new ProbationRecoveryRuntime(binding){Idle=true};var life=new WindowsScmRuntimeLifecycle(()=>session);
            await life.StartProbationAsync(binding);await Reject(()=>life.ReleaseEffectsAsync(binding,new('1',32),default));RequireFakeCapture(life,session);
            Assert(await life.PrepareUpdateAsync(new('2',32),default)&&life.Phase=="effects_outcome_unknown");
            await life.StopAsync();life.RetainedGate.RequireQuiescence();Assert(session.Stops==1&&session.Exited&&session.Empty&&life.Phase=="stopped");
            Assert(session.Order.IndexOf("capture")<session.Order.IndexOf("prepare")&&session.Order.IndexOf("prepare")<session.Order.IndexOf("stop"));
            life.ReleaseStoppedResources();Assert(session.Disposed&&session.ReleaseCalls==1);
        });
        await check("lost effects ACK changed generation cannot be captured prepared or stopped",async()=>
        {
            var binding=Binding();var session=new ProbationRecoveryRuntime(binding){Idle=true};var life=new WindowsScmRuntimeLifecycle(()=>session);
            await life.StartProbationAsync(binding);await Reject(()=>life.ReleaseEffectsAsync(binding,new('1',32),default));
            Assert(await life.PrepareUpdateAsync(new('2',32),default));session.Changed=true;
            await Reject(()=>{_ =life.AcquireRecoveryQuiescence();return Task.CompletedTask;});await Reject(()=>life.StopAsync());
            await Reject(()=>life.PrepareUpdateAsync(new('3',32),default));Assert(session.Captures==0&&session.Stops==0&&life.Phase=="effects_outcome_unknown");
            session.Changed=false;await FinishUnknown(life,session);
        });
        await check("unknown effects cancellation removes its idle contraction without claiming health",async()=>
        {
            var binding=Binding();var session=new ProbationRecoveryRuntime(binding){Idle=true};var life=new WindowsScmRuntimeLifecycle(()=>session);
            await life.StartProbationAsync(binding);await Reject(()=>life.ReleaseEffectsAsync(binding,new('1',32),default));
            Assert(await life.PrepareUpdateAsync(new('2',32),default));await life.CancelUpdateAsync(new('2',32),default);await Reject(()=>life.StopAsync());
            Assert(session.Cancels==1&&session.Stops==0&&life.Phase=="effects_outcome_unknown");await FinishUnknown(life,session);
        });
        await check("unknown effects idle reply cannot bless a generation changed during that reply",async()=>
        {
            var binding=Binding();var session=new ProbationRecoveryRuntime(binding){Idle=true,ChangeDuringPrepare=true};var life=new WindowsScmRuntimeLifecycle(()=>session);
            await life.StartProbationAsync(binding);await Reject(()=>life.ReleaseEffectsAsync(binding,new('1',32),default));
            await Reject(()=>life.PrepareUpdateAsync(new('2',32),default));await Reject(()=>life.StopAsync());Assert(session.Stops==0&&life.Phase=="effects_outcome_unknown");
            session.Changed=false;session.ChangeDuringPrepare=false;await FinishUnknown(life,session);
        });
        await check("migration package ACL and Low MIC are confined to new shadow clone and scratch scopes",async()=>
        {
            var spec=Spec();var kernel=new ScopeKernel(spec);var helper=ScopeHelper(spec,kernel);var identities=kernel.ActorReads;await helper.GrantAsync(spec,default);Assert(kernel.ActorReads==identities+1);
            var items=helper.Inventory(spec,default);Assert(items.All(x=>kernel.Mutations.Any(m=>m.Path==x.Path)));
            foreach(var item in items)
            {
                var descriptor=kernel.Read(item);Assert(NativeMigrationScopeAccess.Integrity(descriptor)==(item.PackageWrite?"S-1-16-4096":"S-1-16-8192"));
                Assert(PackageMask(descriptor,spec.ExpectedAppContainerSid)==(item.PackageWrite?0x1301bf:0x1200a9));
            }
            Assert(!kernel.Mutations.Any(m=>m.Path==spec.ControlRoot||m.Path==spec.SourceDataDirectory||m.Path==spec.CloneRoot||m.Path.Contains("identity",StringComparison.Ordinal)));
            var request=items.Single(x=>x.Path==spec.RequestFile);Assert(!request.PackageWrite&&(PackageMask(kernel.Read(request),spec.ExpectedAppContainerSid)&0x10040)==0);
        });
        await check("migration scope refuses an unlisted or unenrolled helper actor before mutating any ACL",async()=>
        {
            foreach(var actor in new[]{UpdaterServicePrincipals.RecoveryA.Value,"S-1-5-80-9-9-9-9-9"})
            {
                var spec=Spec();var kernel=new ScopeKernel(spec);var helper=ScopeHelper(spec,kernel);kernel.Actor=actor;
                await Reject(()=>helper.GrantAsync(spec,default));Assert(kernel.Mutations.Count==0);
            }
        });
        await check("migration scope normalization handles inherited Runtime-owned output and removes package and security rights",async()=>
        {
            var spec=Spec();var kernel=new ScopeKernel(spec);var helper=ScopeHelper(spec,kernel);await helper.GrantAsync(spec,default);
            kernel.Objects.Add((spec.ResultFile,false));var created=new MigrationScopeObject(spec.ResultFile,false,true,InstalledFileRole.Control);
            var inherited=helper.Descriptor(created,spec,true,true);inherited.Owner=new SecurityIdentifier(spec.ExpectedRuntimeSid);
            foreach(GenericAce raw in inherited.DiscretionaryAcl!)raw.AceFlags|=AceFlags.Inherited;kernel.Descriptors[spec.ResultFile]=inherited;
            await helper.NormalizeAsync(spec,default);await helper.NormalizeAsync(spec,default);
            foreach(var item in helper.Inventory(spec,default))
            {
                var descriptor=kernel.Read(item);Assert(descriptor.Owner?.Value==spec.ExpectedAuthoritySid&&NativeMigrationScopeAccess.Integrity(descriptor)=="S-1-16-8192");
                Assert(PackageMask(descriptor,spec.ExpectedAppContainerSid)==0&&(PackageMask(descriptor,spec.ExpectedAuthoritySid)&0xc0000)==0);
            }
        });
        await check("fixed helper B normalizes helper A's interrupted bounded workspace without security rights remaining",async()=>
        {
            var spec=Spec();var kernel=new ScopeKernel(spec){Actor=UpdaterServicePrincipals.RecoveryA.Value};
            var roles=ScopeRoles(spec).WithRecoveryHelpers();var helper=new NativeMigrationScopeAccess(spec.ControlRoot,roles,kernel);
            foreach(var item in helper.Inventory(spec,default))kernel.Descriptors[item.Path]=helper.Descriptor(item,spec,false,false);
            await helper.GrantAsync(spec,default);kernel.Actor=UpdaterServicePrincipals.RecoveryB.Value;
            foreach(var item in helper.Inventory(spec,default))Assert((PackageMask(kernel.Read(item),kernel.Actor)&0xc0000)==0xc0000);
            await helper.NormalizeAsync(spec,default);await helper.NormalizeAsync(spec,default);
            foreach(var item in helper.Inventory(spec,default))
            {
                var descriptor=kernel.Read(item);Assert(descriptor.Owner?.Value==kernel.Actor);
                foreach(var sid in roles.UpdaterActors)Assert((PackageMask(descriptor,sid.Value)&0xc0000)==0);
                Assert(PackageMask(descriptor,spec.ExpectedAppContainerSid)==0);
            }
            Assert(!kernel.Mutations.Any(m=>m.Path==spec.ControlRoot||m.Path==spec.SourceDataDirectory||m.Path==spec.CloneRoot));
        });
        await check("helper B skips A's already-final normalized members after interruption",async()=>
        {
            var spec=Spec();var kernel=new ScopeKernel(spec){Actor=UpdaterServicePrincipals.RecoveryA.Value,EnforceSecurity=true};
            var roles=ScopeRoles(spec).WithRecoveryHelpers();var helper=new NativeMigrationScopeAccess(spec.ControlRoot,roles,kernel);
            var items=helper.Inventory(spec,default);foreach(var item in items)kernel.Descriptors[item.Path]=helper.Descriptor(item,spec,false,false);
            await helper.GrantAsync(spec,default);
            foreach(var item in items.Take(items.Count/2))kernel.Descriptors[item.Path]=helper.Descriptor(item,spec,false,false);
            kernel.Actor=UpdaterServicePrincipals.RecoveryB.Value;await helper.NormalizeAsync(spec,default);
            var writes=kernel.Mutations.Count;await helper.NormalizeAsync(spec,default);Assert(writes==kernel.Mutations.Count);
            foreach(var item in items)foreach(var actor in roles.UpdaterActors)Assert((PackageMask(kernel.Read(item),actor.Value)&0xc0000)==0);
        });
        await check("interrupted new-scope label publication can normalize without touching source or control",async()=>
        {
            var spec=Spec();var kernel=new ScopeKernel(spec){FailLabelPath=spec.DataDirectory};var helper=ScopeHelper(spec,kernel);
            await Reject(()=>helper.GrantAsync(spec,default));Assert(kernel.Mutations.Count>0);await helper.NormalizeAsync(spec,default);
            Assert(helper.Inventory(spec,default).All(x=>PackageMask(kernel.Read(x),spec.ExpectedAppContainerSid)==0));
        });
        await check("migration ACL inventory refuses an injected source object before any ACL mutation",async()=>
        {
            var spec=Spec();var kernel=new ScopeKernel(spec){Outside=spec.SourceDataDirectory};var helper=new NativeMigrationScopeAccess(spec.ControlRoot,ScopeRoles(spec),kernel);
            await Reject(()=>helper.GrantAsync(spec,default));Assert(kernel.Mutations.Count==0);
        });
        await check("migration normalization refuses broadened package write access on read-only payload",async()=>
        {
            var spec=Spec();var kernel=new ScopeKernel(spec);var helper=ScopeHelper(spec,kernel);await helper.GrantAsync(spec,default);
            var descriptor=kernel.Descriptors[spec.NodePath];foreach(GenericAce raw in descriptor.DiscretionaryAcl!)if(raw is CommonAce ace&&ace.SecurityIdentifier.Value==spec.ExpectedAppContainerSid)ace.AccessMask=0x1301bf;
            var count=kernel.Mutations.Count;await Reject(()=>helper.NormalizeAsync(spec,default));Assert(kernel.Mutations.Count==count);
        });
        await check("instant finite migration retains SCM lifecycle until delayed started and completed acknowledgements",async()=>
        {
            var spec=Spec();var child=new Kernel(spec).Child;var binding=new RuntimeAdmissionBinding(new(spec.Plan.InstallationId,new('8',64)),spec.Plan.PayloadDigest,spec.Plan.OperationId,321,654);
            var channel=new DelayedPurpose();var session=new InstalledOfflineMigrationStart.Session(child,binding,channel);
            var controller=new ServiceController(new FiniteAuthorizer(new SessionLauncher(session),ServiceRunPurpose.FiniteOfflineTask));var reports=new List<ServiceReport>();
            var running=controller.RunAsync(reports.Add);await channel.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert(!running.IsCompleted&&child.RootExited&&!child.Disposed);channel.StartedAck.TrySetResult();
            await channel.Completed.Task.WaitAsync(TimeSpan.FromSeconds(1));Assert(!running.IsCompleted&&!child.Disposed);channel.CompletedAck.TrySetResult();
            var result=await running.WaitAsync(TimeSpan.FromSeconds(1));Assert(result==new ServiceRunResult(ServiceFailure.None,true)&&child.Disposed);
            Assert(reports.Last().State==ServiceState.Stopped&&reports.Last().Win32ExitCode==0&&reports.Last().ServiceSpecificExitCode==0);
        });
        foreach(var purpose in new[]{ServiceRunPurpose.Continuous,ServiceRunPurpose.FiniteOfflineTask})
            await check("finite service completion does not hide nonzero child exit for "+purpose,async()=>
            {
                var child=new FiniteChild(7,false);var controller=new ServiceController(new FiniteAuthorizer(new DirectLauncher(child),purpose));
                var result=await controller.RunAsync(_=>{});Assert(result.Failure!=ServiceFailure.None&&result.QuiescenceConfirmed&&child.Disposed);
            });
        await check("finite migration cannot publish STOPPED when actual tree cleanup remains unknown",async()=>
        {
            var child=new FiniteChild(0,true);var controller=new ServiceController(new FiniteAuthorizer(new DirectLauncher(child),ServiceRunPurpose.FiniteOfflineTask));var reports=new List<ServiceReport>();
            var result=await controller.RunAsync(reports.Add);Assert(!result.QuiescenceConfirmed&&result.Failure==ServiceFailure.QuiescenceUnknown&&!reports.Any(x=>x.State==ServiceState.Stopped));
        });
        foreach(var kind in new[]{"migration-started","migration-completed"})
            await check("migration wire awaits authenticated delayed "+kind+" ACK",async()=>
            {
                var spec=Spec();var binding=new RuntimeAdmissionBinding(new(spec.Plan.InstallationId,new('8',64)),spec.Plan.PayloadDigest,spec.Plan.OperationId,321,654);
                var frame=kind=="migration-started"?new RuntimePurposeMessage(2,kind,binding.Nonce,binding,new(101,202,spec.ExpectedRuntimeSid,spec.ExpectedAppContainerSid,[],false,spec.Plan.EntryDigest,spec.NodeDigest)):
                    new RuntimePurposeMessage(2,kind,binding.Nonce,binding,ExitCode:0);
                using var stream=new AckStream();var observations=0;
                var exchange=RuntimePurposeWire.ReportAndWaitAsync(stream,frame,kind+"-accepted",()=>observations++,default);
                await stream.Written.Task.WaitAsync(TimeSpan.FromSeconds(1));Assert(!exchange.IsCompleted&&observations==1);
                using var ack=new MemoryStream();await RuntimePurposeWire.WriteAsync(ack,new(2,kind+"-accepted",binding.Nonce,binding),default);stream.Reply.TrySetResult(ack.ToArray());
                await exchange;Assert(observations==2);
            });
        await check("migration ACK rejects changed pinned generation",async()=>
        {
            var spec=Spec();var binding=new RuntimeAdmissionBinding(new(spec.Plan.InstallationId,new('8',64)),spec.Plan.PayloadDigest,spec.Plan.OperationId,321,654);
            using var stream=new AckStream();using var ack=new MemoryStream();await RuntimePurposeWire.WriteAsync(ack,new(2,"migration-completed-accepted",binding.Nonce,binding with{Created=655}),default);stream.Reply.SetResult(ack.ToArray());
            await Reject(()=>RuntimePurposeWire.ReportAndWaitAsync(stream,new(2,"migration-completed",binding.Nonce,binding,ExitCode:0),"migration-completed-accepted",()=>{},default));
        });
        await check("migration proxy waiting-host startup is bounded with no caller cancellation",async()=>
        {
            var spec=Spec();var runtime=new MigrationRuntime(spec){NeverStarts=true};
            await Reject(()=>new NativeOfflineMigrationProxy(new Reader(runtime.Binding),()=>runtime,TimeSpan.FromMilliseconds(20)).StartAsync(spec,default));
            Assert(runtime.StartupCancelled&&runtime.Disposed);
        });
        await check("migration purpose wire preserves exact generation and forbids mixed terminal bodies",async()=>
        {
            var spec=Spec();var runtime=new RuntimeAdmissionBinding(new(spec.Plan.InstallationId,new('8',64)),spec.Plan.PayloadDigest,spec.Plan.OperationId,321,654);
            var actor=new MigrationSandboxObservation(101,202,spec.ExpectedRuntimeSid,spec.ExpectedAppContainerSid,[],false,spec.Plan.EntryDigest,spec.NodeDigest);
            using var stream=new MemoryStream();var frame=new RuntimePurposeMessage(2,"migration-started",runtime.Nonce,runtime,actor);
            await RuntimePurposeWire.WriteAsync(stream,frame,default);stream.Position=0;var read=await RuntimePurposeWire.ReadAsync(stream,default);
            Assert(read.Admission==runtime&&read.Observation?.ProcessId==actor.ProcessId&&read.Kind==frame.Kind);
            await Reject(()=>RuntimePurposeWire.WriteAsync(new MemoryStream(),frame with{ExitCode=0},default));
            await Reject(()=>RuntimePurposeWire.WriteAsync(new MemoryStream(),frame with{Id=new('9',32)},default));
            await Reject(()=>RuntimePurposeWire.WriteAsync(new MemoryStream(),frame with{Kind="migration-completed"},default));
        });
        await check("SCM migration proxy assigns the waiting host before purpose release and retains outer job",async()=>
        {
            var spec=Spec();var runtime=new MigrationRuntime(spec);var proxy=new NativeOfflineMigrationProxy(new Reader(runtime.Binding),()=>runtime);
            var session=await proxy.StartAsync(spec,default);Assert(session.Observe().ProcessId==101&&runtime.Assigned&&runtime.Released&&!runtime.Disposed);
            Assert(runtime.Order.SequenceEqual(new[]{"waiting","assigned","release","observation","started-accepted"}));
            await Reject(()=>session.DisposeAsync().AsTask());Assert(await session.WaitForRootExitAsync(default)==0);
            await session.WaitForTreeEmptyAsync(default);await session.DisposeAsync();Assert(runtime.Disposed&&runtime.Exited&&runtime.Empty);
        });
        await check("SCM migration proxy unknown cleanup transfers exact retained owner and can reconcile",async()=>
        {
            var spec=Spec();var runtime=new MigrationRuntime(spec){FailObservation=true,FailStop=true};
            var session=await new NativeOfflineMigrationProxy(new Reader(runtime.Binding),()=>runtime).StartAsync(spec,default);
            await Reject(()=>{_ =session.Observe();return Task.CompletedTask;});Assert(!runtime.Disposed&&!runtime.Empty);
            var normalized=0;var pending=new MigrationOwnershipException(session,new IOException("FAKE_ADMISSION_FAILURE"),new IOException("FAKE_CLEANUP_FAILURE"),c=>{normalized++;return Task.CompletedTask;});
            runtime.FailStop=false;await pending.RetryCleanupAsync(default);Assert(runtime.Disposed&&normalized==1);
        });
        await check("SCM migration proxy rejects changed native token before returning an admitted child",async()=>
        {
            var spec=Spec();var runtime=new MigrationRuntime(spec);runtime.Actor=runtime.Actor with{CapabilitySids=["S-1-15-3-1"]};
            await Reject(()=>new NativeOfflineMigrationProxy(new Reader(runtime.Binding),()=>runtime).StartAsync(spec,default));Assert(runtime.Disposed&&runtime.Empty);
        });
        await check("waiting Runtime dispatches offline purpose before normal installed resolver",async()=>
        {
            var spec=Spec();var runtime=new MigrationRuntime(spec);var channel=new PurposeChannel(new(runtime.Binding,spec.Plan.PayloadDigest,spec.Plan.OperationId,321,654));
            var normal=new NeverInstalled();var calls=0;
            var authorizer=new WaitingRuntimeAuthorizer(normal,new Reader(runtime.Binding),channel,()=>new(321,654),(binding,purpose,c)=>
            {calls++;Assert(purpose=="offline-migrate");return Task.FromResult(new AuthorizedServiceStart(new(spec.Plan.PayloadDigest),new NeverLauncher(),new Barrier()));});
            using var grant=await authorizer.AuthorizeAsync(default);Assert(calls==1&&normal.Calls==0);
        });
        await check("offline migration observes restricted token before resume and retains its actual session",async()=>
        {
            var spec=Spec();var kernel=new Kernel(spec);var factory=new NativeMigrationSandbox(kernel);var owned=await factory.StartAsync(spec,default);
            Assert(kernel.Order.SequenceEqual(new[]{"create","observe","resume","observe"})&&!kernel.Child.Disposed);
            await owned.WaitForRootExitAsync(default);await owned.WaitForTreeEmptyAsync(default);await owned.DisposeAsync();Assert(kernel.Child.Disposed);
        });
        foreach(var fault in new[]{"runtime","package","capability","loopback","entry","node","pid","created"})
            await check("offline migration refuses native "+fault+" observation before resume",async()=>
            {
                var spec=Spec();var kernel=new Kernel(spec);kernel.Child.Observation=Fault(kernel.Child.Observation,fault);
                await Reject(()=>new NativeMigrationSandbox(kernel).StartAsync(spec,default));
                Assert(kernel.Child.Resumes==0&&kernel.Child.Disposed&&kernel.Child.RootExited&&kernel.Child.TreeEmpty);
            });
        await check("offline migration invalid fixed paths cannot create a process",async()=>
        {
            var spec=Spec();var kernel=new Kernel(spec);
            await Reject(()=>new NativeMigrationSandbox(kernel).StartAsync(spec with{EntryPath=Path.Combine(spec.PayloadRoot,"app","other.ts")},default));Assert(!kernel.Created);
            await Reject(()=>new NativeMigrationSandbox(kernel).StartAsync(spec with{SourceDataDirectory=spec.DataDirectory},default));Assert(!kernel.Created);
            await Reject(()=>new NativeMigrationSandbox(kernel).StartAsync(spec with{ResultFile=Path.Combine(spec.SourceDataDirectory,"result.json")},default));Assert(!kernel.Created);
        });
        await check("offline migration revocation before resume cleans the suspended owner",async()=>
        {
            var spec=Spec();var kernel=new Kernel(spec);using var revoked=new CancellationTokenSource();kernel.Child.OnObserve=()=>revoked.Cancel();
            await Reject(()=>new NativeMigrationSandbox(kernel).StartAsync(spec,revoked.Token));Assert(kernel.Child.Resumes==0&&kernel.Child.Disposed);
        });
        await check("offline migration failed resume cleans the exact root and descendants",async()=>
        {
            var spec=Spec();var kernel=new Kernel(spec);kernel.Child.FailResume=true;
            await Reject(()=>new NativeMigrationSandbox(kernel).StartAsync(spec,default));Assert(kernel.Child.Resumes==1&&kernel.Child.RootExited&&kernel.Child.TreeEmpty&&kernel.Child.Disposed);
        });
        await check("offline migration unknown failed-start cleanup transfers ownership for later retry",async()=>
        {
            var spec=Spec();var kernel=new Kernel(spec);kernel.Child.FailObservation=true;kernel.Child.FailCleanup=true;
            var owned=await new NativeMigrationSandbox(kernel).StartAsync(spec,default);await Reject(()=>{_ =owned.Observe();return Task.CompletedTask;});
            Assert(!kernel.Child.Disposed&&kernel.Child.Resumes==0);
            kernel.Child.FailCleanup=false;await owned.TerminateAndWaitForTreeEmptyAsync(default);await owned.DisposeAsync();Assert(kernel.Child.Disposed);
        });
        await check("offline migration post-resume observation failure cannot release resources before cleanup",async()=>
        {
            var spec=Spec();var kernel=new Kernel(spec);kernel.Child.FailSecondObservation=true;
            await Reject(()=>new NativeMigrationSandbox(kernel).StartAsync(spec,default));Assert(kernel.Child.Resumes==1&&kernel.Child.Disposed&&kernel.Child.TreeEmpty);
        });
        await check("offline purpose admission never invokes a normal web launcher and cannot replay",async()=>
        {
            var spec=Spec();var kernel=new Kernel(spec);var channel=new Admission(spec);var reader=new Reader(channel.Value.Runtime.Binding);var resolver=new Resolver(spec);
            var launcher=new WaitingOfflineMigrationLaunch(reader,()=>channel.Host,channel,resolver,new NativeMigrationSandbox(kernel));
            var owned=await launcher.StartAsync(default);Assert(resolver.Calls==1&&kernel.Child.Resumes==1&&channel.Checked>=2);
            await Reject(()=>launcher.StartAsync(default));await owned.TerminateAndWaitForTreeEmptyAsync(default);await owned.DisposeAsync();
        });
        foreach(var fault in new[]{"host","bootstrap","entry","nonce","target"})
            await check("offline purpose admission rejects changed "+fault+" before child creation",async()=>
            {
                var spec=Spec();var kernel=new Kernel(spec);var channel=new Admission(spec);var reader=new Reader(channel.Value.Runtime.Binding);var resolver=new Resolver(spec);
                if(fault=="host")resolver.After=()=>channel.Host=channel.Host with{Created=999};
                if(fault=="bootstrap")resolver.After=()=>reader.Value=reader.Value with{ActivationReceiptDigest=new('9',64)};
                if(fault=="entry")resolver.Spec=spec with{Plan=spec.Plan with{EntryDigest=new('9',64)}};
                if(fault=="nonce")resolver.Spec=spec with{Nonce=new('9',32)};
                if(fault=="target")resolver.Spec=spec with{Plan=spec.Plan with{NewDataId=new('9',32)}};
                await Reject(()=>new WaitingOfflineMigrationLaunch(reader,()=>channel.Host,channel,resolver,new NativeMigrationSandbox(kernel)).StartAsync(default));Assert(!kernel.Created);
            });
        await check("probation effects intent is durable before provider and HTTP release",async()=>
        {
            var order=new List<string>();var journal=new EffectsJournal(order);var runtime=new EffectsRuntime(order);var gate=new ProbationEffectsCoordinator(Binding(),journal,runtime);
            await gate.ReleaseAsync();Assert(order.SequenceEqual(new[]{"journal","release"})&&gate.Phase==ProbationEffectsPhase.Released&&gate.EffectsMayHaveBeenReleased);
            await Reject(()=>gate.ReleaseAsync());Assert(runtime.Calls==1);
        });
        await check("probation journal failure sends no release command",async()=>
        {
            var order=new List<string>();var journal=new EffectsJournal(order){Fail=true};var runtime=new EffectsRuntime(order);var gate=new ProbationEffectsCoordinator(Binding(),journal,runtime);
            await Reject(()=>gate.ReleaseAsync());Assert(runtime.Calls==0&&gate.Phase==ProbationEffectsPhase.FailedBeforeRelease&&!gate.EffectsMayHaveBeenReleased);
        });
        await check("probation lost release acknowledgement cannot authorize old-data rollback or retry",async()=>
        {
            var order=new List<string>();var runtime=new EffectsRuntime(order){LoseReply=true};var gate=new ProbationEffectsCoordinator(Binding(),new EffectsJournal(order),runtime);
            await Reject(()=>gate.ReleaseAsync());Assert(gate.EffectsMayHaveBeenReleased&&gate.Phase==ProbationEffectsPhase.OutcomeUnknown&&runtime.Calls==1);
            await Reject(()=>gate.ReleaseAsync());Assert(runtime.Calls==1);
        });
        await check("probation wire binds exact activation, operation and nonce",()=>
        {
            var b=Binding();var id=new string('7',32);
            var ready=JsonSerializer.SerializeToUtf8Bytes(new{type="probation-ready",nonce=b.Nonce,activationReceiptDigest=b.Binding.ActivationReceiptDigest,operationId=b.OperationId});
            ProbationRuntimeWire.Require(ready,b,"probation-ready");
            using var release=JsonDocument.Parse(ProbationRuntimeWire.Release(b,id));Assert(release.RootElement.GetProperty("id").GetString()==id);
            var ack=JsonSerializer.SerializeToUtf8Bytes(new{type="effects-released",id,nonce=b.Nonce,activationReceiptDigest=b.Binding.ActivationReceiptDigest,operationId=b.OperationId});
            ProbationRuntimeWire.Require(ack,b,"effects-released",id);return Task.CompletedTask;
        });
        foreach(var fault in new[]{"operation","activation","nonce","id","type","extra","duplicate"})
            await check("probation wire refuses spoofed "+fault+" acknowledgement",async()=>
            {
                var b=Binding();var id=new string('7',32);var fields=new Dictionary<string,object?>{{"type","effects-released"},{"id",id},{"nonce",b.Nonce},{"activationReceiptDigest",b.Binding.ActivationReceiptDigest},{"operationId",b.OperationId}};
                if(fault=="operation")fields["operationId"]=new string('9',32);if(fault=="activation")fields["activationReceiptDigest"]=new string('9',64);if(fault=="nonce")fields["nonce"]=new string('9',32);
                if(fault=="id")fields["id"]=new string('9',32);if(fault=="type")fields["type"]="ready";if(fault=="extra")fields["release"]=true;
                var bytes=JsonSerializer.SerializeToUtf8Bytes(fields);if(fault=="duplicate")bytes=System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(bytes).Replace("{","{\"id\":\""+id+"\","));
                await Reject(()=>{ProbationRuntimeWire.Require(bytes,b,"effects-released",id);return Task.CompletedTask;});
            });
    }
    private static ProbationRuntimeBinding Binding()=>new(new(new('a',32),new('b',64)),new('c',32),new('d',64),new('e',32),new('f',32));
    private static VerifiedMigrationSpec Spec()
    {
        var plan=new OfflineMigrationPlan(1,new('a',32),new('b',32),new('c',32),new('d',32),new('e',32),new('f',32),new('a',64),5,6,new('b',64),new('c',64),new('d',64),new('e',64),new('f',64));
        var root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"synthetic-migration-scope"));var work=Path.Combine(root,"migration-staging",plan.OperationId);var payload=Path.Combine(work,"payload");
        var clone=Path.Combine(root,"datasets",plan.SourceDataId,"restore-"+plan.CloneOperationId,"completed");
        return new(plan,Path.Combine(payload,"runtime","node","node.exe"),new('a',64),Path.Combine(payload,"app","server","native-migrate-v1.mjs"),payload,clone,Path.Combine(clone,"data"),
            Path.Combine(work,"work","request.json"),Path.Combine(work,"work","result.json"),Path.Combine(work,"temp"),"S-1-5-80-1-2-3-4-5","S-1-15-2-1-2-3-4-5-6-7",new('1',32),Path.Combine(root,"datasets",plan.SourceDataId,"data"),root,"S-1-5-80-5-4-3-2-1");
    }
    private static MigrationSandboxObservation Fault(MigrationSandboxObservation value,string kind)=>kind switch
    {
        "runtime"=>value with{RuntimeSid="S-1-5-18"},"package"=>value with{AppContainerSid="S-1-15-2-1"},"capability"=>value with{CapabilitySids=["S-1-15-3-1"]},
        "loopback"=>value with{LoopbackExempt=true},"entry"=>value with{EntryDigest=new('9',64)},"node"=>value with{NodeDigest=new('9',64)},"pid"=>value with{ProcessId=0},"created"=>value with{Created=0},_=>throw new Exception()
    };
    private static async Task Reject(Func<Task> action)
    {try{await action();}catch(Exception e)when(e is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException){return;}throw new Exception("MIGRATION_UNEXPECTED_ADMISSION");}
    private static void Assert(bool value){if(!value)throw new Exception("MIGRATION_RUNTIME_ASSERTION");}
    private static void RequireFakeCapture(WindowsScmRuntimeLifecycle lifecycle,ProbationRecoveryRuntime session)
    {
        // The fake deliberately CANNOT manufacture RetainedRuntimeQuiescence.
        // Verify that authentication reached the capture boundary; native
        // handle duplication and RequireAsync remain Windows acceptance gates.
        try{_ =lifecycle.AcquireRecoveryQuiescence();throw new Exception("FAKE_NATIVE_PROOF_CREATED");}
        catch(IOException error)when(error.Message=="FAKE_NATIVE_PROOF_NOT_CREATED"){Assert(session.Captures==1);}
    }
    private static async Task FinishUnknown(WindowsScmRuntimeLifecycle life,ProbationRecoveryRuntime session)
    {session.Idle=true;Assert(await life.PrepareUpdateAsync(new('f',32),default));await life.StopAsync();life.RetainedGate.RequireQuiescence();life.ReleaseStoppedResources();}
    private static InstalledFileRoles ScopeRoles(VerifiedMigrationSpec spec)=>new(new SecurityIdentifier(spec.ExpectedAuthoritySid),new SecurityIdentifier(spec.ExpectedRuntimeSid));
    private static NativeMigrationScopeAccess ScopeHelper(VerifiedMigrationSpec spec,ScopeKernel kernel)
    {
        var helper=new NativeMigrationScopeAccess(spec.ControlRoot,ScopeRoles(spec),kernel);
        foreach(var item in helper.Inventory(spec,default))kernel.Descriptors[item.Path]=helper.Descriptor(item,spec,false,false);return helper;
    }
    private static int PackageMask(RawSecurityDescriptor descriptor,string sid)
    {var result=0;foreach(GenericAce raw in descriptor.DiscretionaryAcl!)if(raw is CommonAce ace&&ace.SecurityIdentifier.Value==sid)result|=ace.AccessMask;return result;}
    private sealed class ScopeKernel:IMigrationScopeAclKernel
    {
        private readonly VerifiedMigrationSpec spec;internal readonly List<(string Path,bool Directory)> Objects=[];
        internal readonly Dictionary<string,RawSecurityDescriptor> Descriptors=new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<(string Path,string Stage)> Mutations=[];internal string? FailLabelPath,Outside,Actor;
        internal bool EnforceSecurity;internal int ActorReads;
        internal ScopeKernel(VerifiedMigrationSpec spec)
        {
            this.spec=spec;
            Objects.AddRange(new[]{(spec.PayloadRoot,true),(Path.Combine(spec.PayloadRoot,"runtime"),true),(Path.GetDirectoryName(spec.NodePath)!,true),(spec.NodePath,false),
                (Path.Combine(spec.PayloadRoot,"app"),true),(Path.GetDirectoryName(spec.EntryPath)!,true),(spec.EntryPath,false),(spec.DataDirectory,true),
                (Path.Combine(spec.DataDirectory,"app.sqlite"),false),(Path.Combine(spec.DataDirectory,"process.lock"),false),(Path.GetDirectoryName(spec.RequestFile)!,true),(spec.RequestFile,false),(spec.TemporaryDirectory,true)});
        }
        public void RequireAuthority(string sid)=>Assert(sid==spec.ExpectedAuthoritySid);
        public string ActorSid(string authority){ActorReads++;return Actor??authority;}
        public IReadOnlyList<(string Path,bool Directory)> Enumerate(string root,CancellationToken c)
        {
            var values=Objects.Where(x=>VerifiedMigrationSpec.Same(x.Path,root)||VerifiedMigrationSpec.Within(x.Path,root)).ToList();
            if(Outside is { } path)values.Add((path,true));return values;
        }
        private static RawSecurityDescriptor Copy(RawSecurityDescriptor source){var bytes=new byte[source.BinaryLength];source.GetBinaryForm(bytes,0);return new(bytes,0);}
        public RawSecurityDescriptor Read(MigrationScopeObject item)=>Copy(Descriptors[item.Path]);
        public void SetDacl(MigrationScopeObject item,RawSecurityDescriptor descriptor)
        {
            if(EnforceSecurity&&Descriptors[item.Path].Owner?.Value!=Actor&&(PackageMask(Descriptors[item.Path],Actor!)&0x40000)==0)throw new IOException("FAKE_WRITE_DAC_DENIED");
            var value=Copy(descriptor);var current=Descriptors[item.Path];value.Owner=current.Owner;value.SystemAcl=current.SystemAcl;Descriptors[item.Path]=value;Mutations.Add((item.Path,"dacl"));
        }
        public void SetOwnerAndLabel(MigrationScopeObject item,RawSecurityDescriptor descriptor)
        {
            if(EnforceSecurity&&(descriptor.Owner?.Value!=Actor||(PackageMask(Descriptors[item.Path],Actor!)&0x80000)==0))throw new IOException("FAKE_WRITE_OWNER_DENIED");
            if(FailLabelPath==item.Path){FailLabelPath=null;throw new IOException("FAKE_INTERRUPTED_LABEL_PUBLICATION");}
            var current=Descriptors[item.Path];current.Owner=descriptor.Owner;current.SystemAcl=descriptor.SystemAcl;Mutations.Add((item.Path,"label"));
        }
    }
    private sealed class Kernel:IMigrationSandboxKernel
    {
        internal readonly List<string> Order=[];internal readonly Child Child;internal bool Created;
        internal Kernel(VerifiedMigrationSpec spec)=>Child=new(Order,new(101,202,spec.ExpectedRuntimeSid,spec.ExpectedAppContainerSid,[],false,spec.Plan.EntryDigest,spec.NodeDigest));
        public IMigrationSuspendedChild CreateSuspended(VerifiedMigrationSpec spec){Created=true;Order.Add("create");return Child;}
    }
    private sealed class Child(List<string> order,MigrationSandboxObservation observation):IMigrationSuspendedChild
    {
        internal MigrationSandboxObservation Observation=observation;internal int Resumes;internal bool FailResume,FailCleanup,FailObservation,FailSecondObservation,Disposed,RootExited,TreeEmpty;internal Action? OnObserve;private int observations;
        public MigrationSandboxObservation Observe(){order.Add("observe");observations++;OnObserve?.Invoke();if(FailObservation||FailSecondObservation&&observations==2)throw new IOException("FAKE_TOKEN_QUERY");return Observation;}
        public void Resume(){Resumes++;order.Add("resume");if(FailResume)throw new IOException("FAKE_RESUME");}
        public Task RequestGracefulStopAsync(CancellationToken c)=>Task.CompletedTask;
        public Task<int> WaitForRootExitAsync(CancellationToken c){RootExited=true;order.Add("root-exit");return Task.FromResult(0);}
        public Task WaitForTreeEmptyAsync(CancellationToken c){TreeEmpty=true;order.Add("tree-empty");return Task.CompletedTask;}
        public async Task TerminateAndWaitForTreeEmptyAsync(CancellationToken c){order.Add("terminate");if(FailCleanup)throw new IOException("FAKE_UNKNOWN_TREE");await WaitForRootExitAsync(c);await WaitForTreeEmptyAsync(c);}
        public ValueTask DisposeAsync(){if(!RootExited||!TreeEmpty)throw new IOException("FAKE_EARLY_DISPOSE");Disposed=true;order.Add("dispose");return ValueTask.CompletedTask;}
    }
    private sealed class Reader(InstallationBinding value):IInstallationReader{internal InstallationBinding Value=value;public InstallationBinding Read()=>Value;}
    private sealed class Admission:IOfflineMigrationAdmissionChannel
    {
        internal RuntimeHostIdentity Host=new(333,444);internal OfflineMigrationAdmission Value;internal int Checked;
        internal Admission(VerifiedMigrationSpec spec)=>Value=new(new(new(spec.Plan.InstallationId,new('8',64)),spec.Plan.PayloadDigest,new('7',32),Host.ProcessId,Host.Created),spec.Plan.OperationId,spec.Plan.SourceDataId,spec.Plan.NewDataId,spec.Plan.SourceSchema,spec.Plan.TargetSchema,spec.Plan.EntryDigest,spec.Nonce);
        public Task<OfflineMigrationAdmission> WaitAsync(InstallationBinding installation,RuntimeHostIdentity host,CancellationToken c)=>Task.FromResult(Value);
        public void RequireCurrent(OfflineMigrationAdmission admission){Checked++;if(admission!=Value)throw new UnauthorizedAccessException("FAKE_CHANGED_ADMISSION");}
    }
    private sealed class Resolver(VerifiedMigrationSpec spec):IInstalledMigrationSpecResolver
    {internal VerifiedMigrationSpec Spec=spec;internal Action? After;internal int Calls;public Task<VerifiedMigrationSpec> ResolveAsync(OfflineMigrationAdmission admission,CancellationToken c){Calls++;After?.Invoke();return Task.FromResult(Spec);}}
    private sealed class EffectsJournal(List<string> order):IProbationEffectsJournal
    {internal bool Fail;public Task RecordIntentAsync(ProbationRuntimeBinding binding,string id,CancellationToken c){order.Add("journal");if(Fail)throw new IOException("FAKE_DISK_FULL");return Task.CompletedTask;}}
    private sealed class EffectsRuntime(List<string> order):IProbationEffectsRuntime
    {internal int Calls;internal bool LoseReply;public Task ReleaseEffectsAsync(ProbationRuntimeBinding binding,string id,CancellationToken c){Calls++;order.Add("release");if(LoseReply)throw new IOException("FAKE_LOST_ACK");return Task.CompletedTask;}}
    private sealed class PurposeChannel(RuntimeAdmissionBinding binding):IRuntimeAdmissionChannel,IRuntimePurposeAdmissionChannel
    {
        public string Purpose=>"offline-migrate";
        public Task<RuntimeAdmissionBinding> WaitForAdmissionAsync(InstallationBinding installation,RuntimeHostIdentity host,CancellationToken c)=>Task.FromResult(binding);
        public void RequireLaunch(RuntimeAdmissionBinding value){if(value!=binding)throw new IOException("FAKE_BINDING");}
        public Task ReportReadyAsync(RuntimeAdmissionBinding value,CancellationToken c)=>throw new IOException("FAKE_NORMAL_READY_FORBIDDEN");
        public Task ReportMigrationAsync(RuntimeAdmissionBinding value,MigrationSandboxObservation actor,CancellationToken c)=>Task.CompletedTask;
        public Task ReportMigrationExitAsync(RuntimeAdmissionBinding value,int code,CancellationToken c)=>Task.CompletedTask;
    }
    private sealed class NeverInstalled:IServiceStartAuthorizer
    {internal int Calls;public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken c){Calls++;throw new IOException("NORMAL_RESOLVER_INVOKED");}}
    private sealed class NeverLauncher:IPrivateAppLauncher
    {public Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken c)=>throw new IOException("UNEXPECTED_LAUNCH");public Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken c)=>throw new IOException("UNEXPECTED_HEALTH");}
    private sealed class Barrier:IAppRecoveryBarrier{public Task RequireStartAllowedAsync(AppRelease release,CancellationToken c)=>Task.CompletedTask;}
    private sealed class FiniteAuthorizer(IPrivateAppLauncher launcher,ServiceRunPurpose purpose):IServiceStartAuthorizer
    {public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken c)=>Task.FromResult(new AuthorizedServiceStart(new(new('a',64)),launcher,new Barrier(),purpose:purpose));}
    private sealed class SessionLauncher(InstalledOfflineMigrationStart.Session session):IPrivateAppLauncher
    {public Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken c)=>Task.FromResult<IContainedAppSession>(session);public Task WaitForHealthyAsync(IContainedAppSession value,CancellationToken c)=>session.ReportHealthyAsync(c);}
    private sealed class DirectLauncher(IContainedAppSession session):IPrivateAppLauncher
    {public Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken c)=>Task.FromResult(session);public Task WaitForHealthyAsync(IContainedAppSession value,CancellationToken c)=>Task.CompletedTask;}
    private sealed class FiniteChild(int code,bool unknown):IContainedAppSession
    {
        internal bool Disposed;public Task RequestGracefulStopAsync(CancellationToken c)=>Task.CompletedTask;
        public Task<int> WaitForRootExitAsync(CancellationToken c)=>Task.FromResult(code);
        public Task WaitForTreeEmptyAsync(CancellationToken c)=>unknown?Task.FromException(new IOException("FAKE_TREE_UNKNOWN")):Task.CompletedTask;
        public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken c)=>WaitForTreeEmptyAsync(c);
        public ValueTask DisposeAsync(){if(unknown)throw new IOException("FAKE_TREE_UNKNOWN");Disposed=true;return ValueTask.CompletedTask;}
    }
    private sealed class DelayedPurpose:IRuntimePurposeAdmissionChannel
    {
        public string Purpose=>"offline-migrate";internal readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously),StartedAck=new(TaskCreationOptions.RunContinuationsAsynchronously),Completed=new(TaskCreationOptions.RunContinuationsAsynchronously),CompletedAck=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ReportMigrationAsync(RuntimeAdmissionBinding binding,MigrationSandboxObservation actor,CancellationToken c){Started.TrySetResult();await StartedAck.Task.WaitAsync(c);}
        public async Task ReportMigrationExitAsync(RuntimeAdmissionBinding binding,int code,CancellationToken c){Completed.TrySetResult();await CompletedAck.Task.WaitAsync(c);}
    }
    private sealed class AckStream:Stream
    {
        internal readonly TaskCompletionSource Written=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<byte[]> Reply=new(TaskCreationOptions.RunContinuationsAsynchronously);private MemoryStream? read;
        public override bool CanRead=>true;public override bool CanWrite=>true;public override bool CanSeek=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override Task FlushAsync(CancellationToken cancellation){Written.TrySetResult();return Task.CompletedTask;}
        public override void Flush()=>Written.TrySetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes,CancellationToken cancellation=default)=>ValueTask.CompletedTask;
        public override void Write(byte[] bytes,int offset,int count){}
        public override async ValueTask<int> ReadAsync(Memory<byte> bytes,CancellationToken cancellation=default)
        {read??=new MemoryStream(await Reply.Task.WaitAsync(cancellation));return read.Read(bytes.Span);}
        public override int Read(byte[] bytes,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long value,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing)read?.Dispose();base.Dispose(disposing);}
    }
    private sealed class MigrationRuntime:IRetainedScmMigrationSession,IRuntimeGateKernel
    {
        internal readonly List<string> Order=[];internal readonly InstallationBinding Binding;internal MigrationSandboxObservation Actor;
        private RuntimeAdmissionBinding? admission;internal bool Assigned,Released,Exited,Empty,Disposed,FailObservation,FailStop,NeverStarts,StartupCancelled,StartedAccepted;
        internal MigrationRuntime(VerifiedMigrationSpec spec)
        {Binding=new(spec.Plan.InstallationId,new('8',64));Actor=new(101,202,spec.ExpectedRuntimeSid,spec.ExpectedAppContainerSid,[],false,spec.Plan.EntryDigest,spec.NodeDigest);}
        public IRuntimeGateKernel Kernel=>this;
        public async Task<RuntimeAdmissionBinding> StartWaitingAsync(RuntimeStartIntent intent,CancellationToken c)
        {Assert(intent.Purpose=="offline-migrate");Order.Add("waiting");if(NeverStarts){try{await new TaskCompletionSource().Task.WaitAsync(c);}catch(OperationCanceledException){StartupCancelled=true;throw;}}admission=new(intent.Binding,intent.ReleaseDigest,intent.Nonce,321,654);return admission;}
        public void RequireAuthenticatedCurrent(RuntimeAdmissionBinding value){if(value!=admission||Exited)throw new IOException("FAKE_HOST_CHANGED");}
        public Task ReleaseLaunchAsync(RuntimeAdmissionBinding value,CancellationToken c){RequireAuthenticatedCurrent(value);Assert(Assigned);Order.Add("release");Released=true;return Task.CompletedTask;}
        public Task<RuntimeAdmissionBinding> WaitReadyAsync(CancellationToken c)=>throw new IOException("FAKE_NORMAL_READY");
        public Task<MigrationSandboxObservation> WaitMigrationStartedAsync(RuntimeAdmissionBinding value,CancellationToken c)
        {RequireAuthenticatedCurrent(value);Order.Add("observation");if(FailObservation)throw new IOException("FAKE_OBSERVATION");return Task.FromResult(Actor);}
        public Task AcknowledgeMigrationStartedAsync(RuntimeAdmissionBinding value,CancellationToken c){RequireAuthenticatedCurrent(value);StartedAccepted=true;Order.Add("started-accepted");return Task.CompletedTask;}
        public Task<int> WaitMigrationCompletedAsync(RuntimeAdmissionBinding value,CancellationToken c){RequireAuthenticatedCurrent(value);Assert(StartedAccepted);Exited=true;Empty=true;return Task.FromResult(0);}
        public Task WaitForExitAsync(CancellationToken c){if(!Exited)throw new IOException("FAKE_ROOT_ACTIVE");return Task.CompletedTask;}
        public Task StopAsync(CancellationToken c){if(FailStop)throw new IOException("FAKE_UNKNOWN_CLEANUP");Exited=true;Empty=true;return Task.CompletedTask;}
        public void RequireQuiescence(){if(!Exited||!Empty)throw new IOException("FAKE_TREE_ACTIVE");}
        public void Dispose(){RequireQuiescence();Disposed=true;}
        public RuntimeGeneration ObserveHost(){var value=admission??throw new IOException("FAKE_NOT_STARTED");return new(value.Binding.InstallationId,value.ReleaseDigest,value.Nonce,value.ProcessId,value.Created);}
        public void AssignWaitingHost(RuntimeGeneration generation){Assigned=true;Order.Add("assigned");}
        public bool InOwnedJob(RuntimeGeneration generation)=>Assigned;
        public bool HostExited(RuntimeGeneration generation)=>Exited;
        public bool OwnedTreeEmpty()=>Empty;
    }
    private sealed class ProbationRecoveryRuntime(ProbationRuntimeBinding expected):IRetainedScmRuntimeSession,IRetainedScmUpdatePreparation,IRetainedScmRecoveryObservation,IProbationEffectsRuntime,IRuntimeGateKernel
    {
        private RuntimeAdmissionBinding? admission;private readonly TaskCompletionSource exit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Order=[];internal bool Idle,Changed,ChangeDuringPrepare,Exited,Empty,Disposed,ExitDuringRelease,AllowOffline;private bool assigned;
        internal int ReleaseCalls,Stops,Cancels,Captures,RecoveryRetains;public IRuntimeGateKernel Kernel=>this;
        public Task<RuntimeAdmissionBinding> StartWaitingAsync(RuntimeStartIntent intent,CancellationToken c)
        {
            Assert((AllowOffline?intent.Purpose=="admit":intent.Purpose=="probation"&&intent.Nonce==expected.Nonce)&&intent.Binding==expected.Binding&&intent.ReleaseDigest==expected.ReleaseDigest);
            admission=new(intent.Binding,intent.ReleaseDigest,intent.Nonce,765,876);return Task.FromResult(admission);
        }
        public void RequireAuthenticatedCurrent(RuntimeAdmissionBinding value)
        {if(value!=admission||Changed||Exited)throw new UnauthorizedAccessException("FAKE_CURRENT_GENERATION_CHANGED");}
        public Task ReleaseLaunchAsync(RuntimeAdmissionBinding value,CancellationToken c){RequireAuthenticatedCurrent(value);Assert(assigned&&!AllowOffline);return Task.CompletedTask;}
        public Task<RuntimeAdmissionBinding> WaitReadyAsync(CancellationToken c)=>Task.FromResult(admission!);
        public Task ReleaseEffectsAsync(ProbationRuntimeBinding binding,string id,CancellationToken c)
        {Assert(binding==expected);ReleaseCalls++;if(ExitDuringRelease)NaturalExit();throw new IOException("FAKE_EFFECTS_ACK_LOST");}
        public Task<bool> PrepareUpdateAsync(RuntimeAdmissionBinding value,string id,CancellationToken c)
        {RequireAuthenticatedCurrent(value);Order.Add("prepare");if(ChangeDuringPrepare)Changed=true;return Task.FromResult(Idle);}
        public Task CancelUpdateAsync(RuntimeAdmissionBinding value,string id,CancellationToken c)
        {RequireAuthenticatedCurrent(value);Cancels++;return Task.CompletedTask;}
        public Task StopAsync(CancellationToken c)
        {if(Changed)throw new UnauthorizedAccessException("FAKE_STOP_CHANGED_GENERATION");Order.Add("stop");Stops++;Exited=true;Empty=true;exit.TrySetResult();return Task.CompletedTask;}
        public Task WaitForExitAsync(CancellationToken c)=>exit.Task.WaitAsync(c);
        public void RequireQuiescence(){if(!Exited||!Empty)throw new IOException("FAKE_OWNED_JOB_OCCUPIED");}
        public void RetainRecoveryObservation(){Assert(AllowOffline&&assigned&&!Exited);RecoveryRetains++;}
        public RetainedRuntimeQuiescence AcquireRecoveryQuiescence(){Order.Add("capture");Captures++;throw new IOException("FAKE_NATIVE_PROOF_NOT_CREATED");}
        public void Dispose(){RequireQuiescence();Disposed=true;}
        public RuntimeGeneration ObserveHost(){var value=admission??throw new IOException("FAKE_NOT_STARTED");return new(value.Binding.InstallationId,value.ReleaseDigest,value.Nonce,value.ProcessId,Changed?value.Created+1:value.Created);}
        public void AssignWaitingHost(RuntimeGeneration value)=>assigned=true;
        public bool InOwnedJob(RuntimeGeneration value)=>assigned;
        public bool HostExited(RuntimeGeneration value)=>Exited;
        public bool OwnedTreeEmpty()=>Empty;
        internal void NaturalExit(){Exited=true;Empty=true;exit.TrySetResult();}
    }
    private sealed class ProfileKernel:IInstalledMigrationProfileKernel
    {
        internal string Sid="S-1-15-2-1-2-3-4-5-6-7";internal string? CreatedSid="S-1-15-2-1-2-3-4-5-6-7",CreatedName;
        internal int Creates,ScopeReads,Capabilities=-1,CreateResult,ChangeIdentityAt,ExemptAt;internal bool ChangeDerivedSid;private int identities,derives,networkChecks;
        internal readonly List<string> Order=[];
        internal MigrationProfileRuntime Runtime=new(new("S-1-5-80-1-2-3-4-5",true,false,["SeChangeNotifyPrivilege"]),"S-1-5-80-1-2-3-4-5","S-1-5-80-1-2-3-4-5",false);
        internal MigrationProfileUserScope Scope;
        internal ProfileKernel()
        {
            var root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"synthetic-runtime-user-profile"));
            Scope=new(Runtime.ExpectedRuntimeSid,root,Path.Combine(root,"AppData","Local","Packages","Yahad.Migrate.synthetic"),true,true,true);
        }
        public MigrationProfileRuntime ObserveRuntime(){Order.Add("identity");identities++;return identities==ChangeIdentityAt?Runtime with{ProcessSid="S-1-5-18"}:Runtime;}
        public string DeriveSid(string name){Order.Add("derive");derives++;return ChangeDerivedSid&&derives>1?"S-1-15-2-9-8-7-6-5-4-3":Sid;}
        public bool LoopbackExempt(string sid){Order.Add("loopback");networkChecks++;return networkChecks==ExemptAt;}
        public MigrationProfileCreation Create(string name,IReadOnlyList<string> capabilities){Order.Add("create");Creates++;CreatedName=name;Capabilities=capabilities.Count;return new(CreateResult,CreatedSid);}
        public MigrationProfileUserScope ObserveCurrentUserScope(string sid){Order.Add("scope");ScopeReads++;return Scope;}
    }
}

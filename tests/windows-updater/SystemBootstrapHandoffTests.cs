using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;

internal static class SystemBootstrapHandoffTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("full bootstrap updates helper last and never changes family bytes",async()=>
        {
            var f=new Fixture();var op=f.Authorize();using var journal=new SystemBootstrapJournal(f.State,op);var update=f.Update();
            await update.RunAsync(op,journal,default);Assert(journal.Current!.Phase==SystemBootstrapPhase.AwaitingHelperExit&&!f.Kernel.Published.Contains(SystemComponentKind.Helper));
            f.Kernel.HelperExited=true;await update.FinishHelperAsync(op,journal,default);
            Assert(journal.Current.Phase==SystemBootstrapPhase.Complete&&f.Kernel.Published.Last()==SystemComponentKind.Helper&&f.Kernel.Current.Epoch==2);f.Kernel.AssertData();
        });
        await check("full bootstrap does not replace a running old helper and can retry after actual-exit contract",async()=>
        {
            var f=new Fixture();var op=f.Authorize();using var journal=new SystemBootstrapJournal(f.State,op);var update=f.Update();await update.RunAsync(op,journal,default);
            await Denied(()=>update.FinishHelperAsync(op,journal,default));Assert(!f.Kernel.Published.Contains(SystemComponentKind.Helper));
            f.Kernel.HelperExited=true;await update.ReconcileAsync(op,journal,default);Assert(journal.Current!.Phase==SystemBootstrapPhase.Complete);f.Kernel.AssertData();
        });
        await check("three successive synthetic full bootstrap generations preserve identity and family data",async()=>
        {
            var f=new Fixture();var installation=f.Installation;
            for(var n=1;n<=3;n++)
            {
                f.SetCandidate($"0.6.{n}");var op=f.Authorize();using var journal=new SystemBootstrapJournal(f.State,op);var update=f.Update();
                await update.RunAsync(op,journal,default);f.Kernel.HelperExited=true;await update.FinishHelperAsync(op,journal,default);
                Assert(f.Kernel.Current.InstallationId==installation&&f.Kernel.Current.Epoch==n+1&&f.Kernel.Current.Version==$"0.6.{n}");f.Kernel.AssertData();
            }
        });
        await check("full bootstrap revocation after custody rolls binaries back without reusing a grant",async()=>
        {
            var f=new Fixture();var op=f.Authorize();var revoked=false;f.Kernel.CustodyObservation=()=>{if(!revoked){revoked=true;f.Policy.DisableCurrent();}};var update=f.Update();
            using var journal=new SystemBootstrapJournal(f.State,op);await Denied(()=>update.RunAsync(op,journal,default));
            Assert(journal.Current!.Phase==SystemBootstrapPhase.RolledBack&&f.Kernel.Failures==0);f.Kernel.AssertPrevious();f.Kernel.AssertData();
        });
        await check("two observed unhealthy bootstrap candidates quarantine that release only",async()=>
        {
            var f=new Fixture();f.Kernel.BadAuthority=true;
            for(var n=0;n<2;n++){var op=f.Authorize();using var journal=new SystemBootstrapJournal(f.State,op);await Denied(()=>f.Update().RunAsync(op,journal,default));Assert(journal.Current!.Phase==SystemBootstrapPhase.RolledBack);}
            var third=f.Authorize();using var blocked=new SystemBootstrapJournal(f.State,third);await Denied(()=>f.Update().RunAsync(third,blocked,default));Assert(f.Kernel.Failures==2);f.Kernel.AssertData();
        });
        foreach(var outcome in new[]{1175,1176,1177})await check("synthetic ReplaceFile outcome "+outcome+" reconciles private file bytes without DB restore",async()=>
        {
            var f=new Fixture();f.Kernel.ReplaceOutcome=outcome;var op=f.Authorize();using var journal=new SystemBootstrapJournal(f.State,op);
            await Denied(()=>f.Update().RunAsync(op,journal,default));Assert(journal.Current!.Phase==SystemBootstrapPhase.RolledBack&&f.Kernel.Failures==0);f.Kernel.AssertPrevious();f.Kernel.AssertData();
        });
        await check("unknown active binary after interrupted replacement is retained rather than overwritten",async()=>
        {
            var f=new Fixture();f.Kernel.ReplaceOutcome=1176;f.Kernel.ForeignFile=true;var op=f.Authorize();using var journal=new SystemBootstrapJournal(f.State,op);
            await Denied(()=>f.Update().RunAsync(op,journal,default));Assert(journal.Current!.Phase==SystemBootstrapPhase.RecoveryRequired);
            Assert(File.ReadAllText(f.Kernel.Entry(SystemComponentKind.Owner))=="foreign-unowned-by-plan");f.Kernel.AssertData();
        });
        await check("helper custody loss prevents publication and native proof cannot be inferred from a journal",async()=>
        {
            var f=new Fixture();f.Kernel.CustodyAvailable=false;var op=f.Authorize();using var journal=new SystemBootstrapJournal(f.State,op);
            await Denied(()=>f.Update().RunAsync(op,journal,default));Assert(f.Kernel.Published.Count==0&&journal.Current!.Phase==SystemBootstrapPhase.Prepared);f.Kernel.AssertData();
        });
        foreach(var step in new[]{"runtime","data"})await check("revocation during final "+step+" observation prevents every helper publication",async()=>
        {
            var f=new Fixture();var op=f.Authorize();using var journal=new SystemBootstrapJournal(f.State,op);var update=f.Update();await update.RunAsync(op,journal,default);f.Kernel.HelperExited=true;
            if(step=="runtime")f.Kernel.RuntimeObservation=f.Policy.DisableCurrent;else f.Kernel.DataObservation=f.Policy.DisableCurrent;
            await Denied(()=>update.FinishHelperAsync(op,journal,default));Assert(!f.Kernel.Published.Contains(SystemComponentKind.Helper)&&journal.Current!.Phase==SystemBootstrapPhase.RecoveryRequired);
        });
        await check("verified backup gate executes after durable preparation and before any publication",async()=>
        {
            var f=new Fixture();var op=f.Authorize();using var journal=new SystemBootstrapJournal(f.State,op);var update=f.Update();await update.PrepareAsync(op,journal,default);
            Assert(journal.Current!.Phase==SystemBootstrapPhase.Prepared&&f.Kernel.Published.Count==0);
            await Denied(()=>update.ContinuePreparedAsync(op,journal,default,(value,c)=>throw new IOException("SYNTHETIC_BACKUP_FAILED")));
            Assert(f.Kernel.Published.Count==0&&journal.Current!.Phase==SystemBootstrapPhase.Prepared);f.Kernel.AssertData();
        });
        await check("a terminal record cannot hide a corrupted earlier handoff hash chain",async()=>
        {
            var f=new Fixture();var op=f.Authorize();using(var journal=new SystemBootstrapJournal(f.State,op)){await f.Update().RunAsync(op,journal,default);f.Kernel.HelperExited=true;await f.Update().FinishHelperAsync(op,journal,default);}
            var first=f.State.PathOf(".authority/bootstrap-handoffs/"+op.OperationId+"/00001.json");var value=NativeJson.Read<SystemBootstrapRecord>(File.ReadAllBytes(first));File.WriteAllBytes(first,JsonSerializer.SerializeToUtf8Bytes(value with{PreviousDigest=new string('e',64)}));
            f.SetCandidate("0.6.2");var next=f.Authorize();Directory.CreateDirectory(f.State.PathOf(".authority/bootstrap-handoffs/"+next.OperationId));
            await Denied(()=>{using var invalid=new SystemBootstrapJournal(f.State,next);return Task.CompletedTask;});
        });
        await check("bootstrap channel rejects cross-operation, stale nonce and generation markers without native effects",()=>
        {
            var f=new Fixture();var op=f.Authorize();var value=new SystemBootstrapChallenge(1,op.OperationId,op.ManifestDigest,op.GrantId,op.PolicyDigest,Guid.NewGuid().ToString("N"),10,100,new string('a',64),20,200,new string('b',64),false);
            SystemBootstrapAuthorityChannel.RequireChallenge(op,value);var marker=SystemBootstrapAuthorityChannel.Marker(value,"health");SystemBootstrapAuthorityChannel.RequireMarker(value,marker,"health");
            foreach(var bad in new[]{marker with{Nonce=Guid.NewGuid().ToString("N")},marker with{OperationId=Guid.NewGuid().ToString("N")},marker with{AuthorityCreated=201},marker with{HelperPid=11},marker with{Phase="adopted"}})
            {var rejected=false;try{SystemBootstrapAuthorityChannel.RequireMarker(value,bad,"health");}catch(UnauthorizedAccessException){rejected=true;}Assert(rejected);}
            return Task.CompletedTask;
        });
        await check("helper root exit cannot permit replacement while an owned descendant remains",async()=>
        {
            var f=new Fixture();var op=f.Authorize();using var journal=new SystemBootstrapJournal(f.State,op);var update=f.Update();await update.RunAsync(op,journal,default);
            f.Kernel.HelperExited=true;f.Kernel.HelperChildAlive=true;await Denied(()=>update.FinishHelperAsync(op,journal,default));Assert(!f.Kernel.Published.Contains(SystemComponentKind.Helper));
            f.Kernel.HelperChildAlive=false;await update.ReconcileAsync(op,journal,default);Assert(journal.Current!.Phase==SystemBootstrapPhase.Complete);
        });
        await check("installed Authority entry accepts only fixed modes and takes no operation or path from CLI",()=>
        {
            Assert(InstalledAuthorityEntrypoint.Parse(["--service"])==InstalledAuthorityMode.Service&&InstalledAuthorityEntrypoint.Parse(["--system-recovery-service","A"])==InstalledAuthorityMode.RecoveryA&&InstalledAuthorityEntrypoint.Parse(["--system-recovery-service","B"])==InstalledAuthorityMode.RecoveryB&&InstalledAuthorityEntrypoint.Parse(["--recovery-slot-probe"])==InstalledAuthorityMode.HelperProbe);
            foreach(var args in new[]{new[]{"--system-handoff"},new[]{"--recovery-slot-probe",Guid.NewGuid().ToString("N")},new[]{"--system-recovery-service","C"},new[]{"--service","C:\\untrusted.exe"}})
            {var denied=false;try{_=InstalledAuthorityEntrypoint.Parse(args);}catch(IOException){denied=true;}Assert(denied);}
            return Task.CompletedTask;
        });
        await check("helper self assignment retains required set-quota and terminate rights without broadening ordinary query handles",()=>
        {
            Assert((SystemBootstrapHelperNative.SelfAssignmentAccess&0x101)==0x101&&(SystemBootstrapHelperNative.QueryDuplicateAccess&0x101)==0&&(SystemBootstrapHelperNative.SelfAssignmentAccess&0x00101000)==0x00101000);
            return Task.CompletedTask;
        });
        await check("readonly bootstrap completion inspects full journal independently from candidate cache or RPC markers",async()=>
        {
            var f=new Fixture();var op=f.Authorize();using(var journal=new SystemBootstrapJournal(f.State,op))
            {
                await f.Update().RunAsync(op,journal,default);Assert(SystemBootstrapJournal.Inspect(f.State,f.Installation,op.OperationId)!.Phase==SystemBootstrapPhase.AwaitingHelperExit);
                f.Kernel.HelperExited=true;await f.Update().FinishHelperAsync(op,journal,default);
            }
            f.RejectSourceLookup();await Denied(()=>{_=f.Policy.RemintBootstrapRecovery(op.OperationId);return Task.CompletedTask;});
            Assert(SystemBootstrapJournal.Inspect(f.State,f.Installation,op.OperationId)!.Phase==SystemBootstrapPhase.Complete);
            var first=f.State.PathOf(".authority/bootstrap-handoffs/"+op.OperationId+"/00001.json");var value=NativeJson.Read<SystemBootstrapRecord>(File.ReadAllBytes(first));File.WriteAllBytes(first,JsonSerializer.SerializeToUtf8Bytes(value with{GrantBindingDigest=new string('f',64)}));
            await Denied(()=>{_=SystemBootstrapJournal.Inspect(f.State,f.Installation,op.OperationId);return Task.CompletedTask;});
        });
        foreach(var phase in new[]{SystemBootstrapPhase.Prepared,SystemBootstrapPhase.AwaitingHelperExit,SystemBootstrapPhase.HelperIntent,SystemBootstrapPhase.HelperPublished})
            await check("revoked full update returns previous bootstrap from "+phase+" without new candidate publication",async()=>
            {
                var f=new Fixture();var op=f.Authorize();var update=f.Update();using var journal=new SystemBootstrapJournal(f.State,op);
                if(phase==SystemBootstrapPhase.Prepared)await update.PrepareAsync(op,journal,default);
                else
                {
                    await update.RunAsync(op,journal,default);f.Kernel.HelperExited=true;
                    if(phase is SystemBootstrapPhase.HelperIntent or SystemBootstrapPhase.HelperPublished)
                        await Denied(()=>update.FinishHelperAsync(op,journal,default,point=>{if(point=="journal-"+phase)f.Policy.DisableCurrent();}));
                }
                f.Policy.DisableCurrent();var published=f.Kernel.Published.Count;await update.ReconcileAsync(op,journal,default);
                Assert(journal.Current!.Phase==SystemBootstrapPhase.RolledBack&&f.Kernel.Published.Count==published&&f.Kernel.PreviousStarts==1);f.Kernel.AssertPrevious();f.Kernel.AssertData();
            });
        await check("unknown runtime bootstrap owner is rooted before start and blocks a new observation until actual cleanup retry",async()=>
        {
            Assert(NativeCleanupOwnershipGate.Count==0);var source=new FaultBootstrapSource();var owner=new RuntimeBootstrapObservationOwner(source);RuntimeBootstrapOwnershipException? retained=null;
            try{_=await owner.ObserveAsync(new(Guid.NewGuid().ToString("N"),new string('a',64)),new(new string('b',64)),default);}catch(RuntimeBootstrapOwnershipException error){retained=error;}
            Assert(retained is not null&&source.Starts==1&&NativeCleanupOwnershipGate.Count==1);
            var second=new FaultBootstrapSource();await Denied(async()=>{_=await new RuntimeBootstrapObservationOwner(second).ObserveAsync(new(Guid.NewGuid().ToString("N"),new string('a',64)),new(new string('b',64)),default);});Assert(second.Starts==0);
            await Denied(()=>retained!.RetryCleanupAsync(default));Assert(NativeCleanupOwnershipGate.Count==1&&source.OwnsResources);
            source.FailCleanup=false;await retained!.RetryCleanupAsync(default);Assert(NativeCleanupOwnershipGate.Count==0&&!source.OwnsResources&&source.Releases==1);
        });
        await check("unknown probe tree and pins block a fresh native slot kernel until observed cleanup",async()=>
        {
            var f=new Fixture();var owner=new object();var descendant=true;var pinsOpen=true;
            NativeCleanupOwnershipGate.Retain(owner,c=>{if(descendant)throw new IOException("SYNTHETIC_PROBE_CHILD_REMAINS");pinsOpen=false;return Task.CompletedTask;});
            try
            {
                var kernel=new WindowsRecoveryHelperSlotKernel(f.State,f.State,null!);var blocked=false;
                try{kernel.RequireProvisioned();}catch(IOException error)when(error.Message=="NATIVE_CLEANUP_OWNER_RETAINED"){blocked=true;}
                Assert(blocked&&pinsOpen);await Denied(()=>NativeCleanupOwnershipGate.RetryAsync(owner,default));Assert(pinsOpen&&NativeCleanupOwnershipGate.Count==1);
                descendant=false;await NativeCleanupOwnershipGate.RetryAsync(owner,default);Assert(!pinsOpen&&NativeCleanupOwnershipGate.Count==0);
            }
            finally{descendant=false;await NativeCleanupOwnershipGate.RetryAsync(owner,default);}
        });
        await check("recovery Go lineage survives a replacement helper dying before its new Go without accepting a stale Ready",()=>
        {
            // Transport metadata only: these synthetic numeric handles are
            // NEVER adopted or presented as actual native ownership.
            var f=new Fixture();var op=f.Authorize();var nonce=Guid.NewGuid().ToString("N");
            var original=new SystemBootstrapHelperCapsule(1,op.OperationId,op.ManifestDigest,nonce,11,22,new string('a',64),"metadata-only",7,8,99,100,new(11,22,33,44,1,2),new(11,22,[3],4),new(11,22,[5],6));
            SystemBootstrapHelperApplication.WriteMarker(f.State,original,"go");var origin=SystemBootstrapHelperApplication.RequireGoEvidence(f.State,original);
            var replacement=original with{Nonce=Guid.NewGuid().ToString("N"),HelperPid=12,HelperCreated=23,RecoveryOnly=true,RecoveryOrigin=origin};
            Assert(SystemBootstrapHelperApplication.MarkerPath(f.State,op.OperationId,"ready",nonce)!=SystemBootstrapHelperApplication.MarkerPath(f.State,op.OperationId,"ready",replacement.Nonce));
            Assert(SystemBootstrapHelperApplication.RequireGoEvidence(f.State,replacement)==origin);
            var denied=false;try{_=SystemBootstrapHelperApplication.RequireGoEvidence(f.State,replacement with{RecoveryOrigin=origin with{ManifestDigest=new string('b',64)}});}catch(IOException){denied=true;}Assert(denied);
            SystemBootstrapHelperApplication.WriteMarker(f.State,replacement,"go");Assert(SystemBootstrapHelperApplication.RequireGoEvidence(f.State,replacement).Nonce==replacement.Nonce);return Task.CompletedTask;
        });
        await check("dead or reused helper generations are ignored before any receiver acknowledgement",()=>
        {
            Assert(SystemBootstrapAuthorityChannel.IsStaleHelperObservation(10,100,10,100,true));
            Assert(SystemBootstrapAuthorityChannel.IsStaleHelperObservation(10,100,10,101,false));
            Assert(SystemBootstrapAuthorityChannel.IsStaleHelperObservation(10,100,11,100,false));
            Assert(!SystemBootstrapAuthorityChannel.IsStaleHelperObservation(10,100,10,100,false));return Task.CompletedTask;
        });
        await check("Go uncertainty recovery distinguishes preflush and published cuts only after full helper quiescence",()=>
        {
            var reads=0;bool ReadNoGo(){reads++;return false;}
            Assert(HelperHandoffObservationRules.Select(false,false,ReadNoGo)==HelperHandoffObservation.Active&&reads==0);
            Assert(HelperHandoffObservationRules.Select(true,false,ReadNoGo)==HelperHandoffObservation.Active&&reads==0);
            Assert(HelperHandoffObservationRules.Select(true,true,ReadNoGo)==HelperHandoffObservation.QuiescedWithoutPublishedGo&&reads==1);
            Assert(HelperHandoffObservationRules.Select(true,true,()=>true)==HelperHandoffObservation.QuiescedWithPublishedGo);
            var refused=false;try{_=HelperHandoffObservationRules.Select(true,true,()=>throw new IOException("SYNTHETIC_INVALID_PUBLISHED_GO"));}catch(IOException){refused=true;}Assert(refused);return Task.CompletedTask;
        });
        await check("production recovery sweep retries failed owners but cannot release an in-progress native observation",async()=>
        {
            var owner=new object();var cleanups=0;var observed=false;NativeCleanupOwnershipGate.Retain(owner,c=>{cleanups++;if(!observed)throw new IOException("SYNTHETIC_STILL_OWNED");return Task.CompletedTask;},active:true);
            try
            {
                await NativeCleanupOwnershipGate.RetryRetainedAsync(default);Assert(cleanups==0&&NativeCleanupOwnershipGate.Count==1);
                await Denied(()=>NativeCleanupOwnershipGate.RetryAsync(owner,default));Assert(cleanups==0);
                NativeCleanupOwnershipGate.MarkUnresolved(owner);await Denied(()=>NativeCleanupOwnershipGate.RetryRetainedAsync(default));Assert(cleanups==1&&NativeCleanupOwnershipGate.Count==1);
                observed=true;await NativeCleanupOwnershipGate.RetryRetainedAsync(default);Assert(cleanups==2&&NativeCleanupOwnershipGate.Count==0);
            }
            finally
            {
                observed=true;try{NativeCleanupOwnershipGate.MarkUnresolved(owner);}catch(IOException missing)when(missing.Message=="NATIVE_CLEANUP_OWNER_MISSING"){}
                await NativeCleanupOwnershipGate.RetryAsync(owner,default);
            }
        });
        await check("unobserved SCM helper start is recoverable through the production sweep without requiring exception retry at the caller",async()=>
        {
            Assert(NativeCleanupOwnershipGate.Count==0);var serviceStopped=false;var rootExited=false;var treeEmpty=false;var pinsHeld=true;var retries=0;
            var retained=new RecoveryHelperStartOwnershipException(c=>
            {
                c.ThrowIfCancellationRequested();retries++;
                if(!serviceStopped||!rootExited||!treeEmpty)throw new IOException("SYNTHETIC_SCM_HELPER_STILL_OWNED");
                pinsHeld=false;return Task.CompletedTask;
            },new IOException("SYNTHETIC_SCM_START_UNKNOWN"));
            try
            {
                Assert(NativeCleanupOwnershipGate.Count==1);var blocked=false;
                try{NativeCleanupOwnershipGate.RequireClear();}catch(IOException){blocked=true;}Assert(blocked);
                await Denied(()=>NativeCleanupOwnershipGate.RetryRetainedAsync(default));Assert(retries==1&&pinsHeld&&NativeCleanupOwnershipGate.Count==1);
                serviceStopped=true;rootExited=true;
                await Denied(()=>NativeCleanupOwnershipGate.RetryRetainedAsync(default));Assert(retries==2&&pinsHeld&&NativeCleanupOwnershipGate.Count==1);
                treeEmpty=true;await NativeCleanupOwnershipGate.RetryRetainedAsync(default);Assert(retries==3&&!pinsHeld&&NativeCleanupOwnershipGate.Count==0);
                await retained.RetryCleanupAsync(default);Assert(retries==3);NativeCleanupOwnershipGate.RequireClear();
            }
            finally{serviceStopped=true;rootExited=true;treeEmpty=true;await retained.RetryCleanupAsync(default);}
        });
        await RunRecoverySlots(check);
        var snapshots=new List<(string StateRoot,string BinaryRoot,string Point,Fixture Original,VerifiedSystemOperation Operation)>();
        await check("capture private filesystem snapshots at every bootstrap durable boundary",async()=>
        {
            var f=new Fixture();var op=f.Authorize();using var journal=new SystemBootstrapJournal(f.State,op);var update=f.Update();var index=0;
            void Capture(string point)
            {
                var stateRoot=SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"yahad-system-crash-state"));
                var binaryRoot=SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"yahad-system-crash-binaries"));
                Copy(f.State.Root,stateRoot);Copy(f.Kernel.Root,binaryRoot);snapshots.Add((stateRoot,binaryRoot,$"{++index:D2}-"+point,f,op));
            }
            await update.RunAsync(op,journal,default,Capture);f.Kernel.HelperExited=true;await update.FinishHelperAsync(op,journal,default,Capture);Assert(snapshots.Count>=30);
        });
        await check("synthetic SCM helper death after Authority stop wakes peer and reconciles without reboot",async()=>
        {
            var cut=snapshots.First(s=>s.Point.EndsWith("journal-AuthorityStartIntent",StringComparison.Ordinal));
            var stateRoot=SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"yahad-peer-state"));var binaryRoot=SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"yahad-peer-binaries"));
            Copy(cut.StateRoot,stateRoot);Copy(cut.BinaryRoot,binaryRoot);var state=InstalledStateFiles.Private(stateRoot);var peer=cut.Original.Kernel.Clone(binaryRoot);
            // The fixed SCM failure contract wakes B after A's actual process
            // failure; this fixture models that wake, not Windows SCM execution.
            var wake=BootstrapRecoveryProvisioningContract.Describe(RecoveryHelperSlot.A);Assert(BootstrapRecoveryProvisioningContract.Peer(wake.Slot)==RecoveryHelperSlot.B&&wake.FailureCommand.EndsWith(BootstrapRecoveryLayout.ServiceName(RecoveryHelperSlot.B),StringComparison.Ordinal)&&wake.StartType==2&&wake.FailureActionsOnNonCrash);
            using var journal=new SystemBootstrapJournal(state,cut.Operation);await new SystemBootstrapHandoff(peer,(op,c)=>{cut.Original.Policy.RequireCurrent(op);return Task.CompletedTask;}).ReconcileAsync(cut.Operation,journal,default);
            Assert(journal.Current!.Phase==SystemBootstrapPhase.RolledBack);peer.AssertPrevious();peer.AssertData();
        });
        foreach(var snapshot in snapshots)await check("synthetic reboot reconciles snapshot "+snapshot.Point,async()=>
        {
            var state=InstalledStateFiles.Private(snapshot.StateRoot);var kernel=snapshot.Original.Kernel.Clone(snapshot.BinaryRoot);kernel.HelperExited=true;
            var update=new SystemBootstrapHandoff(kernel,(op,c)=>{snapshot.Original.Policy.RequireCurrent(op);return Task.CompletedTask;});
            using var journal=new SystemBootstrapJournal(state,snapshot.Operation);
            if(journal.Current is null){await update.RunAsync(snapshot.Operation,journal,default);await update.FinishHelperAsync(snapshot.Operation,journal,default);}
            else await update.ReconcileAsync(snapshot.Operation,journal,default);
            Assert(journal.Current!.Phase is SystemBootstrapPhase.Complete or SystemBootstrapPhase.RolledBack);kernel.AssertData();
            if(journal.Current.Phase==SystemBootstrapPhase.RolledBack)kernel.AssertPrevious();else kernel.AssertCandidate();
        });
    }
    private static void Copy(string source,string target)
    {
        foreach(var path in Directory.EnumerateFileSystemEntries(source))
        {
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new Exception("UNEXPECTED_TEST_REPARSE");var name=Path.GetFileName(path);if(name is "process.lock" or ".yahad-fixture")continue;
            var destination=Path.Combine(target,name);
            if(Directory.Exists(path)){Directory.CreateDirectory(destination);Copy(path,destination);}
            else try{File.Copy(path,destination);}catch(IOException)when(name.Contains(".partial-",StringComparison.Ordinal)){File.WriteAllText(destination,"synthetic interrupted unpublished bytes");}
        }
    }
    private static async Task RunRecoverySlots(Func<string,Func<Task>,Task> check)
    {
        var cuts=new List<(Fixture Fixture,VerifiedSystemOperation Operation,SystemBootstrapSnapshot Before,string State,string Binaries,string Point)>();
        await check("A/B helper publication keeps one complete boot closure through every private file cut",async()=>
        {
            var f=new Fixture();var op=f.Authorize();var before=f.Kernel.Current;var kernel=new SlotKernel(f,op,before);var index=0;
            kernel.Cut=point=>
            {
                kernel.AssertBootable();var state=SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"yahad-ab-state"));var binaries=SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"yahad-ab-binaries"));
                Copy(f.State.Root,state);Copy(kernel.Root,binaries);cuts.Add((f,op,before,state,binaries,$"{++index:D2}-"+point));
            };
            await new SystemBootstrapRecoverySlots(f.State,kernel,f.Policy.RequireCurrent).PublishAsync(op,before,default);
            var selected=SystemBootstrapRecoverySlots.Read(f.State,f.Installation,op.PolicyDigest);Assert(selected.PendingOperationId is null&&selected.Slots.All(s=>s.Closure.ManifestDigest==op.ManifestDigest));kernel.AssertBootable();Assert(cuts.Count>=8);
        });
        foreach(var cut in cuts)await check("A/B helper + unavailable Authority synthetic reboot at "+cut.Point,async()=>
        {
            var files=InstalledStateFiles.Private(cut.State);var kernel=new SlotKernel(cut.Fixture,cut.Operation,cut.Before,cut.Binaries,files);
            File.WriteAllText(Path.Combine(cut.Binaries,"primary-authority.exe"),"synthetic unavailable primary image");kernel.AssertBootable();
            await new SystemBootstrapRecoverySlots(files,kernel,cut.Fixture.Policy.RequireCurrent).PublishAsync(cut.Operation,cut.Before,default);
            kernel.AssertBootable();var selected=SystemBootstrapRecoverySlots.Read(files,cut.Operation.InstallationId,cut.Operation.PolicyDigest);Assert(selected.PendingOperationId is null&&selected.Slots.All(s=>s.Phase==RecoveryHelperSlotPhase.Verified&&s.Closure.ManifestDigest==cut.Operation.ManifestDigest));
        });
        await check("unhealthy standby helper cannot switch active slot and rollback keeps a running helper untouched",async()=>
        {
            var f=new Fixture();var op=f.Authorize();var before=f.Kernel.Current;var kernel=new SlotKernel(f,op,before){BadHealth=true};var update=new SystemBootstrapRecoverySlots(f.State,kernel,f.Policy.RequireCurrent);
            await Denied(()=>update.PublishAsync(op,before,default));Assert(SystemBootstrapRecoverySlots.Read(f.State,f.Installation,op.PolicyDigest).Active==RecoveryHelperSlot.A);kernel.AssertBootable();
            kernel.BadHealth=false;kernel.Executing=RecoveryHelperSlot.A;f.Policy.DisableCurrent();await update.RestoreAsync(op,before,default);kernel.AssertBootable();
            Assert(!kernel.Writes.Contains(RecoveryHelperSlot.A));
        });
    }
    private static void Assert(bool value){if(!value)throw new Exception("SYSTEM_BOOTSTRAP_ASSERTION");}
    private static async Task Denied(Func<Task> action)
    {try{await action();}catch(Exception e)when(e is IOException or UnauthorizedAccessException or Win32Exception){return;}throw new Exception("SYSTEM_BOOTSTRAP_UNEXPECTED_SUCCESS");}
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed class Fixture
    {
        private readonly PolicyFixture existing=new();private readonly Resolver resolver=new();
        internal InstalledStateFiles State=>existing.Files;internal FullSystemUpdatePolicy Policy{get;}internal Kernel Kernel{get;}
        internal string Installation{get;}private readonly string receipt=new('d',64);
        internal Fixture()
        {
            var binding=WindowsInstallationReader.Parse(State.Read(State.PathOf("bootstrap.json"),1024));Installation=binding.InstallationId;
            var enrollment=new FullSystemEnrollment(1,FullSystemUpdatePolicy.Profile,Guid.NewGuid().ToString("N"),Installation,existing.Owner.Sid,NativeCandidateIntake.Repository,"stable","https-github-unsigned",2,existing.Now.AddMinutes(-1).ToUnixTimeSeconds(),FullSystemUpdatePolicy.RequiredScopes.ToArray());
            var bytes=JsonSerializer.SerializeToUtf8Bytes(enrollment);File.WriteAllBytes(State.PathOf("intake/full-system-enrollment.json"),bytes);var policy=Hash(bytes);
            var grant=new FullSystemModeGrant(1,Guid.NewGuid().ToString("N"),Installation,existing.Owner.Sid,enrollment.Id,policy,existing.Now.ToUnixTimeSeconds());
            File.WriteAllBytes(State.PathOf("owner-ledger/system-grant-"+grant.Id+".json"),JsonSerializer.SerializeToUtf8Bytes(grant));
            File.WriteAllBytes(State.PathOf("intake/mode.json"),JsonSerializer.SerializeToUtf8Bytes(new{Format=2,InstallationId=Installation,Mode="auto",DelegationId=grant.Id,ModeControlAllowed=true,GrantKind="system"}));
            Policy=FullSystemUpdatePolicy.Open(State,Installation,existing.Owner.Sid,policy,()=>existing.Now,resolver);
            Kernel=new(Installation);SetCandidate("0.6.1");
        }
        internal void SetCandidate(string version)
        {var components=Kernel.SetCandidate(version);resolver.Facts=new(Hash(Encoding.UTF8.GetBytes("system:"+version)),Hash(Encoding.UTF8.GetBytes("application:"+version)),version,new string('a',40),6,[6],components);}
        internal VerifiedSystemOperation Authorize()=>Policy.Authorize(Guid.NewGuid().ToString("N"),receipt,new(Kernel.Current.Version,Kernel.Current.Epoch,
            WindowsInstallationReader.Parse(State.Read(State.PathOf("bootstrap.json"),1024)).ActivationReceiptDigest,6));
        internal SystemBootstrapHandoff Update()=>new(Kernel,(op,c)=>{Policy.RequireCurrent(op);return Task.CompletedTask;});
        internal void RejectSourceLookup()=>resolver.Unavailable=true;
        private sealed class Resolver:IProtectedSystemCandidateResolver{internal SystemCandidatePolicyFacts Facts=null!;internal bool Unavailable;public SystemCandidatePolicyFacts RequireVerified(string digest)=>Unavailable?throw new IOException("SYNTHETIC_SOURCE_CACHE_UNAVAILABLE"):Facts;}
    }
    // Private-file model only. No native service/token/job success is manufactured.
    private sealed class Kernel:ISystemBootstrapKernel
    {
        internal string Root{get;}internal List<SystemComponentKind> Published=[];internal bool HelperExited,HelperChildAlive,CustodyAvailable=true,BadAuthority,ForeignFile;
        internal Action? RuntimeObservation,DataObservation,CustodyObservation;
        internal int ReplaceOutcome,Failures,PreviousStarts;private SystemComponentDescriptor[] candidate=[];private SystemBootstrapSnapshot baseline;
        internal SystemBootstrapSnapshot Current=>NativeJson.Read<SystemBootstrapSnapshot>(File.ReadAllBytes(Path.Combine(Root,"selection.json")));
        internal Kernel(string installation)
        {
            Root=SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"yahad-system-binaries"));Directory.CreateDirectory(Path.Combine(Root,"family"));File.WriteAllText(Path.Combine(Root,"family/data.sqlite"),"synthetic family bytes");
            var old=new List<SystemBootstrapComponentState>();
            foreach(var kind in Enum.GetValues<SystemComponentKind>())
            {
                var files=Members(kind,"0.6.0");foreach(var f in files)Write(Live(kind,f.RelativePath),Bytes(kind,"0.6.0",f.RelativePath));
                old.Add(new(kind,FixedSystemComponents.EntryPoint(kind),files.Single(f=>f.RelativePath==FixedSystemComponents.EntryPoint(kind)).Sha256,files.Select(f=>new SystemBootstrapFileState(f.RelativePath,f.Size,f.Sha256)).ToArray()));
            }
            baseline=new(1,installation,1,"0.6.0",Hash(Encoding.UTF8.GetBytes("system:0.6.0")),null,old.ToArray());Save(baseline);
        }
        private Kernel(string root,SystemBootstrapSnapshot baseline,SystemComponentDescriptor[] candidate){Root=root;this.baseline=baseline;this.candidate=candidate;}
        internal Kernel Clone(string root)=>new(root,baseline,candidate);
        internal IReadOnlyList<SystemComponentDescriptor> SetCandidate(string version)
        {
            candidate=Enum.GetValues<SystemComponentKind>().Select(kind=>
            {var files=Members(kind,version);foreach(var file in files)Write(Candidate(kind,file.RelativePath),Bytes(kind,version,file.RelativePath));return new SystemComponentDescriptor(kind,FixedSystemComponents.SlotPrefix(kind),FixedSystemComponents.EntryPoint(kind),files.Single(f=>f.RelativePath==FixedSystemComponents.EntryPoint(kind)).Sha256,files);}).ToArray();return candidate;
        }
        private static byte[] Bytes(SystemComponentKind kind,string version,string path)=>Encoding.UTF8.GetBytes(kind+":"+version+":"+path);
        private static SystemComponentFile[] Members(SystemComponentKind kind,string version)=>new[]{FixedSystemComponents.EntryPoint(kind),"support.dll"}.Select(path=>{var bytes=Bytes(kind,version,path);return new SystemComponentFile(path,bytes.Length,Hash(bytes));}).ToArray();
        internal string Entry(SystemComponentKind kind)=>Live(kind,FixedSystemComponents.EntryPoint(kind));
        private string Live(SystemComponentKind kind,string path)=>Path.Combine(Root,"live",kind.ToString(),path.Replace('/',Path.DirectorySeparatorChar));
        private string Candidate(SystemComponentKind kind,string path)=>Path.Combine(Root,"candidate",kind.ToString(),path.Replace('/',Path.DirectorySeparatorChar));
        private static void Write(string path,byte[] bytes){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,bytes);}
        private void Save(SystemBootstrapSnapshot value)=>File.WriteAllBytes(Path.Combine(Root,"selection.json"),JsonSerializer.SerializeToUtf8Bytes(value));
        internal void AssertData()=>Assert(File.ReadAllText(Path.Combine(Root,"family/data.sqlite"))=="synthetic family bytes");
        private void AssertBaselineProbe(){foreach(var file in Members(SystemComponentKind.Probe,"0.6.0"))Assert(Hash(File.ReadAllBytes(Live(SystemComponentKind.Probe,file.RelativePath)))==file.Sha256);}
        internal void AssertPrevious(){foreach(var c in baseline.Components.Where(x=>x.Kind!=SystemComponentKind.Probe))foreach(var file in c.Files)Assert(Hash(File.ReadAllBytes(Live(c.Kind,file.RelativePath)))==file.Sha256);AssertBaselineProbe();}
        internal void AssertCandidate(){foreach(var c in candidate.Where(x=>x.Kind!=SystemComponentKind.Probe))foreach(var file in c.Files)Assert(Hash(File.ReadAllBytes(Live(c.Kind,file.RelativePath)))==file.Sha256);AssertBaselineProbe();}
        public Task<SystemBootstrapSnapshot> PrepareAsync(VerifiedSystemOperation op,CancellationToken c){baseline=Current;AssertPrevious();return Task.FromResult(baseline);}
        public Task VerifyHelperCandidateAsync(VerifiedSystemOperation op,CancellationToken c)=>Task.CompletedTask;
        public Task RequireCustodyAsync(VerifiedSystemOperation op,CancellationToken c){if(!CustodyAvailable)throw new IOException("SYNTHETIC_CUSTODY_UNAVAILABLE");CustodyObservation?.Invoke();return Task.CompletedTask;}
        public Task RequireDataUnchangedAsync(VerifiedSystemOperation op,CancellationToken c){AssertData();DataObservation?.Invoke();return Task.CompletedTask;}
        public Task PublishComponentAsync(VerifiedSystemOperation op,SystemBootstrapSnapshot before,SystemComponentKind kind,CancellationToken c)
        {
            if(kind==SystemComponentKind.Probe){Published.Add(kind);AssertBaselineProbe();return Task.CompletedTask;}
            var component=candidate.Single(f=>f.Kind==kind);var count=0;
            foreach(var file in component.Files)
            {
                var target=Live(kind,file.RelativePath);
                if(kind==SystemComponentKind.Owner&&ReplaceOutcome!=0&&++count==1)
                {
                    if(ReplaceOutcome is 1176 or 1177){var backup=Path.Combine(Root,"backups",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.GetDirectoryName(backup)!);File.Move(target,backup);}
                    if(ForeignFile)File.WriteAllText(Entry(kind),"foreign-unowned-by-plan");throw new Win32Exception(ReplaceOutcome);
                }
                Write(target,File.ReadAllBytes(Candidate(kind,file.RelativePath)));
            }
            Published.Add(kind);return Task.CompletedTask;
        }
        public Task StartAndObserveAuthorityAsync(VerifiedSystemOperation op,bool previous,CancellationToken c)
        {if(previous)PreviousStarts++;if(!previous&&BadAuthority)throw new SystemBootstrapCandidateFailure("SYNTHETIC_AUTHORITY_UNHEALTHY");return Task.CompletedTask;}
        public Task ObserveRuntimeBootstrapAsync(VerifiedSystemOperation op,CancellationToken c){RuntimeObservation?.Invoke();return Task.CompletedTask;}
        public Task WaitForOldHelperExitAsync(VerifiedSystemOperation op,CancellationToken c){if(!HelperExited||HelperChildAlive)throw new IOException("SYNTHETIC_HELPER_TREE_STILL_RUNNING");return Task.CompletedTask;}
        public Task PublishSelectionAsync(VerifiedSystemOperation op,CancellationToken c)
        {Save(new(1,op.InstallationId,op.ExpectedBootstrapEpoch+1,op.ToVersion,op.ManifestDigest,op.ManifestDigest,candidate.Select(x=>new SystemBootstrapComponentState(x.Kind,x.EntryPoint,x.EntrySha256,x.Files.Select(f=>new SystemBootstrapFileState(f.RelativePath,f.Size,f.Sha256)).ToArray())).ToArray()));return Task.CompletedTask;}
        public Task RestorePreviousAsync(VerifiedSystemOperation op,SystemBootstrapSnapshot before,CancellationToken c)
        {
            foreach(var old in before.Components.Where(x=>x.Kind!=SystemComponentKind.Probe))foreach(var file in old.Files)
            {
                var path=Live(old.Kind,file.RelativePath);var current=File.Exists(path)?Hash(File.ReadAllBytes(path)):null;var next=candidate.Single(x=>x.Kind==old.Kind).Files.Single(x=>x.RelativePath==file.RelativePath).Sha256;
                if(current is not null&&current!=file.Sha256&&current!=next)throw new IOException("SYNTHETIC_UNKNOWN_BINARY");Write(path,Bytes(old.Kind,before.Version,file.RelativePath));
            }
            Save(before);baseline=before;return Task.CompletedTask;
        }
        public void RecordCandidateFailure(VerifiedSystemOperation op)=>Failures++;
        public void RequireNotQuarantined(VerifiedSystemOperation op){if(Failures>=2)throw new IOException("SYNTHETIC_RELEASE_QUARANTINED");}
    }
    // Private byte model only. Independent OS A/B registrations and account
    // acceptance remain native gates; these cuts prove closure preservation.
    private sealed class SlotKernel:IRecoveryHelperSlotKernel
    {
        private readonly Fixture fixture;private readonly VerifiedSystemOperation operation;private readonly InstalledStateFiles files;
        internal string Root{get;}internal Action<string>? Cut;internal bool BadHealth;internal RecoveryHelperSlot? Executing;internal List<RecoveryHelperSlot> Writes=[];
        internal SlotKernel(Fixture fixture,VerifiedSystemOperation operation,SystemBootstrapSnapshot before,string? root=null,InstalledStateFiles? files=null)
        {
            this.fixture=fixture;this.operation=operation;this.files=files??fixture.State;Root=root??SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"yahad-ab-live"));
            if(root is not null)return;
            var old=before.Components.Single(c=>c.Kind==SystemComponentKind.Helper);var closure=new RecoveryHelperClosure(before.ManifestDigest,before.Version,old.EntrySha256,old.Files);
            foreach(var slot in Enum.GetValues<RecoveryHelperSlot>())foreach(var member in old.Files)Put(PathFor(slot,member.RelativePath),File.ReadAllBytes(Path.Combine(fixture.Kernel.Root,"live","Helper",member.RelativePath.Replace('/',Path.DirectorySeparatorChar))));
            File.WriteAllBytes(this.files.PathOf("bootstrap-helper-selection.json"),JsonSerializer.SerializeToUtf8Bytes(new RecoveryHelperSlotsSelection(1,operation.InstallationId,operation.PolicyDigest,1,RecoveryHelperSlot.A,[new(RecoveryHelperSlot.A,RecoveryHelperSlotPhase.Verified,closure),new(RecoveryHelperSlot.B,RecoveryHelperSlotPhase.Verified,closure)])));
        }
        public bool IsExecuting(RecoveryHelperSlot slot)=>Executing==slot;
        public void RequireProvisioned(){}
        private string PathFor(RecoveryHelperSlot slot,string relative)=>Path.Combine(Root,slot.ToString(),relative.Replace('/',Path.DirectorySeparatorChar));
        private static void Put(string path,byte[] bytes){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,bytes);}
        private bool Closed(RecoveryHelperSlot slot,RecoveryHelperClosure closure)=>closure.Files.All(f=>File.Exists(PathFor(slot,f.RelativePath))&&Hash(File.ReadAllBytes(PathFor(slot,f.RelativePath)))==f.Sha256);
        internal void AssertBootable()
        {
            var selected=SystemBootstrapRecoverySlots.Read(files,operation.InstallationId,operation.PolicyDigest);
            Assert(selected.Slots.Any(s=>Closed(s.Slot,s.Closure)||s.Prior is{} prior&&Closed(s.Slot,prior)));
            var active=selected.Slots.Single(s=>s.Slot==selected.Active);Assert(active.Phase==RecoveryHelperSlotPhase.Verified&&Closed(active.Slot,active.Closure));
        }
        public Task RequireClosureAsync(RecoveryHelperSlot slot,RecoveryHelperClosure closure,CancellationToken cancellation)
        {if(!Closed(slot,closure))throw new IOException("SYNTHETIC_SLOT_NOT_CLOSED");Cut?.Invoke("verified-"+slot);return Task.CompletedTask;}
        public Task PublishAsync(VerifiedSystemOperation op,RecoveryHelperSlot slot,RecoveryHelperClosure wanted,IReadOnlyList<RecoveryHelperClosure> prior,bool rollback,CancellationToken cancellation)
        {
            Assert(!IsExecuting(slot));Cut?.Invoke("intent-"+slot);Writes.Add(slot);
            foreach(var member in wanted.Files)
            {
                var source=Path.Combine(fixture.Kernel.Root,wanted.ManifestDigest==operation.ManifestDigest?"candidate":"live","Helper",member.RelativePath.Replace('/',Path.DirectorySeparatorChar));
                Put(PathFor(slot,member.RelativePath),File.ReadAllBytes(source));Cut?.Invoke("file-"+slot+"-"+member.RelativePath);
            }
            return Task.CompletedTask;
        }
        public Task<RecoveryHelperHealth> ObserveHealthyAsync(VerifiedSystemOperation op,RecoveryHelperSlot slot,RecoveryHelperClosure closure,string nonce,CancellationToken cancellation)
        {Cut?.Invoke("health-"+slot);return Task.FromResult(new RecoveryHelperHealth(slot,100+(int)slot,123,closure.EntrySha256,1,BadHealth?Guid.NewGuid().ToString("N"):nonce));}
    }
    private sealed class FaultBootstrapSource:IRuntimeBootstrapObservationSource
    {
        public bool OwnsResources{get;private set;}public bool CleanupRequired=>OwnsResources&&!observed;
        internal bool FailCleanup=true;internal int Starts,Releases;private bool observed;
        public Task PrepareAsync(InstallationBinding binding,AppRelease release,CancellationToken cancellation)
        {Starts++;OwnsResources=true;throw new IOException("SYNTHETIC_UNKNOWN_STARTED_RUNTIME");}
        public RetainedRuntimeQuiescence AcquireObservation()=>throw new IOException("NO_SYNTHETIC_NATIVE_PROOF");
        public Task RetryCleanupAsync(CancellationToken cancellation)
        {if(FailCleanup)throw new IOException("SYNTHETIC_TREE_NOT_EMPTY");observed=true;return Task.CompletedTask;}
        public void ReleaseStoppedResources(){if(!observed)throw new IOException("SYNTHETIC_NO_EMPTY_OBSERVATION");OwnsResources=false;Releases++;}
    }
}

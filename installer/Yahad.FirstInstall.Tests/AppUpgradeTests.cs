using System.IO;
using Yahad.Install;
using Yahad.Setup;

// The app track's state machine (docs/WINDOWS-APP-PACKAGE-PLAN.md 7) against a model
// of the machine: the process is killed at every effect in turn, the machine reboots as
// Windows would (lease gone, the Runtime started on whatever active.json names, the
// proxy back), and the real repair rule resumes from the persisted phase. Every run
// must end committed or exactly on the from-pair, never in between.
internal static class AppUpgradeTests
{
    private static void Assert(bool value,string what){if(!value)throw new Exception("APP_UPGRADE_ASSERTION "+what);}
    // Not an IOException: a kill is no failure any step expects.
    private sealed class Killed():Exception("KILLED");
    internal sealed class Machine(InstallerState record,InstalledPair from):IAppUpgradeMachine
    {
        internal InstallerState Record=record;internal InstalledPair Pointer=from;internal UpgradeState? State;
        internal bool RuntimeRunning=true,ProxyRunning=true,FailureActions=true,Lease;internal AppRecord RuntimeApp=from.App;internal int Database=from.App.DatabaseSchema;
        internal readonly Dictionary<string,int> Backups=[];internal readonly HashSet<string> Trees=[from.App.Version],Partials=[];internal string HelpPage=from.App.Version;
        internal readonly List<string> Violations=[];internal readonly HashSet<string> Unhealthy=[];
        internal int KillAt=int.MaxValue,Operations;internal bool Dead;internal string? FailStop,FailBackup;
        // An app whose Runtime cannot start: the fault an app update may be the fix for.
        internal readonly HashSet<string> Unstartable=[];
        private void Step(){Operations++;if(Dead)throw new Killed();if(Operations==KillAt){Dead=true;throw new Killed();}}
        // What Windows does after the process died: the lease handle is gone, the
        // automatic Runtime starts on the pointer (migrating a database it can), and the
        // proxy starts.
        internal void Reboot()
        {
            Dead=false;KillAt=int.MaxValue;Lease=false;ProxyRunning=true;Run();
        }
        private void Run(){if(Unstartable.Contains(Pointer.App.Version)){RuntimeRunning=false;return;}RuntimeRunning=true;RuntimeApp=Pointer.App;if(Database<RuntimeApp.DatabaseSchema)Database=RuntimeApp.DatabaseSchema;}
        public long Now=>1700000000;
        public void Save(UpgradeState state){Step();State=UpgradeState.Parse(state.Encode());}
        public void DeleteState(){Step();State=null;}
        public void Commit(InstallerState next){Step();next.Encode();Record=next;}
        public void RequireSpace(AppSource source)=>Step();
        public void Prepare(AppSource source,Action<int> progress){Step();Partials.Add(source.App.Version);Step();Partials.Remove(source.App.Version);Trees.Add(source.App.Version);}
        public void Remove(AppRecord app){Step();Trees.Remove(app.Version);Partials.Remove(app.Version);}
        public void Retain(InstalledPair active,InstalledPair previous,string keepBackup)
        {
            Step();Trees.RemoveWhere(v=>v!=active.App.Version&&v!=previous.App.Version);
            foreach(var id in Backups.Keys.Where(k=>k!=keepBackup).ToArray())Backups.Remove(id);
        }
        public void ClearFailureActions(){Step();FailureActions=false;}
        public void RestoreFailureActions(){Step();FailureActions=true;}
        public Task StopProxyAsync(){Step();ProxyRunning=false;return Task.CompletedTask;}
        public Task StartProxyAsync(){Step();ProxyRunning=true;return Task.CompletedTask;}
        public Task TryRestartProxyAsync(){Step();ProxyRunning=true;return Task.CompletedTask;}
        public Task StopRuntimeAsync(){Step();if(FailStop is {} code)throw new IOException(code);RuntimeRunning=false;return Task.CompletedTask;}
        public Task StartRuntimeAsync()
        {
            Step();if(Lease)Violations.Add("Runtime started under the engine's lease");
            if(!RuntimeRunning){Run();if(!RuntimeRunning)throw new IOException("SERVICE_START_FAILED");}
            return Task.CompletedTask;
        }
        public void HoldLease(){Step();if(RuntimeRunning)Violations.Add("lease taken while the Runtime runs");Lease=true;}
        public void ReleaseLease()=>Lease=false;
        public void Backup(string id){Step();if(!Lease)Violations.Add("backup without the lease");if(FailBackup is {} code)throw new IOException(code);Backups[id]=Database;}
        public void Restore(string id){Step();if(!Lease)Violations.Add("restore without the lease");Database=Backups.TryGetValue(id,out var schema)?schema:throw new IOException("UPGRADE_BACKUP_INVALID");}
        public void RemoveBackup(string id){Step();Backups.Remove(id);}
        public void WritePointer(InstalledPair pair){Step();if(RuntimeRunning)Violations.Add("pointer written while the Runtime runs");Pointer=pair;}
        public void RefreshHelpPage(InstalledPair pair){Step();HelpPage=pair.App.Version;}
        public Task RequireHealthyAsync(string version,int schema,long deadlineUnix)
        {
            Step();
            // An app never opens a database newer than it knows.
            if(!RuntimeRunning||RuntimeApp.Version!=version||Database!=schema||Database!=RuntimeApp.DatabaseSchema||Unhealthy.Contains(version))throw new IOException("SERVER_NOT_READY");
            return Task.CompletedTask;
        }
        public void Dispose(){}
    }
    internal sealed record Scenario(int FromSchema,bool UnhealthyTarget)
    {
        internal InstalledPair From=>PairFixture.Pair("1.0.0","1.0.0",schema:FromSchema);
        internal InstalledPair To=>PairFixture.Pair("1.0.0","1.0.1",schema:7);
        internal string Name=>(FromSchema==7?"same schema":"schema "+FromSchema+" to 7")+(UnhealthyTarget?", unhealthy target":"");
    }
    private static Machine Fresh(Scenario scenario){var machine=new Machine(PairFixture.State(active:scenario.From),scenario.From);if(scenario.UnhealthyTarget)machine.Unhealthy.Add("1.0.1");return machine;}
    private static async Task<UpgradeOutcome?> ForwardAsync(Machine machine,Scenario scenario)
    {
        var to=scenario.To;var host=new AppUpgradeHost(machine.Record,UpgradeState.Begin(UpgradeState.AppKind,machine.Record.Active,to,machine.Now),machine,_=>{});
        try{return await host.ForwardAsync(new AppSource(to.App,(_,_,_)=>throw new InvalidOperationException("the model extracts nothing")));}
        catch(Killed){return null;}
        finally{host.Dispose();}
    }
    // The repair task, run until nothing is left to resume.
    private static async Task RepairAsync(Machine machine)
    {
        for(var round=0;machine.State is {} state;round++)
        {
            Assert(round<3,"repair converges");
            var action=UpgradeRules.ForRepair(state.Kind,state.Phase,state.Attempts);Assert(action!=RepairAction.GiveUp,"no give-up after one kill");
            using var host=new AppUpgradeHost(machine.Record,state,machine,_=>{});
            var outcome=await RepairHost.Resume(machine.Record,state,action,host);Assert(outcome.Result.Code=="REPAIRED",state.Phase+" repaired: "+outcome.Result.Code);
        }
    }
    // Committed, or exactly the from-pair: nothing in between, nothing held.
    private static void RequireSettled(Machine machine,Scenario scenario,string what,bool? committed=null)
    {
        var from=scenario.From;var to=scenario.To;
        Assert(machine.State is null,what+": no upgrade state");Assert(!machine.Lease,what+": lease released");Assert(machine.Violations.Count==0,what+": "+string.Join("; ",machine.Violations));
        Assert(machine.RuntimeRunning&&machine.FailureActions&&machine.ProxyRunning,what+": services restored");
        Assert(UninstallChoice.SamePair(machine.Pointer,machine.Record.Active)&&machine.RuntimeApp.TreeSha256==machine.Pointer.App.TreeSha256,what+": the Runtime runs the recorded pair");
        Assert(machine.HelpPage==machine.Pointer.App.Version,what+": help page from the active app");
        Assert(machine.Partials.Count==0,what+": no partial tree");
        machine.Record.Require();
        if(UninstallChoice.SamePair(machine.Record.Active,to))
        {
            Assert(committed!=false,what+": committed");
            Assert(machine.Record.Previous is {} previous&&UninstallChoice.SamePair(previous,from),what+": the from-pair is previous");
            Assert(machine.Database==7&&machine.Trees.SetEquals([from.App.Version,to.App.Version]),what+": target schema and both app trees");
            Assert(machine.Backups.Count==(from.App.DatabaseSchema!=7?1:0),what+": only this upgrade's backup kept");
        }
        else
        {
            Assert(committed!=true,what+": rolled back");
            Assert(UninstallChoice.SamePair(machine.Record.Active,from)&&machine.Record.Previous is null,what+": the from-pair, unchanged");
            Assert(machine.Database==from.App.DatabaseSchema&&machine.Trees.SetEquals([from.App.Version])&&machine.Backups.Count==0,what+": from schema, from tree, no backup");
        }
    }
    internal static async Task RunAsync(Func<string,Func<Task>,Task> check)
    {
        var scenarios=new[]{new Scenario(6,false),new Scenario(7,false),new Scenario(6,true),new Scenario(7,true)};
        foreach(var scenario in scenarios)
            await check("an uninterrupted app apply: "+scenario.Name,async()=>
            {
                var machine=Fresh(scenario);var outcome=(await ForwardAsync(machine,scenario))!;
                Assert(outcome.Result.Code==(scenario.UnhealthyTarget?"ROLLED_BACK_UNHEALTHY":"UPGRADED"),outcome.Result.Code);
                Assert(outcome.RolledBack==scenario.UnhealthyTarget&&outcome.Exit==(scenario.UnhealthyTarget?4:0),"exit");
                RequireSettled(machine,scenario,"clean",!scenario.UnhealthyTarget);
            });
        foreach(var scenario in scenarios)
            await check("an app apply killed at every step is finished or undone by repair: "+scenario.Name,async()=>
            {
                var clean=Fresh(scenario);await ForwardAsync(clean,scenario);var total=clean.Operations;Assert(total>20,"steps counted");
                var outcomes=new HashSet<string>();
                for(var kill=1;kill<=total;kill++)
                {
                    var machine=Fresh(scenario);machine.KillAt=kill;var outcome=await ForwardAsync(machine,scenario);
                    Assert(machine.Dead,"killed at "+kill);if(outcome is not null)outcomes.Add(outcome.Result.Code);
                    Assert(outcome?.Result.Code is null or "ROLLBACK_FAILED" or "UPGRADE_COMMIT_INCOMPLETE","a killed engine reports no outcome it did not reach: "+outcome?.Result.Code);
                    machine.Reboot();await RepairAsync(machine);
                    RequireSettled(machine,scenario,"killed at "+kill+" of "+total,scenario.UnhealthyTarget?false:null);
                }
                Assert(outcomes.Contains("ROLLBACK_FAILED"),"kills during recovery are seen");
                if(!scenario.UnhealthyTarget)Assert(outcomes.Contains("UPGRADE_COMMIT_INCOMPLETE"),"kills during commit are seen");
            });
        await check("a kill in each recorded phase leaves the phase repair resumes from",async()=>
        {
            // A kill stops every later effect, so rolling_back is only persisted on the
            // way back from an unhealthy target.
            var seen=new HashSet<string>();
            foreach(var scenario in new[]{new Scenario(6,false),new Scenario(6,true)})
            {
                var clean=Fresh(scenario);await ForwardAsync(clean,scenario);
                for(var kill=1;kill<=clean.Operations;kill++)
                {var machine=Fresh(scenario);machine.KillAt=kill;await ForwardAsync(machine,scenario);if(machine.State is {} state)seen.Add(state.Phase);}
            }
            foreach(var phase in new[]{"preparing","fencing","stopping","stopped","switching","started","healthy","committed","rolling_back"})Assert(seen.Contains(phase),"phase "+phase+" reached by a kill");
        });
        foreach(var(name,configure) in new (string,Action<Machine>)[]{("the Runtime does not stop",m=>m.FailStop="INSTALL_SERVICE_STOP_STALLED"),("the backup fails",m=>m.FailBackup="UPGRADE_BACKUP_INVALID")})
            await check("an apply whose "+name.Replace("the ","")+" is discarded with the pointer unchanged",async()=>
            {
                var scenario=new Scenario(6,false);var machine=Fresh(scenario);configure(machine);
                var outcome=(await ForwardAsync(machine,scenario))!;
                Assert(outcome.Result.Code=="UPGRADE_PREPARE_FAILED"&&outcome.RolledBack&&outcome.Exit==4,outcome.Result.Code);
                Assert(outcome.Result.Failure is not null,"the cause is reported");
                RequireSettled(machine,scenario,name,false);
                Assert(UninstallChoice.SamePair(machine.Pointer,scenario.From)&&machine.HelpPage=="1.0.0","pointer and help page never moved");
            });
        // An app update may be the fix for an app that cannot start (7, admitting): a
        // discard that cannot bring the old app back still leaves the machine as it was
        // before the attempt, failure actions restored, nothing held and no upgrade state,
        // so the corrective apply is admitted.
        await check("a discard whose old app cannot start still restores failure actions, releases the lease and resolves the state",async()=>
        {
            var scenario=new Scenario(6,false);var machine=Fresh(scenario);machine.Unstartable.Add("1.0.0");machine.RuntimeRunning=false;machine.FailBackup="UPGRADE_BACKUP_INVALID";
            var outcome=(await ForwardAsync(machine,scenario))!;
            Assert(outcome.Result.Code=="ROLLBACK_FAILED"&&outcome.Exit==3&&outcome.Result.Failure is not null,"the old app is not running again: "+outcome.Result.Code);
            Assert(machine.State is null,"no upgrade state is left, so a corrective apply is admitted");
            Assert(!machine.Lease&&machine.FailureActions&&machine.Violations.Count==0,"failure actions restored, lease released: "+string.Join("; ",machine.Violations));
            Assert(UninstallChoice.SamePair(machine.Pointer,scenario.From)&&UninstallChoice.SamePair(machine.Record.Active,scenario.From)&&machine.Trees.SetEquals(["1.0.0"])&&machine.Backups.Count==0,"the from-pair, unchanged");
            // The corrective apply: the next app starts.
            var fixedApp=new Machine(machine.Record,machine.Pointer){RuntimeRunning=false};fixedApp.Unstartable.Add("1.0.0");
            var corrected=(await ForwardAsync(fixedApp,scenario))!;Assert(corrected.Result.Code=="UPGRADED",corrected.Result.Code);
        });
        await check("a repair that discards while the old app cannot start resolves the state instead of looping",async()=>
        {
            var scenario=new Scenario(6,false);var clean=Fresh(scenario);clean.Unstartable.Add("1.0.0");clean.RuntimeRunning=false;await ForwardAsync(clean,scenario);
            var discarding=0;
            for(var kill=1;kill<=clean.Operations;kill++)
            {
                var machine=Fresh(scenario);machine.Unstartable.Add("1.0.0");machine.RuntimeRunning=false;machine.KillAt=kill;await ForwardAsync(machine,scenario);machine.Reboot();
                if(machine.State is not {} state||UpgradeRules.ForRepair(state.Kind,state.Phase,state.Attempts)!=RepairAction.Discard)continue;
                discarding++;
                using var host=new AppUpgradeHost(machine.Record,state,machine,_=>{});
                var outcome=await RepairHost.Resume(machine.Record,state,RepairAction.Discard,host);
                // Only a discard that stopped the Runtime starts it again.
                var restarts=state.Phase is "stopping" or "stopped";
                Assert(outcome.Result.Code==(restarts?"ROLLBACK_FAILED":"REPAIRED"),"killed at "+kill+" in "+state.Phase+": "+outcome.Result.Code);
                Assert(machine.State is null&&!machine.Lease&&machine.FailureActions,"killed at "+kill+": state resolved, failure actions restored, lease released");
                Assert(UninstallChoice.SamePair(machine.Pointer,scenario.From)&&machine.Trees.SetEquals(["1.0.0"])&&machine.Partials.Count==0&&machine.Backups.Count==0,"killed at "+kill+": the from-pair, unchanged");
            }
            Assert(discarding>=4,"every discard phase is reached: "+discarding);
        });
        await check("a refused archive member is reported as itself once nothing of it is left",async()=>
        {
            var scenario=new Scenario(6,false);var machine=Fresh(scenario);var to=scenario.To;
            using var host=new AppUpgradeHost(machine.Record,UpgradeState.Begin(UpgradeState.AppKind,machine.Record.Active,to,machine.Now),new Refusing(machine),_=>{});
            var outcome=await host.ForwardAsync(new AppSource(to.App,(_,_,_)=>{}));
            Assert(outcome.Result.Code=="APP_PACKAGE_MEMBER_HASH"&&outcome.Exit==2,outcome.Result.Code);
            RequireSettled(machine,scenario,"refused member",false);
        });
        await check("a repair that cannot settle gives up after three rollbacks",async()=>
        {
            var scenario=new Scenario(6,true);var machine=Fresh(scenario);machine.Unhealthy.Add("1.0.0");
            var outcome=(await ForwardAsync(machine,scenario))!;Assert(outcome.Result.Code=="ROLLBACK_FAILED"&&outcome.Exit==3,outcome.Result.Code);
            for(var attempt=2;attempt<=3;attempt++)
            {
                var state=machine.State!;Assert(state.Phase=="rolling_back"&&state.Attempts==attempt-1&&state.RestoreData,"attempt "+attempt);
                using var host=new AppUpgradeHost(machine.Record,state,machine,_=>{});
                Assert((await RepairHost.Resume(machine.Record,state,UpgradeRules.ForRepair(state.Kind,state.Phase,state.Attempts),host)).Result.Code=="ROLLBACK_FAILED","attempt fails");
                Assert(!machine.Lease,"no lease held after a failed rollback");
            }
            Assert(UpgradeRules.ForRepair(machine.State!.Kind,machine.State.Phase,machine.State.Attempts)==RepairAction.GiveUp,"gives up");
        });
    }
    // The machine model, with extraction refusing one member as AppPackage would.
    private sealed class Refusing(Machine inner):IAppUpgradeMachine
    {
        public long Now=>inner.Now;
        public void Save(UpgradeState state)=>inner.Save(state);
        public void DeleteState()=>inner.DeleteState();
        public void Commit(InstallerState next)=>inner.Commit(next);
        public void RequireSpace(AppSource source)=>inner.RequireSpace(source);
        public void Prepare(AppSource source,Action<int> progress){inner.Partials.Add(source.App.Version);throw new IOException("APP_PACKAGE_MEMBER_HASH");}
        public void Remove(AppRecord app)=>inner.Remove(app);
        public void Retain(InstalledPair active,InstalledPair previous,string keepBackup)=>inner.Retain(active,previous,keepBackup);
        public void ClearFailureActions()=>inner.ClearFailureActions();
        public void RestoreFailureActions()=>inner.RestoreFailureActions();
        public Task StopProxyAsync()=>inner.StopProxyAsync();
        public Task StartProxyAsync()=>inner.StartProxyAsync();
        public Task TryRestartProxyAsync()=>inner.TryRestartProxyAsync();
        public Task StopRuntimeAsync()=>inner.StopRuntimeAsync();
        public Task StartRuntimeAsync()=>inner.StartRuntimeAsync();
        public void HoldLease()=>inner.HoldLease();
        public void ReleaseLease()=>inner.ReleaseLease();
        public void Backup(string id)=>inner.Backup(id);
        public void Restore(string id)=>inner.Restore(id);
        public void RemoveBackup(string id)=>inner.RemoveBackup(id);
        public void WritePointer(InstalledPair pair)=>inner.WritePointer(pair);
        public void RefreshHelpPage(InstalledPair pair)=>inner.RefreshHelpPage(pair);
        public Task RequireHealthyAsync(string version,int schema,long deadlineUnix)=>inner.RequireHealthyAsync(version,schema,deadlineUnix);
        public void Dispose(){}
    }
}

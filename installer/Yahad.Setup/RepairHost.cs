using System.IO;
using Yahad.Install;
namespace Yahad.Setup;

// --repair: finishes or undoes an interrupted upgrade, or brings the active pair's
// services and task back to their recorded configuration. It needs no package (4.4):
// the upgrade state, the installer record and the retained trees hold everything.
internal static class RepairHost
{
    internal static readonly TimeSpan LockWait=TimeSpan.FromSeconds(60);
    // Not an outcome: the caller hands the repair to a private copy (PrivateCopy), whose
    // result and exit code are the repair's.
    internal const string HandOff="REPAIR_HAND_OFF";
    internal static async Task<UpgradeOutcome> RunAsync(Action<SetupProgress> progress)
    {
        try
        {
            NativeInstallFiles.RequireAdministrator();
            using var gate=UpgradeHost.Lock(LockWait);
            var installed=UpgradeFiles.ReadInstalled();progress(new("checking",0));
            if(File.Exists(InstallLocations.UpgradeStateFile))return await ResumeAsync(installed,UpgradeFiles.Load(),progress);
            return await HealthPassAsync(installed,progress);
        }
        catch(Exception error){return new(new(false,SetupOperation.Code(error),Failure:SetupFailureDetail.From(error)));}
    }
    // The infrastructure trees an action deletes: undoing deletes the target's own new
    // tree, a commit every tree outside the two retained pairs.
    internal static bool Deletes(UpgradeState state,RepairAction action,string infrastructure)=>action switch
    {
        RepairAction.FinishCommit=>!UpgradeRules.Retained(TreeKind.Infrastructure,infrastructure,state.To,state.From),
        RepairAction.Discard or RepairAction.RollBack or RepairAction.FinishRollback=>infrastructure==state.To.Infrastructure.Version&&!state.To.SameInfrastructure(state.From),
        _=>false
    };
    // The infrastructure version whose engine this process is, when it runs from one.
    private static string? RunningTree()
    {
        var image=Environment.ProcessPath??throw new IOException("SETUP_IMAGE");
        if(!SetupOperation.Within(image,InstalledLayout.InfraParent))return null;
        return Path.GetRelativePath(InstalledLayout.InfraParent,image).Split(Path.DirectorySeparatorChar)[0];
    }
    private static async Task<UpgradeOutcome> ResumeAsync(InstallerState installed,UpgradeState state,Action<SetupProgress> progress)
    {
        var url="http://127.0.0.1:"+installed.Port+"/";
        UpgradeOutcome Done(string code,bool rolledBack=false,Exception? failure=null)=>new(new(code=="REPAIRED",code,url,Failure:failure is null?null:SetupFailureDetail.From(failure)),state.From,state.To,rolledBack);
        var action=UpgradeRules.ForRepair(state.Phase,state.Attempts);
        // An engine never deletes the tree it runs from: it hands the repair to a private
        // copy of itself (8.1), which takes the lock once this process has let it go.
        if(RunningTree() is {} own&&Deletes(state,action,own))return Done(HandOff);
        if(action==RepairAction.GiveUp)
        {
            // Stop retrying until an administrator acts; the record stays for them.
            ServiceStartupDiagnostic.TryRecordUpgrade(state.Id,state.From.Describe,state.To.Describe,state.Attempts);
            return Done("ROLLBACK_FAILED");
        }
        using var host=new UpgradeHost(installed,state,progress);
        try
        {
            switch(action)
            {
                case RepairAction.Discard:await host.DiscardAsync(null);return Done("REPAIRED",true);
                case RepairAction.RollBack:await host.RollBackAsync(state.Failure??"UPGRADE_INTERRUPTED");return Done("REPAIRED",true);
                case RepairAction.FinishRollback:host.FinishRollback();return Done("REPAIRED",true);
                default:host.FinishCommitAfterRepair();return Done("REPAIRED");
            }
        }
        catch(Exception failure){return Done(action==RepairAction.FinishCommit?"UPGRADE_COMMIT_INCOMPLETE":"ROLLBACK_FAILED",failure:failure);}
    }
    private static async Task<UpgradeOutcome> HealthPassAsync(InstallerState installed,Action<SetupProgress> progress)
    {
        var url="http://127.0.0.1:"+installed.Port+"/";var repaired=false;var active=installed.Active;var infrastructure=active.Infrastructure.Version;
        // The pointer and both trees it names are what every binary starts from.
        var pointer=active.Pointer().Encode();
        var stale=!File.Exists(InstalledLayout.ActivePointerFile)||!NativeInstallFiles.ReadRecord(InstalledLayout.ActivePointerFile,ActivePointer.MaximumBytes).SequenceEqual(pointer);
        TreeRecord.VerifyInstalled(InstalledLayout.InfraRoot(infrastructure),TreeKind.Infrastructure,infrastructure,active.Infrastructure.TreeSha256);
        TreeRecord.VerifyInstalled(InstalledLayout.AppRoot(active.App.Version),TreeKind.App,active.App.Version,active.App.TreeSha256);
        // Each on its own: an Updater that cannot start says nothing about the Runtime.
        async Task<NativeService> Restore(ServiceSpec spec)
        {
            var repointed=false;var service=OpenOrCreate(installed,spec,ref repointed);repaired|=repointed;
            try
            {
                // A process started from another image, or from a pointer naming another pair,
                // is not the installed pair.
                if((repointed||stale)&&!service.IsStopped())await service.StopAsync(CancellationToken.None);
                // The pointer is written only while the Runtime is stopped (UpgradeFiles.WritePointer).
                if(stale&&spec==ServiceSpec.Runtime){service.ConfirmStopped();UpgradeFiles.WritePointer(active);repaired=true;}
                if(!service.StartupConfigured()){service.EnableStartup();repaired=true;}
                if(!service.IsRunning()){progress(new("starting_server",50));await service.StartAsync(CancellationToken.None);repaired=true;}
                return service;
            }
            catch{service.Dispose();throw;}
        }
        using var service=await Restore(ServiceSpec.Runtime);
        UpgradeFiles.EnsureUpdaterState(installed.Consent.Automatic);
        using var updater=await Restore(ServiceSpec.Updater);
        if(NativeService.Exists(ServiceSpec.Lan))
        {
            var repointed=false;using var proxy=NativeService.OpenRepointed(infrastructure,ref repointed,ServiceSpec.Lan);
            if(repointed)
            {
                repaired=true;await NativeLanInstall.StopForUpgradeAsync(proxy);
                new NativeLanInstall(installed).MoveFirewall(installed.Previous?.Infrastructure.Version);
            }
            repaired|=await NativeLanInstall.TryRestartAsync(proxy);
        }
        if(!RepairTask.Matches(infrastructure)){RepairTask.Register(infrastructure,DateTimeOffset.Now);repaired=true;}
        try{await UpgradeFiles.RequireHealthyAsync(service,installed.Port,active.App.Version,active.App.DatabaseSchema,UpgradeFiles.HealthDeadline());}
        catch(IOException failure){return new(new(false,"REPAIR_UNHEALTHY",url,Failure:SetupFailureDetail.From(failure)),active);}
        return new(new(true,repaired?"REPAIRED":"NOTHING_TO_REPAIR",url),active);
    }
    private static NativeService OpenOrCreate(InstallerState installed,ServiceSpec spec,ref bool repaired)
    {
        var infrastructure=installed.Active.Infrastructure.Version;
        if(!NativeService.Exists(spec)){repaired=true;return NativeService.Create(infrastructure,installed.OwnerSid,spec);}
        return NativeService.OpenRepointed(infrastructure,ref repaired,spec);
    }
}

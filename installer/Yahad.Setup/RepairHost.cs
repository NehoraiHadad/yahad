using System.IO;
using Yahad.Install;
namespace Yahad.Setup;

// --repair: finishes or undoes an interrupted upgrade, or brings the installed
// release's services and task back to their recorded configuration. It needs no release: the
// upgrade state and the retained version directories hold everything.
internal static class RepairHost
{
    internal static readonly TimeSpan LockWait=TimeSpan.FromSeconds(60);
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
    private static async Task<UpgradeOutcome> ResumeAsync(InstallerState installed,UpgradeState state,Action<SetupProgress> progress)
    {
        var url="http://127.0.0.1:"+installed.Port+"/";
        UpgradeOutcome Done(string code,bool rolledBack=false,Exception? failure=null)=>new(new(code=="REPAIRED",code,url,Failure:failure is null?null:SetupFailureDetail.From(failure)),state.From,state.To,rolledBack);
        var action=UpgradeRules.ForRepair(state.Phase,state.Attempts);
        // Undoing the upgrade deletes the target directory, so it cannot be done
        // from that directory's own Setup; the retained one is the previous release's.
        if(action!=RepairAction.FinishCommit&&SetupOperation.Within(Environment.ProcessPath!,InstallLocations.VersionRoot(state.To)))throw new IOException("REPAIR_RUNNING_FROM_TARGET");
        if(action==RepairAction.GiveUp)
        {
            // Stop retrying until an administrator acts; the record stays for them.
            ServiceStartupDiagnostic.TryRecordUpgrade(state.Id,state.From,state.To,state.Attempts);
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
        var url="http://127.0.0.1:"+installed.Port+"/";var repaired=false;
        // Each on its own: an Updater that cannot start says nothing about the Runtime.
        async Task<NativeService> Restore(ServiceSpec spec)
        {
            var repointed=false;var service=OpenOrCreate(installed,spec,ref repointed);repaired|=repointed;
            try
            {
                // A process started from another image is not the installed release.
                if(repointed&&!service.IsStopped())await service.StopAsync(CancellationToken.None);
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
            var repointed=false;using var proxy=NativeService.OpenRepointed(installed.Version,ref repointed,ServiceSpec.Lan);
            if(repointed)
            {
                repaired=true;await NativeLanInstall.StopForUpgradeAsync(proxy);
                new NativeLanInstall(installed).MoveFirewall(installed.Previous?.Version);
            }
            repaired|=await NativeLanInstall.TryRestartAsync(proxy);
        }
        if(!RepairTask.Matches(installed.Version)){RepairTask.Register(installed.Version,DateTimeOffset.Now);repaired=true;}
        try{await UpgradeFiles.RequireHealthyAsync(service,installed.Port,installed.Version,null,UpgradeFiles.HealthDeadline());}
        catch(IOException failure){return new(new(false,"REPAIR_UNHEALTHY",url,Failure:SetupFailureDetail.From(failure)),installed.Version);}
        return new(new(true,repaired?"REPAIRED":"NOTHING_TO_REPAIR",url),installed.Version);
    }
    private static NativeService OpenOrCreate(InstallerState installed,ServiceSpec spec,ref bool repaired)
    {
        if(!NativeService.Exists(spec)){repaired=true;return NativeService.Create(installed.Version,installed.OwnerSid,spec);}
        return NativeService.OpenRepointed(installed.Version,ref repaired,spec);
    }
}

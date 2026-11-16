using System.IO;
using Yahad.Install;
namespace Yahad.Setup;

// The app a move installs and how its members reach a tree root: the signed app
// archive (AppPackage) or the app namespace of a Setup's own package (8.2).
internal sealed record AppSource(AppRecord App,Action<string,ExtractTarget,Action<int>> Extract)
{internal long Bytes=>App.Files.Sum(f=>f.Size);}
// Every effect the app track has on the machine. NativeAppMachine is the machine;
// the tests drive the same host through a model of it.
internal interface IAppUpgradeMachine:IDisposable
{
    long Now{get;}
    void Save(UpgradeState state);
    void DeleteState();
    void Commit(InstallerState next);
    void RequireSpace(AppSource source);
    void Prepare(AppSource source,Action<int> progress);
    // The tree of that app version, whole or partial.
    void Remove(AppRecord app);
    void Retain(InstalledPair active,InstalledPair previous,string keepBackup);
    void ClearFailureActions();
    void RestoreFailureActions();
    Task StopProxyAsync();
    // Strict: serving again, as after a full upgrade.
    Task StartProxyAsync();
    // Best effort: a proxy that cannot serve must not hold up the Runtime's recovery.
    Task TryRestartProxyAsync();
    Task StopRuntimeAsync();
    // Running afterwards, whether or not it already was.
    Task StartRuntimeAsync();
    void HoldLease();
    // Idempotent and never throws.
    void ReleaseLease();
    void Backup(string id);
    void Restore(string id);
    void RemoveBackup(string id);
    void WritePointer(InstalledPair pair);
    void RefreshHelpPage(InstalledPair pair);
    Task RequireHealthyAsync(string version,int schema,long deadlineUnix);
}
// The app track (docs/WINDOWS-APP-PACKAGE-PLAN.md 7): the same infrastructure tree,
// its images, firewall rule, Caddyfile and Updater untouched, and a newer app. Each
// phase is recorded as its step begins, so a repair applies that step's "on failure"
// rule: until the switch only what was fenced or stopped is undone, from the switch on
// the from-pair is restored. Every exit ends with failure actions restored and the
// data lease released.
internal sealed class AppUpgradeHost(InstallerState installed,UpgradeState state,IAppUpgradeMachine machine,Action<SetupProgress> progress):IUpgradeRepair
{
    private InstallerState installed=installed;private UpgradeState state=state;
    internal UpgradeState State=>state;
    // --apply-app <stage>: the Updater staged the release in a directory named by its
    // version (9.3) and launched the active infrastructure's engine on it.
    internal static async Task<UpgradeOutcome> RunAsync(string stage,Action<SetupProgress> progress)
    {
        InstalledPair? from=null,to=null;
        try
        {
            NativeInstallFiles.RequireAdministrator();
            using var gate=UpgradeHost.Lock(null);
            var installed=UpgradeFiles.ReadInstalled();from=installed.Active;
            if(File.Exists(InstallLocations.UpgradeStateFile))throw new IOException("UPGRADE_INCOMPLETE");
            progress(new("admitting",0));
            var key=TrustedReleaseKey.Require(UpgradeFiles.InstalledKey(),TrustedReleaseKey.Embedded);
            var version=Path.GetFileName(Path.TrimEndingDirectorySeparator(stage));
            var release=ReleaseStage.Read(stage,version,key);
            ReleaseStage.RequireTrack(release,installed,ReleaseTrack.App);
            using var package=AppPackage.Open(Path.Combine(stage,ReleaseManifest.AppArchiveName(version)),release);
            to=installed.Active with{App=package.App};
            return await ApplyAsync(installed,new AppSource(package.App,(root,target,p)=>package.Extract(root,p,CancellationToken.None,target)),progress);
        }
        catch(Exception error){return new(new(false,SetupOperation.Code(error),DataPreserved:true,Failure:SetupFailureDetail.From(error)),from,to);}
    }
    // Admitted by the caller, which holds the install lock.
    internal static async Task<UpgradeOutcome> ApplyAsync(InstallerState installed,AppSource source,Action<SetupProgress> progress)
    {
        var to=installed.Active with{App=source.App};
        try
        {
            if(UpgradeRules.Decide(installed.Active,to)!=UpgradeTrack.App)throw new IOException("RELEASE_TRACK_MISMATCH");
            // The record the commit writes, checked before anything changes.
            _=installed.After(to).Encode();
            var machine=new NativeAppMachine(installed);
            using var host=new AppUpgradeHost(installed,UpgradeState.Begin(UpgradeState.AppKind,installed.Active,to,machine.Now),machine,progress);
            return await host.ForwardAsync(source);
        }
        catch(Exception error){return new(new(false,SetupOperation.Code(error),DataPreserved:true,Failure:SetupFailureDetail.From(error)),installed.Active,to);}
    }
    private static int Position(string phase)=>Array.IndexOf(UpgradeState.AppPhases,phase);
    private bool Reached(string phase)=>Position(state.Phase)>=Position(phase);
    private void Advance(string phase){state=state with{Phase=phase};machine.Save(state);progress(new(phase,0));}
    private string LocalUrl=>"http://127.0.0.1:"+installed.Port+"/";
    private UpgradeOutcome Result(string code,bool rolledBack=false,Exception? failure=null)
        =>new(new(code is "UPGRADED",code,LocalUrl,DataPreserved:true,Failure:failure is null?null:SetupFailureDetail.From(failure)),state.From,state.To,rolledBack);
    // Admission's last check, the free space, writes nothing; from here every step is
    // recorded before it begins.
    internal async Task<UpgradeOutcome> ForwardAsync(AppSource source)
    {
        machine.RequireSpace(source);
        try
        {
            Advance("preparing");machine.Prepare(source,p=>progress(new("extracting",p)));
            Advance("fencing");machine.ClearFailureActions();
            Advance("stopping");await machine.StopProxyAsync();await machine.StopRuntimeAsync();
            Advance("stopped");machine.HoldLease();if(state.SchemaChanged)machine.Backup(state.Id);
            Advance("switching");machine.WritePointer(state.To);machine.RefreshHelpPage(state.To);
            state=state with{HealthDeadlineUnix=machine.Now+UpgradeRules.HealthWindowSeconds};
            Advance("started");machine.ReleaseLease();await machine.StartRuntimeAsync();
            await machine.RequireHealthyAsync(state.To.App.Version,state.ToSchema,state.HealthDeadlineUnix);
            Advance("healthy");await machine.StartProxyAsync();machine.RestoreFailureActions();
        }
        catch(Exception failure){return await RecoverAsync(failure);}
        try{Commit();}
        catch(Exception failure){return Result("UPGRADE_COMMIT_INCOMPLETE",failure:failure);}
        return Result("UPGRADED");
    }
    private async Task<UpgradeOutcome> RecoverAsync(Exception cause)
    {
        var code=SetupOperation.Code(cause);
        try
        {
            if(!Reached("switching")){await DiscardAsync(code);return Result(UpgradeRules.Discarded(code),true,cause);}
            await RollBackAsync(code);return Result("ROLLED_BACK_UNHEALTHY",true,cause);
        }
        catch(Exception failure){machine.ReleaseLease();return Result("ROLLBACK_FAILED",failure:failure);}
    }
    // The pointer never moved, so the from-pair is whole once its failure actions are
    // back and the target is gone: the attempt is resolved before what it stopped is
    // started again. The old app may be unable to start (an app update may be its fix,
    // 7 admitting); that leaves no upgrade state to refuse the next apply or to repeat.
    public async Task DiscardAsync(string? failure)
    {
        state=state with{Failure=failure??state.Failure};machine.ReleaseLease();
        if(Reached("fencing"))machine.RestoreFailureActions();
        var stopped=Reached("stopping");
        FinishRollback();
        if(stopped){await machine.StartRuntimeAsync();await machine.TryRestartProxyAsync();}
    }
    public async Task RollBackAsync(string failure)
    {
        machine.ReleaseLease();
        state=state with{Phase="rolling_back",Failure=failure,RestoreData=UpgradeRules.RestoresData(state),Attempts=state.Attempts+1};machine.Save(state);progress(new("rolling_back",0));
        await machine.StopProxyAsync();await machine.StopRuntimeAsync();machine.HoldLease();
        machine.WritePointer(state.From);if(state.RestoreData)machine.Restore(state.Id);machine.RefreshHelpPage(state.From);
        machine.ReleaseLease();await machine.StartRuntimeAsync();
        await machine.RequireHealthyAsync(state.From.App.Version,state.FromSchema,machine.Now+UpgradeRules.HealthWindowSeconds);
        await machine.TryRestartProxyAsync();machine.RestoreFailureActions();
        Advance("rolled_back");FinishRollback();
    }
    // The target app tree goes, partial or whole: it is newer than every retained one.
    public void FinishRollback(){machine.Remove(state.To.App);machine.RemoveBackup(state.Id);machine.DeleteState();}
    public void FinishCommitAfterRepair(){machine.RestoreFailureActions();Commit();}
    // Idempotent: the record moves once, and a repair repeats the rest.
    private void Commit()
    {
        Advance("committed");
        InstallerState next;
        if(UninstallChoice.SamePair(installed.Active,state.From))next=installed.After(state.To);
        else if(UninstallChoice.SamePair(installed.Active,state.To))next=installed;
        else throw new IOException("INSTALL_STATE_INVALID");
        machine.Commit(next);installed=next;
        machine.Retain(state.To,state.From,state.Id);
        machine.DeleteState();
    }
    // The host owns its machine.
    public void Dispose()=>machine.Dispose();
}
// The machine the app track moves: the active infrastructure's Runtime and proxy
// services, its dataset, the binary root and the installer record.
internal sealed class NativeAppMachine(InstallerState installed):IAppUpgradeMachine
{
    private NativeService? runtime,proxy;private bool proxyKnown;private WindowsFileBoundary.DataLease? lease;
    private string Dataset=>Path.Combine(InstallLocations.State,"datasets",installed.DataId);
    private string DataPath=>Path.Combine(Dataset,"data");
    private string BackupRoot=>Path.Combine(Dataset,"upgrade-backup");
    private string Infrastructure=>installed.Active.Infrastructure.Version;
    private NativeService Runtime(){runtime??=NativeService.Open(Infrastructure);return runtime;}
    private NativeService? Proxy()
    {if(!proxyKnown){proxyKnown=true;if(NativeService.Exists(ServiceSpec.Lan))proxy=NativeService.Open(Infrastructure,ServiceSpec.Lan);}return proxy;}
    private WindowsFileBoundary.DataLease Held()=>lease??throw new InvalidOperationException("The data lease is not held.");
    public long Now=>DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public void Save(UpgradeState state)=>UpgradeFiles.Save(state);
    public void DeleteState()=>UpgradeFiles.DeleteState();
    public void Commit(InstallerState next)
    {
        NativeInstallFiles.Publish(InstallLocations.StateFile,next.Encode());installed=next;
        NativeInstallFiles.SetTreeAcl(InstallLocations.Binaries,NativeSetupHost.BinaryAcl);NativeSetupHost.Register(next);
    }
    public void RequireSpace(AppSource source)=>UpgradeFiles.RequireSpace(source.Bytes,DataPath);
    public void Prepare(AppSource source,Action<int> progress)
    {
        FullInstallerResidue.Clear(InstallLocations.State);
        var app=source.App;var root=InstalledLayout.AppRoot(app.Version);
        UpgradeFiles.PrepareTree(root,(partial,target)=>source.Extract(partial,target,progress));
        TreeRecord.VerifyInstalled(root,TreeKind.App,app.Version,app.TreeSha256);
    }
    public void Remove(AppRecord app){var root=InstalledLayout.AppRoot(app.Version);UpgradeFiles.Remove(UpgradeFiles.Partial(root));UpgradeFiles.Remove(root);}
    public void Retain(InstalledPair active,InstalledPair previous,string keepBackup)=>UpgradeFiles.Retain(active,previous,BackupRoot,keepBackup);
    public void ClearFailureActions()=>Runtime().ClearFailureActions();
    public void RestoreFailureActions()=>Runtime().EnableStartup();
    public async Task StopProxyAsync(){if(Proxy() is {} lan)await NativeLanInstall.StopForUpgradeAsync(lan);}
    public async Task StartProxyAsync(){if(Proxy() is {} lan)await new NativeLanInstall(installed).StartAfterUpgradeAsync(lan);}
    public async Task TryRestartProxyAsync(){if(Proxy() is {} lan)await NativeLanInstall.TryRestartAsync(lan);}
    public Task StopRuntimeAsync()=>Runtime().StopAsync(CancellationToken.None);
    public async Task StartRuntimeAsync()
    {
        // A stop may still be settling after a kill: settle it, then start.
        var service=Runtime();
        if(!service.IsRunning()){await service.StopAsync(CancellationToken.None);await service.StartAsync(CancellationToken.None);}
    }
    public void HoldLease()=>lease??=WindowsFileBoundary.AcquireDataLease(DataPath);
    public void ReleaseLease(){lease?.Dispose();lease=null;}
    public void Backup(string id)=>DatabaseBackup.Create(DataPath,Path.Combine(BackupRoot,id),Held());
    public void Restore(string id)=>DatabaseBackup.Restore(DataPath,Path.Combine(BackupRoot,id),Held());
    public void RemoveBackup(string id)=>UpgradeFiles.Remove(Path.Combine(BackupRoot,id));
    public void WritePointer(InstalledPair pair){Runtime().ConfirmStopped();UpgradeFiles.WritePointer(pair);}
    public void RefreshHelpPage(InstalledPair pair){if(Proxy() is not null)new NativeLanInstall(installed,pair).RefreshHelpPage(pair.App);}
    public Task RequireHealthyAsync(string version,int schema,long deadlineUnix)=>UpgradeFiles.RequireHealthyAsync(Runtime(),installed.Port,version,schema,deadlineUnix);
    public void Dispose(){ReleaseLease();runtime?.Dispose();runtime=null;proxy?.Dispose();proxy=null;}
}

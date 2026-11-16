using System.IO;
using System.Net.Http;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;

internal sealed record UpgradeOutcome(SetupResult Result,InstalledPair? From=null,InstalledPair? To=null,bool RolledBack=false)
{internal int Exit=>UpgradeRules.Exit(Result.Code);}
internal static class UpgradeFiles
{
    // Same link checks as removal: a link is never followed out of the tree, and the
    // image of an engine waiting on its private copy is moved out, not deleted.
    internal static void Remove(string path)
    {
        if(!Directory.Exists(path))return;
        var tree=RemovalTree.Inspect(path,WindowsFileBoundary.RequireNoLink);
        tree.DeleteContents(PrivateCopy.DeleteMember,_=>{},CancellationToken.None);
        WindowsFileBoundary.DeleteNoFollow(tree.Root,true);
    }
    // The Updater's own folder and mode record, which outlive any one release.
    internal static void EnsureUpdaterState(bool automatic)
    {
        var root=InstalledLayout.UpdaterRoot;
        if(!Directory.Exists(root))NativeInstallFiles.NewDirectory(root);
        var state=Path.Combine(root,"state.json");
        if(!File.Exists(state))NativeInstallFiles.PublishNew(state,UpdaterRecord.Seed(automatic).Encode());
    }
    internal static void Save(UpgradeState state)=>NativeInstallFiles.Publish(InstallLocations.UpgradeStateFile,state.Encode());
    internal static UpgradeState Load()
    {InstallLocations.RequireStateRoot();return UpgradeState.Parse(NativeInstallFiles.ReadRecord(InstallLocations.UpgradeStateFile,UpgradeState.MaximumBytes));}
    internal static void DeleteState()=>File.Delete(InstallLocations.UpgradeStateFile);
    // The pointer every binary runs; written only while the Runtime is stopped.
    internal static void WritePointer(InstalledPair pair)=>NativeInstallFiles.Publish(InstalledLayout.ActivePointerFile,pair.Pointer().Encode());
    internal static InstallerState ReadInstalled()
    {
        ExistingInstallation.RequireNotLegacy();
        if(File.Exists(InstallLocations.RemovalStateFile)||!File.Exists(InstallLocations.StateFile))throw new IOException("NOT_INSTALLED");
        var state=NativeSetupHost.ReadState();
        if(state.Phase is "uninstalled")throw new IOException("NOT_INSTALLED");
        if(state.Phase!="committed")throw new IOException("INSTALL_INCOMPLETE");
        return state;
    }
    // The installed key is the root of trust (TrustedReleaseKey.Require).
    internal static byte[]? InstalledKey()
    {
        var path=InstallLocations.ReleaseKeyFile;if(!File.Exists(path))return null;
        return NativeInstallFiles.ReadRecord(path);
    }
    internal static string Partial(string root)=>root+".partial";
    // The space for the new trees and the database copy, on both volumes.
    internal static void RequireSpace(long treeBytes,string data)
    {
        if(new DriveInfo(Path.GetPathRoot(InstallLocations.Binaries)!).AvailableFreeSpace<checked(treeBytes+536870912L)||
            new DriveInfo(Path.GetPathRoot(InstallLocations.State)!).AvailableFreeSpace<checked(DatabaseBackup.Size(data)+536870912L))throw new IOException("DISK_SPACE");
    }
    // One new tree, extracted beside its final name and renamed into it whole. Anything
    // already there is left from an earlier failed attempt at this version, never a tree
    // in use: each target version is newer than every retained one.
    internal static void PrepareTree(string root,Action<string,ExtractTarget> extract)
    {
        var partial=Partial(root);Remove(partial);Remove(root);var parent=Path.GetDirectoryName(root)!;
        if(!Directory.Exists(parent))NativeInstallFiles.EnsureDirectoryUnder(InstallLocations.Binaries,parent);
        NativeInstallFiles.EnsureDirectoryUnder(parent,partial);
        extract(partial,new ExtractTarget(directory=>NativeInstallFiles.EnsureDirectoryUnder(partial,directory),NativeInstallFiles.CreateNewFile,NativeInstallFiles.CommitNew,(_,_)=>false));
        Directory.Move(partial,root);
    }
    // Retention after a commit is the active pair and the one it replaced; every other
    // tree goes, and every backup but the one this upgrade took.
    internal static void Retain(InstalledPair active,InstalledPair previous,string backupRoot,string keepBackup)
    {
        foreach(var(kind,parent) in new[]{(TreeKind.Infrastructure,InstalledLayout.InfraParent),(TreeKind.App,InstalledLayout.AppParent)})
        {
            if(!Directory.Exists(parent))continue;
            foreach(var directory in Directory.EnumerateDirectories(parent))
            {if(!UpgradeRules.Retained(kind,Path.GetFileName(directory),active,previous))Remove(directory);}
        }
        if(Directory.Exists(backupRoot))foreach(var directory in Directory.EnumerateDirectories(backupRoot))
        {if(Path.GetFileName(directory)!=keepBackup)Remove(directory);}
    }
    internal static async Task<HealthObservation?> ReadHealthAsync(int port)
    {
        using var client=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(10)};
        try
        {
            using var response=await client.GetAsync("http://127.0.0.1:"+port+"/health/ready");if(!response.IsSuccessStatusCode)return new(false,null,null);
            var bytes=await response.Content.ReadAsByteArrayAsync();if(bytes.Length>16384)return null;
            var value=NativeJson.Read<JsonElement>(bytes);return new(value.GetProperty("ok").GetBoolean(),value.GetProperty("version").GetString(),value.GetProperty("databaseSchema").GetInt32());
        }
        catch(Exception error)when(error is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or IOException){return null;}
    }
    // Consecutive answers from this app at this schema, not one lucky poll, and an
    // early end when the service itself has already stopped.
    internal static async Task RequireHealthyAsync(NativeService service,int port,string app,int schema,long deadlineUnix)
    {
        var healthy=0;
        while(true)
        {
            healthy=UpgradeRules.NextHealthyPolls(healthy,UpgradeRules.HealthPasses(await ReadHealthAsync(port),app,schema));
            if(healthy>=UpgradeRules.RequiredHealthyPolls)return;
            if(service.IsStopped())throw new IOException("SERVICE_START_FAILED");
            if(DateTimeOffset.UtcNow.ToUnixTimeSeconds()>=deadlineUnix)throw new IOException("SERVER_NOT_READY");
            await Task.Delay(TimeSpan.FromSeconds(UpgradeRules.HealthPollSeconds));
        }
    }
    internal static long HealthDeadline()=>DateTimeOffset.UtcNow.ToUnixTimeSeconds()+UpgradeRules.HealthWindowSeconds;
}
// What repair asks of either track's host (RepairHost.Resume).
internal interface IUpgradeRepair:IDisposable
{
    Task DiscardAsync(string? failure);
    Task RollBackAsync(string failure);
    void FinishRollback();
    void FinishCommitAfterRepair();
}
// The Setup track (8.1): a Setup carrying (infraB, appY) moves an installation from
// (infraA, appX) with infraB newer. It prepares infra\B (and apps\Y when the app
// changes) and switches the three images, the firewall, the Caddyfile, the help page
// and the pointer. Rollback returns everything to the from-pair, which was committed
// together. A service's image is its infrastructure pointer; one SCM call moves it.
// The same infrastructure with a newer app is the app track (7, AppUpgradeHost).
internal sealed class UpgradeHost(InstallerState installed,UpgradeState state,Action<SetupProgress> progress):IUpgradeRepair
{
    private InstallerState installed=installed;private UpgradeState state=state;private NativeService? service,updater,proxy;
    private string Dataset=>Path.Combine(InstallLocations.State,"datasets",installed.DataId);
    private string DataPath=>Path.Combine(Dataset,"data");
    private string BackupRoot=>Path.Combine(Dataset,"upgrade-backup");
    private string Backup=>Path.Combine(BackupRoot,state.Id);
    private bool MovesApp=>!state.To.SameApp(state.From);
    internal UpgradeState State=>state;
    // An administrator ran a newer Setup over this installation: the same move, instead
    // of asking them to remove it first. They elevated this image, which is what a signed
    // manifest would vouch for, and removing and reinstalling needs no manifest either.
    // It must still carry the key the installation trusts. The caller holds the lock.
    internal static bool Applies(InstallPackage package)
    {
        if(File.Exists(InstallLocations.RemovalStateFile)||File.Exists(InstallLocations.UpgradeStateFile)||!File.Exists(InstallLocations.StateFile))return false;
        ExistingInstallation.RequireNotLegacy();
        var state=NativeSetupHost.ReadState();
        return state.Phase=="committed"&&UpgradeRules.IsUpgrade(state.Active,package.Pair());
    }
    // The manual path (8.2): the embedded pair decides the track. The same infrastructure,
    // byte for byte, applies only the embedded app through the app track's state machine.
    internal static async Task<UpgradeOutcome> FromThisSetupAsync(InstallPackage package,Action<SetupProgress> progress)
    {
        InstalledPair? from=null;var to=package.Pair();
        try
        {
            var installed=UpgradeFiles.ReadInstalled();from=installed.Active;
            TrustedReleaseKey.Require(UpgradeFiles.InstalledKey(),TrustedReleaseKey.Embedded);
            if(RequireAdmission(installed,package)==UpgradeTrack.App)
                return await AppUpgradeHost.ApplyAsync(installed,new AppSource(package.AppRecord(),(root,target,p)=>package.Extract(TreeKind.App,root,p,CancellationToken.None,target)),progress);
            using var host=new UpgradeHost(installed,UpgradeState.Begin(UpgradeState.SetupKind,installed.Active,to,DateTimeOffset.UtcNow.ToUnixTimeSeconds()),progress);
            return await host.ForwardAsync(package);
        }
        catch(Exception error){return new(new(false,SetupOperation.Code(error),DataPreserved:true,Failure:SetupFailureDetail.From(error)),from,to);}
    }
    // --quiet-upgrade <stage>: the Updater staged this Setup with its signed release.
    internal static async Task<UpgradeOutcome> RunAsync(string directory,Action<SetupProgress> progress)
    {
        InstalledPair? from=null,to=null;
        try
        {
            NativeInstallFiles.RequireAdministrator();
            using var gate=Lock(null);
            var installed=UpgradeFiles.ReadInstalled();from=installed.Active;
            if(File.Exists(InstallLocations.UpgradeStateFile))throw new IOException("UPGRADE_INCOMPLETE");
            progress(new("checking",0));
            var key=TrustedReleaseKey.Require(UpgradeFiles.InstalledKey(),TrustedReleaseKey.Embedded);
            // The release this image is: its own version names the files it reads.
            using var package=InstallPackage.Embedded();to=package.Pair();
            var release=ReleaseStage.Read(directory,package.Manifest.App.Version,key);
            ReleaseStage.RequireTrack(release,installed,ReleaseTrack.Setup);
            RequireThisSetup(release,package);
            if(RequireAdmission(installed,package)!=UpgradeTrack.Infrastructure)throw new IOException("RELEASE_TRACK_MISMATCH");
            using var host=new UpgradeHost(installed,UpgradeState.Begin(UpgradeState.SetupKind,installed.Active,to,DateTimeOffset.UtcNow.ToUnixTimeSeconds()),progress);
            return await host.ForwardAsync(package);
        }
        catch(Exception error){return new(new(false,SetupOperation.Code(error),DataPreserved:true,Failure:SetupFailureDetail.From(error)),from,to);}
    }
    // Which track, or the refusal by name; the installed pair must be one this release
    // moves from, and the recorded schema the target or one the app migrates from.
    private static UpgradeTrack RequireAdmission(InstallerState installed,InstallPackage package)
    {
        var to=package.Pair();var track=UpgradeRules.Decide(installed.Active,to);
        package.RequireFrom(installed.Active);
        if(!package.AcceptsSchema(installed.Active.App.DatabaseSchema))throw new IOException("RELEASE_UNSUPPORTED_FROM");
        // The record the commit writes, checked before anything changes.
        _=installed.After(to).Encode();
        return track;
    }
    // Refuses at once when another Setup is running; repair may wait for it.
    internal static SetupInstallLock Lock(TimeSpan? wait)
    {try{return new SetupInstallLock(wait);}catch(IOException error)when(error.Message=="INSTALL_BUSY"){throw new IOException("UPGRADE_LOCKED");}}
    // Only the signed Setup installs its release. The running image cannot change
    // under this process, and it carries exactly the pair the manifest names.
    private static void RequireThisSetup(ReleaseManifest release,InstallPackage package)
    {
        var image=Environment.ProcessPath??throw new IOException("SETUP_IMAGE");var setup=release.Infrastructure.Setup;
        if(new FileInfo(image).Length!=setup.Bytes||SetupOperation.HashFile(image)!=setup.Sha256)throw new IOException("RELEASE_SETUP_MISMATCH");
        RequireAgrees(release,package.Manifest);
    }
    // Every field both manifests carry is the same. Migration sources are a set: the
    // package lists them ascending, the release in any order.
    internal static void RequireAgrees(ReleaseManifest release,InstallPackageManifest package)
    {
        var ri=release.Infrastructure;var pi=package.Infrastructure;var ra=release.App;var pa=package.App;
        if(pi.Version!=ri.Version||pi.ContractMin!=ri.ContractMin||pi.ContractMax!=ri.ContractMax||pi.SourceCommit!=ri.SourceCommit||pi.TreeSha256!=ri.TreeSha256||
            pa.Version!=release.Version||pa.SourceCommit!=release.SourceCommit||pa.TreeSha256!=ra.TreeSha256||pa.RequiresContract!=ra.RequiresContract||pa.DatabaseSchema!=ra.DatabaseSchema||
            !pa.DatabaseMigrationsFrom.SequenceEqual(ra.DatabaseMigrationsFrom.Order())||
            package.MinimumFrom.App!=release.MinimumFrom.App||package.MinimumFrom.Infrastructure!=release.MinimumFrom.Infrastructure)throw new IOException("RELEASE_SETUP_MISMATCH");
    }
    private void Advance(string phase){state=state with{Phase=phase};UpgradeFiles.Save(state);progress(new(phase,0));}
    private async Task<UpgradeOutcome> ForwardAsync(InstallPackage package)
    {
        ClearStateResidue();Advance("preparing");
        var from=state.From;var to=state.To;
        try
        {
            service=NativeService.Open(from.Infrastructure.Version);updater=NativeService.Open(from.Infrastructure.Version,ServiceSpec.Updater);
            UpgradeFiles.EnsureUpdaterState(installed.Consent.Automatic);
            Prepare(package);service.ClearFailureActions();updater.ClearFailureActions();Advance("prepared");
            await updater.StopAsync(CancellationToken.None);await StopProxyAsync();
            await service.StopAsync(CancellationToken.None);Advance("stopped");
            if(state.SchemaChanged)DatabaseBackup.Create(DataPath,Backup);
            Advance("backed_up");
            Point(to,from);Advance("switched");
            state=state with{HealthDeadlineUnix=UpgradeFiles.HealthDeadline()};
            await service.StartAsync(CancellationToken.None);Advance("started");
            await UpgradeFiles.RequireHealthyAsync(service,installed.Port,to.App.Version,state.ToSchema,state.HealthDeadlineUnix);
            if(Proxy() is {} lan)await Network(to).StartAfterUpgradeAsync(lan);
            await updater.StartAsync(CancellationToken.None);
            Advance("healthy");
        }
        catch(Exception failure){return await RecoverAsync(failure);}
        try{service.EnableStartup();updater.EnableStartup();Commit();}
        catch(Exception failure){return Result("UPGRADE_COMMIT_INCOMPLETE",failure:failure);}
        return new(new(true,"UPGRADED",LocalUrl),state.From,state.To);
    }
    // Everything a pair names, moved while the services it moves are stopped: the
    // images, the firewall rule and the Caddyfile, then the help page of the pair's app
    // and the pointer.
    private void Point(InstalledPair pair,InstalledPair other)
    {
        Service().SwitchImage(pair.Infrastructure.Version);Updater().SwitchImage(pair.Infrastructure.Version);
        if(Proxy() is {} lan){var network=Network(pair);network.Move(lan,other.Infrastructure.Version);network.WriteProxyConfig();}
        if(MovesApp&&Proxy() is not null)Network(pair).RefreshHelpPage(pair.App);
        UpgradeFiles.WritePointer(pair);
    }
    private string LocalUrl=>"http://127.0.0.1:"+installed.Port+"/";
    private UpgradeOutcome Result(string code,bool rolledBack=false,Exception? failure=null)
        =>new(new(code is "UPGRADED" or "REPAIRED",code,LocalUrl,Failure:failure is null?null:SetupFailureDetail.From(failure)),state.From,state.To,rolledBack);
    private static void ClearStateResidue()=>FullInstallerResidue.Clear(InstallLocations.State);
    // The target trees: infra\B, and apps\Y when the app changes.
    private IEnumerable<(TreeKind Kind,string Root)> Targets()
    {
        yield return(TreeKind.Infrastructure,InstalledLayout.InfraRoot(state.To.Infrastructure.Version));
        if(MovesApp)yield return(TreeKind.App,InstalledLayout.AppRoot(state.To.App.Version));
    }
    private void Prepare(InstallPackage package)
    {
        UpgradeFiles.RequireSpace(Targets().Sum(t=>package.Members(t.Kind).Sum(f=>f.Size)),DataPath);
        foreach(var(kind,root) in Targets())
        {
            UpgradeFiles.PrepareTree(root,(partial,target)=>package.Extract(kind,partial,p=>progress(new("extracting",p)),CancellationToken.None,target));
            var pair=state.To;
            if(kind==TreeKind.Infrastructure)TreeRecord.VerifyInstalled(root,kind,pair.Infrastructure.Version,pair.Infrastructure.TreeSha256);
            else TreeRecord.VerifyInstalled(root,kind,pair.App.Version,pair.App.TreeSha256);
        }
    }
    private async Task<UpgradeOutcome> RecoverAsync(Exception cause)
    {
        var code=SetupOperation.Code(cause);
        try
        {
            if(state.Phase is "preparing" or "prepared"){await DiscardAsync(code);return Result("UPGRADE_PREPARE_FAILED",true,cause);}
            await RollBackAsync(code);return Result("ROLLED_BACK_UNHEALTHY",true,cause);
        }
        catch(Exception failure){return Result("ROLLBACK_FAILED",failure:failure);}
    }
    private NativeService Service(){service??=NativeService.OpenAt(state.To.Infrastructure.Version,state.From.Infrastructure.Version);return service;}
    private NativeService Updater(){updater??=NativeService.OpenAt(state.To.Infrastructure.Version,state.From.Infrastructure.Version,ServiceSpec.Updater);return updater;}
    private NativeService? Proxy()
    {if(proxy is null&&NativeService.Exists(ServiceSpec.Lan))proxy=NativeService.OpenAt(state.To.Infrastructure.Version,state.From.Infrastructure.Version,ServiceSpec.Lan);return proxy;}
    private NativeLanInstall Network(InstalledPair pair)=>new(installed,pair);
    private async Task StopProxyAsync(){if(Proxy() is {} lan)await NativeLanInstall.StopForUpgradeAsync(lan);}
    private async Task RestartProxyAsync(){if(Proxy() is {} lan)await NativeLanInstall.TryRestartAsync(lan);}
    private static async Task RunningAsync(NativeService value)
    {if(!value.IsRunning()){await value.StopAsync(CancellationToken.None);await value.StartAsync(CancellationToken.None);}}
    // Nothing moved yet, so the installed pair only has to be running again: a failed
    // or killed Setup may have cleared its failure actions or asked it to stop. The
    // attempt is resolved first (failure actions back, target trees and state gone), so
    // an installed app that cannot start leaves no upgrade state behind.
    public async Task DiscardAsync(string? failure)
    {
        state=state with{Failure=failure??state.Failure};
        Service().EnableStartup();Updater().EnableStartup();FinishRollback();
        await RunningAsync(Service());await RunningAsync(Updater());await RestartProxyAsync();
    }
    public async Task RollBackAsync(string failure)
    {
        state=state with{Phase="rolling_back",Failure=failure,RestoreData=UpgradeRules.RestoresData(state),Attempts=state.Attempts+1};UpgradeFiles.Save(state);progress(new("rolling_back",0));
        var runtime=Service();var self=Updater();
        await self.StopAsync(CancellationToken.None);await StopProxyAsync();
        await runtime.StopAsync(CancellationToken.None);
        Point(state.From,state.To);
        if(state.RestoreData)DatabaseBackup.Restore(DataPath,Backup);
        await runtime.StartAsync(CancellationToken.None);
        await UpgradeFiles.RequireHealthyAsync(runtime,installed.Port,state.From.App.Version,state.FromSchema,UpgradeFiles.HealthDeadline());
        await self.StartAsync(CancellationToken.None);
        await RestartProxyAsync();
        runtime.EnableStartup();self.EnableStartup();Advance("rolled_back");FinishRollback();
    }
    // The target trees go, partial or whole; none of them is a from-pair tree.
    public void FinishRollback()
    {
        foreach(var(_,root) in Targets()){UpgradeFiles.Remove(UpgradeFiles.Partial(root));UpgradeFiles.Remove(root);}
        UpgradeFiles.Remove(Backup);UpgradeFiles.DeleteState();
    }
    // Everything from here is idempotent: a repair after a crash repeats it.
    public void FinishCommitAfterRepair(){Service().EnableStartup();Updater().EnableStartup();Commit();}
    private void Commit()
    {
        if(UninstallChoice.SamePair(installed.Active,state.From))
        {
            var next=installed.After(state.To);
            NativeInstallFiles.Publish(InstallLocations.StateFile,next.Encode());installed=next;
        }
        else if(!UninstallChoice.SamePair(installed.Active,state.To))throw new IOException("INSTALL_STATE_INVALID");
        Advance("committed");UpgradeFiles.WritePointer(state.To);
        NativeInstallFiles.SetTreeAcl(InstallLocations.Binaries,NativeSetupHost.BinaryAcl);NativeSetupHost.Register(installed);
        // Repair moves to the new tree's engine only now (8.1).
        RepairTask.Register(state.To.Infrastructure.Version,DateTimeOffset.Now);
        UpgradeFiles.Retain(state.To,state.From,BackupRoot,state.Id);
        UpgradeFiles.DeleteState();
    }
    public void Dispose(){service?.Dispose();service=null;updater?.Dispose();updater=null;proxy?.Dispose();proxy=null;}
}

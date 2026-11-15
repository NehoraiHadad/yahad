using System.IO;
using System.Net.Http;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;

internal sealed record UpgradeOutcome(SetupResult Result,InstalledPair? From=null,InstalledPair? To=null,bool RolledBack=false)
{internal int Exit=>UpgradeRules.Exit(Result.Code);}
internal static class UpgradeFiles
{
    // Same link checks as removal: a link is never followed out of the tree.
    internal static void Remove(string path)
    {
        if(!Directory.Exists(path))return;
        var tree=RemovalTree.Inspect(path,WindowsFileBoundary.RequireNoLink);
        tree.DeleteContents(WindowsFileBoundary.DeleteNoFollow,_=>{},CancellationToken.None);
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
    {InstallLocations.RequireStateRoot();return UpgradeState.Parse(NativeInstallFiles.ReadRecord(InstallLocations.UpgradeStateFile,67108864));}
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
// The Setup track (8.1, 8.2): a Setup carrying (infraB, appY) moves an installation
// from (infraA, appX). A newer infrastructure prepares infra\B (and apps\Y when the app
// changes) and switches the three images, the firewall, the Caddyfile, the help page
// and the pointer. The same infrastructure, byte for byte, prepares only apps\Y and
// switches only the help page and the pointer: the Updater and the proxy keep running
// on their images. Rollback returns everything to the from-pair, which was committed
// together. A service's image is its infrastructure pointer; one SCM call moves it.
internal sealed class UpgradeHost(InstallerState installed,UpgradeState state,Action<SetupProgress> progress):IDisposable
{
    private InstallerState installed=installed;private UpgradeState state=state;private NativeService? service,updater,proxy;
    private string Dataset=>Path.Combine(InstallLocations.State,"datasets",installed.DataId);
    private string DataPath=>Path.Combine(Dataset,"data");
    private string BackupRoot=>Path.Combine(Dataset,"upgrade-backup");
    private string Backup=>Path.Combine(BackupRoot,state.Id);
    private bool MovesInfrastructure=>state.Track==UpgradeTrack.Infrastructure;
    private bool MovesApp=>!state.To.SameApp(state.From);
    private static string Partial(string root)=>root+".partial";
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
    internal static async Task<UpgradeOutcome> FromThisSetupAsync(InstallPackage package,Action<SetupProgress> progress)
    {
        InstalledPair? from=null;var to=package.Pair();
        try
        {
            var installed=UpgradeFiles.ReadInstalled();from=installed.Active;
            TrustedReleaseKey.Require(InstalledKey(),TrustedReleaseKey.Embedded);
            RequireAdmission(installed,package);
            using var host=new UpgradeHost(installed,UpgradeState.Begin(installed.Active,to,DateTimeOffset.UtcNow.ToUnixTimeSeconds()),progress);
            return await host.ForwardAsync(package);
        }
        catch(Exception error){return new(new(false,SetupOperation.Code(error),DataPreserved:true,Failure:SetupFailureDetail.From(error)),from,to);}
    }
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
            var key=TrustedReleaseKey.Require(InstalledKey(),TrustedReleaseKey.Embedded);
            // The signed v1 manifest describes the app this Setup carries; the installed
            // schema it is checked against is the record's, never a live answer (4.5).
            var manifest=ReadRelease(directory,key,installed.Active.App.Version,installed.Active.App.DatabaseSchema);
            using var package=InstallPackage.Embedded();RequireThisSetup(manifest,package);to=package.Pair();
            RequireAdmission(installed,package);
            using var host=new UpgradeHost(installed,UpgradeState.Begin(installed.Active,to,DateTimeOffset.UtcNow.ToUnixTimeSeconds()),progress);
            return await host.ForwardAsync(package);
        }
        catch(Exception error){return new(new(false,SetupOperation.Code(error),DataPreserved:true,Failure:SetupFailureDetail.From(error)),from,to);}
    }
    // Which track, or the refusal by name; and the recorded schema must be the target
    // or one the app migrates from.
    private static void RequireAdmission(InstallerState installed,InstallPackage package)
    {
        _=UpgradeRules.Decide(installed.Active,package.Pair());
        if(!package.AcceptsSchema(installed.Active.App.DatabaseSchema))throw new IOException("RELEASE_UNSUPPORTED_FROM");
    }
    // Refuses at once when another Setup is running; repair may wait for it.
    internal static SetupInstallLock Lock(TimeSpan? wait)
    {try{return new SetupInstallLock(wait);}catch(IOException error)when(error.Message=="INSTALL_BUSY"){throw new IOException("UPGRADE_LOCKED");}}
    private static byte[]? InstalledKey()
    {
        var path=InstallLocations.ReleaseKeyFile;if(!File.Exists(path))return null;
        return NativeInstallFiles.ReadRecord(path);
    }
    // The directory is not trusted: exactly one manifest, read once, and only
    // those bytes are verified and parsed.
    private static ReleaseManifest ReadRelease(string directory,byte[] key,string installedVersion,int installedSchema)
    {
        if(!Path.IsPathFullyQualified(directory)||!Directory.Exists(directory))throw new IOException("RELEASE_SIGNATURE_INVALID");
        var found=Directory.EnumerateFiles(directory,"yahad-windows-*.json").Take(2).ToArray();if(found.Length!=1)throw new IOException("RELEASE_SIGNATURE_INVALID");
        var manifest=ReadBounded(found[0],ReleaseManifest.MaximumBytes+1);if(manifest is null)throw new IOException("RELEASE_SIGNATURE_INVALID");
        var value=ReleaseVerification.Verify(key,manifest,ReadBounded(found[0]+".sig",65),installedVersion,installedSchema);
        if(Path.GetFileName(found[0])!=ReleaseManifest.ManifestName(value.Version))throw new IOException("RELEASE_MANIFEST_INVALID");
        return value;
    }
    private static byte[]? ReadBounded(string path,int maximum)
    {
        if(!File.Exists(path))return null;
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(file.Length>maximum)return null;
        var bytes=new byte[file.Length];file.ReadExactly(bytes);return bytes;
    }
    // Only the signed Setup installs its release. The running image cannot change
    // under this process, and it carries the app the manifest names.
    private static void RequireThisSetup(ReleaseManifest manifest,InstallPackage package)
    {
        var image=Environment.ProcessPath??throw new IOException("SETUP_IMAGE");var app=package.Manifest.App;
        if(new FileInfo(image).Length!=manifest.Setup.Bytes||SetupOperation.HashFile(image)!=manifest.Setup.Sha256||
            app.Version!=manifest.Version||app.SourceCommit!=manifest.SourceCommit||app.DatabaseSchema!=manifest.DatabaseSchema)throw new IOException("RELEASE_SETUP_MISMATCH");
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
            Prepare(package);service.ClearFailureActions();if(MovesInfrastructure)updater.ClearFailureActions();Advance("prepared");
            if(MovesInfrastructure){await updater.StopAsync(CancellationToken.None);await StopProxyAsync();}
            await service.StopAsync(CancellationToken.None);Advance("stopped");
            if(state.SchemaChanged)DatabaseBackup.Create(DataPath,Backup);
            Advance("backed_up");
            Point(to,from);Advance("switched");
            state=state with{HealthDeadlineUnix=UpgradeFiles.HealthDeadline()};
            await service.StartAsync(CancellationToken.None);Advance("started");
            await UpgradeFiles.RequireHealthyAsync(service,installed.Port,to.App.Version,state.ToSchema,state.HealthDeadlineUnix);
            if(MovesInfrastructure)
            {
                if(Proxy() is {} lan)await Network(to).StartAfterUpgradeAsync(lan);
                await updater.StartAsync(CancellationToken.None);
            }
            Advance("healthy");
        }
        catch(Exception failure){return await RecoverAsync(failure);}
        try{service.EnableStartup();updater.EnableStartup();Commit();}
        catch(Exception failure){return Result("UPGRADE_COMMIT_INCOMPLETE",failure:failure);}
        return new(new(true,"UPGRADED",LocalUrl),state.From,state.To);
    }
    // Everything a pair names, moved while the services it moves are stopped: the
    // images, the firewall rule and the Caddyfile on an infrastructure move, then the
    // help page of the pair's app and the pointer.
    private void Point(InstalledPair pair,InstalledPair other)
    {
        if(MovesInfrastructure)
        {
            Service().SwitchImage(pair.Infrastructure.Version);Updater().SwitchImage(pair.Infrastructure.Version);
            if(Proxy() is {} lan){var network=Network(pair);network.Move(lan,other.Infrastructure.Version);network.WriteProxyConfig();}
        }
        if(MovesApp&&Proxy() is not null)Network(pair).RefreshHelpPage(pair.App);
        UpgradeFiles.WritePointer(pair);
    }
    private string LocalUrl=>"http://127.0.0.1:"+installed.Port+"/";
    private UpgradeOutcome Result(string code,bool rolledBack=false,Exception? failure=null)
        =>new(new(code is "UPGRADED" or "REPAIRED",code,LocalUrl,Failure:failure is null?null:SetupFailureDetail.From(failure)),state.From,state.To,rolledBack);
    private static void ClearStateResidue()=>FullInstallerResidue.Clear(InstallLocations.State);
    // The target trees: infra\B on an infrastructure move, apps\Y when the app changes.
    private IEnumerable<(TreeKind Kind,string Root)> Targets()
    {
        if(MovesInfrastructure)yield return(TreeKind.Infrastructure,InstalledLayout.InfraRoot(state.To.Infrastructure.Version));
        if(MovesApp)yield return(TreeKind.App,InstalledLayout.AppRoot(state.To.App.Version));
    }
    private void Prepare(InstallPackage package)
    {
        // The space for the new trees and the database copy, on both volumes.
        var files=Targets().Sum(t=>package.Members(t.Kind).Sum(f=>f.Size));
        if(new DriveInfo(Path.GetPathRoot(InstallLocations.Binaries)!).AvailableFreeSpace<checked(files+536870912L)||
            new DriveInfo(Path.GetPathRoot(InstallLocations.State)!).AvailableFreeSpace<checked(DatabaseBackup.Size(DataPath)+536870912L))throw new IOException("DISK_SPACE");
        foreach(var(kind,root) in Targets())
        {
            // Anything here is left from an earlier failed attempt at this version, never
            // a tree in use: each target version is newer than every retained one.
            UpgradeFiles.Remove(Partial(root));UpgradeFiles.Remove(root);
            var partial=Partial(root);var parent=Path.GetDirectoryName(root)!;
            if(!Directory.Exists(parent))NativeInstallFiles.EnsureDirectoryUnder(InstallLocations.Binaries,parent);
            NativeInstallFiles.EnsureDirectoryUnder(parent,partial);
            package.Extract(kind,partial,p=>progress(new("extracting",p)),CancellationToken.None,new ExtractTarget(
                directory=>NativeInstallFiles.EnsureDirectoryUnder(partial,directory),NativeInstallFiles.CreateNewFile,NativeInstallFiles.CommitNew,(_,_)=>false));
            Directory.Move(partial,root);
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
    {
        value.EnableStartup();
        if(!value.IsRunning()){await value.StopAsync(CancellationToken.None);await value.StartAsync(CancellationToken.None);}
    }
    // Nothing moved yet, so the installed pair only has to be running again: a failed
    // or killed Setup may have cleared its failure actions or asked it to stop.
    internal async Task DiscardAsync(string? failure)
    {
        state=state with{Failure=failure??state.Failure};
        await RunningAsync(Service());await RunningAsync(Updater());await RestartProxyAsync();
        FinishRollback();
    }
    internal async Task RollBackAsync(string failure)
    {
        state=state with{Phase="rolling_back",Failure=failure,RestoreData=UpgradeRules.RestoresData(state),Attempts=state.Attempts+1};UpgradeFiles.Save(state);progress(new("rolling_back",0));
        var runtime=Service();var self=Updater();
        if(MovesInfrastructure){await self.StopAsync(CancellationToken.None);await StopProxyAsync();}
        await runtime.StopAsync(CancellationToken.None);
        Point(state.From,state.To);
        if(state.RestoreData)DatabaseBackup.Restore(DataPath,Backup);
        await runtime.StartAsync(CancellationToken.None);
        await UpgradeFiles.RequireHealthyAsync(runtime,installed.Port,state.From.App.Version,state.FromSchema,UpgradeFiles.HealthDeadline());
        if(MovesInfrastructure)await self.StartAsync(CancellationToken.None);
        await RestartProxyAsync();
        runtime.EnableStartup();self.EnableStartup();Advance("rolled_back");FinishRollback();
    }
    // The target trees go, partial or whole; none of them is a from-pair tree.
    internal void FinishRollback()
    {
        foreach(var(_,root) in Targets()){UpgradeFiles.Remove(Partial(root));UpgradeFiles.Remove(root);}
        UpgradeFiles.Remove(Backup);UpgradeFiles.DeleteState();
    }
    // Everything from here is idempotent: a repair after a crash repeats it.
    internal void FinishCommitAfterRepair(){Service().EnableStartup();Updater().EnableStartup();Commit();}
    private void Commit()
    {
        if(UninstallChoice.SamePair(installed.Active,state.From))
        {
            var next=installed with{Active=state.To,Previous=state.From};
            NativeInstallFiles.Publish(InstallLocations.StateFile,next.Encode());installed=next;
        }
        else if(!UninstallChoice.SamePair(installed.Active,state.To))throw new IOException("INSTALL_STATE_INVALID");
        Advance("committed");UpgradeFiles.WritePointer(state.To);
        NativeInstallFiles.SetTreeAcl(InstallLocations.Binaries,NativeSetupHost.BinaryAcl);NativeSetupHost.Register(installed);
        // Repair moves to the new tree's engine only now (8.1).
        RepairTask.Register(state.To.Infrastructure.Version,DateTimeOffset.Now);
        // Retention is the active pair and the one it replaced; every other tree goes.
        foreach(var(kind,parent) in new[]{(TreeKind.Infrastructure,InstalledLayout.InfraParent),(TreeKind.App,InstalledLayout.AppParent)})
        {
            if(!Directory.Exists(parent))continue;
            foreach(var directory in Directory.EnumerateDirectories(parent))
            {if(!UpgradeRules.Retained(kind,Path.GetFileName(directory),state.To,state.From))UpgradeFiles.Remove(directory);}
        }
        if(Directory.Exists(BackupRoot))foreach(var directory in Directory.EnumerateDirectories(BackupRoot))
        {if(Path.GetFileName(directory)!=state.Id)UpgradeFiles.Remove(directory);}
        UpgradeFiles.DeleteState();
    }
    public void Dispose(){service?.Dispose();service=null;updater?.Dispose();updater=null;proxy?.Dispose();proxy=null;}
}

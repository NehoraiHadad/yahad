using System.IO;
using System.Net.Http;
using System.Security.Principal;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;

internal sealed record UpgradeOutcome(SetupResult Result,string? From=null,string? To=null,bool RolledBack=false)
{internal int Exit=>UpgradeRules.Exit(Result.Code);}
internal static class UpgradeFiles
{
    private static SecurityIdentifier Application=>new(InstallLocations.ServiceSid);
    internal static string Immutable=>NativeInstallFiles.Descriptor(InstallLocations.ServiceSid);
    internal static string ImmutableFile=>NativeInstallFiles.FileDescriptor(InstallLocations.ServiceSid);
    internal static string AdminFile=>NativeInstallFiles.FileDescriptor();
    internal static string Versions=>Path.Combine(InstallLocations.Binaries,"versions");
    // Same ownership checks as removal: nothing unexpected is deleted through here.
    internal static void Remove(string path)
    {
        if(!Directory.Exists(path))return;
        var tree=RemovalTree.Inspect(path,(member,directory)=>WindowsFileBoundary.ValidateRemovalMember(member,directory,Application));
        tree.DeleteContents((member,directory)=>WindowsFileBoundary.DeleteRemovalMember(member,directory,Application),_=>{},CancellationToken.None);
        WindowsFileBoundary.DeleteRemovalMember(tree.Root,true,Application);
    }
    internal static void Save(UpgradeState state)=>NativeInstallFiles.PublishProtectedFile(InstallLocations.UpgradeStateFile,state.Encode(),AdminFile);
    internal static UpgradeState Load()
    {using var file=WindowsFileBoundary.OpenInstallerMetadata(InstallLocations.UpgradeStateFile);return UpgradeState.Parse(file.Small(33554432));}
    internal static void DeleteState()
    {using(WindowsFileBoundary.OpenInstallerMetadata(InstallLocations.UpgradeStateFile)){}File.Delete(InstallLocations.UpgradeStateFile);}
    internal static InstallerState ReadInstalled()
    {
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
    // Consecutive answers from this release, not one lucky poll, and an early
    // end when the service itself has already stopped.
    internal static async Task RequireHealthyAsync(NativeRuntimeService service,int port,string version,int? schema,long deadlineUnix)
    {
        var healthy=0;
        while(true)
        {
            healthy=UpgradeRules.NextHealthyPolls(healthy,UpgradeRules.HealthPasses(await ReadHealthAsync(port),version,schema));
            if(healthy>=UpgradeRules.RequiredHealthyPolls)return;
            if(service.IsStopped())throw new IOException("SERVICE_START_FAILED");
            if(DateTimeOffset.UtcNow.ToUnixTimeSeconds()>=deadlineUnix)throw new IOException("SERVER_NOT_READY");
            await Task.Delay(TimeSpan.FromSeconds(UpgradeRules.HealthPollSeconds));
        }
    }
    internal static long HealthDeadline()=>DateTimeOffset.UtcNow.ToUnixTimeSeconds()+UpgradeRules.HealthWindowSeconds;
}
// --quiet-upgrade: the release's own Setup, carrying the new payload, moves the
// one service to the new version directory, or back. The service image is the
// version pointer; one SCM call moves it and nothing else follows it.
internal sealed class UpgradeHost(InstallerState installed,UpgradeState state,Action<SetupProgress> progress):IDisposable
{
    private InstallerState installed=installed;private UpgradeState state=state;private NativeRuntimeService? service;
    private string Dataset=>Path.Combine(InstallLocations.State,"datasets",installed.DataId);
    private string DataPath=>Path.Combine(Dataset,"data");
    private string BackupRoot=>Path.Combine(Dataset,"upgrade-backup");
    private string Backup=>Path.Combine(BackupRoot,state.To);
    private string TargetRoot=>InstallLocations.VersionRoot(state.To);
    private string TargetPartial=>TargetRoot+".partial";
    internal UpgradeState State=>state;
    internal static async Task<UpgradeOutcome> RunAsync(string directory,Action<SetupProgress> progress)
    {
        string? from=null,to=null;
        try
        {
            NativeInstallFiles.RequireAdministrator();
            using var gate=Lock(null);
            var installed=UpgradeFiles.ReadInstalled();from=installed.Version;
            if(File.Exists(InstallLocations.UpgradeStateFile))throw new IOException("UPGRADE_INCOMPLETE");
            progress(new("checking",0));
            var key=TrustedReleaseKey.Require(InstalledKey(),TrustedReleaseKey.Embedded);
            var liveSchema=(await UpgradeFiles.ReadHealthAsync(installed.Port))?.Schema;
            var manifest=ReadRelease(directory,key,installed.Version,liveSchema);to=manifest.Version;
            using var package=InstallPackage.Embedded();RequireThisSetup(manifest,package);
            var setup=new FileInfo(Environment.ProcessPath!);
            var target=new UpgradeTarget(package.Manifest.ZipSha256,package.Manifest.PayloadDigest,package.Manifest.SourceCommit,manifest.Setup.Sha256,package.Manifest.Files);
            using var host=new UpgradeHost(installed,UpgradeState.Begin(installed.Version,manifest.Version,liveSchema,manifest.DatabaseSchema,target,DateTimeOffset.UtcNow.ToUnixTimeSeconds()),progress);
            return await host.ForwardAsync(package,setup.Length);
        }
        catch(Exception error){return new(new(false,SetupOperation.Code(error),DataPreserved:true,Failure:SetupFailureDetail.From(error)),from,to);}
    }
    // Refuses at once when another Setup is running; repair may wait for it.
    internal static SetupInstallLock Lock(TimeSpan? wait)
    {try{return new SetupInstallLock(wait);}catch(IOException error)when(error.Message=="INSTALL_BUSY"){throw new IOException("UPGRADE_LOCKED");}}
    private static byte[]? InstalledKey()
    {
        var path=InstallLocations.ReleaseKeyFile;if(!File.Exists(path))return null;
        return NativeInstallFiles.ReadOwned(path,InstallLocations.ServiceSid);
    }
    // The directory is not trusted: exactly one manifest, read once, and only
    // those bytes are verified and parsed.
    private static ReleaseManifest ReadRelease(string directory,byte[] key,string installedVersion,int? installedSchema)
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
    // under this process, and it is the payload the manifest names.
    private static void RequireThisSetup(ReleaseManifest manifest,InstallPackage package)
    {
        var image=Environment.ProcessPath??throw new IOException("SETUP_IMAGE");
        if(new FileInfo(image).Length!=manifest.Setup.Bytes||SetupOperation.HashFile(image)!=manifest.Setup.Sha256||
            package.Manifest.Version!=manifest.Version||package.Manifest.SourceCommit!=manifest.SourceCommit)throw new IOException("RELEASE_SETUP_MISMATCH");
    }
    private void Advance(string phase){state=state with{Phase=phase};UpgradeFiles.Save(state);progress(new(phase,0));}
    private async Task<UpgradeOutcome> ForwardAsync(InstallPackage package,long setupBytes)
    {
        ClearStateResidue();Advance("preparing");
        try
        {
            service=NativeRuntimeService.Open(state.From,installed.OwnerSid);
            Prepare(package,setupBytes);service.ClearFailureActions();Advance("prepared");
            await service.StopAsync(CancellationToken.None);Advance("stopped");
            if(state.SchemaChanged)DatabaseBackup.Create(DataPath,Backup);
            Advance("backed_up");
            service.SwitchImage(state.To);Advance("switched");
            state=state with{HealthDeadlineUnix=UpgradeFiles.HealthDeadline()};
            await service.StartAsync(CancellationToken.None);Advance("started");
            await UpgradeFiles.RequireHealthyAsync(service,installed.Port,state.To,state.ToSchema,state.HealthDeadlineUnix);Advance("healthy");
        }
        catch(Exception failure){return await RecoverAsync(failure);}
        try{service.EnableStartup();Commit();}
        catch(Exception failure){return Result("UPGRADE_COMMIT_INCOMPLETE",failure:failure);}
        return new(new(true,"UPGRADED",LocalUrl),state.From,state.To);
    }
    private string LocalUrl=>"http://127.0.0.1:"+installed.Port+"/";
    private UpgradeOutcome Result(string code,bool rolledBack=false,Exception? failure=null)
        =>new(new(code is "UPGRADED" or "REPAIRED",code,LocalUrl,Failure:failure is null?null:SetupFailureDetail.From(failure)),state.From,state.To,rolledBack);
    private static void ClearStateResidue()=>FullInstallerResidue.Clear(InstallLocations.State,new SecurityIdentifier(InstallLocations.ServiceSid));
    private void Prepare(InstallPackage package,long setupBytes)
    {
        // The space for the new release and its database copy, on both volumes.
        var files=package.Manifest.Files.Sum(f=>f.Size)+setupBytes;
        if(new DriveInfo(Path.GetPathRoot(InstallLocations.Binaries)!).AvailableFreeSpace<checked(files+536870912L)||
            new DriveInfo(Path.GetPathRoot(InstallLocations.State)!).AvailableFreeSpace<checked(DatabaseBackup.Size(DataPath)+536870912L))throw new IOException("DISK_SPACE");
        // Anything here is left from an earlier failed attempt at this version,
        // never the running one: the release is newer than everything retained.
        UpgradeFiles.Remove(TargetPartial);UpgradeFiles.Remove(TargetRoot);
        var partial=TargetPartial;NativeInstallFiles.EnsureStageDirectory(UpgradeFiles.Versions,partial,UpgradeFiles.Immutable);
        package.Extract(partial,p=>progress(new("extracting",p)),CancellationToken.None,new ExtractTarget(
            directory=>NativeInstallFiles.EnsureStageDirectory(partial,directory,UpgradeFiles.Immutable),path=>NativeInstallFiles.CreateNewFile(path,UpgradeFiles.ImmutableFile),
            NativeInstallFiles.CommitNew,(_,_)=>false));
        NativeSetupHost.CopyVerified(Environment.ProcessPath!,Path.Combine(partial,RemovalIntent.SetupImage),new(RemovalIntent.SetupImage,setupBytes,state.Target.SetupSha256));
        Directory.Move(partial,TargetRoot);
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
    private NativeRuntimeService Service(){service??=NativeRuntimeService.OpenAt(state.To,state.From,installed.OwnerSid);return service;}
    // The image never moved, so the installed release only has to be running again:
    // a failed or killed Setup may have cleared its failure actions or asked it to stop.
    internal async Task DiscardAsync(string? failure)
    {
        state=state with{Failure=failure??state.Failure};
        var runtime=Service();runtime.EnableStartup();
        if(!runtime.IsRunning()){await runtime.StopAsync(CancellationToken.None);await runtime.StartAsync(CancellationToken.None);}
        FinishRollback();
    }
    internal async Task RollBackAsync(string failure)
    {
        state=state with{Phase="rolling_back",Failure=failure,RestoreData=UpgradeRules.RestoresData(state),Attempts=state.Attempts+1};UpgradeFiles.Save(state);progress(new("rolling_back",0));
        var runtime=Service();
        await runtime.StopAsync(CancellationToken.None);runtime.SwitchImage(state.From);
        if(state.RestoreData)DatabaseBackup.Restore(DataPath,Backup);
        await runtime.StartAsync(CancellationToken.None);
        await UpgradeFiles.RequireHealthyAsync(runtime,installed.Port,state.From,state.FromSchema,UpgradeFiles.HealthDeadline());
        runtime.EnableStartup();Advance("rolled_back");FinishRollback();
    }
    internal void FinishRollback()
    {
        UpgradeFiles.Remove(TargetPartial);UpgradeFiles.Remove(TargetRoot);UpgradeFiles.Remove(Backup);
        UpgradeFiles.DeleteState();
    }
    // Everything from here is idempotent: a repair after a crash repeats it.
    internal void FinishCommitAfterRepair(){Service().EnableStartup();Commit();}
    private void Commit()
    {
        if(installed.Version==state.From)
        {
            var previous=new RetainedRelease(state.From,installed.SetupSha256,new FileInfo(InstallLocations.InstallerImage(state.From)).Length,installed.Files);
            var next=installed with{Version=state.To,PackageDigest=state.Target.PackageDigest,PayloadDigest=state.Target.PayloadDigest,SourceCommit=state.Target.SourceCommit,SetupSha256=state.Target.SetupSha256,Files=state.Target.Files,Previous=previous};
            next.Require();NativeInstallFiles.PublishProtectedFile(InstallLocations.StateFile,JsonSerializer.SerializeToUtf8Bytes(next),UpgradeFiles.AdminFile);installed=next;
        }
        else if(installed.Version!=state.To)throw new IOException("INSTALL_STATE_INVALID");
        Advance("committed");NativeSetupHost.Register(installed);
        foreach(var directory in Directory.EnumerateDirectories(UpgradeFiles.Versions))
        {var name=Path.GetFileName(directory);if(name!=state.To&&name!=state.From)UpgradeFiles.Remove(directory);}
        if(Directory.Exists(BackupRoot))foreach(var directory in Directory.EnumerateDirectories(BackupRoot))
        {if(Path.GetFileName(directory)!=state.To)UpgradeFiles.Remove(directory);}
        UpgradeFiles.DeleteState();
    }
    public void Dispose(){service?.Dispose();service=null;}
}

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Updater;

internal sealed record UpdaterPaths(string Root)
{
    internal static UpdaterPaths Installed=>new(InstalledLayout.UpdaterRoot);
    internal string State=>Path.Combine(Root,"state.json");
    internal string LastUpgrade=>Path.Combine(Root,"last-upgrade.json");
    internal string Staging=>Path.Combine(Root,"staging");
    internal string Stage(string version)=>Path.Combine(Staging,version);
}
// A whole release in the staging folder, verified from its bytes each time it is
// found. A record never says a release is staged; the folder and the key do.
internal sealed record StagedRelease(string Version,string Fingerprint,string Directory,string Setup);
// What a Setup's result means to the person reading the updates page.
internal readonly record struct UpgradeEnding(bool Succeeded,string Phase,string? Error,bool Refuse);
internal static class UpdaterRules
{
    internal static readonly TimeSpan FirstCheck=TimeSpan.FromMinutes(10),CheckEvery=TimeSpan.FromHours(24),ResultTimeout=TimeSpan.FromMinutes(30),Tick=TimeSpan.FromMinutes(1);
    internal static UpgradeEnding Ending(string? code)=>code switch
    {
        "UPGRADED"=>new(true,"healthy",null,false),
        "ROLLED_BACK_UNHEALTHY"=>new(false,"rolled_back","health",true),
        "ROLLBACK_FAILED"=>new(false,"failed","rollback",false),
        "DISK_SPACE"=>new(false,"failed","storage",false),
        "RELEASE_UNSUPPORTED_FROM"=>new(false,"incompatible","incompatible",true),
        "RELEASE_SIGNATURE_INVALID" or "RELEASE_KEY_UNTRUSTED" or "RELEASE_NOT_NEWER" or "RELEASE_SETUP_MISMATCH" or "RELEASE_MANIFEST_INVALID"=>new(false,"failed","signature",true),
        _=>new(false,"failed","internal",false)
    };
    internal static string CheckError(Exception error)=>error switch
    {
        System.Net.Http.HttpRequestException or TaskCanceledException=>"network",
        IOException{Message:"RELEASE_UNSUPPORTED_FROM"}=>"incompatible",
        IOException{Message:"RELEASE_SETUP_SIZE" or "RELEASE_SETUP_HASH"}=>"archive",
        IOException{Message:"DISK_SPACE"}=>"storage",
        IOException{Message:var code}when code.StartsWith("RELEASE_",StringComparison.Ordinal)=>"signature",
        IOException{Message:var code}when code.StartsWith("GITHUB_",StringComparison.Ordinal)=>"network",
        _=>"internal"
    };
    internal static bool CheckDue(DateTimeOffset now,DateTimeOffset started,long? checkedAt,TimeSpan jitter)
        =>now-started>=FirstCheck&&(checkedAt is not {} last||now-DateTimeOffset.FromUnixTimeMilliseconds(last)>=CheckEvery+jitter);
    // Unattended means quiet hours, and one attempt a day: a failure waits for tomorrow.
    internal static bool AutoInstallDue(DateTimeOffset local,UpdaterLastRequest? last)
        =>local.Hour is 3 or 4&&(last is not {Action:"install"}||local.ToUnixTimeMilliseconds()-last.StartedAt>=CheckEvery.TotalMilliseconds);
}
// The decisions and the record. Before a signature verifies, the only foreign bytes
// parsed are the size-capped GitHub list; the manifest's signature is checked over
// its raw bytes before it is parsed, and a Setup is run only from a whole, re-verified
// staging folder. Without an installed key nothing is ever downloaded to run.
internal sealed class UpdaterEngine(UpdaterPaths paths,string current,Func<byte[]?> installedKey,byte[]? embeddedKey,GitHubReleaseSource source,Action<StagedRelease,string> launch,Func<DateTimeOffset> clock)
{
    private readonly object gate=new();private readonly DateTimeOffset started=clock();private readonly TimeSpan jitter=TimeSpan.FromSeconds(Random.Shared.Next(3600));
    private UpdaterRecord record=UpdaterRecord.Seed(false);private StagedRelease? staged;private string? progress;private CancellationToken stop;
    internal Task Work{get;private set;}=Task.CompletedTask;
    private long Now=>clock().ToUnixTimeMilliseconds();
    private bool Activating=>record.Phase=="activating";
    private byte[]? Key()
    {try{return TrustedReleaseKey.Require(installedKey(),embeddedKey);}catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){return null;}}
    internal void Start(CancellationToken stopping=default)
    {
        stop=stopping;
        lock(gate)
        {
            try{record=File.Exists(paths.State)?UpdaterRecord.Parse(ReadBounded(paths.State,65536)):record;}
            // The mode falls back to the one that never installs anything by itself.
            catch(IOException){record=UpdaterRecord.Seed(false);}
            foreach(var partial in Directory.EnumerateFiles(paths.Root,"*.partial-*"))TryDelete(partial);
            // A check the last run did not finish is over.
            if(record.LastRequest is {Action:"check",State:"running"})record=record with{LastRequest=Finished(record.LastRequest,"internal")};
            CleanStaging();staged=Key() is {} key?Rediscover(key):null;Ingest();
        }
    }
    private void Save()=>NativeInstallFiles.Publish(paths.State,record.Encode());
    private UpdaterLastRequest Finished(UpdaterLastRequest request,string? error)=>request with{State=error is null?"succeeded":"failed",FinishedAt=Now,Error=error};

    internal UpdaterReply Handle(UpdaterRequest request)
    {
        try
        {
            if(request.V!=1)return Refuse("BAD_REQUEST");
            lock(gate)
            {
                Ingest();
                switch(request.Op)
                {
                    case "status":return new(1,true,Status:Status());
                    case "check":if(!UpdaterProtocol.IsId(request.Id))return Refuse("BAD_REQUEST");return BeginCheck(request.Id);
                    case "install":
                        if(!UpdaterProtocol.IsId(request.Id)||request.Version is null||!ReleaseManifest.IsVersion(request.Version)||!UpdaterProtocol.IsFingerprint(request.Fingerprint))return Refuse("BAD_REQUEST");
                        return Install(request.Id!,new(request.Version,request.Fingerprint!));
                    case "set-mode":
                        if(!UpdaterProtocol.IsId(request.Id)||request.Mode is not(UpdaterProtocol.Auto or UpdaterProtocol.Notify))return Refuse("BAD_REQUEST");
                        if(Key() is null)return Refuse("NOT_AVAILABLE");
                        record=record with{Mode=request.Mode,LastModeRequest=new(request.Id!,request.Mode,"applied")};Save();return new(1,true);
                    default:return Refuse("BAD_REQUEST");
                }
            }
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException){return Refuse("INTERNAL");}
    }
    private static UpdaterReply Refuse(string code)=>new(1,false,code);
    private string Phase()=>progress??(Activating?"activating":staged is not null?"available":record.Phase??"idle");
    private UpdaterStatus Status()=>new(Key() is not null,record.Mode,Phase(),current,staged?.Version??record.Latest,staged is null?null:new(staged.Version,staged.Fingerprint),record.CheckedAt,Now,record.Error,record.LastRequest,record.LastModeRequest);

    private UpdaterReply BeginCheck(string? id)
    {
        if(Key() is not {} key)return Refuse("NOT_AVAILABLE");
        if(progress is not null||Activating)return Refuse("BUSY");
        if(id is not null){record=record with{LastRequest=new(id,"check","running",Now,null,null,null)};Save();}
        progress="checking";Work=Task.Run(()=>CheckAsync(id,key));return new(1,true);
    }
    private async Task CheckAsync(string? id,byte[] key)
    {
        string? failure=null;
        try{await CheckCoreAsync(key,stop);}
        // The service is stopping; the next start closes what this left open.
        catch(OperationCanceledException)when(stop.IsCancellationRequested){return;}
        catch(Exception error){failure=UpdaterRules.CheckError(error);}
        lock(gate)
        {
            progress=null;
            record=record with{CheckedAt=Now,Error=failure,Phase=failure switch{null=>"idle","incompatible"=>"incompatible",_=>"failed"},LastRequest=id is not null&&record.LastRequest?.Id==id?Finished(record.LastRequest,failure):record.LastRequest};
            Save();
        }
    }
    private async Task CheckCoreAsync(byte[] key,CancellationToken cancellation)
    {
        var release=await source.LatestAsync(cancellation);
        var version=release.TagName.StartsWith('v')?release.TagName[1..]:release.TagName;
        if(release.Draft||release.Prerelease||!ReleaseManifest.IsVersion(version)||ReleaseManifest.Compare(version,current)<=0){Latest(null);return;}
        if(version==record.RefusedVersion){Latest(version);return;}
        // A newer tag without a Windows release is simply not an update for this machine.
        var manifestName=ReleaseManifest.ManifestName(version);
        var manifestAsset=release.Assets.SingleOrDefault(a=>a.Name==manifestName);var signatureAsset=release.Assets.SingleOrDefault(a=>a.Name==manifestName+".sig");
        if(manifestAsset is null||signatureAsset is null){Latest(null);return;}
        if(manifestAsset.Size>ReleaseManifest.MaximumBytes||signatureAsset.Size!=64)throw new IOException("RELEASE_SIGNATURE_INVALID");
        Latest(version);lock(gate){progress="verifying";if(staged?.Version==version)return;}
        var manifest=await source.GetAssetAsync(manifestAsset.BrowserDownloadUrl,ReleaseManifest.MaximumBytes,cancellation);
        var signature=await source.GetAssetAsync(signatureAsset.BrowserDownloadUrl,64,cancellation);
        var value=ReleaseVerification.Verify(key,manifest,signature,current,null);
        if(value.Version!=version)throw new IOException("RELEASE_MANIFEST_INVALID");
        var setup=release.Assets.SingleOrDefault(a=>a.Name==value.Setup.Name)??throw new IOException("RELEASE_SETUP_SIZE");
        if(setup.Size!=value.Setup.Bytes)throw new IOException("RELEASE_SETUP_SIZE");
        if(setup.Digest is not null&&setup.Digest!="sha256:"+value.Setup.Sha256)throw new IOException("RELEASE_SETUP_HASH");
        if(new DriveInfo(Path.GetPathRoot(Path.GetFullPath(paths.Root))!).AvailableFreeSpace<checked(value.Setup.Bytes+1073741824L))throw new IOException("DISK_SPACE");
        lock(gate)progress="downloading";
        await StageAsync(setup,value,manifest,signature,cancellation);
        lock(gate){staged=Rediscover(key);CleanStaging();}
    }
    private void Latest(string? version){lock(gate){record=record with{Latest=version};}}
    // The Setup appears under its name only when whole and matching, so a present
    // name is complete; a folder left by an interrupted download starts over.
    private async Task StageAsync(GitHubAsset setup,ReleaseManifest value,byte[] manifest,byte[] signature,CancellationToken cancellation)
    {
        var stage=paths.Stage(value.Version);if(Directory.Exists(stage))Directory.Delete(stage,true);
        if(!Directory.Exists(paths.Staging))NativeInstallFiles.NewDirectory(paths.Staging);
        NativeInstallFiles.NewDirectory(stage);
        NativeInstallFiles.WriteNew(Path.Combine(stage,ReleaseManifest.ManifestName(value.Version)),manifest);
        NativeInstallFiles.WriteNew(Path.Combine(stage,ReleaseManifest.ManifestName(value.Version)+".sig"),signature);
        var target=Path.Combine(stage,value.Setup.Name);var partial=target+".partial";
        try
        {
            await using(var output=NativeInstallFiles.CreateNewFile(partial))
            {await source.DownloadAsync(setup.BrowserDownloadUrl,output,value.Setup.Bytes,value.Setup.Sha256,cancellation);output.Flush(true);}
            NativeInstallFiles.CommitNew(partial,target);
        }
        catch{TryDeleteTree(stage);throw;}
    }
    private StagedRelease? Rediscover(byte[] key)
    {
        if(!Directory.Exists(paths.Staging))return null;
        var versions=Directory.EnumerateDirectories(paths.Staging).Select(path=>Path.GetFileName(path)!).Where(name=>ReleaseManifest.IsVersion(name)&&name!=record.RefusedVersion&&ReleaseManifest.Compare(name,current)>0).ToList();
        versions.Sort(ReleaseManifest.Compare);versions.Reverse();
        return versions.Select(version=>Verified(version,key)).FirstOrDefault(found=>found is not null);
    }
    private StagedRelease? Verified(string version,byte[] key)
    {
        try
        {
            var directory=paths.Stage(version);var name=Path.Combine(directory,ReleaseManifest.ManifestName(version));
            var manifest=ReadBounded(name,ReleaseManifest.MaximumBytes);
            var value=ReleaseVerification.Verify(key,manifest,ReadBounded(name+".sig",64),current,null);
            var setup=Path.Combine(directory,value.Setup.Name);
            if(value.Version!=version||new FileInfo(setup).Length!=value.Setup.Bytes)return null;
            using var file=new FileStream(setup,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(Convert.ToHexStringLower(SHA256.HashData(file))!=value.Setup.Sha256)return null;
            return new(version,Convert.ToHexStringLower(SHA256.HashData(manifest)),directory,setup);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){return null;}
    }
    private static byte[] ReadBounded(string path,int maximum)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(file.Length>maximum)throw new IOException("RELEASE_SIGNATURE_INVALID");
        var bytes=new byte[file.Length];file.ReadExactly(bytes);return bytes;
    }
    private UpdaterReply Install(string id,UpdaterTarget target)
    {
        if(Key() is not {} key)return Refuse("NOT_AVAILABLE");
        if(progress is not null||Activating)return Refuse("BUSY");
        staged=Rediscover(key);
        if(staged is null||staged.Version!=target.Version||staged.Fingerprint!=target.Fingerprint)return Refuse("STALE_TARGET");
        File.Delete(paths.LastUpgrade);
        record=record with{Phase="activating",Error=null,LastRequest=new(id,"install","running",Now,null,null,target)};Save();
        try{launch(staged,paths.LastUpgrade);}
        catch(Exception error)when(error is IOException or System.ComponentModel.Win32Exception)
        {record=record with{Phase="failed",Error="internal",LastRequest=Finished(record.LastRequest!,"internal")};Save();return Refuse("INTERNAL");}
        return new(1,true);
    }
    // The launched Setup outlives this process and may replace it, so its result is
    // read lazily by whichever Updater is running by the time it appears.
    private void Ingest()
    {
        if(!Activating)return;var request=record.LastRequest;UpgradeEnding ending;
        try
        {
            if(!File.Exists(paths.LastUpgrade))throw new FileNotFoundException();
            using var result=JsonDocument.Parse(ReadBounded(paths.LastUpgrade,65536));
            ending=UpdaterRules.Ending(result.RootElement.GetProperty("Code").GetString());
        }
        // Not there yet, or being written by the Setup that is finishing now.
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if(request is not null&&Now-request.StartedAt<UpdaterRules.ResultTimeout.TotalMilliseconds)return;
            ending=new(false,"failed","internal",false);
        }
        record=record with{Phase=ending.Phase,Error=ending.Error,LastRequest=request is null?null:Finished(request,ending.Error),LastUpdateAt=ending.Succeeded?Now:record.LastUpdateAt,
            RefusedVersion=ending.Refuse?request?.Target?.Version:record.RefusedVersion,Latest=ending.Succeeded?null:record.Latest};
        Save();staged=Key() is {} key?Rediscover(key):null;CleanStaging();
    }
    // Nothing older than or equal to this release, or refused, stays. A Setup still
    // exiting holds its image; the next pass takes what this one could not.
    private void CleanStaging()
    {
        if(!Directory.Exists(paths.Staging))return;
        foreach(var directory in Directory.EnumerateDirectories(paths.Staging))
        {
            var name=Path.GetFileName(directory)!;
            if(!ReleaseManifest.IsVersion(name)||name==record.RefusedVersion||ReleaseManifest.Compare(name,staged?.Version??current)<=0&&name!=staged?.Version)TryDeleteTree(directory);
        }
    }
    private static void TryDelete(string path){try{File.Delete(path);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){}}
    private static void TryDeleteTree(string path){try{Directory.Delete(path,true);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){}}

    // One pass of the daily schedule; also how an unattended install happens.
    internal void Tick()
    {
        lock(gate)
        {
            Ingest();CleanStaging();
            if(progress is not null||Activating||Key() is null)return;
            if(UpdaterRules.CheckDue(clock(),started,record.CheckedAt,jitter)){BeginCheck(null);return;}
            if(record.Mode==UpdaterProtocol.Auto&&staged is {} release&&UpdaterRules.AutoInstallDue(clock().ToLocalTime(),record.LastRequest))
                _=Install(Guid.NewGuid().ToString("D"),new(release.Version,release.Fingerprint));
        }
    }
    internal async Task RunScheduleAsync(CancellationToken cancellation)
    {
        while(true)
        {
            await Task.Delay(UpdaterRules.Tick,cancellation);
            try{Tick();}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException){}
        }
    }
}

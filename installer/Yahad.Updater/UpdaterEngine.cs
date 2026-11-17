using System.IO;
using System.Security.Cryptography;
using System.Text;
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
// found. A record never says a release is staged; the folder, the key and the live
// active pair do. Payload is the track's one download: the Setup or the app archive.
internal sealed record StagedRelease(string Version,ReleaseTrack Track,string Fingerprint,string Directory,string Payload);
// What an install starts (docs/WINDOWS-APP-PACKAGE-PLAN.md 7, 8.1): the Setup track
// runs the staged Setup, the app track the installed engine of the active infrastructure.
internal sealed record UpdateLaunch(ReleaseTrack Track,string Image,string Directory)
{
    internal string Verb=>Track==ReleaseTrack.Setup?"--quiet-upgrade":"--apply-app";
    internal static UpdateLaunch For(StagedRelease release,ActivePointer installed)
        =>release.Track==ReleaseTrack.Setup?new(release.Track,release.Payload,release.Directory):new(release.Track,InstalledLayout.EngineImage(installed.Infrastructure.Version),release.Directory);
}
// What a Setup's result means to the person reading the updates page. Code is the
// precise one beside the error word (UpdaterRules.Code); null only for a success.
internal readonly record struct UpgradeEnding(bool Succeeded,string Phase,string? Error,bool Refuse,string? Code);
// What a failed check means (9.4): the error word, whether that version is refused for
// good, and the precise code. A bad signature or a defective release never selects
// another path.
internal readonly record struct CheckFailure(string Error,bool Refuse,string Code);
// One failure for the developer record (event 2001): fixed words, versions and the
// cause's sites only. Permanent is true only when a version was actually refused.
internal sealed record UpdateFailure(string Operation,string Error,string Code,bool Permanent,string? From,string? Version,Exception? Cause);
internal static class UpdaterRules
{
    internal static readonly TimeSpan FirstCheck=TimeSpan.FromMinutes(10),CheckEvery=TimeSpan.FromHours(24),ResultTimeout=TimeSpan.FromMinutes(30),Tick=TimeSpan.FromMinutes(1);
    internal const long DiskReserve=1073741824L;
    // The codes the Updater itself gives a failure that has no thrown code.
    internal const string CheckInterrupted="CHECK_INTERRUPTED",ResultMissing="UPGRADE_RESULT_MISSING",ResultInvalid="UPGRADE_RESULT_INVALID";
    // A deliberate code: the UPPER_SNAKE word a Yahad failure is thrown or reported with.
    internal static bool IsCode(string? value)=>value is {Length:>=3 and <=64}&&value[0] is >='A' and <='Z'&&value.All(c=>c is >='A' and <='Z' or >='0' and <='9' or '_');
    // The precise cause, for support: the deliberate code, or else the exception's type;
    // then at most one fixed detail (an HTTP status, the HTTP error kind or a Windows
    // error number). Never the message, which may hold a path.
    internal static string Code(Exception error)
    {
        var detail=error switch
        {
            System.Net.Http.HttpRequestException{StatusCode:{} status}=>((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture),
            System.Net.Http.HttpRequestException{HttpRequestError:var kind}when kind!=System.Net.Http.HttpRequestError.Unknown=>kind.ToString(),
            System.ComponentModel.Win32Exception win32=>win32.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ when (uint)error.HResult>>16==0x8007=>(error.HResult&0xFFFF).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _=>null
        };
        var name=IsCode(error.Message)?error.Message:new string(error.GetType().Name.TakeWhile(char.IsAsciiLetterOrDigit).Take(64).ToArray());
        return detail is null?name:name+":"+detail;
    }
    internal static UpgradeEnding Ending(string? code)
    {
        var precise=IsCode(code)?code:ResultInvalid;
        return code switch
        {
            "UPGRADED"=>new(true,"healthy",null,false,null),
            "ROLLED_BACK_UNHEALTHY"=>new(false,"rolled_back","health",true,precise),
            "ROLLBACK_FAILED"=>new(false,"failed","rollback",false,precise),
            "DISK_SPACE"=>new(false,"failed","storage",false,precise),
            "RELEASE_UNSUPPORTED_FROM" or "APP_NEEDS_INFRASTRUCTURE"=>new(false,"incompatible","incompatible",true,precise),
            "RELEASE_APP_SIZE" or "RELEASE_APP_HASH"=>new(false,"failed","archive",false,precise),
            {}when code.StartsWith("APP_PACKAGE_",StringComparison.Ordinal)=>new(false,"failed","archive",false,precise),
            "RELEASE_SIGNATURE_INVALID" or "RELEASE_KEY_UNTRUSTED" or "RELEASE_NOT_NEWER" or "RELEASE_SETUP_MISMATCH" or "RELEASE_MANIFEST_INVALID"=>new(false,"failed","signature",true,precise),
            // INFRA_IDENTITY_MISMATCH, UPGRADE_COMMIT_INCOMPLETE and anything unforeseen.
            _=>new(false,"failed","internal",false,precise)
        };
    }
    internal static CheckFailure CheckError(Exception error)
    {
        var (word,refuse)=error switch
        {
            System.Net.Http.HttpRequestException or TaskCanceledException=>("network",false),
            IOException{Message:"RELEASE_UNSUPPORTED_FROM" or "APP_NEEDS_INFRASTRUCTURE"}=>("incompatible",true),
            IOException{Message:"RELEASE_SETUP_SIZE" or "RELEASE_SETUP_HASH" or "RELEASE_APP_SIZE" or "RELEASE_APP_HASH"}=>("archive",false),
            IOException{Message:"DISK_SPACE"}=>("storage",false),
            IOException{Message:var code}when code.StartsWith("RELEASE_",StringComparison.Ordinal)=>("signature",true),
            IOException{Message:var code}when code.StartsWith("GITHUB_",StringComparison.Ordinal)=>("network",false),
            // INFRA_IDENTITY_MISMATCH, ACTIVE_POINTER_INVALID and anything unforeseen.
            _=>("internal",false)
        };
        return new(word,refuse,Code(error));
    }
    internal static string TrackName(ReleaseTrack track)=>track==ReleaseTrack.Setup?"setup":"app";
    // The target names the track as well as the signed bytes (9.3), so the same
    // release staged for the other track is another target.
    internal static string Fingerprint(ReleaseTrack track,byte[] manifest)
        =>Convert.ToHexStringLower(SHA256.HashData((byte[])[..Encoding.ASCII.GetBytes(TrackName(track)+"\n"),..manifest]));
    internal static ReleaseFile Payload(ReleaseManifest release,ReleaseTrack track)=>track==ReleaseTrack.Setup?release.Infrastructure.Setup:release.App.Archive;
    internal static (string Size,string Hash) PayloadCodes(ReleaseTrack track)
        =>track==ReleaseTrack.Setup?("RELEASE_SETUP_SIZE","RELEASE_SETUP_HASH"):("RELEASE_APP_SIZE","RELEASE_APP_HASH");
    // A Setup carries its own payload. An app apply also extracts a tree whose size the
    // manifest does not bound beside the archive and copies the database: three archives.
    internal static long DiskBudget(ReleaseManifest release,ReleaseTrack track)
        =>track==ReleaseTrack.Setup?checked(release.Infrastructure.Setup.Bytes+DiskReserve):checked(release.App.Archive.Bytes*3+DiskReserve);
    internal static bool CheckDue(DateTimeOffset now,DateTimeOffset started,long? checkedAt,TimeSpan jitter)
        =>now-started>=FirstCheck&&(checkedAt is not {} last||now-DateTimeOffset.FromUnixTimeMilliseconds(last)>=CheckEvery+jitter);
    // Unattended means quiet hours, and one attempt a day: a failure waits for tomorrow.
    internal static bool AutoInstallDue(DateTimeOffset local,UpdaterLastRequest? last)
        =>local.Hour is 3 or 4&&(last is not {Action:"install"}||local.ToUnixTimeMilliseconds()-last.StartedAt>=CheckEvery.TotalMilliseconds);
}
// The decisions and the record. Before a signature verifies, the only foreign bytes
// parsed are the size-capped GitHub list; the manifest's signature is checked over
// its raw bytes before it is parsed, and an install runs only from a whole, re-verified
// staging folder. Without an installed key nothing is ever downloaded to run. The
// installed pair is current(), read again for every decision (9.2): an app apply
// changes it while this process keeps running.
// Every failure the page shows is also handed to report, for the event log.
internal sealed class UpdaterEngine(UpdaterPaths paths,Func<ActivePointer> current,Func<byte[]?> installedKey,byte[]? embeddedKey,GitHubReleaseSource source,Action<UpdateLaunch,string> launch,Func<DateTimeOffset> clock,Action<UpdateFailure>? report=null)
{
    private readonly object gate=new();private readonly DateTimeOffset started=clock();private readonly TimeSpan jitter=TimeSpan.FromSeconds(Random.Shared.Next(3600));
    private UpdaterRecord record=UpdaterRecord.Seed(false);private StagedRelease? staged;private string? progress,candidate;private CancellationToken stop;
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
            if(record.LastRequest is {Action:"check",State:"running"})
            {
                record=record with{LastRequest=Finished(record.LastRequest,"internal",UpdaterRules.CheckInterrupted)};
                Report(new("check","internal",UpdaterRules.CheckInterrupted,false,From(),null,null));
            }
            staged=Key() is {} key?Rediscover(key,current()):null;CleanStaging();Ingest();
        }
    }
    private void Save()=>NativeInstallFiles.Publish(paths.State,record.Encode());
    private UpdaterLastRequest Finished(UpdaterLastRequest request,string? error,string? code)=>request with{State=error is null?"succeeded":"failed",FinishedAt=Now,Error=error,Code=code};
    // The record never stops a decision: a pointer that cannot be read leaves From empty.
    private string? From(){try{return current().App.Version;}catch(Exception error)when(error is IOException or UnauthorizedAccessException){return null;}}
    private void Report(UpdateFailure failure)=>report?.Invoke(failure);

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
                        if(!UpdaterProtocol.IsId(request.Id)||request.Version is null||!SemVer.IsVersion(request.Version)||!UpdaterProtocol.IsFingerprint(request.Fingerprint))return Refuse("BAD_REQUEST");
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
    // currentVersion stays the app version, the one src/updates.ts shows.
    private UpdaterStatus Status()=>new(Key() is not null,record.Mode,Phase(),current().App.Version,staged?.Version??record.Latest,staged is null?null:new(staged.Version,staged.Fingerprint),record.CheckedAt,Now,record.Error,record.LastRequest,record.LastModeRequest,
        record.ErrorCode,record.ErrorPermanent);

    private UpdaterReply BeginCheck(string? id)
    {
        if(Key() is not {} key)return Refuse("NOT_AVAILABLE");
        if(progress is not null||Activating)return Refuse("BUSY");
        if(id is not null){record=record with{LastRequest=new(id,"check","running",Now,null,null,null)};Save();}
        progress="checking";candidate=null;Work=Task.Run(()=>CheckAsync(id,key));return new(1,true);
    }
    private async Task CheckAsync(string? id,byte[] key)
    {
        CheckFailure? failure=null;Exception? cause=null;
        try{await CheckCoreAsync(key,stop);}
        // The service is stopping; the next start closes what this left open.
        catch(OperationCanceledException)when(stop.IsCancellationRequested){return;}
        catch(Exception error){failure=UpdaterRules.CheckError(error);cause=error;}
        lock(gate)
        {
            progress=null;var error=failure?.Error;
            var refusing=failure is {Refuse:true}&&candidate is not null;
            var refused=refusing?candidate:record.RefusedVersion;
            if(staged?.Version==refused)staged=null;
            record=record with{CheckedAt=Now,Error=error,ErrorCode=failure?.Code,ErrorPermanent=failure is null?null:refusing,
                Phase=error switch{null=>"idle","incompatible"=>"incompatible",_=>"failed"},RefusedVersion=refused,
                LastRequest=id is not null&&record.LastRequest?.Id==id?Finished(record.LastRequest,error,failure?.Code):record.LastRequest};
            Save();CleanStaging();
            if(failure is {} known)Report(new("check",known.Error,known.Code,refusing,From(),candidate,cause));
        }
    }
    private async Task CheckCoreAsync(byte[] key,CancellationToken cancellation)
    {
        var release=await source.LatestAsync(cancellation);
        var version=release.TagName.StartsWith('v')?release.TagName[1..]:release.TagName;
        if(release.Draft||release.Prerelease||!SemVer.IsVersion(version)||SemVer.Compare(version,current().App.Version)<=0){Latest(null);return;}
        if(version==record.RefusedVersion){Latest(version);return;}
        // A newer tag without the format-2 release is simply not an update for this machine.
        var manifestAsset=Asset(release,ReleaseManifest.ManifestName(version));var signatureAsset=Asset(release,ReleaseManifest.SignatureName(version));
        if(manifestAsset is null||signatureAsset is null){Latest(null);return;}
        lock(gate){candidate=version;record=record with{Latest=version};progress="verifying";}
        if(manifestAsset.Size>ReleaseManifest.MaximumBytes||signatureAsset.Size!=64)throw new IOException("RELEASE_SIGNATURE_INVALID");
        var manifest=await source.GetAssetAsync(manifestAsset.BrowserDownloadUrl,ReleaseManifest.MaximumBytes,cancellation);
        var signature=await source.GetAssetAsync(signatureAsset.BrowserDownloadUrl,64,cancellation);
        var value=ReleaseVerification.Verify(key,manifest,signature);
        if(value.Version!=version)throw new IOException("RELEASE_MANIFEST_INVALID");
        // Which track, against the pair that is active now (9.1).
        var installed=current();
        if(ReleaseSelection.Select(value,installed) is not {} track){Latest(null);return;}
        // Already staged is believed from the stage's bytes, not from memory.
        var fingerprint=UpdaterRules.Fingerprint(track,manifest);
        lock(gate){staged=Rediscover(key,installed);if(staged is {} same&&same.Version==version&&same.Fingerprint==fingerprint)return;}
        var file=UpdaterRules.Payload(value,track);var codes=UpdaterRules.PayloadCodes(track);
        var payload=Asset(release,file.Name)??throw new IOException(codes.Size);
        if(payload.Size!=file.Bytes)throw new IOException(codes.Size);
        if(payload.Digest is not null&&payload.Digest!="sha256:"+file.Sha256)throw new IOException(codes.Hash);
        if(new DriveInfo(Path.GetPathRoot(Path.GetFullPath(paths.Root))!).AvailableFreeSpace<UpdaterRules.DiskBudget(value,track))throw new IOException("DISK_SPACE");
        // A stage of this version for another track or other bytes is replaced, never kept beside it.
        lock(gate){progress="downloading";if(staged?.Version==version)staged=null;}
        await StageAsync(version,track,payload,file,manifest,signature,cancellation);
        lock(gate)staged=Rediscover(key,current());
    }
    private static GitHubAsset? Asset(GitHubRelease release,string name)=>release.Assets.SingleOrDefault(a=>a.Name==name);
    private void Latest(string? version){lock(gate){record=record with{Latest=version};}}
    // The stage holds exactly the track's three files under their exact names. The
    // payload appears under its name only when whole and matching, so a present name is
    // complete; a folder left by an interrupted download starts over.
    private async Task StageAsync(string version,ReleaseTrack track,GitHubAsset payload,ReleaseFile file,byte[] manifest,byte[] signature,CancellationToken cancellation)
    {
        var stage=paths.Stage(version);if(Directory.Exists(stage))Directory.Delete(stage,true);
        if(!Directory.Exists(paths.Staging))NativeInstallFiles.NewDirectory(paths.Staging);
        NativeInstallFiles.NewDirectory(stage);
        try
        {
            NativeInstallFiles.WriteNew(Path.Combine(stage,ReleaseManifest.ManifestName(version)),manifest);
            NativeInstallFiles.WriteNew(Path.Combine(stage,ReleaseManifest.SignatureName(version)),signature);
            var target=Path.Combine(stage,file.Name);var partial=target+".partial";var codes=UpdaterRules.PayloadCodes(track);
            await using(var output=NativeInstallFiles.CreateNewFile(partial))
            {await source.DownloadAsync(payload.BrowserDownloadUrl,output,file.Bytes,file.Sha256,codes.Size,codes.Hash,cancellation);output.Flush(true);}
            NativeInstallFiles.CommitNew(partial,target);
        }
        catch{TryDeleteTree(stage);throw;}
    }
    private StagedRelease? Rediscover(byte[] key,ActivePointer installed)
    {
        if(!Directory.Exists(paths.Staging))return null;
        var versions=Directory.EnumerateDirectories(paths.Staging).Select(path=>Path.GetFileName(path)!).Where(name=>SemVer.IsVersion(name)&&name!=record.RefusedVersion&&SemVer.Compare(name,installed.App.Version)>0).ToList();
        versions.Sort(SemVer.Compare);versions.Reverse();
        return versions.Select(version=>Verified(version,key,installed)).FirstOrDefault(found=>found is not null);
    }
    // Re-verified from the exact names, for the track the installed pair selects now.
    // A stage holding anything else, or another track's payload, is not a release.
    private StagedRelease? Verified(string version,byte[] key,ActivePointer installed)
    {
        try
        {
            var directory=paths.Stage(version);
            var manifest=ReadBounded(Path.Combine(directory,ReleaseManifest.ManifestName(version)),ReleaseManifest.MaximumBytes);
            var value=ReleaseVerification.Verify(key,manifest,ReadBounded(Path.Combine(directory,ReleaseManifest.SignatureName(version)),64));
            if(value.Version!=version||ReleaseSelection.Select(value,installed) is not {} track)return null;
            var file=UpdaterRules.Payload(value,track);var payload=Path.Combine(directory,file.Name);
            string[] expected=[ReleaseManifest.ManifestName(version),ReleaseManifest.SignatureName(version),file.Name];
            var present=Directory.EnumerateFileSystemEntries(directory).Select(path=>Path.GetFileName(path)!).Order(StringComparer.Ordinal);
            if(!present.SequenceEqual(expected.Order(StringComparer.Ordinal))||new FileInfo(payload).Length!=file.Bytes)return null;
            using var stream=new FileStream(payload,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(Convert.ToHexStringLower(SHA256.HashData(stream))!=file.Sha256)return null;
            return new(version,track,UpdaterRules.Fingerprint(track,manifest),directory,payload);
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
        var installed=current();staged=Rediscover(key,installed);
        // A stage the live pair no longer selects, such as another track's, goes now.
        if(staged is null||staged.Version!=target.Version||staged.Fingerprint!=target.Fingerprint){CleanStaging();return Refuse("STALE_TARGET");}
        File.Delete(paths.LastUpgrade);
        record=record with{Phase="activating",Error=null,ErrorCode=null,ErrorPermanent=null,LastRequest=new(id,"install","running",Now,null,null,target)};Save();
        try{launch(UpdateLaunch.For(staged,installed),paths.LastUpgrade);}
        catch(Exception error)when(error is IOException or System.ComponentModel.Win32Exception)
        {
            var code=UpdaterRules.Code(error);
            record=record with{Phase="failed",Error="internal",ErrorCode=code,ErrorPermanent=false,LastRequest=Finished(record.LastRequest!,"internal",code)};Save();
            Report(new("install","internal",code,false,installed.App.Version,target.Version,error));return Refuse("INTERNAL");
        }
        return new(1,true);
    }
    // The launched engine outlives this process and may replace it, so its result is
    // read lazily by whichever Updater is running by the time it appears. The pair is
    // read again after it: a commit or a rollback has changed what is installed.
    private void Ingest()
    {
        if(!Activating)return;var request=record.LastRequest;UpgradeEnding ending;
        try
        {
            if(!File.Exists(paths.LastUpgrade))throw new FileNotFoundException();
            using var result=JsonDocument.Parse(ReadBounded(paths.LastUpgrade,65536));
            ending=UpdaterRules.Ending(result.RootElement.GetProperty("Code").GetString());
        }
        // Not there yet, or being written by the engine that is finishing now.
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if(request is not null&&Now-request.StartedAt<UpdaterRules.ResultTimeout.TotalMilliseconds)return;
            ending=new(false,"failed","internal",false,File.Exists(paths.LastUpgrade)?UpdaterRules.ResultInvalid:UpdaterRules.ResultMissing);
        }
        var permanent=ending.Refuse&&request?.Target is not null;
        record=record with{Phase=ending.Phase,Error=ending.Error,ErrorCode=ending.Code,ErrorPermanent=ending.Succeeded?null:permanent,
            LastRequest=request is null?null:Finished(request,ending.Error,ending.Code),LastUpdateAt=ending.Succeeded?Now:record.LastUpdateAt,
            RefusedVersion=ending.Refuse?request?.Target?.Version:record.RefusedVersion,Latest=ending.Succeeded?null:record.Latest};
        Save();
        if(!ending.Succeeded)Report(new("install",ending.Error!,ending.Code!,permanent,From(),request?.Target?.Version,null));
        var installed=current();staged=Key() is {} key?Rediscover(key,installed):null;CleanStaging();
    }
    // Only the offered release stays. A folder a check is still writing, and the stage
    // an install is reading until its result is in, are left alone. A process still
    // exiting holds its stage; the next pass takes what this one could not.
    private void CleanStaging()
    {
        if(progress is not null||Activating||!Directory.Exists(paths.Staging))return;
        foreach(var directory in Directory.EnumerateDirectories(paths.Staging))
            if(Path.GetFileName(directory)!=staged?.Version)TryDeleteTree(directory);
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

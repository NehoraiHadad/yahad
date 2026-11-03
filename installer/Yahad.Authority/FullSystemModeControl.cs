using System.Runtime.InteropServices;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;

namespace Yahad.Authority;

internal sealed record FullSystemInstallProposal(int Format,string InstallationId,string PolicyDigest,string OperationId,string SourceReceiptDigest,string SystemManifestDigest,string ApplicationBundleDigest,string SourceCommit,string FromVersion,string ToVersion,long BootstrapEpoch,string ActivationDigest,int SourceSchema,int TargetSchema,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? BootstrapVersion=null);
internal sealed record FullSystemDispatchRequest(string OperationId,string SourceReceiptDigest,FullSystemInstallProposal? ManualApproval);

// Only protected, explicitly pinned full enrollment enters this path. The
// authenticated Runtime request is the approval; there is no separate approver.
internal sealed class FullSystemModeControl(InstalledStateFiles files,InstalledAuthorityAnchor anchor,FullSystemUpdatePolicy policy,IProtectedSystemCandidateResolver candidates,Func<DateTimeOffset> clock)
{
    internal string PolicyDigest=>policy.PolicyDigest;
    internal async Task<ScheduledUpdateCandidate?> CheckSourceAsync(SystemCandidateIntake full,Func<SystemCandidateEnvironment> observe,CancellationToken cancellation)
    {
        _=RequireEnrollment();var candidate=await full.CheckAndStageAsync(observe(),cancellation);return candidate is null?null:new(candidate.SourceReceiptDigest);
    }
    internal FullSystemEnrollment RequireEnrollment()
    {if(anchor.FullSystemPolicyDigest!=policy.PolicyDigest)throw new UnauthorizedAccessException("FULL_SYSTEM_ANCHOR_PIN");return policy.Enrollment;}
    private NativeUpdateMode Mode()=>new InstalledIntakeSelection(files,anchor).Mode();
    internal void EnableAutomatic()
    {
        using var held=ModeLease();var enrollment=RequireEnrollment();var prior=Mode();
        if(prior.Mode=="auto")policy.DisableCurrent();
        var grant=new FullSystemModeGrant(1,Guid.NewGuid().ToString("N"),anchor.InstallationId,enrollment.Id,policy.PolicyDigest,clock().ToUnixTimeSeconds());
        WriteOnce("grants/system-grant-"+grant.Id+".json",grant);Publish(prior with{Format=2,GrantKind="system",Mode="auto",DelegationId=grant.Id});
    }
    internal bool AutomaticActive()
    {
        _=RequireEnrollment();if(Mode().Mode!="auto")return false;
        try{_=policy.CurrentGrant();return true;}catch(UnauthorizedAccessException){return false;}catch(IOException){return false;}
    }
    internal void Notify()
    {
        using var held=ModeLease();_=RequireEnrollment();var mode=Mode();
        policy.DisableCurrent();Publish(mode with{Mode="notify",DelegationId=null});
    }
    internal AuthorityTarget? Target()
    {
        _=RequireEnrollment();var pointerPath=files.PathOf("intake/system-available.json");if(!File.Exists(pointerPath))return null;
        var pointer=NativeJson.Read<SystemPointer>(files.Read(pointerPath,4096));FixedSystemComponents.Digest(pointer.SourceReceiptDigest);FixedSystemComponents.Digest(pointer.SystemManifestDigest);
        if(pointer.Format!=2||pointer.Profile!=FullSystemUpdatePolicy.Profile||pointer.InstallationId!=anchor.InstallationId)throw new IOException("FULL_SYSTEM_POINTER");
        var bytes=files.Read(files.PathOf("intake/system-source-"+pointer.SourceReceiptDigest+".json"),32768);if(SystemCandidateManifest.Hash(bytes)!=pointer.SourceReceiptDigest)throw new IOException("FULL_SYSTEM_SOURCE_RECEIPT");var receipt=NativeJson.Read<SystemSourceReceipt>(bytes);
        if(receipt.InstallationId!=anchor.InstallationId||receipt.SystemManifestDigest!=pointer.SystemManifestDigest)throw new IOException("FULL_SYSTEM_SOURCE_RECEIPT");_=SystemSourceRelease.FromProtectedReceipt(receipt);
        var head=new InstalledIntakeSelection(files,anchor).Head();var current=files.Read(files.PathOf("payloads/"+head.ReleaseDigest+"/bundle.json"),8*1024*1024);if(SystemCandidateManifest.Hash(current)!=head.ReleaseDigest)throw new IOException("FULL_SYSTEM_CURRENT_MANIFEST");var version=NativeJson.Read<JsonElement>(current).GetProperty("version").GetString()!;
        return Version.Parse(receipt.Version)<=Version.Parse(version)?null:new(receipt.Version,pointer.SourceReceiptDigest);
    }
    internal void RequireAutomatic()=>policy.CurrentGrant();
    internal FullSystemInstallProposal ProposeInstall(string operationId,string target,FullSystemInstalledState observed)
    {
        _=RequireEnrollment();InstalledActivationResolver.Id(operationId);var selected=Target()??throw new IOException("FULL_SYSTEM_NO_TARGET");if(selected.Fingerprint!=target)throw new IOException("FULL_SYSTEM_TARGET_CHANGED");var candidate=candidates.RequireVerified(target);
        if(candidate.Version!=selected.Version)throw new IOException("FULL_SYSTEM_TARGET_CHANGED");
        if(candidate.TargetSchema<observed.DatabaseSchema||candidate.TargetSchema!=observed.DatabaseSchema&&!candidate.MigrationSources.Contains(observed.DatabaseSchema))throw new UnauthorizedAccessException("FULL_SYSTEM_MIGRATION_SCOPE");
        if(Version.Parse(candidate.Version)<Version.Parse(observed.BootstrapVersion??observed.Version))throw new UnauthorizedAccessException("FULL_SYSTEM_BOOTSTRAP_DOWNGRADE");
        return new(1,anchor.InstallationId,policy.PolicyDigest,operationId,target,candidate.ManifestDigest,candidate.ApplicationBundleDigest,candidate.SourceCommit,observed.Version,candidate.Version,observed.BootstrapEpoch,observed.ActivationDigest,observed.DatabaseSchema,candidate.TargetSchema,observed.BootstrapVersion);
    }
    private IDisposable ModeLease()
    {
        // The download/staging lease may be held for minutes. Mode contraction
        // must remain available while that background work is running.
        foreach(var relative in new[]{".authority",".authority/mode-control"})
        {var directory=files.PathOf(relative);if(!Directory.Exists(directory))files.CreateDirectory(directory);using var pin=files.PinDirectory(directory);}
        return files.Lease(files.PathOf(".authority/mode-control"));
    }
    private void WriteOnce<T>(string relative,T value){var bytes=JsonSerializer.SerializeToUtf8Bytes(value);if(bytes.Length>8192)throw new IOException("FULL_SYSTEM_RECORD_BOUND");using var output=files.Create(files.PathOf(relative));output.Write(bytes);output.Flush(true);}
    private void Publish(NativeUpdateMode mode)
    {
        var temporary=files.PathOf("intake/mode.json.partial-"+Guid.NewGuid().ToString("N"));using(var output=files.Create(temporary)){output.Write(JsonSerializer.SerializeToUtf8Bytes(mode));output.Flush(true);}
        if(!MoveFileExW(@"\\?\"+temporary,@"\\?\"+files.PathOf("intake/mode.json"),9))throw new IOException("AUTHORITY_MODE_PUBLICATION");
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool MoveFileExW(string from,string to,uint flags);
    private sealed record SystemPointer(int Format,string Profile,string InstallationId,string SourceReceiptDigest,string SystemManifestDigest);
}

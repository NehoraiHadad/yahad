using System.Runtime.InteropServices;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;

namespace Yahad.Authority;

internal sealed record FullSystemInstallProposal(int Format,string InstallationId,string PolicyDigest,string OperationId,string SourceReceiptDigest,string SystemManifestDigest,string ApplicationBundleDigest,string SourceCommit,string FromVersion,string ToVersion,long BootstrapEpoch,string ActivationDigest,int SourceSchema,int TargetSchema,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? BootstrapVersion=null);
internal sealed record FullSystemDispatchRequest(string OperationId,string SourceReceiptDigest,FullSystemInstallProposal? ManualApproval);
internal sealed record FullSystemGrantProposal(int Format,string Profile,int ConsentVersion,string InstallationId,string OwnerSid,string EnrollmentId,string PolicyDigest,string GrantId,string ExpectedModeDigest,IReadOnlyList<string> Scopes);
internal sealed record FullSystemConsentView(int Format,string Profile,int ConsentVersion,string Repository,string Trust,string EnrollmentId,string PolicyDigest,string GrantId,IReadOnlyList<string> Scopes);
internal sealed record FullSystemGrantStatus(string State,string Profile,bool UntilRevoked,IReadOnlyList<string> Scopes);

// Only protected, explicitly pinned full enrollment enters this path. Routine
// delegation creation/expiry/budget/schema checks remain in the existing ledger.
internal sealed class FullSystemModeControl(InstalledStateFiles files,InstalledAuthorityAnchor anchor,OwnerApprovalLedger ledger,FullSystemUpdatePolicy policy,IProtectedSystemCandidateResolver candidates,Func<DateTimeOffset> clock)
{
    internal string PolicyDigest=>policy.PolicyDigest;
    internal async Task<ScheduledUpdateCandidate?> CheckSourceAsync(NativeCandidateIntake legacy,SystemCandidateIntake full,Func<SystemCandidateEnvironment> observe,CancellationToken cancellation)
    {
        if(Mode().IsSystem){_=RequireEnrollment();var candidate=await full.CheckAndStageAsync(observe(),cancellation);return candidate is null?null:new(candidate.SourceReceiptDigest);}
        var selected=await legacy.CheckAndPublishAsync(cancellation);return selected is null?null:new(selected.Target);
    }
    internal FullSystemEnrollment RequireEnrollment()
    {if(anchor.FullSystemPolicyDigest!=policy.PolicyDigest)throw new UnauthorizedAccessException("FULL_SYSTEM_ANCHOR_PIN");return policy.Enrollment;}
    private NativeUpdateMode Mode()=>new InstalledIntakeSelection(files,anchor,clock).Mode();
    internal FullSystemGrantProposal ProposeGrant()
    {
        var enrollment=RequireEnrollment();if(!Mode().IsSystem)throw new UnauthorizedAccessException("FULL_SYSTEM_MODE_PROFILE");
        return new(1,FullSystemUpdatePolicy.Profile,2,anchor.InstallationId,anchor.OwnerSid,enrollment.Id,policy.PolicyDigest,Guid.NewGuid().ToString("N"),ModeDigest(),Array.AsReadOnly(FullSystemUpdatePolicy.RequiredScopes.ToArray()));
    }
    internal FullSystemConsentView ConsentView(FullSystemGrantProposal proposal)=>new(1,proposal.Profile,proposal.ConsentVersion,NativeCandidateIntake.Repository,"https-github-unsigned",proposal.EnrollmentId,proposal.PolicyDigest,proposal.GrantId,proposal.Scopes);
    internal void AnswerGrant(AuthorityPeer peer,OwnerAnswer answer,FullSystemGrantProposal proposal)
    {
        using var held=ModeLease();var enrollment=RequireEnrollment();
        if(proposal.InstallationId!=anchor.InstallationId||proposal.OwnerSid!=anchor.OwnerSid||proposal.EnrollmentId!=enrollment.Id||proposal.PolicyDigest!=policy.PolicyDigest||proposal.Profile!=FullSystemUpdatePolicy.Profile||proposal.ConsentVersion!=2||proposal.ExpectedModeDigest!=ModeDigest()||!proposal.Scopes.SequenceEqual(FullSystemUpdatePolicy.RequiredScopes))throw new UnauthorizedAccessException("FULL_SYSTEM_CONSENT_CHANGED");
        ledger.ApproveSystem(peer,answer,"delegate-system",AuthorityProtocol.Digest(proposal));if(!answer.Approve)return;
        var approved=ledger.AuthenticateAsync(answer.OperationDigest,"delegate-system",default).GetAwaiter().GetResult();
        if(approved.Subject!=anchor.OwnerSid||approved.InstallationId!=anchor.InstallationId)throw new UnauthorizedAccessException("FULL_SYSTEM_CONSENT_OWNER");
        var prior=Mode();if(prior.IsSystem&&prior.Mode=="auto")policy.DisableCurrent();
        var grant=new FullSystemModeGrant(1,proposal.GrantId,anchor.InstallationId,anchor.OwnerSid,enrollment.Id,policy.PolicyDigest,approved.ApprovedUnix);
        WriteOnce("owner-ledger/system-grant-"+grant.Id+".json",grant);Publish(prior with{Format=2,GrantKind="system",Mode="auto",DelegationId=grant.Id});
    }
    internal void Notify()
    {
        using var held=ModeLease();_=RequireEnrollment();var mode=Mode();if(!mode.IsSystem)throw new UnauthorizedAccessException("FULL_SYSTEM_MODE_PROFILE");
        policy.DisableCurrent();Publish(mode with{Mode="notify",DelegationId=null});
    }
    internal FullSystemGrantStatus Status()
    {
        _=RequireEnrollment();var mode=Mode();var state="disabled";
        if(mode.Mode=="auto")try{_=policy.CurrentGrant();state="active";}catch(UnauthorizedAccessException error){state=error.Message=="FULL_SYSTEM_GRANT_REVOKED"?"revoked":"unavailable";}catch(IOException){state="unavailable";}
        return new(state,FullSystemUpdatePolicy.Profile,true,Array.AsReadOnly(FullSystemUpdatePolicy.RequiredScopes.ToArray()));
    }
    internal AuthorityTarget? Target()
    {
        _=RequireEnrollment();var pointerPath=files.PathOf("intake/system-available.json");if(!File.Exists(pointerPath))return null;
        var pointer=NativeJson.Read<SystemPointer>(files.Read(pointerPath,4096));FixedSystemComponents.Digest(pointer.SourceReceiptDigest);FixedSystemComponents.Digest(pointer.SystemManifestDigest);
        if(pointer.Format!=2||pointer.Profile!=FullSystemUpdatePolicy.Profile||pointer.InstallationId!=anchor.InstallationId)throw new IOException("FULL_SYSTEM_POINTER");
        var bytes=files.Read(files.PathOf("intake/system-source-"+pointer.SourceReceiptDigest+".json"),32768);if(SystemCandidateManifest.Hash(bytes)!=pointer.SourceReceiptDigest)throw new IOException("FULL_SYSTEM_SOURCE_RECEIPT");var receipt=NativeJson.Read<SystemSourceReceipt>(bytes);
        if(receipt.InstallationId!=anchor.InstallationId||receipt.SystemManifestDigest!=pointer.SystemManifestDigest)throw new IOException("FULL_SYSTEM_SOURCE_RECEIPT");_=SystemSourceRelease.FromProtectedReceipt(receipt);
        var head=new InstalledIntakeSelection(files,anchor,clock).Head();var current=files.Read(files.PathOf("payloads/"+head.ReleaseDigest+"/bundle.json"),8*1024*1024);if(SystemCandidateManifest.Hash(current)!=head.ReleaseDigest)throw new IOException("FULL_SYSTEM_CURRENT_MANIFEST");var version=NativeJson.Read<JsonElement>(current).GetProperty("version").GetString()!;
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
    internal void RequireInstallUnchanged(FullSystemInstallProposal proposal,FullSystemInstalledState observed)
    {if(ProposeInstall(proposal.OperationId,proposal.SourceReceiptDigest,observed)!=proposal)throw new IOException("FULL_SYSTEM_APPROVAL_TARGET_CHANGED");}
    private string ModeDigest()=>SystemCandidateManifest.Hash(files.Read(files.PathOf("intake/mode.json"),8192));
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

using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record FullSystemEnrollment(int Format,string Profile,string Id,string InstallationId,string OwnerSid,
    string Repository,string Channel,string Trust,int ConsentVersion,long ApprovedUnix,string[] Scopes);
internal sealed record FullSystemModeGrant(int Format,string Id,string InstallationId,string OwnerSid,string EnrollmentId,string PolicyDigest,long ApprovedUnix);
internal sealed record FullSystemManualGrant(int Format,string Id,string InstallationId,string OwnerSid,string EnrollmentId,string PolicyDigest,string ProposalDigest,long ApprovedUnix,long ValidUntilUnix);
internal sealed record FullSystemInstalledState(string Version,long BootstrapEpoch,string ActivationDigest,int DatabaseSchema,string? BootstrapVersion=null);
// Facts are returned by the configured protected candidate resolver, never read
// from an HTTP/owner frame. The resolver revalidates source receipt + slot closure.
internal sealed record SystemCandidatePolicyFacts(string ManifestDigest,string ApplicationBundleDigest,string Version,string SourceCommit,int TargetSchema,
    IReadOnlyList<int> MigrationSources,IReadOnlyList<SystemComponentDescriptor> Components);
internal interface IProtectedSystemCandidateResolver
{SystemCandidatePolicyFacts RequireVerified(string receiptDigest);}

// Explicit new enrollment for unattended application, migration and bootstrap
// updates. It NEVER interprets an old routine delegation as this consent and
// never extends its 90-day/100-operation/same-schema limits.
internal sealed class FullSystemUpdatePolicy
{
    internal const string Profile="full-system-v1";
    internal static readonly IReadOnlyList<string> RequiredScopes=Array.AsReadOnly(new[]{"application","database","authority","servicehost","owner","probe","handoff"});
    private readonly InstalledStateFiles files;private readonly string installation,owner,pin;
    private readonly Func<DateTimeOffset> clock;private readonly IProtectedSystemCandidateResolver candidates;
    private FullSystemUpdatePolicy(InstalledStateFiles files,string installation,string owner,string pin,Func<DateTimeOffset> clock,IProtectedSystemCandidateResolver candidates)
    {this.files=files;this.installation=installation;this.owner=owner;this.pin=pin;this.clock=clock;this.candidates=candidates;}
    internal string PolicyDigest=>pin;
    internal FullSystemEnrollment Enrollment=>ReadEnrollment();
    internal static FullSystemUpdatePolicy Open(InstalledStateFiles files,string installation,string owner,string? immutablePolicyDigest,
        Func<DateTimeOffset> clock,IProtectedSystemCandidateResolver candidates)
    {
        if(immutablePolicyDigest is null)throw new UnauthorizedAccessException("FULL_SYSTEM_EXPLICIT_CONSENT_REQUIRED");
        InstalledActivationResolver.Id(installation);InstalledActivationResolver.Digest(immutablePolicyDigest);
        var result=new FullSystemUpdatePolicy(files,installation,owner,immutablePolicyDigest,clock,candidates);_=result.ReadEnrollment();return result;
    }
    private FullSystemEnrollment ReadEnrollment()
    {
        var bytes=files.Read(files.PathOf("intake/full-system-enrollment.json"),8192);
        if(Hash(bytes)!=pin)throw new UnauthorizedAccessException("FULL_SYSTEM_POLICY_CHANGED");
        var e=NativeJson.Read<FullSystemEnrollment>(bytes);InstalledActivationResolver.Id(e.Id);
        if(e.Format!=1||e.Profile!=Profile||e.ConsentVersion!=2||e.InstallationId!=installation||e.OwnerSid!=owner||
            e.Repository!=NativeCandidateIntake.Repository||e.Channel!="stable"||e.Trust!="https-github-unsigned"||e.ApprovedUnix<=0||e.ApprovedUnix>clock().ToUnixTimeSeconds()||
            e.Scopes is null||e.Scopes.Length!=RequiredScopes.Count||!e.Scopes.Order(StringComparer.Ordinal).SequenceEqual(RequiredScopes.Order(StringComparer.Ordinal)))
            throw new UnauthorizedAccessException("FULL_SYSTEM_POLICY_SCOPE");
        return e;
    }
    internal (FullSystemModeGrant Grant,string Binding) CurrentGrant()
    {
        var e=ReadEnrollment();var mode=NativeJson.Read<SystemMode>(files.Read(files.PathOf("intake/mode.json"),8192));
        if(mode.Format!=2||mode.InstallationId!=installation||mode.GrantKind!="system"||mode.Mode!="auto"||mode.DelegationId is null)
            throw new UnauthorizedAccessException("FULL_SYSTEM_AUTOMATIC_DISABLED");
        InstalledActivationResolver.Id(mode.DelegationId);
        if(Present(files.PathOf("owner-ledger/revoked-system-grant-"+mode.DelegationId+".json")))throw new UnauthorizedAccessException("FULL_SYSTEM_GRANT_REVOKED");
        var bytes=files.Read(files.PathOf("owner-ledger/system-grant-"+mode.DelegationId+".json"),8192);var grant=NativeJson.Read<FullSystemModeGrant>(bytes);
        if(grant.Format!=1||grant.Id!=mode.DelegationId||grant.InstallationId!=installation||grant.OwnerSid!=owner||grant.EnrollmentId!=e.Id||grant.PolicyDigest!=pin||grant.ApprovedUnix<e.ApprovedUnix||grant.ApprovedUnix>clock().ToUnixTimeSeconds())
            throw new UnauthorizedAccessException("FULL_SYSTEM_GRANT_BINDING");
        return(grant,Hash(bytes));
    }
    internal VerifiedSystemOperation Authorize(string operationId,string receiptDigest,FullSystemInstalledState observed)
    {
        InstalledActivationResolver.Id(operationId);InstalledActivationResolver.Digest(receiptDigest);InstalledActivationResolver.Digest(observed.ActivationDigest);
        var current=CurrentGrant();var candidate=candidates.RequireVerified(receiptDigest);
        if(observed.BootstrapEpoch<1||observed.DatabaseSchema is <1 or >100000||candidate.TargetSchema is <1 or >100000||
            candidate.TargetSchema<observed.DatabaseSchema||candidate.TargetSchema!=observed.DatabaseSchema&&!candidate.MigrationSources.Contains(observed.DatabaseSchema))
            throw new UnauthorizedAccessException("FULL_SYSTEM_MIGRATION_SCOPE");
        var operation=VerifiedSystemOperation.CreateAuthorized(installation,operationId,candidate.ManifestDigest,candidate.ApplicationBundleDigest,pin,current.Grant.Id,current.Binding,
            observed.Version,candidate.Version,observed.BootstrapEpoch,observed.ActivationDigest,observed.DatabaseSchema,candidate.TargetSchema,candidate.SourceCommit,candidate.Components,observed.BootstrapVersion);
        FullSystemOperationJournal.Publish(files,new(1,installation,operationId,receiptDigest,pin,current.Grant.Id,current.Binding,observed,
            candidate.ManifestDigest,candidate.ApplicationBundleDigest,candidate.Version,candidate.SourceCommit,candidate.TargetSchema));
        return operation;
    }
    // Reconstruct only from an Authority-protected durable admission, the exact
    // original grant bytes and reverified source closure. A revoked grant may
    // still repair its own interrupted transaction; EVERY forward publication
    // must separately pass RequireCurrent, so this cannot renew authorization.
    internal VerifiedSystemOperation RemintBootstrapRecovery(string operationId)
    {
        _=ReadEnrollment();var record=FullSystemOperationJournal.Read(files,installation,operationId);
        if(record.PolicyDigest!=pin)throw new UnauthorizedAccessException("FULL_SYSTEM_RECOVERY_POLICY");
        var enrollment=ReadEnrollment();
        if(record.GrantKind=="auto")
        {
            var grantBytes=files.Read(files.PathOf("owner-ledger/system-grant-"+record.GrantId+".json"),8192);var grant=NativeJson.Read<FullSystemModeGrant>(grantBytes);
            if(Hash(grantBytes)!=record.GrantBindingDigest||grant.Format!=1||grant.Id!=record.GrantId||grant.InstallationId!=installation||
                grant.OwnerSid!=owner||grant.EnrollmentId!=enrollment.Id||grant.PolicyDigest!=pin||grant.ApprovedUnix<enrollment.ApprovedUnix||grant.ApprovedUnix>clock().ToUnixTimeSeconds())
                throw new UnauthorizedAccessException("FULL_SYSTEM_RECOVERY_GRANT");
        }
        else _=ReadManualGrant(record,enrollment);
        var candidate=candidates.RequireVerified(record.SourceReceiptDigest);
        if(candidate.ManifestDigest!=record.ManifestDigest||candidate.ApplicationBundleDigest!=record.ApplicationBundleDigest||candidate.Version!=record.TargetVersion||
            candidate.SourceCommit!=record.SourceCommit||candidate.TargetSchema!=record.TargetSchema||candidate.TargetSchema<record.Observed.DatabaseSchema||
            candidate.TargetSchema!=record.Observed.DatabaseSchema&&!candidate.MigrationSources.Contains(record.Observed.DatabaseSchema))
            throw new UnauthorizedAccessException("FULL_SYSTEM_RECOVERY_SOURCE_CHANGED");
        return VerifiedSystemOperation.CreateAuthorized(installation,operationId,candidate.ManifestDigest,candidate.ApplicationBundleDigest,pin,record.GrantId,record.GrantBindingDigest,
            record.Observed.Version,candidate.Version,record.Observed.BootstrapEpoch,record.Observed.ActivationDigest,record.Observed.DatabaseSchema,candidate.TargetSchema,candidate.SourceCommit,candidate.Components,record.Observed.BootstrapVersion);
    }
    internal void RequireCurrent(VerifiedSystemOperation operation)
    {
        var record=FullSystemOperationJournal.Read(files,installation,operation.OperationId);
        if(operation.InstallationId!=record.InstallationId||operation.PolicyDigest!=record.PolicyDigest||operation.GrantId!=record.GrantId||operation.GrantBindingDigest!=record.GrantBindingDigest||
            operation.ManifestDigest!=record.ManifestDigest||operation.ApplicationBundleDigest!=record.ApplicationBundleDigest||operation.ToVersion!=record.TargetVersion||operation.SourceCommit!=record.SourceCommit||
            operation.FromVersion!=record.Observed.Version||operation.ExpectedBootstrapEpoch!=record.Observed.BootstrapEpoch||operation.PreviousActivationDigest!=record.Observed.ActivationDigest||
            operation.SourceSchema!=record.Observed.DatabaseSchema||operation.TargetSchema!=record.TargetSchema||operation.BootstrapFromVersion!=(record.Observed.BootstrapVersion??record.Observed.Version))throw new UnauthorizedAccessException("FULL_SYSTEM_ADMISSION_CHANGED");
        if(record.GrantKind=="manual")
        {
            var manual=ReadManualGrant(record,ReadEnrollment());
            var approved=new OwnerApprovalLedger(files,installation,owner,clock).AuthenticateAsync(manual.ProposalDigest,"activate-system",CancellationToken.None).GetAwaiter().GetResult();
            if(approved.Subject!=owner||approved.InstallationId!=installation||approved.ApprovedUnix!=manual.ApprovedUnix||approved.ValidUntilUnix!=manual.ValidUntilUnix||
                operation.PolicyDigest!=pin||operation.GrantId!=manual.Id||operation.GrantBindingDigest!=record.GrantBindingDigest||operation.ManifestDigest!=record.ManifestDigest||operation.ApplicationBundleDigest!=record.ApplicationBundleDigest)
                throw new UnauthorizedAccessException("FULL_SYSTEM_MANUAL_OPERATION_CHANGED");return;
        }
        var current=CurrentGrant();
        if(operation.InstallationId!=installation||operation.PolicyDigest!=pin||operation.GrantId!=current.Grant.Id||operation.GrantBindingDigest!=current.Binding)
            throw new UnauthorizedAccessException("FULL_SYSTEM_OPERATION_REVOKED");
    }
    // Only repair adapters may use this check. It proves the exact original
    // protected operation despite later expiry/revocation; it does NOT permit
    // candidate publication, a new migration, or release of candidate effects.
    internal void RequireRecoveryEvidence(VerifiedSystemOperation operation)
    {
        var original=RemintBootstrapRecovery(operation.OperationId);
        if(AuthorityProtocol.Digest(original)!=AuthorityProtocol.Digest(operation))throw new UnauthorizedAccessException("FULL_SYSTEM_RECOVERY_CAPABILITY_CHANGED");
    }
    internal async Task<VerifiedSystemOperation> AuthorizeManualAsync(FullSystemInstallProposal proposal,OwnerApprovalLedger ledger,CancellationToken cancellation)
    {
        InstalledActivationResolver.Id(proposal.OperationId);InstalledActivationResolver.Digest(proposal.SourceReceiptDigest);
        var enrollment=ReadEnrollment();var digest=AuthorityProtocol.Digest(proposal);
        var approved=await ledger.AuthenticateAsync(digest,"activate-system",cancellation);var candidate=candidates.RequireVerified(proposal.SourceReceiptDigest);
        if(approved.Subject!=owner||approved.InstallationId!=installation||proposal.Format!=1||proposal.InstallationId!=installation||proposal.PolicyDigest!=pin||
            proposal.SystemManifestDigest!=candidate.ManifestDigest||proposal.ApplicationBundleDigest!=candidate.ApplicationBundleDigest||proposal.SourceCommit!=candidate.SourceCommit||
            proposal.ToVersion!=candidate.Version||proposal.TargetSchema!=candidate.TargetSchema||candidate.TargetSchema<proposal.SourceSchema||
            candidate.TargetSchema!=proposal.SourceSchema&&!candidate.MigrationSources.Contains(proposal.SourceSchema))throw new UnauthorizedAccessException("FULL_SYSTEM_MANUAL_PROPOSAL_CHANGED");
        var grant=new FullSystemManualGrant(1,proposal.OperationId,installation,owner,enrollment.Id,pin,digest,approved.ApprovedUnix,approved.ValidUntilUnix);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(grant);var path=files.PathOf("owner-ledger/system-manual-grant-"+grant.Id+".json");
        if(Present(path)){if(!files.Read(path,8192).AsSpan().SequenceEqual(bytes))throw new IOException("FULL_SYSTEM_MANUAL_GRANT_REPLAY");}
        else {using var output=files.Create(path);output.Write(bytes);output.Flush(true);}
        var observed=new FullSystemInstalledState(proposal.FromVersion,proposal.BootstrapEpoch,proposal.ActivationDigest,proposal.SourceSchema,proposal.BootstrapVersion);
        var operation=VerifiedSystemOperation.CreateAuthorized(installation,proposal.OperationId,candidate.ManifestDigest,candidate.ApplicationBundleDigest,pin,grant.Id,Hash(bytes),
            proposal.FromVersion,candidate.Version,proposal.BootstrapEpoch,proposal.ActivationDigest,proposal.SourceSchema,candidate.TargetSchema,candidate.SourceCommit,candidate.Components,proposal.BootstrapVersion);
        FullSystemOperationJournal.Publish(files,new(1,installation,proposal.OperationId,proposal.SourceReceiptDigest,pin,grant.Id,Hash(bytes),observed,
            candidate.ManifestDigest,candidate.ApplicationBundleDigest,candidate.Version,candidate.SourceCommit,candidate.TargetSchema,"manual",digest));
        RequireCurrent(operation);return operation;
    }
    private FullSystemManualGrant ReadManualGrant(FullSystemOperationRecord record,FullSystemEnrollment enrollment)
    {
        var bytes=files.Read(files.PathOf("owner-ledger/system-manual-grant-"+record.GrantId+".json"),8192);var manual=NativeJson.Read<FullSystemManualGrant>(bytes);
        if(Hash(bytes)!=record.GrantBindingDigest||manual.Format!=1||manual.Id!=record.GrantId||manual.InstallationId!=installation||manual.OwnerSid!=owner||manual.EnrollmentId!=enrollment.Id||
            manual.PolicyDigest!=pin||manual.ProposalDigest!=record.ManualProposalDigest||manual.ApprovedUnix<enrollment.ApprovedUnix||manual.ApprovedUnix>clock().ToUnixTimeSeconds()||manual.ValidUntilUnix<=manual.ApprovedUnix)
            throw new UnauthorizedAccessException("FULL_SYSTEM_MANUAL_GRANT_BINDING");return manual;
    }
    internal void DisableCurrent()
    {
        var mode=NativeJson.Read<SystemMode>(files.Read(files.PathOf("intake/mode.json"),8192));
        if(mode.Format!=2||mode.InstallationId!=installation||mode.GrantKind!="system")throw new UnauthorizedAccessException("FULL_SYSTEM_AUTOMATIC_DISABLED");
        if(mode.Mode=="notify"&&mode.DelegationId is null)return;
        if(mode.DelegationId is null)throw new UnauthorizedAccessException("FULL_SYSTEM_GRANT_BINDING");InstalledActivationResolver.Id(mode.DelegationId);
        var path=files.PathOf("owner-ledger/revoked-system-grant-"+mode.DelegationId+".json");
        if(Present(path))return;
        var current=CurrentGrant();
        using var output=files.Create(path);output.Write(JsonSerializer.SerializeToUtf8Bytes(new{Format=1,Id=current.Grant.Id,InstallationId=installation,RevokedUnix=clock().ToUnixTimeSeconds()}));output.Flush(true);
    }
    private static bool Present(string path)
    {try{var a=File.GetAttributes(path);if((a&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)throw new IOException("FULL_SYSTEM_RECORD_KIND");return true;}catch(FileNotFoundException){return false;}catch(DirectoryNotFoundException){return false;}}
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed record SystemMode(int Format,string InstallationId,string Mode,string? DelegationId,bool ModeControlAllowed,string GrantKind);
}

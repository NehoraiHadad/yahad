using System.Security.Principal;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;

namespace Yahad.Authority;

internal sealed record FullSystemInstallerPlan(string PolicyDigest,string EnrollmentPath,byte[] Enrollment,string GrantPath,byte[] InitialGrant,byte[] Mode,byte[] OperatorBinding);

// Pure initial-installer handoff. This does not write files, pin an anchor, alter
// ACLs or accept consent from HTTP/IPC. The installer must call it only after its
// existing authenticated ORIGINAL Owner process accepted consentVersion 2 before
// UAC; an approving administrator's SID must never replace that captured Owner.
// Admin provisioning pins PolicyDigest as FullSystemPolicyDigest, writes these
// records in their existing protected roles, and preserves enrollment on repair.
internal static class FullSystemInstallerEnrollment
{
    internal static FullSystemInstallerPlan Prepare(string installationId,string originalOwnerSid,int consentVersion,string familyId,string parentUserId,string intakePolicyDigest,string intakePinDigest,DateTimeOffset approved,bool automatic)
    {
        InstalledActivationResolver.Id(installationId);InstalledActivationResolver.Digest(intakePolicyDigest);InstalledActivationResolver.Digest(intakePinDigest);
        var owner=new SecurityIdentifier(originalOwnerSid);if(consentVersion!=2||!owner.IsAccountSid()||!originalOwnerSid.StartsWith("S-1-5-21-",StringComparison.Ordinal)||string.IsNullOrWhiteSpace(familyId)||familyId.Length>100||string.IsNullOrWhiteSpace(parentUserId)||parentUserId.Length>100||approved.ToUnixTimeSeconds()<=0)throw new IOException("FULL_SYSTEM_INSTALLER_CONSENT");
        var enrollment=new FullSystemEnrollment(1,FullSystemUpdatePolicy.Profile,Guid.NewGuid().ToString("N"),installationId,originalOwnerSid,NativeCandidateIntake.Repository,"stable","https-github-unsigned",2,approved.ToUnixTimeSeconds(),FullSystemUpdatePolicy.RequiredScopes.ToArray());
        var bytes=JsonSerializer.SerializeToUtf8Bytes(enrollment);var pin=SystemCandidateManifest.Hash(bytes);var grant=new FullSystemModeGrant(1,Guid.NewGuid().ToString("N"),installationId,originalOwnerSid,enrollment.Id,pin,approved.ToUnixTimeSeconds());
        var mode=new NativeUpdateMode(2,installationId,automatic?"auto":"notify",automatic?grant.Id:null,true,"system");
        // Existing record retains designated-parent/operator binding. Source
        // selection is determined by the pinned profile + Format-2 mode, never
        // by an app-supplied URL or this compatibility binding's asset pattern.
        var binding=new NativeUpdateEnrollment(1,installationId,NativeCandidateIntake.Repository,"stable","yahad-app-native-{version}.zip","https-github-unsigned",familyId,parentUserId,intakePolicyDigest,intakePinDigest);
        return new(pin,"intake/full-system-enrollment.json",bytes,"owner-ledger/system-grant-"+grant.Id+".json",JsonSerializer.SerializeToUtf8Bytes(grant),JsonSerializer.SerializeToUtf8Bytes(mode),JsonSerializer.SerializeToUtf8Bytes(binding));
    }
}

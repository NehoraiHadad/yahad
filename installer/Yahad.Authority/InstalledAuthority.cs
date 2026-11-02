using System.Security.Principal;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record InstalledAuthorityAnchor(int Format,string InstallationId,string RuntimeService,string AuthorityService,string OwnerSid,string IntakePolicyDigest,string IntakePinDigest,string? ProbeSha256=null,string? FullSystemPolicyDigest=null);
internal static class InstalledAuthorityReader
{
    internal const string AuthorityService=InstalledUpdaterLayout.AuthorityServiceName;
    internal static string RequireUpdater()
    {
        _=Read();return new WindowsServiceTokenReader().Read().UserSid;
    }
    internal static InstalledAuthorityAnchor Read(bool probe=false)
    {
        var authority=(SecurityIdentifier)new NTAccount("NT SERVICE",AuthorityService).Translate(typeof(SecurityIdentifier));
        var runtime=(SecurityIdentifier)new NTAccount("NT SERVICE",ServiceDefinition.Name).Translate(typeof(SecurityIdentifier));
        var token=new WindowsServiceTokenReader().Read();var user=new SecurityIdentifier(token.UserSid);
        var actor=UpdaterServiceIdentity.FromUser(user,authority);
        ServiceIdentityPolicy.Require(token,user.Value);
        var (roles,bytes)=InstalledUpdaterLayout.ReadRoles(authority,runtime);
        if(probe)
        {
            if(!roles.HasRecoveryHelpers)throw new IOException("HELPER_BOOT_FULL_CONSENT_ANCHOR_REQUIRED");
            SystemBootstrapHelperProbeApplication.RequireCurrentProbe(roles);
        }
        else
        {
            if(actor!=UpdaterServiceActor.Authority&&!roles.HasRecoveryHelpers)throw new IOException("HELPER_BOOT_FULL_CONSENT_ANCHOR_REQUIRED");
            if(!string.Equals(Environment.ProcessPath,UpdaterServiceIdentity.Image(actor),StringComparison.OrdinalIgnoreCase))throw new IOException("HELPER_BOOT_SELF_IMAGE");
            UpdaterServiceIdentity.Require(actor,Environment.ProcessId);
        }
        return Parse(bytes);
    }
    internal static InstalledAuthorityAnchor Parse(byte[] bytes)
    {
        if(bytes.Length is <2 or >4096)throw new IOException("AUTHORITY_ANCHOR_BOUND");var a=NativeJson.Read<InstalledAuthorityAnchor>(bytes);
        InstalledActivationResolver.Id(a.InstallationId);InstalledActivationResolver.Digest(a.IntakePolicyDigest);InstalledActivationResolver.Digest(a.IntakePinDigest);
        if(a.ProbeSha256 is not null)InstalledActivationResolver.Digest(a.ProbeSha256);if(a.FullSystemPolicyDigest is not null)InstalledActivationResolver.Digest(a.FullSystemPolicyDigest);var sid=new SecurityIdentifier(a.OwnerSid);
        if(a.Format!=1||a.RuntimeService!=ServiceDefinition.Name||a.AuthorityService!=AuthorityService||!sid.IsAccountSid()||!a.OwnerSid.StartsWith("S-1-5-21-",StringComparison.Ordinal))throw new IOException("AUTHORITY_ANCHOR_BINDING");return a;
    }
}

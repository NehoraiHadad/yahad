using System.Security.Principal;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record InstalledAuthorityAnchor(int Format,string InstallationId,string RuntimeService,string AuthorityService,string OwnerSid,string IntakePolicyDigest,string IntakePinDigest);
internal static class InstalledAuthorityReader
{
    internal const string AuthorityService="Yahad.Acceptance.Authority";
    internal static InstalledAuthorityAnchor Read()
    {
        var authority=(SecurityIdentifier)new NTAccount("NT SERVICE",AuthorityService).Translate(typeof(SecurityIdentifier));
        var runtime=(SecurityIdentifier)new NTAccount("NT SERVICE",ServiceDefinition.Name).Translate(typeof(SecurityIdentifier));
        ServiceIdentityPolicy.Require(new WindowsServiceTokenReader().Read(),authority.Value);
        var roles=new InstalledFileRoles(authority,runtime);
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),ServiceDefinition.Name,"installation.json");
        using var file=WindowsFileBoundary.OpenInstalledRoleRead(path,roles,InstalledFileRole.Anchor);return Parse(file.Small(4096));
    }
    internal static InstalledAuthorityAnchor Parse(byte[] bytes)
    {
        if(bytes.Length is <2 or >4096)throw new IOException("AUTHORITY_ANCHOR_BOUND");var a=NativeJson.Read<InstalledAuthorityAnchor>(bytes);
        InstalledActivationResolver.Id(a.InstallationId);InstalledActivationResolver.Digest(a.IntakePolicyDigest);InstalledActivationResolver.Digest(a.IntakePinDigest);
        var sid=new SecurityIdentifier(a.OwnerSid);
        if(a.Format!=1||a.RuntimeService!=ServiceDefinition.Name||a.AuthorityService!=AuthorityService||!sid.IsAccountSid()||!a.OwnerSid.StartsWith("S-1-5-21-",StringComparison.Ordinal))throw new IOException("AUTHORITY_ANCHOR_BINDING");return a;
    }
}

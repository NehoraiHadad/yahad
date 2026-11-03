using System.Security.Principal;
using System.Text.Json;
namespace Yahad.Install;

// Extends the existing installation in place. The Format-2 installation receipt
// keeps its own name/owner; only the new updater anchor uses AnchorFile. This
// class neither provisions nor migrates any path, principal or receipt.
internal static class InstalledUpdaterLayout
{
    internal const string StateProduct="Yahad.FirstInstall",BinaryProduct="Yahad.FirstInstall";
    internal const string RuntimeServiceName="Yahad.Acceptance",AuthorityServiceName="Yahad.Acceptance.Authority";
    internal const string AnchorFile="update-authority.json";
    internal static string StateRoot=>Path.Combine(MachineFolders.Required(Environment.SpecialFolder.CommonApplicationData),StateProduct);
    internal static string BinaryRoot=>Path.Combine(MachineFolders.Required(Environment.SpecialFolder.ProgramFiles),BinaryProduct);
    // Immutable application payloads: written by Setup/updater, read and
    // executed by the Runtime. Mutable data and secrets stay under StateRoot.
    internal static string PayloadRoot=>Path.Combine(BinaryRoot,"payloads");
    internal static string AnchorPath=>Path.Combine(StateRoot,AnchorFile);
    internal static string DescriptorPath=>Path.Combine(StateRoot,"bootstrap.json");
    internal static string RuntimeExecutable=>Path.Combine(BinaryRoot,"host","Yahad.ServiceHost.exe");
    internal static string AuthorityExecutable=>Path.Combine(BinaryRoot,"bootstrap","authority","Yahad.Authority.exe");
    internal static string ProbeExecutable=>Path.Combine(BinaryRoot,"bootstrap","probe","node","node.exe");
    // Call only after validating the actual service token. File.Exists would
    // conflate access denial with absence and could choose an unsafe fallback.
    internal static bool AnchorPresent()=>AnchorPresent(()=>File.GetAttributes(AnchorPath));
    // Trusted test seam; never selectable through protocol, CLI or environment.
    internal static bool AnchorPresent(Func<FileAttributes> attributes)
    {
        try
        {
            var observed=attributes();
            if((observed&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)throw new IOException("UPDATER_ANCHOR_KIND");
            return true;
        }
        catch(FileNotFoundException){return false;}
        catch(DirectoryNotFoundException){return false;}
    }
    internal static void RequireInstalledAnchor(byte[] bytes)
    {
        var value=NativeJson.Read<UpdaterAnchorDocument>(bytes);
        if(value.Format!=1||value.RuntimeService!=RuntimeServiceName||value.AuthorityService!=AuthorityServiceName||
            !Guid.TryParseExact(value.InstallationId,"N",out var id)||id.ToString("N")!=value.InstallationId||
            !Digest(value.IntakePolicyDigest)||!Digest(value.IntakePinDigest)||!Digest(value.ProbeSha256)||value.FullSystemPolicyDigest is not null&&!Digest(value.FullSystemPolicyDigest))throw new IOException("INSTALLED_TOKEN_ANCHOR");
    }
    internal static (InstalledFileRoles Roles,byte[] Bytes) ReadRoles(SecurityIdentifier authority,SecurityIdentifier runtime)
    {
        var roles=new InstalledFileRoles(authority,runtime);
        // The anchor is always administrative-owned. Read the bounded full
        // descriptor first, then require explicit immutable full-profile consent
        // before returning helper rights. An old app-only anchor stays strict.
        using var pinned=WindowsFileBoundary.OpenInstalledRoleRead(AnchorPath,roles.WithRecoveryHelpers(),InstalledFileRole.Anchor);
        var bytes=pinned.Small(4096);RequireInstalledAnchor(bytes);
        if(NativeJson.Read<UpdaterAnchorDocument>(bytes).FullSystemPolicyDigest is not null)return(roles.WithRecoveryHelpers(),bytes);
        using var legacy=WindowsFileBoundary.OpenInstalledRoleRead(AnchorPath,roles,InstalledFileRole.Anchor);
        if(!legacy.Small(4096).AsSpan().SequenceEqual(bytes))throw new IOException("INSTALLED_TOKEN_ANCHOR_CHANGED");
        return(roles,bytes);
    }
    internal static bool AllowsLoopbackOrigin(byte[] bytes,string installation)
    {
        RequireInstalledAnchor(bytes);var anchor=NativeJson.Read<UpdaterAnchorDocument>(bytes);
        if(anchor.InstallationId!=installation)throw new IOException("INSTALLED_TOKEN_ANCHOR");
        return anchor.FullSystemPolicyDigest is not null;
    }
    private static bool Digest(string? value)=>value is not null&&System.Text.RegularExpressions.Regex.IsMatch(value,"^[a-f0-9]{64}$");
    private sealed record UpdaterAnchorDocument(int Format,string InstallationId,string RuntimeService,string AuthorityService,string IntakePolicyDigest,string IntakePinDigest,string? ProbeSha256=null,string? FullSystemPolicyDigest=null);
}

using System.Security.Principal;
namespace Yahad.Install;

// Fixed installed names/root, discovered from the actual current token only.
// No request, environment variable or projected fixture SID selects this branch.
internal static class InstalledProcessState
{
    internal static InstalledStateFiles? ForPath(string path)
    {
        var sid=WindowsIdentity.GetCurrent().User??throw new IOException("INSTALLED_TOKEN");
        if(!sid.Value.StartsWith("S-1-5-80-",StringComparison.Ordinal))return null;
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Yahad.Acceptance");
        var full=Path.GetFullPath(path);if(!full.Equals(root,StringComparison.OrdinalIgnoreCase)&&!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return null;
        var a=(SecurityIdentifier)new NTAccount("NT SERVICE","Yahad.Acceptance.Authority").Translate(typeof(SecurityIdentifier));var r=(SecurityIdentifier)new NTAccount("NT SERVICE","Yahad.Acceptance").Translate(typeof(SecurityIdentifier));
        if(sid!=a&&sid!=r)throw new IOException("INSTALLED_TOKEN_ROLE");var roles=new InstalledFileRoles(a,r);
        using(var anchor=WindowsFileBoundary.OpenInstalledRoleRead(Path.Combine(root,"installation.json"),roles,InstalledFileRole.Anchor))
        {
            var document=NativeJson.Read<System.Text.Json.JsonElement>(anchor.Small(4096));
            if(document.GetProperty("Format").GetInt32()!=1||document.GetProperty("RuntimeService").GetString()!="Yahad.Acceptance"||document.GetProperty("AuthorityService").GetString()!="Yahad.Acceptance.Authority"||!Guid.TryParseExact(document.GetProperty("InstallationId").GetString(),"N",out _))throw new IOException("INSTALLED_TOKEN_ANCHOR");
        }
        return InstalledStateFiles.Installed(root,roles,"","");
    }
}

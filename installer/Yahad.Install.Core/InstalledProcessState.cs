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
        var root=InstalledUpdaterLayout.StateRoot;
        var full=Path.GetFullPath(path);if(!full.Equals(root,StringComparison.OrdinalIgnoreCase)&&!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return null;
        var r=(SecurityIdentifier)new NTAccount("NT SERVICE",InstalledUpdaterLayout.RuntimeServiceName).Translate(typeof(SecurityIdentifier));
        SecurityIdentifier? a=null;
        if(sid!=r){a=(SecurityIdentifier)new NTAccount("NT SERVICE",InstalledUpdaterLayout.AuthorityServiceName).Translate(typeof(SecurityIdentifier));if(sid!=a&&!UpdaterServicePrincipals.IsRecovery(sid))throw new IOException("INSTALLED_TOKEN_ROLE");}
        // Legacy first-install data retains its existing single-service checks.
        // Only genuine absence permits that branch; malformed/present/denied
        // enrollment must never fall back to the Format-2 installation receipt.
        if(!InstalledUpdaterLayout.AnchorPresent()){if(sid==r)return null;throw new IOException("INSTALLED_UPDATER_ANCHOR_REQUIRED");}
        a??=(SecurityIdentifier)new NTAccount("NT SERVICE",InstalledUpdaterLayout.AuthorityServiceName).Translate(typeof(SecurityIdentifier));
        var (roles,_)=InstalledUpdaterLayout.ReadRoles(a,r);
        if(sid!=r&&!roles.IsUpdater(sid))throw new IOException("INSTALLED_TOKEN_ROLE");
        if(UpdaterServicePrincipals.IsRecovery(sid))UpdaterServiceIdentity.Require(UpdaterServiceIdentity.FromUser(sid,a),Environment.ProcessId);
        return InstalledStateFiles.Installed(root,roles,"","");
    }
}

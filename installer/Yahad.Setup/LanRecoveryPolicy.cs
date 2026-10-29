using System.IO;
namespace Yahad.Setup;

// Shared by the production journal reader and pure fault/retry tests. The
// receipt is kept outside the tree it describes, so deleting owner.json cannot
// erase either the owner identity or the already confirmed removal choice.
internal static class LanRecoveryPolicy
{
    internal static void RequirePreparation(LanPreparation intent,InstallerState application,LanSetupRequest? request=null)
    {
        if(intent.Format!=1||intent.InstallationId!=application.InstallationId||intent.OwnerSid!=application.OwnerSid||
            intent.PayloadDigest!=application.PayloadDigest||request is not null&&intent.Request!=request||
            intent.Request.Action!=LanSetupAction.Enable||intent.ServiceSid is not null&&
            (!intent.ServiceSid.StartsWith("S-1-5-80-",StringComparison.Ordinal)||intent.ServiceSid==application.ServiceSid))
            throw new IOException("LAN_PREPARATION_CHANGED");
        intent.Request.Require();
    }
    internal static void RequireRemoval(LanRemovalIntent intent,InstallerState application,UninstallMode? requested=null)
    {
        UninstallChoice.Require(intent.Mode);
        if(requested is not null&&intent.Mode!=requested)throw new IOException("UNINSTALL_MODE_CHANGED");
        if(intent.Format!=1||intent.State.InstallationId!=application.InstallationId||intent.State.OwnerSid!=application.OwnerSid||
            intent.State.AppPort!=application.Port||intent.State.ServiceSid==application.ServiceSid||
            !intent.State.ServiceSid.StartsWith("S-1-5-80-",StringComparison.Ordinal))throw new IOException("LAN_REMOVAL_INTENT_CHANGED");
    }
}

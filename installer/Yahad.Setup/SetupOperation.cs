using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Yahad.Install;
namespace Yahad.Setup;

// app.manifest requires administrator rights: Windows shows one UAC prompt
// before this process starts, and the window and the quiet modes run every
// install or removal here, in this process. The operation runs on the thread
// pool so the WPF dispatcher stays responsive and is never a continuation target.
internal static class SetupOperation
{
    // The verified full profile publishes thousands of protected files. Keep
    // one absolute bound; progress messages never renew the deadline.
    internal static TimeSpan OperationTimeout(string operation,bool fullProfile)=>operation switch
    {
        "install"=>TimeSpan.FromMinutes(fullProfile?48:12),
        "uninstall"=>TimeSpan.FromMinutes(fullProfile?96:12),
        _=>throw new IOException("SETUP_COMMAND")
    };
    internal static Task<SetupResult> RunAsync(bool uninstall,Action<SetupProgress> progress,CancellationToken cancellation,UninstallMode mode=UninstallMode.KeepData,LanSetupRequest? lan=null,UpdateSetupChoice? update=null)
    {
        UninstallChoice.Require(mode);if(!uninstall&&mode!=UninstallMode.KeepData)throw new IOException("UNINSTALL_MODE_INVALID");
        lan?.Require();if(uninstall&&lan is not null)throw new IOException("LAN_ACTION_INVALID");
        if(uninstall&&update is not null)throw new IOException("UPDATE_CONSENT_INVALID");update?.Require(DateTimeOffset.UtcNow);
        return Task.Run(()=>ExecuteAsync(uninstall,progress,cancellation,mode,lan,update),CancellationToken.None);
    }
    private static async Task<SetupResult> ExecuteAsync(bool uninstall,Action<SetupProgress> progress,CancellationToken cancellation,UninstallMode mode,LanSetupRequest? lan,UpdateSetupChoice? update)
    {
        try
        {
            NativeInstallFiles.RequireAdministrator();
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(OperationTimeout(uninstall?"uninstall":"install",InstallPackage.EmbeddedFullProfile()));
            void Progress(SetupProgress p){deadline.Token.ThrowIfCancellationRequested();progress(p);}
            Progress(new("checking",0));
            using var installLock=new SetupInstallLock();using var payload=InstallPackage.Embedded();var host=SetupHostFactory.Create(payload,uninstall,update);using var cleanup=(IDisposable)host;
            try{return await new InstallFlow(host).RunAsync(SetupOwner.Current(),uninstall,Progress,deadline.Token,mode,lan);}
            catch(Exception failure)when(host is NativeFullSystemSetupHost full){throw full.ReportFailure(failure);}
        }
        catch(Exception failure){return FailureResult(failure,mode);}
    }
    internal static SetupResult FailureResult(Exception failure,UninstallMode mode)=>new(false,Code(failure),DataPreserved:mode==UninstallMode.KeepData,Failure:SetupFailureDetail.From(failure));
    internal static string Code(Exception error)=>SetupFailureDetail.Cause(error) switch
    {
        Win32Exception w when w.NativeErrorCode==112=>"DISK_SPACE",
        Win32Exception w when w.NativeErrorCode==5=>"ACCESS_DENIED",
        Win32Exception w when w.NativeErrorCode==32=>"FILE_LOCKED",
        UnauthorizedAccessException=>"ACCESS_DENIED",
        OperationCanceledException=>"INSTALL_TIMEOUT",
        IOException io when KnownCodes.Contains(io.Message)||LanCodes.Contains(io.Message)||UpdateCodes.Contains(io.Message)||io.Message is "UNINSTALL_DIAGNOSTIC_CHANGED"=>io.Message,
        _=>"INSTALL_FAILED"
    };
    private static readonly HashSet<string> UpdateCodes=new(StringComparer.Ordinal){"INSTALL_STAGING_CHANGED","UPDATE_SERVICE_START_FAILED","UPDATE_SERVICE_QUIESCENCE_UNCONFIRMED","UPDATE_SERVICE_GENERATION_CHANGED","UPDATE_PREPARING_CANCEL_REFUSED","UPDATE_PREPARING_INVENTORY_CHANGED","UPDATE_PREPARING_SERVICE_CHANGED","UPDATE_PREPARING_BINARIES_PUBLISHED","UPDATE_PREPARING_ENROLLED","UPDATE_PREPARING_FILE_CHANGED","UPDATE_PREPARING_STATE_CHANGED","UPDATE_PREPARING_CANCEL_REQUIRES_DELETE","UPDATE_CONSENT_REQUIRED","UPDATE_CONSENT_ALREADY_CAPTURED","UPDATE_CONSENT_INVALID","UPDATE_FULL_PACKAGE_REQUIRED","UPDATE_LEGACY_PROFILE_REQUIRES_REMOVAL","UPDATE_MUST_FINISH_BEFORE_INSTALLER","UPDATE_INSTALL_INCOMPLETE_REPAIR_FIRST","UPDATE_RESTORE_COLLISION","INSTALL_RUNTIME_NOT_AUTHENTICATED","INSTALL_READINESS_BINDING","UPDATE_CURRENT_BINDING","UPDATE_ENROLLMENT_CHANGED","UPDATE_OWNER_BINDING","UPDATE_SERVICE_IDENTITY","UPDATE_SERVICE_NOT_OWNED","UPDATE_RECOVERY_REQUIRED","UPDATE_INSTALLER_PLAN_CHANGED","UPDATE_INSTALLER_PLAN_MISSING","UPDATE_COMPONENT_PACKAGE_BINDING","UNINSTALL_SERVICE_REAPPEARED"};
    private static readonly HashSet<string> LanCodes=new(StringComparer.Ordinal){"LAN_ACTION_INVALID","LAN_CONSENT_REQUIRED","LAN_NOT_SUPPORTED","LAN_PAYLOAD_NOT_SUPPORTED","LAN_PAYLOAD_CHANGED","LAN_SETUP_PAUSED","NETWORK_SELECTION_CHANGED","LAN_PRIVATE_PROFILE_REQUIRED","LAN_NETWORK_PROFILE_UNAVAILABLE","LAN_FIREWALL_UNAVAILABLE","LAN_FIREWALL_CHANGED","LAN_FIREWALL_DUPLICATE","LAN_FIREWALL_REPAIR_REQUIRED","LAN_FIREWALL_NOT_OBSERVED","LAN_PREPARATION_CHANGED","LAN_OWNERSHIP_MISSING","LAN_OWNERSHIP_CHANGED","LAN_INSTALL_COLLISION","LAN_UNINSTALL_INCOMPLETE","LAN_REMOVAL_INTENT_CHANGED","LAN_APP_CONFIGURATION_CHANGED","LAN_JOURNAL_CHANGED","LAN_BINARY_CHANGED","LAN_CA_CHANGED","LAN_CA_STORAGE_DAMAGED","LAN_CERTIFICATE_INVALID","LAN_HTTPS_CONNECTION_FAILED","LAN_QUIESCENCE_UNCONFIRMED"};
    private static readonly HashSet<string> KnownCodes=new(StringComparer.Ordinal){"PACKAGE_NOT_EMBEDDED","PACKAGE_HASH","PACKAGE_MEMBER_HASH","PAYLOAD_VERSION","PAYLOAD_SCHEMA","INSTALL_COLLISION","EXISTING_DATA_PRESERVED","INSTALL_REPAIR_REQUIRED","INSTALL_OWNER_CHANGED","INSTALL_SERVICE_IDENTITY","INSTALL_TARGET_CHANGED","INSTALL_BOOTSTRAP_CHANGED","DISK_SPACE","NO_LOCAL_PORT","SERVICE_START_FAILED","SERVER_NOT_READY","SERVICE_NOT_OWNED","UNINSTALL_UNOWNED_FILE_PRESERVED","INSTALL_STATE_INVALID","ADMIN_REQUIRED","INSTALL_BUSY","UNINSTALL_MODE_INVALID","UNINSTALL_MODE_CHANGED","UNINSTALL_INCOMPLETE","UNINSTALL_INTENT_INVALID","UNINSTALL_QUIESCENCE_UNCONFIRMED","UNINSTALL_SERVICE_REMOVAL_PENDING","UNINSTALL_LINK_PRESERVED","UNINSTALL_ROOT_NOT_EMPTY","UNINSTALL_TREE_LIMIT"};
    internal static string HashFile(string path){using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);return Convert.ToHexStringLower(SHA256.HashData(file));}
    // Removal deletes the installed trees, and a process cannot delete its own
    // running image. Started from inside them (the Apps & Features entry), the
    // removal window reopens from a verified private copy and this process exits.
    internal static bool RunsFromInstallation()=>Environment.ProcessPath is {} image&&(Within(image,InstallLocations.Binaries)||Within(image,InstallLocations.State));
    internal static void OpenRemoval()
    {
        var image=Environment.ProcessPath??throw new IOException("SETUP_IMAGE");
        if(RunsFromInstallation())
        {
            var copy=Path.Combine(SafeDirectory.CreatePrivateSession(Path.GetTempPath()),"Yahad.Setup.exe");File.Copy(image,copy,false);
            if(HashFile(copy)!=HashFile(image))throw new IOException("SETUP_COPY_CHANGED");image=copy;
        }
        // This process is already elevated; the child inherits its token, so no second prompt.
        using var started=Process.Start(new ProcessStartInfo(image){UseShellExecute=false,ArgumentList={"--uninstall"}})??throw new IOException("SETUP_NOT_STARTED");
    }
    private static bool Within(string path,string root)
    {var full=Path.GetFullPath(path);var parent=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))+Path.DirectorySeparatorChar;return full.StartsWith(parent,StringComparison.OrdinalIgnoreCase);}
}

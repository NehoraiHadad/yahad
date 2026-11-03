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
    // Extraction publishes thousands of protected files. Keep one absolute
    // bound; progress messages never renew the deadline.
    internal static TimeSpan OperationTimeout(string operation)=>operation switch
    {
        "install"=>TimeSpan.FromMinutes(48),
        "uninstall"=>TimeSpan.FromMinutes(96),
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
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(OperationTimeout(uninstall?"uninstall":"install"));
            void Progress(SetupProgress p){deadline.Token.ThrowIfCancellationRequested();progress(p);}
            Progress(new("checking",0));
            using var installLock=new SetupInstallLock();using var payload=InstallPackage.Embedded();var host=SetupHostFactory.Create(payload,uninstall,update);using var cleanup=(IDisposable)host;
            try{return await new InstallFlow(host).RunAsync(SetupOwner.Current(),uninstall,Progress,deadline.Token,mode,lan);}
            catch(Exception failure)when(host is NativeSetupHost native){throw native.ReportFailure(failure);}
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
        IOException io when KnownCodes.Contains(io.Message)||LanCodes.Contains(io.Message)||UpdateCodes.Contains(io.Message)||UpgradeCodes.Contains(io.Message)||io.Message is "UNINSTALL_DIAGNOSTIC_CHANGED"=>io.Message,
        _=>"INSTALL_FAILED"
    };
    private static readonly HashSet<string> UpgradeCodes=new(StringComparer.Ordinal){"RELEASE_SIGNATURE_INVALID","RELEASE_KEY_UNTRUSTED","RELEASE_NOT_NEWER","RELEASE_SETUP_MISMATCH","RELEASE_MANIFEST_INVALID","RELEASE_UNSUPPORTED_FROM","UPGRADE_LOCKED","UPGRADE_INCOMPLETE","UPGRADE_BACKUP_INVALID","NOT_INSTALLED","INSTALL_INCOMPLETE","REPAIR_RUNNING_FROM_TARGET"};
    private static readonly HashSet<string> UpdateCodes=new(StringComparer.Ordinal){"UPDATE_CONSENT_REQUIRED","UPDATE_CONSENT_ALREADY_CAPTURED","UPDATE_CONSENT_INVALID","UPDATE_OWNER_BINDING","UNINSTALL_SERVICE_REAPPEARED"};
    private static readonly HashSet<string> LanCodes=new(StringComparer.Ordinal){"LAN_ACTION_INVALID","LAN_CONSENT_REQUIRED","LAN_NOT_SUPPORTED","LAN_PAYLOAD_NOT_SUPPORTED","LAN_PAYLOAD_CHANGED","LAN_SETUP_PAUSED","NETWORK_SELECTION_CHANGED","LAN_PRIVATE_PROFILE_REQUIRED","LAN_NETWORK_PROFILE_UNAVAILABLE","LAN_FIREWALL_UNAVAILABLE","LAN_FIREWALL_CHANGED","LAN_FIREWALL_DUPLICATE","LAN_FIREWALL_REPAIR_REQUIRED","LAN_FIREWALL_NOT_OBSERVED","LAN_PREPARATION_CHANGED","LAN_OWNERSHIP_MISSING","LAN_OWNERSHIP_CHANGED","LAN_INSTALL_COLLISION","LAN_UNINSTALL_INCOMPLETE","LAN_REMOVAL_INTENT_CHANGED","LAN_APP_CONFIGURATION_CHANGED","LAN_JOURNAL_CHANGED","LAN_BINARY_CHANGED","LAN_CA_CHANGED","LAN_CA_STORAGE_DAMAGED","LAN_CERTIFICATE_INVALID","LAN_HTTPS_CONNECTION_FAILED","LAN_QUIESCENCE_UNCONFIRMED"};
    private static readonly HashSet<string> KnownCodes=new(StringComparer.Ordinal){"PACKAGE_NOT_EMBEDDED","PACKAGE_HASH","PACKAGE_MEMBER_HASH","PACKAGE_FORMAT","PACKAGE_SOURCE","PACKAGE_PAYLOAD_ANCHOR","PAYLOAD_VERSION","INSTALL_COLLISION","EXISTING_DATA_PRESERVED","LEGACY_INSTALL_UNINSTALL_FIRST","INSTALL_UPGRADE_REQUIRED","INSTALL_DOWNGRADE_REFUSED","INSTALL_NOT_PREPARED","INSTALL_NOT_COMMITTED","INSTALL_KEY_INVALID","INSTALL_OWNER_CHANGED","INSTALL_SERVICE_IDENTITY","INSTALL_SERVICE_STOP_STALLED","INSTALL_SERVICE_GENERATION_CHANGED","INSTALL_TARGET_CHANGED","DISK_SPACE","NO_LOCAL_PORT","SERVICE_START_FAILED","SERVER_NOT_READY","SERVICE_NOT_OWNED","UNINSTALL_UNOWNED_FILE_PRESERVED","INSTALL_STATE_INVALID","ADMIN_REQUIRED","INSTALL_BUSY","UNINSTALL_MODE_INVALID","UNINSTALL_MODE_CHANGED","UNINSTALL_INCOMPLETE","UNINSTALL_INTENT_INVALID","UNINSTALL_QUIESCENCE_UNCONFIRMED","UNINSTALL_SERVICE_REMOVAL_PENDING","UNINSTALL_REGISTRATION_CHANGED","UNINSTALL_LINK_PRESERVED","UNINSTALL_ROOT_NOT_EMPTY","UNINSTALL_TREE_LIMIT"};
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
    internal static bool Within(string path,string root)
    {var full=Path.GetFullPath(path);var parent=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))+Path.DirectorySeparatorChar;return full.StartsWith(parent,StringComparison.OrdinalIgnoreCase);}
}

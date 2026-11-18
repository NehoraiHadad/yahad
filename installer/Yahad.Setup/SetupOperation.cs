using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
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
    // What the running application says about first-family setup; null when it does not answer.
    internal static async Task<string?> SetupStateAsync(string url)
    {
        try
        {
            using var client=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(5)};
            using var response=await client.GetAsync(url+"api/setup/status");response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("state").GetString();
        }
        catch(Exception error) when(error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException){return null;}
    }
    internal static Task<SetupResult> RunAsync(bool uninstall,Action<SetupProgress> progress,CancellationToken cancellation,UninstallMode mode=UninstallMode.KeepData,LanSetupRequest? lan=null,UpdateSetupChoice? update=null,bool homeName=false)
        =>Task.Run(()=>ExecuteAsync(uninstall,progress,cancellation,mode,lan,update,homeName),CancellationToken.None);
    private static async Task<SetupResult> ExecuteAsync(bool uninstall,Action<SetupProgress> progress,CancellationToken cancellation,UninstallMode mode,LanSetupRequest? lan,UpdateSetupChoice? update,bool homeName)
    {
        try
        {
            NativeInstallFiles.RequireAdministrator();
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(OperationTimeout(uninstall?"uninstall":"install"));
            void Progress(SetupProgress p){deadline.Token.ThrowIfCancellationRequested();progress(p);}
            Progress(new("checking",0));
            // Only an install opens the package; removal is payload-free (4.4) and runs from
            // the installed engine or a private copy of it.
            using var installLock=new SetupInstallLock();using var payload=uninstall?null:InstallPackage.Embedded();
            // Moved first, so the rest is this release's own run: home network and first-family link included.
            if(payload is not null&&UpgradeHost.Applies(payload))
            {
                var upgrade=await UpgradeHost.FromThisSetupAsync(payload,Progress);
                if(!upgrade.Result.Ok)return upgrade.Result;
            }
            using var host=new NativeSetupHost(payload,uninstall?null:update);
            try{return await new InstallFlow(host).RunAsync(SetupOwner.Current(),uninstall,Progress,deadline.Token,mode,lan,homeName);}
            catch(Exception failure){throw host.ReportFailure(failure);}
        }
        catch(Exception failure){return FailureResult(failure,mode);}
    }
    internal static SetupResult FailureResult(Exception failure,UninstallMode mode)=>new(false,Code(failure),DataPreserved:mode==UninstallMode.KeepData,Failure:SetupFailureDetail.From(failure));
    // The code a failure reaches the window, the result file and the log as. A code the
    // engine wrote (SetupErrorCatalog.IsCode) survives as itself, whatever exception
    // carries it; Windows' own errors are named by number; INSTALL_FAILED is left for
    // what nothing named, and the detail then carries the exception's type. A control
    // result is never taken from an exception: no throw can impersonate the hand-off.
    internal static string Code(Exception error)=>SetupFailureDetail.Cause(error) switch
    {
        var cause when SetupErrorCatalog.IsCode(cause.Message)&&cause.Message!=RepairHost.HandOff=>cause.Message,
        Win32Exception w=>Win32Code(w.NativeErrorCode)??"INSTALL_FAILED",
        UnauthorizedAccessException=>"ACCESS_DENIED",
        OperationCanceledException=>"INSTALL_TIMEOUT",
        // System.IO reports a Win32 failure as IOException with HRESULT_FROM_WIN32(error).
        IOException io when((uint)io.HResult>>16)==0x8007=>Win32Code(io.HResult&0xFFFF)??"INSTALL_FAILED",
        _=>"INSTALL_FAILED"
    };
    private static string? Win32Code(int error)=>error switch
    {
        5=>"ACCESS_DENIED",
        32 or 33=>"FILE_LOCKED", // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
        39 or 112=>"DISK_SPACE", // ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL
        _=>null
    };
    internal static string HashFile(string path){using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);return Convert.ToHexStringLower(SHA256.HashData(file));}
    // The app version the window names: the one this image installs or, for the
    // payload-free engine, the active app the installer record (or, mid-removal, its
    // receipt) names. Without a readable record there is no version to name; the
    // operation itself reports why.
    internal static string? DisplayVersion()
    {
        try
        {
            // A damaged image still opens the window, whose install then names the damage.
            if(InstallPackage.IsEmbedded)return InstallPackage.DisplayVersion();
            if(File.Exists(InstallLocations.RemovalStateFile))return RemovalIntent.Read(InstallLocations.RemovalStateFile).State.Active.App.Version;
            return File.Exists(InstallLocations.StateFile)?NativeSetupHost.ReadState().Active.App.Version:null;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException){return null;}
    }
    // The removal window opens as its own process, which applies the private-copy rule
    // (PrivateCopy) when it runs from the installation.
    internal static void OpenRemoval()=>Start(Environment.ProcessPath??throw new IOException("SETUP_IMAGE"),["--uninstall"]);
    // This process is already elevated; the child inherits its token, so no second prompt.
    private static void Start(string image,string[] arguments)
    {
        var start=new ProcessStartInfo(image){UseShellExecute=false};foreach(var argument in arguments)start.ArgumentList.Add(argument);
        using var started=Process.Start(start)??throw new IOException("SETUP_NOT_STARTED");
    }
    internal static bool Within(string path,string root)
    {var full=Path.GetFullPath(path);var parent=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))+Path.DirectorySeparatorChar;return full.StartsWith(parent,StringComparison.OrdinalIgnoreCase);}
}

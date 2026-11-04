using System.IO;
using System.Security.Principal;
namespace Yahad.Setup;

// The account running this elevated Setup and the hash of its image. Under
// Admin Approval Mode the elevated token carries the signed-in user's own SID.
// With over-the-shoulder credentials it is the administrator who approved the
// prompt, which is also the account whose browser the window opens.
internal sealed record SetupOwner(string Sid,string ImageSha256)
{
    internal static SetupOwner Current()
    {
        using var identity=WindowsIdentity.GetCurrent();var sid=identity.User?.Value??throw new IOException("INSTALL_OWNER_IDENTITY");
        return new(sid,SetupOperation.HashFile(Environment.ProcessPath??throw new IOException("SETUP_IMAGE")));
    }
}
internal sealed record SetupProgress(string Phase,int Percent);
internal sealed record SetupResult(bool Ok,string Code,string? Url=null,string? SetupToken=null,bool DataPreserved=true,LanSetupResult? Lan=null,SetupFailureDetail? Failure=null);
// Four concrete operations are injected only by the executable composition or
// tests. No JSON/CLI caller can supply paths, accounts, grants or operations.
internal interface ISetupHostOperations
{
    Task PrepareAsync(SetupOwner owner,Action<SetupProgress> progress,CancellationToken cancellation);
    Task PublishServiceAsync(Action<SetupProgress> progress,CancellationToken cancellation);
    Task ConfigureLanAsync(LanSetupRequest request,Action<SetupProgress> progress,CancellationToken cancellation);
    Task<SetupResult> StartAndObserveAsync(Action<SetupProgress> progress,CancellationToken cancellation);
    Task<SetupResult> UninstallAsync(Action<SetupProgress> progress,CancellationToken cancellation,UninstallMode mode=UninstallMode.KeepData);
}
internal sealed class InstallFlow(ISetupHostOperations host)
{
    internal async Task<SetupResult> RunAsync(SetupOwner owner,bool uninstall,Action<SetupProgress> progress,CancellationToken cancellation,UninstallMode mode=UninstallMode.KeepData,LanSetupRequest? lan=null)
    {
        UninstallChoice.Require(mode);if(!uninstall&&mode!=UninstallMode.KeepData)throw new IOException("UNINSTALL_MODE_INVALID");
        lan?.Require();if(uninstall&&lan is not null)throw new IOException("LAN_ACTION_INVALID");
        if(!owner.Sid.StartsWith("S-1-5-21-",StringComparison.Ordinal))throw new IOException("INSTALL_OWNER_IDENTITY");
        if(uninstall)return await host.UninstallAsync(progress,cancellation,mode);
        progress(new("checking",0));await host.PrepareAsync(owner,progress,cancellation);
        cancellation.ThrowIfCancellationRequested();progress(new("installing_service",70));await host.PublishServiceAsync(progress,cancellation);
        if(lan is not null)
        {progress(new("configuring_lan",80));await host.ConfigureLanAsync(lan,progress,cancellation);}
        progress(new("starting_server",85));var result=await host.StartAndObserveAsync(progress,cancellation);
        if(!result.Ok)return result;
        progress(new("ready",100));return result;
    }
}

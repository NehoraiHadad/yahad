using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Yahad.Install;
namespace Yahad.Setup;

// Cancellation only before enrollment or native binary publication. Source-
// verified payload copies are disposable; datasets must still be empty. This
// is not data migration or recovery of an enrolled profile with missing proof.
internal sealed record PreparingCancellationIntent(int Format,byte[] StateBytes,string StagingDirectory,bool RuntimeCreated,bool AuthorityCreated)
{
    [JsonIgnore] internal FullInstallState State=>NativeJson.Read<FullInstallState>(StateBytes);
    internal void Require(string owner)
    {
        if(Format!=5||StateBytes is null||StateBytes.Length is <2 or >16777216)throw new IOException("UPDATE_PREPARING_CANCEL_REFUSED");
        var state=State;NativeFullSystemSetupHost.ValidateState(state);
        if(state.OwnerSid!=owner)throw new UnauthorizedAccessException("INSTALL_OWNER_CHANGED");
        var provisioned=state.RuntimeSid is not null;
        if(state.Phase!="preparing"||(provisioned?state.AuthoritySid is null||!state.ServicesCompleted||!RuntimeCreated||!AuthorityCreated:state.AuthoritySid is not null||state.ServicesCompleted)||
            state.PolicyDigest is not null||state.PlanDigest is not null||state.PreservedBinaries is not null||
            StagingDirectory is null||!StagingDirectory.StartsWith("setup-stage-",StringComparison.Ordinal)||!Guid.TryParseExact(StagingDirectory[12..],"N",out var id)||"setup-stage-"+id.ToString("N")!=StagingDirectory)
            throw new IOException("UPDATE_PREPARING_CANCEL_REFUSED");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
        foreach(var file in state.Files)if(!names.Add(file.Path)||file.Size>536870912||(total=checked(total+file.Size))>2147483648)throw new IOException("UPDATE_PREPARING_CANCEL_REFUSED");
    }
    internal static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
internal interface IPreparingCancellationHost
{
    PreparingCancellationIntent? ReadIntent();
    PreparingCancellationIntent Prepare();
    Task VerifyAsync(PreparingCancellationIntent intent,bool complete,bool servicesRemoved,CancellationToken cancellation);
    void Publish(PreparingCancellationIntent intent);
    Task RemoveServicesAsync(PreparingCancellationIntent intent,CancellationToken cancellation);
    void RemoveFiles(PreparingCancellationIntent intent,CancellationToken cancellation);
    Task CompleteAsync(PreparingCancellationIntent intent,CancellationToken cancellation);
}
internal static class PreparingInstallCancellation
{
    internal static async Task<SetupResult> RunAsync(string owner,UninstallMode mode,IPreparingCancellationHost host,
        Action<SetupProgress> progress,CancellationToken cancellation,Action<FullInstallState>? observed=null,Action? completed=null)
    {
        if(mode!=UninstallMode.DeleteData)throw new IOException("UPDATE_PREPARING_CANCEL_REQUIRES_DELETE");
        cancellation.ThrowIfCancellationRequested();var existing=host.ReadIntent();var intent=existing??host.Prepare();intent.Require(owner);
        observed?.Invoke(intent.State);
        await host.VerifyAsync(intent,existing is null,false,cancellation);
        if(existing is null)host.Publish(intent); // Durable protected plan precedes every deletion.
        await host.VerifyAsync(intent,false,false,cancellation);progress(new("removing_service",30));
        await host.RemoveServicesAsync(intent,cancellation);await host.VerifyAsync(intent,false,true,cancellation);
        progress(new("deleting_data",60));host.RemoveFiles(intent,cancellation);await host.CompleteAsync(intent,cancellation);
        completed?.Invoke(); // Disarm diagnostics before the final progress notification.
        progress(new("removed",100));return new(true,"REMOVED_ALL","http://127.0.0.1:"+intent.State.Port+"/",DataPreserved:false,FullProfileInstallationId:intent.State.InstallationId);
    }
}

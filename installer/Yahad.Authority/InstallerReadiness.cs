using System.Diagnostics;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record InstallerReadinessRequest(int Format,string InstallationId,string Nonce,string ActivationDigest);
internal sealed record InstallerReadinessObservation(int Format,string InstallationId,string Nonce,string ActivationDigest,string ReleaseDigest,int AuthorityPid,long AuthorityCreated,bool RuntimeHealthy,int RuntimePid,long RuntimeCreated,string? RuntimeNonce);
// A fresh installer challenge is immutable to both service roles. Only the
// authenticated Authority can publish its native Runtime admission result.
// HTTP is checked afterwards for usability, never treated as identity evidence.
internal static class InstallerReadiness
{
    internal const string RequestFile="installer-readiness.json";
    internal static void Require(InstallerReadinessRequest request,string installation)
    {
        InstalledActivationResolver.Id(request.Nonce);InstalledActivationResolver.Digest(request.ActivationDigest);
        if(request.Format!=1||request.InstallationId!=installation)throw new IOException("INSTALL_READINESS_BINDING");
    }
    internal static void Publish(InstalledStateFiles files,InstalledAuthorityAnchor anchor,WindowsScmRuntimeLifecycle runtime)
    {
        var path=files.PathOf(RequestFile);if(!File.Exists(path))return;
        byte[] bytes;
        if(files.Roles is null)bytes=files.Read(path,2048);
        else{using var input=WindowsFileBoundary.OpenInstalledRoleRead(path,files.Roles,InstalledFileRole.Anchor);bytes=input.Small(2048);}
        var request=NativeJson.Read<InstallerReadinessRequest>(bytes);Require(request,anchor.InstallationId);
        var output=files.PathOf(".authority/install-ready-"+request.Nonce+".json");
        // Completed challenges remain evidence for the installer, not commands
        // to replay on normal reboot. Only Admin can replace the Anchor nonce.
        if(File.Exists(output)){using var completed=files.Open(output);return;}
        var activationBytes=files.Read(files.PathOf("activation.json"),16384);var activation=NativeJson.Read<InstalledActivation>(activationBytes);
        if(activation.InstallationId!=anchor.InstallationId||activation.Phase!="committed")throw new IOException("INSTALL_READINESS_BINDING");
        if(InstalledActivationResolver.Hash(activationBytes)!=request.ActivationDigest)return; // Stale after a later update, never a new grant.
        var admission=runtime.ObserveInstallerReadiness(new(anchor.InstallationId,request.ActivationDigest),new(activation.ReleaseDigest));
        using var process=Process.GetCurrentProcess();
        var observation=new InstallerReadinessObservation(1,anchor.InstallationId,request.Nonce,request.ActivationDigest,activation.ReleaseDigest,process.Id,process.StartTime.ToFileTimeUtc(),admission is not null,admission?.ProcessId??0,admission?.Created??0,admission?.Nonce);
        var encoded=JsonSerializer.SerializeToUtf8Bytes(observation);
        if(File.Exists(output)){if(!files.Read(output,2048).AsSpan().SequenceEqual(encoded))throw new IOException("INSTALL_READINESS_REPLAY");return;}
        using var file=files.Create(output);file.Write(encoded);file.Flush(true);
    }
    internal static void RequireObserved(InstallerReadinessRequest request,InstallerReadinessObservation observed,string release,int pid,long created,int runtimePid,long runtimeCreated)
    {
        Require(request,observed.InstallationId);
        if(observed.Format!=1||!observed.RuntimeHealthy||pid<=0||created<=0||runtimePid<=0||runtimeCreated<=0||observed.RuntimeNonce is null||observed.Nonce!=request.Nonce||observed.ActivationDigest!=request.ActivationDigest||observed.ReleaseDigest!=release||observed.AuthorityPid!=pid||observed.AuthorityCreated!=created||observed.RuntimePid!=runtimePid||observed.RuntimeCreated!=runtimeCreated)throw new IOException("INSTALL_RUNTIME_NOT_AUTHENTICATED");
        InstalledActivationResolver.Id(observed.RuntimeNonce);
    }
}

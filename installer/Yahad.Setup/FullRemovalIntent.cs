using System.IO;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Setup;

// The protected external receipt outlives BOTH removable roots. No retry needs
// a deleted activation, payload or bootstrap selection to rediscover its scope.
internal sealed record FullRemovalIntent(int Format,FullInstallState State,UninstallMode Mode,InstallMember[] Binaries,InstallerState LanApplication,string Stage,bool WasPreserved=false)
{
    internal static readonly string[] Stages=["prepared","lan_removed","services_removed","data_removed","binaries_removed"];
    internal void Require()
    {
        NativeFullSystemSetupHost.ValidateState(State);UninstallChoice.Require(Mode);
        if(Format!=4||State.Phase!=UninstallChoice.Phase(Mode)||!Stages.Contains(Stage)||Binaries.Length is <10 or >50000||State.RuntimeSid is null||State.AuthoritySid is null)throw new IOException("UNINSTALL_INTENT_INVALID");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
        foreach(var member in Binaries)
        {FullInstallFiles.BinaryMember(member.Path);InstalledActivationResolver.Digest(member.Sha256);if(!names.Add(member.Path)||member.Size<0||member.Size>FullInstallFiles.MaximumBinaryBytes(member.Path)||(total+=member.Size)>8589934592)throw new IOException("UNINSTALL_INTENT_INVALID");}
        if(!Binaries.Any(f=>f.Path=="Yahad.Setup.exe"&&f.Sha256==State.SetupSha256)||LanApplication.InstallationId!=State.InstallationId||LanApplication.OwnerSid!=State.OwnerSid||LanApplication.ServiceSid!=State.RuntimeSid||LanApplication.Port!=State.Port)throw new IOException("UNINSTALL_INTENT_INVALID");
        if(WasPreserved&&State.PreservedBinaries?.SequenceEqual(Binaries)!=true)throw new IOException("UNINSTALL_INTENT_INVALID");
    }
    internal bool AtLeast(string stage)=>Array.IndexOf(Stages,Stage)>=Array.IndexOf(Stages,stage);
    internal void RequireSameInstallation(FullInstallState other)
    {
        if(other.InstallationId!=State.InstallationId||other.OwnerSid!=State.OwnerSid||other.RuntimeSid!=State.RuntimeSid||other.AuthoritySid!=State.AuthoritySid||other.PackageDigest!=State.PackageDigest||other.PolicyDigest!=State.PolicyDigest||other.SetupSha256!=State.SetupSha256)throw new IOException("UNINSTALL_INTENT_INVALID");
    }
}

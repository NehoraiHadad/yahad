using System.IO;
using Yahad.Install;
namespace Yahad.Setup;

// The protected external receipt outlives both removable roots, so a retry
// never needs a deleted record to rediscover what it is removing.
internal sealed record RemovalIntent(int Format,InstallerState State,UninstallMode Mode,InstallMember[] Binaries,string Stage)
{
    internal static readonly string[] Stages=["prepared","lan_removed","services_removed","data_removed","binaries_removed"];
    internal void Require()
    {
        State.Require();UninstallChoice.Require(Mode);
        if(Format!=InstallerState.CurrentFormat||State.Phase!=UninstallChoice.Phase(Mode)||!Stages.Contains(Stage)||Binaries.Length is <2 or >100003)throw new IOException("UNINSTALL_INTENT_INVALID");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var member in Binaries){BinaryMember(State,member.Path);InstallLocations.RequireDigest(member.Sha256);if(member.Size<0||!names.Add(member.Path))throw new IOException("UNINSTALL_INTENT_INVALID");}
        if(!Binaries.Any(f=>f.Path==SetupMember(State.Version)&&f.Sha256==State.SetupSha256))throw new IOException("UNINSTALL_INTENT_INVALID");
    }
    internal const string SetupImage="Yahad.Setup.exe";
    internal static string SetupMember(string version){InstallLocations.RequireVersion(version);return "versions/"+version+"/"+SetupImage;}
    // The binary root holds exactly the installed release key and one or two
    // version directories: their package members and each release's own Setup.
    internal static string BinaryPath(string version,string packageMember){InstallLocations.RequireVersion(version);InstallPackage.Name(packageMember);return "versions/"+version+"/"+packageMember;}
    // An interrupted first install may not have copied Setup yet; its absent
    // copy is recorded as size 0 and may then never appear during removal.
    internal static InstallMember[] Inventory(InstallerState state,long setupSize)
    {
        var members=state.Files.Select(f=>f with{Path=BinaryPath(state.Version,f.Path)}).Append(new(SetupMember(state.Version),setupSize,state.SetupSha256));
        if(state.ReleaseKeySha256 is {} key)members=members.Append(new(TrustedReleaseKey.InstalledName,TrustedReleaseKey.SpkiBytes,key));
        if(state.Previous is {} previous)members=members.Concat(previous.Files.Select(f=>f with{Path=BinaryPath(previous.Version,f.Path)})).Append(new(SetupMember(previous.Version),previous.SetupBytes,previous.SetupSha256));
        return members.ToArray();
    }
    private static void BinaryMember(InstallerState state,string path)
    {
        if(path==TrustedReleaseKey.InstalledName&&state.ReleaseKeySha256 is not null)return;
        foreach(var version in new[]{state.Version,state.Previous?.Version})
        {
            if(version is null)continue;
            if(path==SetupMember(version))return;
            var prefix="versions/"+version+"/";if(!path.StartsWith(prefix,StringComparison.Ordinal))continue;
            InstallPackage.Name(path[prefix.Length..]);return;
        }
        throw new IOException("UNINSTALL_INTENT_INVALID");
    }
    internal bool AtLeast(string stage)=>Array.IndexOf(Stages,stage) is var wanted and >=0?Array.IndexOf(Stages,Stage)>=wanted:throw new IOException("UNINSTALL_INTENT_INVALID");
    internal void RequireSameInstallation(InstallerState other)
    {if(!UninstallChoice.SameInstallation(State,other))throw new IOException("UNINSTALL_INTENT_INVALID");}
}

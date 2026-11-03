using System.IO;
namespace Yahad.Setup;

// The protected external receipt outlives both removable roots, so a retry
// never needs a deleted record to rediscover what it is removing.
internal sealed record RemovalIntent(int Format,InstallerState State,UninstallMode Mode,InstallMember[] Binaries,string Stage)
{
    internal static readonly string[] Stages=["prepared","lan_removed","services_removed","data_removed","binaries_removed"];
    internal void Require()
    {
        State.Require();UninstallChoice.Require(Mode);
        if(Format!=InstallerState.CurrentFormat||State.Phase!=UninstallChoice.Phase(Mode)||!Stages.Contains(Stage)||Binaries.Length is <2 or >50001)throw new IOException("UNINSTALL_INTENT_INVALID");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var member in Binaries){BinaryMember(State.Version,member.Path);InstallLocations.RequireDigest(member.Sha256);if(member.Size<0||!names.Add(member.Path))throw new IOException("UNINSTALL_INTENT_INVALID");}
        if(!Binaries.Any(f=>f.Path==SetupMember&&f.Sha256==State.SetupSha256))throw new IOException("UNINSTALL_INTENT_INVALID");
    }
    internal const string SetupMember="Yahad.Setup.exe";
    // The binary root holds exactly Setup's own copy and one version directory
    // whose members are the package members of that version.
    internal static string BinaryPath(string version,string packageMember){InstallLocations.RequireVersion(version);InstallPackage.Name(packageMember);return "versions/"+version+"/"+packageMember;}
    // An interrupted first install may not have copied Setup yet; its absent
    // copy is recorded as size 0 and may then never appear during removal.
    internal static InstallMember[] Inventory(InstallerState state,long setupSize)=>
        state.Files.Select(f=>f with{Path=BinaryPath(state.Version,f.Path)}).Append(new(SetupMember,setupSize,state.SetupSha256)).ToArray();
    private static void BinaryMember(string version,string path)
    {
        if(path==SetupMember)return;var prefix="versions/"+version+"/";
        if(!path.StartsWith(prefix,StringComparison.Ordinal))throw new IOException("UNINSTALL_INTENT_INVALID");InstallPackage.Name(path[prefix.Length..]);
    }
    internal bool AtLeast(string stage)=>Array.IndexOf(Stages,stage) is var wanted and >=0?Array.IndexOf(Stages,Stage)>=wanted:throw new IOException("UNINSTALL_INTENT_INVALID");
    internal void RequireSameInstallation(InstallerState other)
    {if(!UninstallChoice.SameInstallation(State,other))throw new IOException("UNINSTALL_INTENT_INVALID");}
}

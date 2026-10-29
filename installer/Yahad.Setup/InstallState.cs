using System.IO;
using Yahad.Service;
namespace Yahad.Setup;

internal sealed record InstallerState(int Format,string InstallationId,string DataId,string PackageDigest,string PayloadDigest,string SourceCommit,string OwnerSid,int Port,string Phase,InstallMember[] Files,string ServiceSid,string SetupSha256,int PayloadSchema=5);
internal static class InstallLocations
{
    internal static string State=>FirstInstallProfile.StateRoot;
    internal static string Binaries=>FirstInstallProfile.BinaryRoot;
    internal static string ServiceImage=>Path.Combine(Binaries,"host","Yahad.ServiceHost.exe");
    internal static string InstallerImage=>Path.Combine(Binaries,"Yahad.Setup.exe");
    internal static string RemovalStateFile=>Binaries+".uninstall-state.json";
    internal static string StateFile=>Path.Combine(State,"installer-state.json");
    internal static string ServiceName=>ServiceDefinition.Name;
}

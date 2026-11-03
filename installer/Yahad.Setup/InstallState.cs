using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Yahad.Service;
using Yahad.Install;
namespace Yahad.Setup;

// The administrator-owned installer record of the one Runtime service. It is
// installer metadata only: the service reads installed.json, never this file.
internal sealed record InstallerState(int Format,string InstallationId,string DataId,string Version,string PackageDigest,string PayloadDigest,string SourceCommit,
    string OwnerSid,int Port,string Phase,string SetupSha256,FirstOwnerBinding FirstOwner,UpdateSetupChoice Consent,InstallMember[] Files,string ServiceSid)
{
    internal const int CurrentFormat=6;
    // Formats 3, 4 and 5 are the retired four-service installer records. They
    // are never adopted or reinterpreted; their own installer must remove them.
    internal static readonly int[] LegacyFormats=[3,4,5];
    internal static readonly string[] Phases=["preparing","committed","uninstalled","removing_keep_data","removing_all_data"];
    internal void Require()
    {
        InstalledLaunch.Id(InstallationId);InstalledLaunch.Id(DataId);InstallLocations.RequireVersion(Version);
        foreach(var digest in new[]{PackageDigest,PayloadDigest,SetupSha256})InstallLocations.RequireDigest(digest);
        if(Format!=CurrentFormat||!OwnerSid.StartsWith("S-1-5-21-",StringComparison.Ordinal)||!new SecurityIdentifier(OwnerSid).IsAccountSid()||Port is <1024 or >65535||
            !Regex.IsMatch(SourceCommit,"^[a-f0-9]{40}$")||Files.Length is <10 or >50000||!Phases.Contains(Phase)||ServiceSid!=InstallLocations.ServiceSid)throw new IOException("INSTALL_STATE_INVALID");
        FirstOwner.Require(InstallationId,OwnerSid);Consent.RequirePersisted(DateTimeOffset.UtcNow);
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var f in Files){InstallPackage.Name(f.Path);InstallLocations.RequireDigest(f.Sha256);if(f.Size<0||!names.Add(f.Path))throw new IOException("INSTALL_STATE_INVALID");}
    }
}
internal static class InstallLocations
{
    internal static string State=>InstalledUpdaterLayout.StateRoot;
    internal static string Binaries=>InstalledUpdaterLayout.BinaryRoot;
    internal static string Versions=>InstalledLaunch.VersionsRoot;
    internal static string VersionRoot(string version){RequireVersion(version);return InstalledLaunch.VersionRoot(version);}
    internal static string ServiceImage(string version)=>Path.Combine(VersionRoot(version),"host","Yahad.ServiceHost.exe");
    internal static string ServiceCommand(string version)=>"\""+ServiceImage(version)+"\" --service";
    internal static string InstallerImage=>Path.Combine(Binaries,"Yahad.Setup.exe");
    internal static string RemovalStateFile=>Binaries+".uninstall-state.json";
    internal static string RemovalDiagnosticFile=>Binaries+".uninstall-diagnostic.json";
    internal static string StateFile=>Path.Combine(State,"installer-state.json");
    internal static string LaunchFile=>Path.Combine(State,InstalledLaunch.FileName);
    internal static string ServiceName=>ServiceDefinition.Name;
    internal static string ServiceAccount=>ServiceDefinition.Account;
    // A service SID is derived from the service name alone (SHA-1 of the
    // upper-case UTF-16LE name), so the installer knows it before CreateService.
    internal static string ServiceSid=>ServiceSidFor(ServiceName);
    internal static string ServiceSidFor(string name)
    {
        var hash=SHA1.HashData(Encoding.Unicode.GetBytes(name.ToUpperInvariant()));
        return "S-1-5-80-"+string.Join('-',Enumerable.Range(0,5).Select(i=>BitConverter.ToUInt32(hash,i*4).ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }
    internal static void RequireVersion(string version)
    {if(version.Length>40||!Regex.IsMatch(version,@"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z",RegexOptions.CultureInvariant))throw new IOException("PAYLOAD_VERSION");}
    internal static void RequireDigest(string? value){if(value is null||!Regex.IsMatch(value,"^[a-f0-9]{64}$"))throw new IOException("INSTALL_STATE_INVALID");}
    internal static Version Ordered(string version){RequireVersion(version);return System.Version.Parse(version);}
}

using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yahad.Service;
using Yahad.Install;
namespace Yahad.Setup;

// The installed infrastructure tree (docs/WINDOWS-APP-PACKAGE-PLAN.md 4.2): its
// identity, the SHA-256 of its infra-tree.json and every member relative to
// infra\<version>\, the tree record included. EngineSha256 names the tree's own
// payload-free Setup engine (setup/Yahad.Setup.exe).
internal sealed record InfrastructureRecord(string Version,int ContractMin,int ContractMax,string SourceCommit,string EngineSha256,string TreeSha256,InstallMember[] Files)
{
    internal const string EngineMember="setup/"+InstalledLayout.EngineName;
    internal void Require()
    {
        InstallLocations.RequireVersion(Version);InstallLocations.RequireDigest(EngineSha256);InstallLocations.RequireDigest(TreeSha256);
        if(!InfrastructureRelease.IsContract(ContractMin)||!InfrastructureRelease.IsContract(ContractMax)||ContractMin>ContractMax||!TreeRecord.IsCommit(SourceCommit))throw new IOException("INSTALL_STATE_INVALID");
        InstallerState.RequireTree(TreeKind.Infrastructure,Files,TreeSha256);
        if(!Files.Any(f=>f.Path==EngineMember&&f.Sha256==EngineSha256))throw new IOException("INSTALL_STATE_INVALID");
    }
    internal bool Supports(int contract)=>contract>=ContractMin&&contract<=ContractMax;
}
// The installed app tree: identity, the contract it needs, its app-tree.json digest,
// the database schema its Runtime last reported healthy (or, before that, the schema
// its package declares) and every member relative to apps\<version>\.
internal sealed record AppRecord(string Version,string SourceCommit,int RequiresContract,string TreeSha256,int DatabaseSchema,InstallMember[] Files)
{
    internal void Require()
    {
        InstallLocations.RequireVersion(Version);InstallLocations.RequireDigest(TreeSha256);
        if(!InfrastructureRelease.IsContract(RequiresContract)||DatabaseSchema is <1 or >1024||!TreeRecord.IsCommit(SourceCommit))throw new IOException("INSTALL_STATE_INVALID");
        InstallerState.RequireTree(TreeKind.App,Files,TreeSha256);
    }
}
// One (infrastructure, app) pair: what active.json names, and what one rollback restores.
internal sealed record InstalledPair(InfrastructureRecord Infrastructure,AppRecord App)
{
    internal void Require()
    {
        if(Infrastructure is null||App is null)throw new IOException("INSTALL_STATE_INVALID");
        Infrastructure.Require();App.Require();
        if(!Infrastructure.Supports(App.RequiresContract))throw new IOException("INSTALL_STATE_INVALID");
    }
    internal ActivePointer Pointer()=>new(ActivePointer.CurrentFormat,new(Infrastructure.Version,Infrastructure.ContractMin,Infrastructure.ContractMax,Infrastructure.TreeSha256),
        new(App.Version,App.RequiresContract,App.TreeSha256));
    // Two trees are the same tree only when version and record bytes both match.
    internal bool SameInfrastructure(InstalledPair other)=>Infrastructure.Version==other.Infrastructure.Version&&Infrastructure.TreeSha256==other.Infrastructure.TreeSha256;
    internal bool SameApp(InstalledPair other)=>App.Version==other.App.Version&&App.TreeSha256==other.App.TreeSha256;
    internal bool SameTrees(InstalledPair other)=>SameInfrastructure(other)&&SameApp(other);
    internal string Describe=>Infrastructure.Version+"/"+App.Version;
}
// The administrator-owned installer record (format 7). It is installer metadata
// only: the binaries read active.json and installed.json, never this file.
//
// Its size is bounded by construction (4.4). It carries the member list of each tree
// of its two pairs, the payload-free uninstall inventory; each list encodes, as this
// record encodes it, to at most TreeListMaximumBytes, which every package that
// installs a tree checks before anything changes. Everything else is validated
// identifiers, far inside FixedMaximumBytes. So every record a valid move writes is at
// most MaximumBytes, the one bound every reader reads with, and Encode refuses a
// record past it before anything is written.
internal sealed record InstallerState(int Format,string InstallationId,string DataId,string OwnerSid,int Port,string Phase,FirstOwnerBinding FirstOwner,UpdateSetupChoice Consent,
    string ServiceSid,InstalledPair Active,string? ReleaseKeySha256=null,InstalledPair? Previous=null)
{
    internal const int CurrentFormat=7;
    internal const int TreeListMaximumBytes=TreeRecord.MaximumBytes,FixedMaximumBytes=65536,MaximumBytes=4*TreeListMaximumBytes+FixedMaximumBytes;
    internal static readonly string[] Phases=["preparing","provisioned","committed","uninstalled","removing_keep_data","removing_all_data"];
    internal void Require()
    {
        InstalledLaunch.Id(InstallationId);InstalledLaunch.Id(DataId);
        if(Format!=CurrentFormat||OwnerSid is null||!OwnerSid.StartsWith("S-1-5-21-",StringComparison.Ordinal)||!new SecurityIdentifier(OwnerSid).IsAccountSid()||Port is <1024 or >65535||
            !Phases.Contains(Phase)||ServiceSid!=InstallLocations.ServiceSid||Active is null||FirstOwner is null||Consent is null)throw new IOException("INSTALL_STATE_INVALID");
        FirstOwner.Require(InstallationId,OwnerSid);Consent.RequirePersisted(DateTimeOffset.UtcNow);
        Active.Require();
        if(ReleaseKeySha256 is not null)InstallLocations.RequireDigest(ReleaseKeySha256);
        if(Previous is {} previous)
        {
            previous.Require();
            // A retained pair is never newer than the active one on either track, is
            // never the active pair itself, and a version shared with the active pair
            // is the very same tree (one directory per version).
            if(SemVer.Compare(previous.Infrastructure.Version,Active.Infrastructure.Version)>0||SemVer.Compare(previous.App.Version,Active.App.Version)>0||
                previous.Infrastructure.Version==Active.Infrastructure.Version&&!previous.SameInfrastructure(Active)||
                previous.App.Version==Active.App.Version&&!previous.SameApp(Active)||previous.SameTrees(Active))throw new IOException("INSTALL_STATE_INVALID");
        }
    }
    // A record of another format is a pre-7 (or unknown) layout. Nothing reads it.
    internal static InstallerState Parse(byte[] bytes)
    {
        if(RecordFormat.Of(bytes,"INSTALL_STATE_INVALID")!=CurrentFormat)throw new IOException("LEGACY_INSTALL_UNSUPPORTED");
        InstallerState state;
        try{state=NativeJson.Read<InstallerState>(bytes);}catch(JsonException){throw new IOException("INSTALL_STATE_INVALID");}
        state.Require();return state;
    }
    internal byte[] Encode(){Require();return Bounded(JsonSerializer.SerializeToUtf8Bytes(this),MaximumBytes);}
    internal static InstallerState Read(string path)=>Parse(NativeInstallFiles.ReadRecord(path,MaximumBytes));
    internal static byte[] Bounded(byte[] bytes,int maximum)=>bytes.Length<=maximum?bytes:throw new IOException("INSTALL_STATE_INVALID");
    // The record a move to that pair commits: the same one whether admission checks it
    // or the commit writes it.
    internal InstallerState After(InstalledPair to)=>this with{Active=to,Previous=Active};
    // One tree's member list as this record encodes it.
    internal static bool WithinListBound(InstallMember[] files)=>JsonSerializer.SerializeToUtf8Bytes(files).Length<=TreeListMaximumBytes;
    // Members of one tree, relative to its root: every name is in that tree or is its
    // record, unique ignoring case, and the record's digest is the recorded tree digest.
    internal static void RequireTree(TreeKind kind,InstallMember[] files,string treeSha256)
    {
        if(files is not {Length:>=2 and <=TreeRecord.MaximumMembers+1})throw new IOException("INSTALL_STATE_INVALID");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var record=TreeRecord.RecordName(kind);
        foreach(var f in files)
        {
            if(f is null||!MemberNames.Valid(f.Path)||f.Path!=record&&!TreeRecord.InTree(kind,f.Path)||f.Size is <0 or >TreeRecord.MaximumMemberBytes||!names.Add(f.Path))throw new IOException("INSTALL_STATE_INVALID");
            InstallLocations.RequireDigest(f.Sha256);
        }
        if(!files.Any(f=>f.Path==record&&f.Sha256==treeSha256)||!WithinListBound(files))throw new IOException("INSTALL_STATE_INVALID");
    }
}
// The one field read before a record's own strict reader: its integer Format. A
// record without one is damaged (the caller's code); any other format is a layout
// this engine does not read, and the caller refuses it as legacy.
internal static class RecordFormat
{
    internal static int Of(byte[] bytes,string invalid)
    {
        try
        {
            using var document=JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind==JsonValueKind.Object&&document.RootElement.TryGetProperty("Format",out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out var parsed)?parsed:throw new IOException(invalid);
        }
        catch(JsonException){throw new IOException(invalid);}
    }
}
internal static class ExistingInstallation
{
    // A preserved computer-only installation has no binaries or LAN state but
    // still has its state root.
    internal static bool Present()=>new[]{InstallLocations.State,InstallLocations.Binaries,InstallLocations.RemovalStateFile}.Any(Path.Exists);
    // The pre-pair layout kept every release under <binaries>\versions\. Nothing
    // migrates it: it is refused before anything is written (8.3).
    internal static void RequireNotLegacy()
    {if(Path.Exists(Path.Combine(InstallLocations.Binaries,"versions")))throw new IOException("LEGACY_INSTALL_UNSUPPORTED");}
}
internal static class InstallLocations
{
    internal static string State=>InstalledLayout.StateRoot;
    internal static string Binaries=>InstalledLayout.BinaryRoot;
    internal static string ServiceCommand(string infrastructure)=>"\""+InstalledLayout.HostImage(infrastructure)+"\" --service";
    internal static string ReleaseKeyFile=>Path.Combine(Binaries,TrustedReleaseKey.InstalledName);
    internal static string UpgradeStateFile=>Path.Combine(State,"upgrade-state.json");
    internal static string RemovalStateFile=>Binaries+".uninstall-state.json";
    internal static string StateFile=>Path.Combine(State,"installer-state.json");
    internal static void RequireStateRoot()=>WindowsFileBoundary.RequireAdministratorOwned(State);
    internal static string LaunchFile=>Path.Combine(State,InstalledLaunch.FileName);
    // A service SID is derived from the service name alone (SHA-1 of the
    // upper-case UTF-16LE name), so the installer knows it before CreateService.
    internal static string ServiceSid=>ServiceSidFor(ServiceDefinition.Name);
    internal static string ServiceSidFor(string name)
    {
        var hash=SHA1.HashData(Encoding.Unicode.GetBytes(name.ToUpperInvariant()));
        return "S-1-5-80-"+string.Join('-',Enumerable.Range(0,5).Select(i=>BitConverter.ToUInt32(hash,i*4).ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }
    internal static void RequireVersion(string version)
    {if(!SemVer.IsVersion(version))throw new IOException("PAYLOAD_VERSION");}
    internal static void RequireDigest(string? value){if(value is null||!Regex.IsMatch(value,"^[a-f0-9]{64}$"))throw new IOException("INSTALL_STATE_INVALID");}
}

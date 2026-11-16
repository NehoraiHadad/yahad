using System.IO;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;

// The protected external receipt outlives both removable roots, so a retry
// never needs a deleted record to rediscover what it is removing. It carries the
// record itself: the binary inventory is the one that record implies (Inventory),
// derived, never stored a second time, so the receipt is the record's bound plus a
// mode and a stage.
internal sealed record RemovalIntent(int Format,InstallerState State,UninstallMode Mode,string Stage)
{
    internal const int MaximumBytes=InstallerState.MaximumBytes+InstallerState.FixedMaximumBytes;
    internal static readonly string[] Stages=["prepared","lan_removed","services_removed","data_removed","binaries_removed"];
    internal InstallMember[] Binaries=>Inventory(State);
    internal void Require()
    {
        if(State is null)throw new IOException("UNINSTALL_INTENT_INVALID");
        State.Require();UninstallChoice.Require(Mode);
        if(Format!=InstallerState.CurrentFormat||State.Phase!=UninstallChoice.Phase(Mode)||!Stages.Contains(Stage))throw new IOException("UNINSTALL_INTENT_INVALID");
    }
    internal byte[] Encode(){Require();return InstallerState.Bounded(JsonSerializer.SerializeToUtf8Bytes(this),MaximumBytes);}
    internal static RemovalIntent Read(string path)=>Parse(NativeInstallFiles.ReadRecord(path,MaximumBytes));
    // A receipt of another format was written by a pre-pair Setup: nothing finishes it.
    internal static RemovalIntent Parse(byte[] bytes)
    {
        if(RecordFormat.Of(bytes,"UNINSTALL_INTENT_INVALID")!=InstallerState.CurrentFormat)throw new IOException("LEGACY_INSTALL_UNSUPPORTED");
        RemovalIntent intent;
        try{intent=NativeJson.Read<RemovalIntent>(bytes);}catch(JsonException){throw new IOException("UNINSTALL_INTENT_INVALID");}
        intent.Require();return intent;
    }
    internal static string InfraMember(string infrastructure,string member){InstallLocations.RequireVersion(infrastructure);return "infra/"+infrastructure+"/"+member;}
    internal static string AppMember(string app,string member){InstallLocations.RequireVersion(app);return "apps/"+app+"/"+member;}
    internal static string EngineMember(string infrastructure)=>InfraMember(infrastructure,InfrastructureRecord.EngineMember);
    // Everything the binary root holds (8.4), relative to it: the installed release
    // key, active.json with the exact bytes of the active pair's pointer, and every
    // member of the active and retained trees. A tree the two pairs share is listed once.
    internal static InstallMember[] Inventory(InstallerState state)
    {
        var pointer=state.Active.Pointer().Encode();
        var members=new List<InstallMember>{new(InstalledLayout.ActivePointerName,pointer.Length,TreeRecord.Sha256(pointer))};
        if(state.ReleaseKeySha256 is {} key)members.Add(new(TrustedReleaseKey.InstalledName,TrustedReleaseKey.SpkiBytes,key));
        var infra=new HashSet<string>(StringComparer.Ordinal);var apps=new HashSet<string>(StringComparer.Ordinal);
        foreach(var pair in state.Previous is {} previous?new[]{state.Active,previous}:[state.Active])
        {
            if(infra.Add(pair.Infrastructure.Version))members.AddRange(pair.Infrastructure.Files.Select(f=>f with{Path=InfraMember(pair.Infrastructure.Version,f.Path)}));
            if(apps.Add(pair.App.Version))members.AddRange(pair.App.Files.Select(f=>f with{Path=AppMember(pair.App.Version,f.Path)}));
        }
        return members.ToArray();
    }
    internal bool AtLeast(string stage)=>Array.IndexOf(Stages,stage) is var wanted and >=0?Array.IndexOf(Stages,Stage)>=wanted:throw new IOException("UNINSTALL_INTENT_INVALID");
    internal void RequireSameInstallation(InstallerState other)
    {if(!UninstallChoice.SameInstallation(State,other))throw new IOException("UNINSTALL_INTENT_INVALID");}
}

using System.IO;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;

// What an interrupted upgrade needs to finish without the release's Setup.
internal sealed record UpgradeTarget(string PackageDigest,string PayloadDigest,string SourceCommit,string SetupSha256,InstallMember[] Files);
// %ProgramData%\Yahad.FirstInstall\upgrade-state.json. Administrator-only and
// present only during an upgrade. Format 1 is frozen: an older retained Setup
// must be able to repair a newer one's interrupted upgrade, and the reverse.
// Each phase is recorded after its idempotent step completed. Paths are not
// stored: images and directories follow from the two versions.
internal sealed record UpgradeState(int Format,string Id,string From,string To,int? FromSchema,int ToSchema,bool SchemaChanged,string Phase,bool RestoreData,long StartedUnix,long HealthDeadlineUnix,int Attempts,string? Failure,UpgradeTarget Target)
{
    internal const int CurrentFormat=1;
    internal static readonly string[] Phases=["preparing","prepared","stopped","backed_up","switched","started","healthy","committed","rolling_back","rolled_back"];
    internal static UpgradeState Begin(string from,string to,int? fromSchema,int toSchema,UpgradeTarget target,long now)
        =>new(CurrentFormat,Guid.NewGuid().ToString("N"),from,to,fromSchema,toSchema,UpgradeRules.SchemaChanged(fromSchema,toSchema),"preparing",false,now,0,0,null,target);
    internal void Require()
    {
        if(!Guid.TryParseExact(Id,"N",out var parsed)||parsed.ToString("N")!=Id)throw new IOException("INSTALL_STATE_INVALID");
        InstallLocations.RequireVersion(From);InstallLocations.RequireVersion(To);
        if(Format!=CurrentFormat||ReleaseManifest.Compare(To,From)<=0||!Phases.Contains(Phase)||ToSchema<1||FromSchema<1||Attempts<0||(RestoreData&&!SchemaChanged)||SchemaChanged!=UpgradeRules.SchemaChanged(FromSchema,ToSchema))throw new IOException("INSTALL_STATE_INVALID");
        foreach(var digest in new[]{Target.PackageDigest,Target.PayloadDigest,Target.SetupSha256})InstallLocations.RequireDigest(digest);
        if(!System.Text.RegularExpressions.Regex.IsMatch(Target.SourceCommit,"^[a-f0-9]{40}$"))throw new IOException("INSTALL_STATE_INVALID");
        InstallerState.RequireMembers(Target.Files);
    }
    internal byte[] Encode(){Require();return JsonSerializer.SerializeToUtf8Bytes(this);}
    internal static UpgradeState Parse(byte[] bytes)
    {
        UpgradeState value;
        try{value=NativeJson.Read<UpgradeState>(bytes);}catch(JsonException){throw new IOException("INSTALL_STATE_INVALID");}
        value.Require();return value;
    }
}
internal enum RepairAction{Discard,RollBack,FinishCommit,FinishRollback,GiveUp}
internal sealed record HealthObservation(bool Ok,string? Version,int? Schema);
// The decisions of the upgrade, free of any machine state.
internal static class UpgradeRules
{
    internal const int MaximumRollbackAttempts=3,RequiredHealthyPolls=3,HealthPollSeconds=5,HealthWindowSeconds=300;
    // An unknown source schema is treated as changed: the data is backed up and restored.
    internal static bool SchemaChanged(int? from,int to)=>from!=to;
    internal static RepairAction ForRepair(string phase,int attempts)=>phase switch
    {
        "preparing" or "prepared"=>RepairAction.Discard,
        "stopped" or "backed_up" or "switched" or "started" or "rolling_back"=>attempts>=MaximumRollbackAttempts?RepairAction.GiveUp:RepairAction.RollBack,
        "healthy" or "committed"=>RepairAction.FinishCommit,
        "rolled_back"=>RepairAction.FinishRollback,
        _=>throw new IOException("INSTALL_STATE_INVALID")
    };
    // Before the new image could run, the live database is the untouched one. From
    // the moment a whole backup exists it is the only copy known to be readable by
    // the old release, whichever step was interrupted.
    internal static bool RestoresData(UpgradeState state)=>state.RestoreData||state.SchemaChanged&&state.Phase is "backed_up" or "switched" or "started";
    internal static bool HealthPasses(HealthObservation? seen,string version,int? schema)=>seen is {Ok:true}&&seen.Version==version&&(schema is null||seen.Schema==schema);
    internal static int NextHealthyPolls(int count,bool passed)=>passed?count+1:0;
    internal static int Exit(string code)=>code switch
    {
        "UPGRADED" or "REPAIRED" or "NOTHING_TO_REPAIR"=>0,
        "RELEASE_SIGNATURE_INVALID" or "RELEASE_KEY_UNTRUSTED" or "RELEASE_NOT_NEWER" or "RELEASE_SETUP_MISMATCH" or "RELEASE_MANIFEST_INVALID" or "RELEASE_UNSUPPORTED_FROM" or "UPGRADE_LOCKED" or "UPGRADE_INCOMPLETE" or "NOT_INSTALLED" or "INSTALL_INCOMPLETE" or "REPAIR_RUNNING_FROM_TARGET"=>2,
        "ROLLBACK_FAILED" or "UPGRADE_COMMIT_INCOMPLETE" or "REPAIR_UNHEALTHY"=>3,
        "ROLLED_BACK_UNHEALTHY" or "UPGRADE_PREPARE_FAILED"=>4,
        _=>1
    };
}

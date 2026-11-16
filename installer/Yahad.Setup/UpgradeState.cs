using System.IO;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;

// %ProgramData%\Yahad.FirstInstall\upgrade-state.json. Administrator-only and
// present only during an upgrade. Format 2 records the two whole pairs, so an
// interrupted upgrade finishes or rolls back with no release at hand. Paths are not
// stored: images and directories follow from the two pairs' versions. FromSchema is
// the installer record's, never a live observation (4.5).
// Kind "setup" is the Setup track (8.1): it moves the infrastructure, and each of its
// phases is recorded after its idempotent step completed. Kind "app" is the app track
// (7): the same infrastructure tree and a newer app, and each phase is recorded as its
// step begins, so the recorded phase names the step whose "on failure" rule applies.
internal sealed record UpgradeState(int Format,string Id,string Kind,InstalledPair From,InstalledPair To,int FromSchema,int ToSchema,bool SchemaChanged,string Phase,bool RestoreData,
    long StartedUnix,long HealthDeadlineUnix,int Attempts,string? Failure)
{
    internal const int CurrentFormat=2;
    // Two pairs and validated identifiers: the installer record's bound (InstallerState).
    internal const int MaximumBytes=InstallerState.MaximumBytes;
    internal const string SetupKind="setup",AppKind="app";
    internal static readonly string[] SetupPhases=["preparing","prepared","stopped","backed_up","switched","started","healthy","committed","rolling_back","rolled_back"];
    // In step order: AppUpgradeHost compares positions in it.
    internal static readonly string[] AppPhases=["admitting","preparing","fencing","stopping","stopped","switching","started","healthy","committed","rolling_back","rolled_back"];
    internal static string[] Phases(string kind)=>kind switch{SetupKind=>SetupPhases,AppKind=>AppPhases,_=>throw new IOException("INSTALL_STATE_INVALID")};
    internal static UpgradeState Begin(string kind,InstalledPair from,InstalledPair to,long now)
        =>new(CurrentFormat,Guid.NewGuid().ToString("N"),kind,from,to,from.App.DatabaseSchema,to.App.DatabaseSchema,UpgradeRules.SchemaChanged(from.App.DatabaseSchema,to.App.DatabaseSchema),
            Phases(kind)[0],false,now,0,0,null);
    internal UpgradeTrack Track=>UpgradeRules.Decide(From,To);
    internal void Require()
    {
        if(!Guid.TryParseExact(Id,"N",out var parsed)||parsed.ToString("N")!=Id||From is null||To is null||Kind is not(SetupKind or AppKind))throw new IOException("INSTALL_STATE_INVALID");
        From.Require();To.Require();
        if(Format!=CurrentFormat||!Phases(Kind).Contains(Phase)||FromSchema!=From.App.DatabaseSchema||ToSchema!=To.App.DatabaseSchema||Attempts<0||
            (RestoreData&&!SchemaChanged)||SchemaChanged!=UpgradeRules.SchemaChanged(FromSchema,ToSchema))throw new IOException("INSTALL_STATE_INVALID");
        // Each kind moves exactly its own track.
        UpgradeTrack track;try{track=Track;}catch(IOException){throw new IOException("INSTALL_STATE_INVALID");}
        if(track!=(Kind==AppKind?UpgradeTrack.App:UpgradeTrack.Infrastructure))throw new IOException("INSTALL_STATE_INVALID");
    }
    internal byte[] Encode(){Require();return InstallerState.Bounded(JsonSerializer.SerializeToUtf8Bytes(this),MaximumBytes);}
    internal static UpgradeState Parse(byte[] bytes)
    {
        if(RecordFormat.Of(bytes,"INSTALL_STATE_INVALID")!=CurrentFormat)throw new IOException("LEGACY_INSTALL_UNSUPPORTED");
        UpgradeState value;
        try{value=NativeJson.Read(bytes,SetupJsonContext.Default.UpgradeState);}catch(JsonException){throw new IOException("INSTALL_STATE_INVALID");}
        value.Require();return value;
    }
}
internal enum RepairAction{Discard,RollBack,FinishCommit,FinishRollback,GiveUp}
// An app-only apply keeps the infrastructure tree, its images and its firewall rule (7);
// an infrastructure move switches all three images and carries an app with it (8.1).
internal enum UpgradeTrack{App,Infrastructure}
internal sealed record HealthObservation(bool Ok,string? Version,int? Schema);
// The decisions of the upgrade, free of any machine state.
internal static class UpgradeRules
{
    internal const int MaximumRollbackAttempts=3,RequiredHealthyPolls=3,HealthPollSeconds=5,HealthWindowSeconds=300;
    internal static bool SchemaChanged(int from,int to)=>from!=to;
    // A version names exactly one tree: the same version with other record bytes is a
    // different build nothing can move between. Neither track ever goes back.
    internal static UpgradeTrack Decide(InstalledPair from,InstalledPair to)
    {
        var infrastructure=SemVer.Compare(to.Infrastructure.Version,from.Infrastructure.Version);var app=SemVer.Compare(to.App.Version,from.App.Version);
        if(infrastructure==0&&!to.SameInfrastructure(from))throw new IOException("INFRA_IDENTITY_MISMATCH");
        if(app==0&&!to.SameApp(from))throw new IOException("APP_IDENTITY_MISMATCH");
        if(infrastructure<0||app<0)throw new IOException("INSTALL_DOWNGRADE_REFUSED");
        if(infrastructure>0)return UpgradeTrack.Infrastructure;
        if(app>0)return UpgradeTrack.App;
        throw new IOException("RELEASE_NOT_NEWER");
    }
    internal static bool IsUpgrade(InstalledPair from,InstalledPair to)
    {try{_=Decide(from,to);return true;}catch(IOException){return false;}}
    // After a commit the binary root keeps the new active pair and the one it replaced,
    // nothing else: every other infra\ and apps\ directory goes.
    internal static bool Retained(TreeKind kind,string version,InstalledPair active,InstalledPair? previous)
        =>kind==TreeKind.Infrastructure?version==active.Infrastructure.Version||version==previous?.Infrastructure.Version:version==active.App.Version||version==previous?.App.Version;
    internal static RepairAction ForRepair(string kind,string phase,int attempts)
    {
        var action=kind switch
        {
            UpgradeState.SetupKind=>phase switch
            {
                "preparing" or "prepared"=>RepairAction.Discard,
                "stopped" or "backed_up" or "switched" or "started" or "rolling_back"=>RepairAction.RollBack,
                "healthy" or "committed"=>RepairAction.FinishCommit,
                "rolled_back"=>RepairAction.FinishRollback,
                _=>throw new IOException("INSTALL_STATE_INVALID")
            },
            // Section 7's table: until the pointer may have moved only what was stopped or
            // fenced is undone; from the switch on, the from-pair is restored.
            UpgradeState.AppKind=>phase switch
            {
                "admitting" or "preparing" or "fencing" or "stopping" or "stopped"=>RepairAction.Discard,
                "switching" or "started" or "healthy" or "rolling_back"=>RepairAction.RollBack,
                "committed"=>RepairAction.FinishCommit,
                "rolled_back"=>RepairAction.FinishRollback,
                _=>throw new IOException("INSTALL_STATE_INVALID")
            },
            _=>throw new IOException("INSTALL_STATE_INVALID")
        };
        return action==RepairAction.RollBack&&attempts>=MaximumRollbackAttempts?RepairAction.GiveUp:action;
    }
    // Before the new image could run, the live database is the untouched one. From
    // the moment a whole backup exists it is the only copy known to be readable by
    // the old release, whichever step was interrupted. The app track's backup is whole
    // once its switch began.
    internal static bool RestoresData(UpgradeState state)=>state.RestoreData||state.SchemaChanged&&(state.Kind==UpgradeState.AppKind?
        state.Phase is "switching" or "started" or "healthy":state.Phase is "backed_up" or "switched" or "started");
    // A discarded attempt reports UPGRADE_PREPARE_FAILED. An archive member the validator
    // refuses while it is extracted is the release's own defect (9.4, archive), so once
    // nothing of it is left it is reported as itself.
    internal static string Discarded(string cause)=>cause.StartsWith("APP_PACKAGE_",StringComparison.Ordinal)?cause:"UPGRADE_PREPARE_FAILED";
    internal static bool HealthPasses(HealthObservation? seen,string version,int schema)=>seen is {Ok:true}&&seen.Version==version&&seen.Schema==schema;
    internal static int NextHealthyPolls(int count,bool passed)=>passed?count+1:0;
    internal static int Exit(string code)=>code switch
    {
        "UPGRADED" or "REPAIRED" or "NOTHING_TO_REPAIR"=>0,
        "RELEASE_SIGNATURE_INVALID" or "RELEASE_KEY_UNTRUSTED" or "RELEASE_NOT_NEWER" or "RELEASE_SETUP_MISMATCH" or "RELEASE_MANIFEST_INVALID" or "RELEASE_UNSUPPORTED_FROM" or "UPGRADE_LOCKED" or
            "UPGRADE_INCOMPLETE" or "NOT_INSTALLED" or "INSTALL_INCOMPLETE" or "LEGACY_INSTALL_UNSUPPORTED" or "INFRA_IDENTITY_MISMATCH" or "APP_IDENTITY_MISMATCH" or "INSTALL_DOWNGRADE_REFUSED" or
            "ACTIVE_POINTER_INVALID" or "TREE_RECORD_INVALID" or "RELEASE_TRACK_MISMATCH" or "APP_NEEDS_INFRASTRUCTURE" or "RELEASE_APP_SIZE" or "RELEASE_APP_HASH"=>2,
        _ when code.StartsWith("APP_PACKAGE_",StringComparison.Ordinal)=>2,
        "ROLLBACK_FAILED" or "UPGRADE_COMMIT_INCOMPLETE" or "REPAIR_UNHEALTHY"=>3,
        "ROLLED_BACK_UNHEALTHY" or "UPGRADE_PREPARE_FAILED"=>4,
        _=>1
    };
}

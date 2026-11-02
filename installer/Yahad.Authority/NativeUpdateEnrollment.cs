using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record NativeUpdateEnrollment(int Format,string InstallationId,string Repository,string Channel,string AssetPattern,string Trust,string FamilyId,string UserId,string PolicyDigest,string PinDigest);
internal sealed record NativeUpdateView(bool Enabled,bool ModeControlAllowed,string FamilyId,string UserId,string CurrentVersion,long? CheckedAt,long? LastUpdateUnix,long UpdatedAt,string? Error,long DeferredUntilUnix=0);
// Provisioned only with the explained installer/Owner consent. No app protocol
// route can publish source enrollment or select the designated parent account.
internal static class NativeUpdateEnrollmentReader
{
    internal static NativeUpdateEnrollment Require(InstalledStateFiles files,InstalledAuthorityAnchor anchor)
    {
        var e=NativeJson.Read<NativeUpdateEnrollment>(files.Read(files.PathOf("intake/enrollment.json"),8192));
        if(e.Format!=1||e.InstallationId!=anchor.InstallationId||e.Repository!=NativeCandidateIntake.Repository||e.Channel!="stable"||
            e.AssetPattern!="yahad-app-native-{version}.zip"||e.Trust!="https-github-unsigned"||e.PolicyDigest!=anchor.IntakePolicyDigest||e.PinDigest!=anchor.IntakePinDigest||
            string.IsNullOrWhiteSpace(e.FamilyId)||e.FamilyId.Length>100||string.IsNullOrWhiteSpace(e.UserId)||e.UserId.Length>100)throw new IOException("NATIVE_UPDATE_ENROLLMENT");
        return e;
    }
    internal static NativeUpdateView View(InstalledStateFiles files,InstalledAuthorityAnchor anchor,InstalledIntakeSelection intake,Func<DateTimeOffset> clock)
    {
        var e=Require(files,anchor);var head=intake.Head();var manifest=NativeJson.Read<JsonElement>(files.Read(files.PathOf("payloads/"+head.ReleaseDigest+"/bundle.json"),8*1024*1024));
        UpdateScheduleState? schedule=null;var path=files.PathOf(".authority/scheduler/state.json");if(File.Exists(path))schedule=NativeJson.Read<UpdateScheduleState>(files.Read(path,8192));
        return new(true,intake.Mode().ModeControlAllowed,e.FamilyId,e.UserId,manifest.GetProperty("version").GetString()!,schedule?.LastCheckUnix*1000,schedule?.LastUpdateUnix,clock().ToUnixTimeMilliseconds(),schedule?.Error,schedule?.DeferredUntilUnix??0);
    }
}

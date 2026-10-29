using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Setup;

internal sealed record LanWebSnapshot(byte[] Web,byte[] Activation,byte[] Epoch,byte[] Bootstrap);
internal sealed record LanWebJournal(int Format,string InstallationId,LanWebSnapshot Before,LanWebSnapshot After);
// No database, family, provider token or key is part of this transaction. The
// application service must be stopped. A pending journal is repaired only when
// each current control file is one of its two exact, validated versions.
internal static class LanWebConfiguration
{
    internal static readonly string[] Names=["web.json","activation.json","epoch.json","bootstrap.json"];
    private static byte[] Json<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value);
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static byte[][] Parts(LanWebSnapshot s)=>[s.Web,s.Activation,s.Epoch,s.Bootstrap];
    internal static LanWebSnapshot Read(Func<string,byte[]> read)=>new(read(Names[0]),read(Names[1]),read(Names[2]),read(Names[3]));
    internal static void Require(LanWebSnapshot snapshot,InstallerState state)
    {
        var web=NativeJson.Read<InstalledWeb>(snapshot.Web);var activation=NativeJson.Read<InstalledActivation>(snapshot.Activation);
        var epoch=NativeJson.Read<InstalledEpoch>(snapshot.Epoch);var bootstrap=NativeJson.Read<JsonElement>(snapshot.Bootstrap);
        if(web.Format!=1||activation.Format!=1||epoch.Format!=1||activation.Epoch<1||state.PayloadSchema is <1 or >64||activation.MinimumSchema!=state.PayloadSchema||activation.MaximumSchema!=state.PayloadSchema||
            bootstrap.GetProperty("schema").GetInt32()!=1||bootstrap.GetProperty("service").GetString()!=InstallLocations.ServiceName||
            web.InstallationId!=state.InstallationId||web.Port!=state.Port||
            !Uri.TryCreate(web.Origin,UriKind.Absolute,out var origin)||origin.UserInfo!=""||origin.AbsolutePath!="/"||origin.Query!=""||origin.Fragment!=""||
            !(origin.Scheme=="http"&&origin.Host=="127.0.0.1"&&origin.Port==state.Port||origin.Scheme=="https"&&origin.Port==443&&NetworkPlan.IsPrivateLanV4(origin.Host))||
            activation.InstallationId!=state.InstallationId||activation.ReleaseDigest!=state.PayloadDigest||activation.DataId!=state.DataId||activation.Phase!="committed"||activation.Recovery is not null||
            activation.WebDigest!=Hash(snapshot.Web)||epoch.InstallationId!=state.InstallationId||epoch.Epoch!=activation.Epoch||epoch.ActivationDigest!=Hash(snapshot.Activation)||
            bootstrap.GetProperty("installationId").GetString()!=state.InstallationId||bootstrap.GetProperty("activationReceiptDigest").GetString()!=Hash(snapshot.Activation))
            throw new IOException("LAN_APP_CONFIGURATION_CHANGED");
    }
    internal static LanWebSnapshot WithOrigin(LanWebSnapshot before,InstallerState state,string origin)
    {
        Require(before,state);var web=Json(new InstalledWeb(1,state.InstallationId,origin,state.Port));
        var activation=Json(NativeJson.Read<InstalledActivation>(before.Activation) with{WebDigest=Hash(web)});
        var epoch=Json(NativeJson.Read<InstalledEpoch>(before.Epoch) with{ActivationDigest=Hash(activation)});
        var bootstrap=Json(new{schema=1,service=InstallLocations.ServiceName,installationId=state.InstallationId,activationReceiptDigest=Hash(activation)});
        var after=new LanWebSnapshot(web,activation,epoch,bootstrap);Require(after,state);return after;
    }
    internal static void RequireRecoverable(LanWebJournal journal,InstallerState state,Func<string,byte[]> read)
    {
        if(journal.Format!=1||journal.InstallationId!=state.InstallationId)throw new IOException("LAN_JOURNAL_CHANGED");
        Require(journal.Before,state);Require(journal.After,state);var before=Parts(journal.Before);var after=Parts(journal.After);
        for(var i=0;i<Names.Length;i++){var current=Hash(read(Names[i]));if(current!=Hash(before[i])&&current!=Hash(after[i]))throw new IOException("LAN_JOURNAL_CHANGED");}
    }
}

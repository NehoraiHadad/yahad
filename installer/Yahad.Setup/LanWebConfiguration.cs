using System.IO;
using System.Security.Cryptography;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Setup;

internal sealed record LanWebSnapshot(byte[] Installed);
internal sealed record LanWebJournal(int Format,string InstallationId,LanWebSnapshot Before,LanWebSnapshot After);
// No database, family, provider token or key is part of this transaction. The
// application service must be stopped. A pending journal is repaired only when
// the current launch file is one of its two exact, validated versions.
internal static class LanWebConfiguration
{
    internal static readonly string[] Names=[InstalledLaunch.FileName];
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static byte[][] Parts(LanWebSnapshot s)=>[s.Installed];
    internal static LanWebSnapshot Read(Func<string,byte[]> read)=>new(read(Names[0]));
    internal static void Require(LanWebSnapshot snapshot,InstallerState state)
    {
        InstalledLaunchConfiguration launch;
        try{launch=InstalledLaunch.Parse(snapshot.Installed);}catch(Exception error)when(error is IOException or System.Text.Json.JsonException){throw new IOException("LAN_APP_CONFIGURATION_CHANGED",error);}
        var origin=new Uri(launch.Origin);
        if(launch.InstallationId!=state.InstallationId||launch.DataId!=state.DataId||launch.Port!=state.Port||
            !(origin.Scheme=="http"||origin.Scheme=="https"&&origin.Port==443&&NetworkPlan.IsPrivateLanV4(origin.Host)))
            throw new IOException("LAN_APP_CONFIGURATION_CHANGED");
    }
    internal static LanWebSnapshot WithOrigin(LanWebSnapshot before,InstallerState state,string origin)
    {
        Require(before,state);var after=new LanWebSnapshot(InstalledLaunch.Encode(InstalledLaunch.Parse(before.Installed) with{Origin=origin}));
        Require(after,state);return after;
    }
    internal static void RequireRecoverable(LanWebJournal journal,InstallerState state,Func<string,byte[]> read)
    {
        if(journal.Format!=1||journal.InstallationId!=state.InstallationId)throw new IOException("LAN_JOURNAL_CHANGED");
        Require(journal.Before,state);Require(journal.After,state);var before=Parts(journal.Before);var after=Parts(journal.After);
        for(var i=0;i<Names.Length;i++){var current=Hash(read(Names[i]));if(current!=Hash(before[i])&&current!=Hash(after[i]))throw new IOException("LAN_JOURNAL_CHANGED");}
    }
}

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yahad.Install;

namespace Yahad.Setup;

// This is diagnostic output, never an instruction or an authority for removal.
// It contains no exception messages, file lists, payloads, SIDs or credentials.
internal sealed record UninstallDiagnosticRecord(int Format,string InstallationBinding,
    string Action,string Mode,string RecordedAtUtc,string SetupSourceCommit,string Code,
    string? Phase,int? NativeError,int HResult,string[]? Sites=null)
{
    internal static string Binding(InstallerState state)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state.InstallationId)));
    internal static UninstallDiagnosticRecord Create(InstallerState state,UninstallMode mode,Exception failure)
    {
        var detail=SetupFailureDetail.From(failure);
        return new(1,Binding(state),"uninstall",mode.ToString(),DateTimeOffset.UtcNow.ToString("O"),
            ApplicationRelease.CompiledSourceCommit(typeof(App).Assembly),SetupOperation.Code(failure),
            detail.Phase,detail.NativeError,detail.HResult,detail.Sites);
    }
    internal void RequireSameInstallation(InstallerState state)
    {
        if(Format!=1||Action!="uninstall"||InstallationBinding!=Binding(state))throw new IOException("UNINSTALL_DIAGNOSTIC_CHANGED");
    }
}

internal static class UninstallDiagnostics
{
    private static void RequireOwned(InstallerState state)
        =>NativeJson.Read<UninstallDiagnosticRecord>(NativeInstallFiles.ReadRecord(InstallLocations.RemovalDiagnosticFile,8192)).RequireSameInstallation(state);
    internal static bool TryRecord(InstallerState state,UninstallMode mode,Exception failure)
    {
        try
        {
            var bytes=JsonSerializer.SerializeToUtf8Bytes(UninstallDiagnosticRecord.Create(state,mode,failure));
            if(bytes.Length>8192)return false;
            if(File.Exists(InstallLocations.RemovalDiagnosticFile))RequireOwned(state);
            NativeInstallFiles.Publish(InstallLocations.RemovalDiagnosticFile,bytes);
            return true;
        }
        catch{return false;} // Reporting must never hide the original removal failure.
    }
    internal static void DeleteOwned(InstallerState state)
    {
        if(!File.Exists(InstallLocations.RemovalDiagnosticFile))return;
        RequireOwned(state);File.Delete(InstallLocations.RemovalDiagnosticFile);
    }
}

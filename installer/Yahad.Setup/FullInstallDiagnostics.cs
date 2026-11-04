using System.IO;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;
internal static class FullInstallDiagnostics
{
    private static string PathOf=>InstallLocations.Binaries+".full-install-diagnostic.json";
    internal static bool TryRecord(InstallerState state,SetupOperationException failure)
    {
        try
        {
            state.Require();
            if(File.Exists(PathOf))
            {using var json=JsonDocument.Parse(NativeInstallFiles.ReadRecord(PathOf,8192));if(json.RootElement.GetProperty("InstallationId").GetString()!=state.InstallationId)return false;}
            var bytes=Encode(state.InstallationId,failure,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            NativeInstallFiles.Publish(PathOf,bytes);return true;
        }
        catch{return false;}
    }
    internal static byte[] Encode(string installationId,SetupOperationException failure,long at)
    {
        var detail=SetupFailureDetail.From(failure);
        return JsonSerializer.SerializeToUtf8Bytes(new{Format=1,InstallationId=installationId,Stage=failure.Phase.ToString(),detail.NativeError,detail.HResult,Code=SetupOperation.Code(failure),detail.Sites,AtUnix=at});
    }
    internal static void DeleteOwned(InstallerState state)
    {
        if(!File.Exists(PathOf))return;
        {using var json=JsonDocument.Parse(NativeInstallFiles.ReadRecord(PathOf,8192));if(json.RootElement.GetProperty("InstallationId").GetString()!=state.InstallationId)throw new IOException("UNINSTALL_DIAGNOSTIC_CHANGED");}
        File.Delete(PathOf);
    }
}

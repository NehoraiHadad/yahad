using System.IO;
using System.Security.Principal;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;
internal static class FullInstallDiagnostics
{
    private static string PathOf=>InstallLocations.Binaries+".full-install-diagnostic.json";
    internal static bool TryRecord(FullInstallState state,SetupOperationException failure)
    {
        try
        {
            NativeFullSystemSetupHost.ValidateState(state);var owner=new SecurityIdentifier(state.OwnerSid);
            if(File.Exists(PathOf))
            {using var existing=WindowsFileBoundary.OpenInstallerDiagnostic(PathOf,owner);using var json=JsonDocument.Parse(existing.Small(8192));if(json.RootElement.GetProperty("InstallationId").GetString()!=state.InstallationId)return false;}
            var bytes=Encode(state.InstallationId,failure,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            NativeInstallFiles.PublishProtectedFile(PathOf,bytes,Descriptor(state.OwnerSid));return true;
        }
        catch{return false;}
    }
    internal static string Descriptor(string ownerSid)=>UninstallDiagnostics.Descriptor(ownerSid);
    internal static byte[] Encode(string installationId,SetupOperationException failure,long at)
    {
        var detail=SetupFailureDetail.From(failure);
        return JsonSerializer.SerializeToUtf8Bytes(new{Format=1,InstallationId=installationId,Stage=failure.Phase.ToString(),detail.NativeError,detail.HResult,Code=SetupElevation.Code(failure),AtUnix=at});
    }
    internal static void DeleteOwned(FullInstallState state)
    {
        if(!File.Exists(PathOf))return;using(var existing=WindowsFileBoundary.OpenInstallerDiagnostic(PathOf,new(state.OwnerSid)))
        {using var json=JsonDocument.Parse(existing.Small(8192));if(json.RootElement.GetProperty("InstallationId").GetString()!=state.InstallationId)throw new IOException("UNINSTALL_DIAGNOSTIC_CHANGED");}
        File.Delete(PathOf);
    }
}

using System.IO;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;
internal static class SetupHostFactory
{
    internal static ISetupHostOperations Create(InstallPackage package,bool uninstall,bool repairReceipt,UpdateSetupChoice? choice)
    {
        NativeInstallFiles.RequireAdministrator();
        if(repairReceipt)return new NativeSetupHost(package);
        if(uninstall)
        {
            var path=File.Exists(InstallLocations.RemovalStateFile)?InstallLocations.RemovalStateFile:InstallLocations.StateFile;
            using var pin=WindowsFileBoundary.OpenInstallerMetadata(path);using var json=JsonDocument.Parse(pin.Small(16777216));
            return json.RootElement.GetProperty("Format").GetInt32() switch
            {1=>new NativeSetupHost(package),3 or 4 or 5=>new NativeFullSystemSetupHost(package,null),_=>throw new IOException("INSTALL_STATE_INVALID")};
        }
        if(!package.FullSystem&&choice is not null)throw new IOException("UPDATE_FULL_PACKAGE_REQUIRED");
        return package.FullSystem?new NativeFullSystemSetupHost(package,choice):new NativeSetupHost(package);
    }
}

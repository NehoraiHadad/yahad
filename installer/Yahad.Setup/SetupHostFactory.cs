using System.IO;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;
internal static class SetupHostFactory
{
    internal static ISetupHostOperations Create(InstallPackage package,bool uninstall,UpdateSetupChoice? choice)
    {
        NativeInstallFiles.RequireAdministrator();
        if(uninstall)
        {
            var path=File.Exists(InstallLocations.RemovalStateFile)?InstallLocations.RemovalStateFile:InstallLocations.StateFile;
            using var pin=WindowsFileBoundary.OpenInstallerMetadata(path);using var json=JsonDocument.Parse(pin.Small(16777216));
            return json.RootElement.GetProperty("Format").GetInt32() switch
            {3 or 4 or 5=>new NativeFullSystemSetupHost(package,null),_=>throw new IOException("INSTALL_STATE_INVALID")};
        }
        if(!package.FullSystem)throw new IOException("UPDATE_FULL_PACKAGE_REQUIRED");
        return new NativeFullSystemSetupHost(package,choice);
    }
}

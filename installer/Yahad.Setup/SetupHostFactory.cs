using System.IO;
using Yahad.Install;
namespace Yahad.Setup;
internal static class SetupHostFactory
{
    internal static ISetupHostOperations Create(InstallPackage package,bool uninstall,UpdateSetupChoice? choice)
    {
        NativeInstallFiles.RequireAdministrator();
        // A retired four-service record (Formats 3-5) is refused before any
        // step, for install and removal alike; it is never adopted.
        foreach(var path in new[]{InstallLocations.RemovalStateFile,InstallLocations.StateFile})
        {
            if(!File.Exists(path))continue;
            if(path==InstallLocations.StateFile)InstallLocations.RequireStateRoot();
            NativeSetupHost.RequireCurrentFormat(NativeInstallFiles.ReadRecord(path));
        }
        if(uninstall&&!File.Exists(InstallLocations.RemovalStateFile)&&!File.Exists(InstallLocations.StateFile))throw new IOException("INSTALL_STATE_INVALID");
        return new NativeSetupHost(package,uninstall?null:choice);
    }
}

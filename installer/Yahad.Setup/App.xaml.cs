using System.Diagnostics;
using System.Windows;
namespace Yahad.Setup;
public partial class App:Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);ShutdownMode=ShutdownMode.OnExplicitShutdown;
        try
        {
            if(e.Args is ["--verify-package"])
            {
                using var package=InstallPackage.Embedded();var m=package.Manifest;
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{artifact="Yahad.Setup",version=m.Version,sourceCommit=m.SourceCommit,payload=m.PayloadDigest,files=m.Files.Length,embeddedZip=m.ZipSha256,service=InstallLocations.ServiceName,serviceInstallerImplemented=true,systemOperationsInvoked=false,machineAcceptance=false}));Shutdown(0);return;
            }
            if(e.Args is [var action,var nonce,var parent]&&action is "--elevated-install" or "--elevated-uninstall"&&int.TryParse(parent,out var pid))
            {var result=await SetupElevation.ElevatedAsync(action=="--elevated-uninstall",nonce,pid);Shutdown(result);return;}
            if(e.Args is ["--uninstall"])
            {
                var copy=SetupElevation.RelocateForUninstall();Process.Start(new ProcessStartInfo(copy){UseShellExecute=true,ArgumentList={"--uninstall-ui"}});Shutdown();return;
            }
            if(e.Args.Length!=0&&e.Args is not ["--uninstall-ui"]){Shutdown(2);return;}
            var window=new MainWindow(e.Args is ["--uninstall-ui"]);MainWindow=window;window.Closed+=(_,_)=>Shutdown();window.Show();
        }
        catch{Shutdown(1);}
    }
}

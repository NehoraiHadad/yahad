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
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{artifact="Yahad.Setup",version=m.Version,sourceCommit=m.SourceCommit,setupSourceCommit=Yahad.Install.ApplicationRelease.CompiledSourceCommit(typeof(App).Assembly),coreSourceCommit=Yahad.Install.ApplicationRelease.SourceCommit,payload=m.PayloadDigest,files=m.Files.Length,embeddedZip=m.ZipSha256,service=InstallLocations.ServiceName,serviceInstallerImplemented=true,systemOperationsInvoked=false,machineAcceptance=false}));Shutdown(0);return;
            }
            if(QuietSetup.Parse(e.Args) is {} quiet){Shutdown(await QuietSetup.RunAsync(quiet));return;}
            var uninstall=e.Args is ["--uninstall"];
            if(e.Args.Length!=0&&!uninstall){Shutdown(2);return;}
            if(uninstall&&SetupOperation.RunsFromInstallation()){SetupOperation.OpenRemoval();Shutdown();return;}
            var window=new MainWindow(uninstall);MainWindow=window;window.Closed+=(_,_)=>Shutdown();window.Show();
        }
        catch{Shutdown(1);}
    }
}

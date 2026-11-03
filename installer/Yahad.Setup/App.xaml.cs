using System.Windows;
namespace Yahad.Setup;
// The interactive window only; SetupEntry has already served every quiet mode.
public partial class App:Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);ShutdownMode=ShutdownMode.OnExplicitShutdown;
        try
        {
            var uninstall=e.Args is ["--uninstall"];
            if(e.Args.Length!=0&&!uninstall){Shutdown(2);return;}
            if(uninstall&&SetupOperation.RunsFromInstallation()){SetupOperation.OpenRemoval();Shutdown();return;}
            var window=new MainWindow(uninstall);MainWindow=window;window.Closed+=(_,_)=>Shutdown();window.Show();
        }
        catch{Shutdown(1);}
    }
}

using System.Windows;
namespace Yahad.Owner;
public partial class App:Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Production activation stays disabled. No endpoint is opened and no
        // window is instantiated by the current executable entry point.
        Shutdown(2);
    }
}

using System.Windows;
namespace Yahad.Owner;
public partial class App:Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args is ["--describe"])
        {
            var assembly=typeof(App).Assembly;
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{artifact="Yahad.Owner",version=assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion,applicationVersion=Yahad.Install.ApplicationRelease.Version,sourceCommit=Yahad.Install.ApplicationRelease.CompiledSourceCommit(assembly),nativeOwnerEntrypoint=true,machineAcceptance=false}));Shutdown(0);return;
        }
        Shutdown(await InstalledOwnerEntrypoint.RunAsync(this,e.Args));
    }
}

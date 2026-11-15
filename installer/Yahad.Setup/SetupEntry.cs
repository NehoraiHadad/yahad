using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;

// The process entry point. Every unattended mode, including the audit, runs and
// returns here; WPF (which needs a desktop) loads only when the window is asked
// for, so a Setup in session 0 never touches it. Only Window names a WPF type.
internal static class SetupEntry
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if(args is ["--describe"]){Describe();return 0;}
            if(args is ["--verify-package"]){VerifyPackage();return 0;}
            if(QuietSetup.Parse(args) is {} quiet)return QuietSetup.RunAsync(quiet,args).GetAwaiter().GetResult();
            // The removal window started from the installation runs from a private copy.
            if(args is ["--uninstall"]&&PrivateCopy.RemovalDeletes(Environment.ProcessPath??throw new IOException("SETUP_IMAGE")))return PrivateCopy.Run(args);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException){return 1;}
        return Window();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Window(){var app=new App();app.InitializeComponent();return app.Run();}
    internal static bool WindowingLoaded()=>AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name is "PresentationFramework" or "PresentationCore");
    // The engine's own identity. It opens no package, so it answers the same for the
    // payload-free engine in infra\<v>\setup and for the distributable Setup.
    private static void Describe()
    {
        var commit=InfrastructureRelease.CompiledSourceCommit(typeof(SetupEntry).Assembly);
        Console.WriteLine(JsonSerializer.Serialize(new{artifact="Yahad.Setup",infrastructureVersion=InfrastructureRelease.Version,contractMin=InfrastructureRelease.ContractMin,
            contractMax=InfrastructureRelease.ContractMax,sourceCommit=commit,version=InfrastructureRelease.Version+"+"+commit,embeddedPackage=InstallPackage.IsEmbedded}));
    }
    private static void VerifyPackage()
    {
        using var package=InstallPackage.Embedded();var m=package.Manifest;var key=TrustedReleaseKey.Embedded;
        Console.WriteLine(JsonSerializer.Serialize(new{artifact="Yahad.Setup",infrastructureVersion=m.Infrastructure.Version,infrastructureSourceCommit=m.Infrastructure.SourceCommit,
            appVersion=m.App.Version,appSourceCommit=m.App.SourceCommit,setupSourceCommit=InfrastructureRelease.CompiledSourceCommit(typeof(SetupEntry).Assembly),coreSourceCommit=InfrastructureRelease.SourceCommit,
            files=m.Files.Length,embeddedZip=m.ZipSha256,service=Yahad.Service.ServiceDefinition.Name,serviceInstallerImplemented=true,
            releaseKeyId=key is null?null:TrustedReleaseKey.Id(key),upgrades=key is null?"refused: built without a release key":"signed releases of this key",windowingLoaded=WindowingLoaded(),systemOperationsInvoked=false,machineAcceptance=false}));
    }
}

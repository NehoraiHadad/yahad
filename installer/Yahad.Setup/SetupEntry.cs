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
            if(args is ["--verify-package"]){VerifyPackage();return 0;}
            if(QuietSetup.Parse(args) is {} quiet)return QuietSetup.RunAsync(quiet).GetAwaiter().GetResult();
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException){return 1;}
        return Window();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Window(){var app=new App();app.InitializeComponent();return app.Run();}
    internal static bool WindowingLoaded()=>AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name is "PresentationFramework" or "PresentationCore");
    private static void VerifyPackage()
    {
        using var package=InstallPackage.Embedded();var m=package.Manifest;var key=TrustedReleaseKey.Embedded;
        Console.WriteLine(JsonSerializer.Serialize(new{artifact="Yahad.Setup",version=m.Version,sourceCommit=m.SourceCommit,setupSourceCommit=ApplicationRelease.CompiledSourceCommit(typeof(SetupEntry).Assembly),coreSourceCommit=ApplicationRelease.SourceCommit,payload=m.PayloadDigest,files=m.Files.Length,embeddedZip=m.ZipSha256,service=Yahad.Service.ServiceDefinition.Name,serviceInstallerImplemented=true,
            releaseKeyId=key is null?null:TrustedReleaseKey.Id(key),upgrades=key is null?"refused: built without a release key":"signed releases of this key",windowingLoaded=WindowingLoaded(),systemOperationsInvoked=false,machineAcceptance=false}));
    }
}

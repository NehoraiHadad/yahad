using System.Reflection;
using System.Text.Json;
using Yahad.Service;
using Yahad.Install;

if(args is ["--describe"])
{
    Console.WriteLine(JsonSerializer.Serialize(new{artifact="Yahad.ServiceHost",serviceName=ServiceDefinition.Name,
        version=Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        applicationVersion=ApplicationRelease.Version,sourceCommit=ApplicationRelease.SourceCommit,
        scmEntryImplemented=true,canInstallService=false,launchConfiguration=InstalledLaunch.FileName,
        familyInstallationReady=false,releaseReady=false}));return 0;
}
if(args is ["--lan-service"])
{
    try{return new LanProxyServiceApplication(new NativeScmApi(),Environment.Exit).Run();}
    catch{Console.Error.WriteLine("YAHAD_LAN_DISPATCH_REFUSED");return 1;}
}
if(args is not ["--service"]){Console.Error.WriteLine("YAHAD_SERVICE_ARGUMENTS_REFUSED");return 2;}
try
{
    return InstalledLaunch.Run();
}
catch(Exception error){_=ServiceStartupDiagnostic.TryRecord(StartupDiagnosticRole.Runtime,error);Console.Error.WriteLine("YAHAD_SCM_DISPATCH_REFUSED");return 1;}

using System.Reflection;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
using Yahad.Updater;

if(args is ["--describe"])
{
    Console.WriteLine(JsonSerializer.Serialize(new{artifact="Yahad.Updater",serviceName=ServiceDefinition.UpdaterName,
        version=Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        applicationVersion=ApplicationRelease.Version,sourceCommit=ApplicationRelease.SourceCommit}));return 0;
}
if(args is not ["--service"]){Console.Error.WriteLine("YAHAD_SERVICE_ARGUMENTS_REFUSED");return 2;}
try{return new UpdaterService(new NativeScmApi(),UpdaterService.WorkAsync).Run();}
catch(Exception error){_=ServiceStartupDiagnostic.TryRecord(StartupDiagnosticRole.Updater,error);Console.Error.WriteLine("YAHAD_SCM_DISPATCH_REFUSED");return 1;}

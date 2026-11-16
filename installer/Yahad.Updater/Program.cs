using System.Reflection;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
using Yahad.Updater;

if(args is ["--describe"])
{
    Console.WriteLine(JsonSerializer.Serialize(new UpdaterDescription("Yahad.Updater",ServiceDefinition.UpdaterName,
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        InfrastructureRelease.Version,InfrastructureRelease.ContractMin,InfrastructureRelease.ContractMax,InfrastructureRelease.SourceCommit,InfrastructureRelease.RuntimeVersion),
        UpdaterDescriptionJsonContext.Default.UpdaterDescription));return 0;
}
if(args is not ["--service"]){Console.Error.WriteLine("YAHAD_SERVICE_ARGUMENTS_REFUSED");return 2;}
try{return new UpdaterService(new NativeScmApi(),UpdaterService.WorkAsync).Run();}
catch(Exception error){_=ServiceStartupDiagnostic.TryRecord(StartupDiagnosticRole.Updater,error);Console.Error.WriteLine("YAHAD_SCM_DISPATCH_REFUSED");return 1;}

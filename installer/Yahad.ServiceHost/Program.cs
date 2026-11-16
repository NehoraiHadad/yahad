using System.Reflection;
using System.Text.Json;
using Yahad.Service;
using Yahad.Install;

if(args is ["--describe"])
{
    Console.WriteLine(JsonSerializer.Serialize(new ServiceHostDescription("Yahad.ServiceHost",ServiceDefinition.Name,
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        InfrastructureRelease.Version,InfrastructureRelease.ContractMin,InfrastructureRelease.ContractMax,InfrastructureRelease.SourceCommit,
        ScmEntryImplemented:true,CanInstallService:false,LaunchConfiguration:InstalledLaunch.FileName,
        FamilyInstallationReady:false,ReleaseReady:false),ServiceHostDescriptionJsonContext.Default.ServiceHostDescription));return 0;
}
if(args is ["--lan-service"])
{
    try{return ScmController.Dispatch(new NativeScmApi(),LanProxyPaths.ServiceName,Environment.Exit,()=>new LanProxyServiceController(new WindowsLanProxyAdmission()));}
    catch{Console.Error.WriteLine("YAHAD_LAN_DISPATCH_REFUSED");return 1;}
}
if(args is not ["--service"]){Console.Error.WriteLine("YAHAD_SERVICE_ARGUMENTS_REFUSED");return 2;}
try
{
    return InstalledLaunch.Run();
}
catch(Exception error){_=ServiceStartupDiagnostic.TryRecord(StartupDiagnosticRole.Runtime,error);Console.Error.WriteLine("YAHAD_SCM_DISPATCH_REFUSED");return 1;}

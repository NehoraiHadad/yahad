using System.Reflection;
using System.Text.Json;
using Yahad.Service;

if(args is ["--describe"])
{
    Console.WriteLine(JsonSerializer.Serialize(new{artifact="Yahad.ServiceHost",serviceName=ServiceDefinition.Name,
        version=Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        scmEntryImplemented=true,canInstallService=false,activationAdapterImplemented=true,activationAuthorityConfigured=false,
        familyInstallationReady=false,releaseReady=false}));return 0;
}
if(args is not ["--service"]){Console.Error.WriteLine("YAHAD_SERVICE_ARGUMENTS_REFUSED");return 2;}
try
{
    var authorizer=new InstalledStartAuthorizer(new WindowsServiceTokenReader(),new WindowsInstallationReader(),new InstalledActivationResolver());
    return new ServiceApplication(new NativeScmApi(),authorizer,Environment.Exit).Run();
}
catch{Console.Error.WriteLine("YAHAD_SCM_DISPATCH_REFUSED");return 1;}

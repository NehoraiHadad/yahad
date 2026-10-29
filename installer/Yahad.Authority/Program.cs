using System.Text.Json;
if(args is ["--describe"])
{Console.WriteLine(JsonSerializer.Serialize(new{artifact="Yahad.Authority",version=System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion,adaptersCompiled=true,productionEnabled=false,canInstallService=false,createsPipeListener=false,issuesProductionCapabilities=false}));return 0;}
Console.Error.WriteLine("YAHAD_AUTHORITY_PRODUCTION_UNCONFIGURED");return 2;

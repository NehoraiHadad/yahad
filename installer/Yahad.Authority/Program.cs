using System.Text.Json;
if(args is ["--describe"])
{Console.WriteLine(JsonSerializer.Serialize(new{artifact="Yahad.Authority",version=System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion,applicationVersion=Yahad.Install.ApplicationRelease.Version,sourceCommit=Yahad.Install.ApplicationRelease.SourceCommit,adaptersCompiled=true,productionEnabled=true,canInstallService=false,createsPipeListener=false,issuesProductionCapabilities=false,machineAcceptance=false}));return 0;}
try{return Yahad.Authority.InstalledAuthorityEntrypoint.Run(args);}
catch(Exception error){Yahad.Install.ServiceStartupDiagnostic.TryRecord(Yahad.Install.StartupDiagnosticRole.Dispatch,error);Console.Error.WriteLine("YAHAD_AUTHORITY_DISPATCH_REFUSED");return 1;}

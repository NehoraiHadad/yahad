using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;

var failed=0;var results=new List<object>();
if(args is ["--own-token-observation-only"])
{
    // Read this test process only. No DACL writes, services or installed-data reads.
    try
    {
        using var session=new WindowsInstalledProcessObjectKernel().OpenCurrent(false);
        var observed=session.ObserveIdentity();
        using var control=System.Security.Principal.WindowsIdentity.GetCurrent();
        var expected=new System.Security.Principal.WindowsPrincipal(control).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        if(observed.Administrator!=expected||observed.CurrentPid!=Environment.ProcessId||observed.UserSid!=control.User?.Value)
            throw new IOException("OWN_TOKEN_OBSERVATION_MISMATCH");
        Console.WriteLine(JsonSerializer.Serialize(new{suite="own-token-observation-readonly",passed=1,failed=0,permissionsChanged=false,realServices=false}));return 0;
    }
    catch(Exception error)
    {Console.WriteLine(JsonSerializer.Serialize(new{suite="own-token-observation-readonly",passed=0,failed=1,type=error.GetType().Name,hresult=error.HResult,permissionsChanged=false,realServices=false}));return 1;}
}
if(args is ["--updater-identities-only"])
{
    await UpdaterServiceIdentityTests.Run(Extra);await ProcessObjectAccessTests.Run(Extra);
    Console.WriteLine(JsonSerializer.Serialize(new{suite="updater-identities-synthetic",passed=results.Count-failed,failed,realServices=false,realPermissions=false}));return failed==0?0:1;
}
await ServiceStartupDiagnosticsTests.Run(async(name,test)=>
{
    try{await test();Console.WriteLine("PASS "+name);results.Add(new{name,status="PASS"});}
    catch(Exception error){failed++;Console.WriteLine("FAIL "+name+" "+error);results.Add(new{name,status="FAIL",reason=error.Message});}
});
if(args is ["--startup-observation-only"])
{
    Console.WriteLine(JsonSerializer.Serialize(new{suite="startup-observation-synthetic",passed=results.Count-failed,failed,realServices=false}));
    return failed==0?0:1;
}

async Task Extra(string name,Func<Task> test)
{
    try{await test();Console.WriteLine("PASS "+name);results.Add(new{name,status="PASS"});}
    catch(Exception error){failed++;Console.WriteLine("FAIL "+name+" "+error);results.Add(new{name,status="FAIL",reason=error.Message});}
}
await RuntimeAdmissionTests.Run(Extra);
await ProcessObjectAccessTests.Run(Extra);
await UpdaterServiceIdentityTests.Run(Extra);
await OperationalProbeTests.Run(Extra);
await SystemCandidateIntakeTests.Run(Extra);
await FullSystemModeRouterTests.Run(Extra);
await SystemBootstrapHandoffTests.Run(Extra);
await HelperBaselineIntegrationTests.Run(Extra);
await InstallerReadinessTests.Run(Extra);
await Yahad.Production.Tests.FullSystemPolicyTests.Run(Extra);
await Yahad.Production.Tests.FullSystemCoordinatorTests.Run(Extra);
await Yahad.Production.Tests.FullSystemNativeBoundaryTests.Run(Extra);
await Yahad.Production.Tests.UpdateJournalTests.Run(Extra);
await ProbationRuntimeTests.Run(Extra);
await InstalledUpdaterLayoutTests.Run(Extra);
await UpdateSchedulerTests.Run(Extra);
await RecoveredUpdateScheduleTests.Run(Extra);
await NativeCandidateIntakeTests.Run(Extra);
await NativeIntakeRecoveryTests.Run(Extra);
await NativeIntakeMetadataTests.Run(Extra);
await Yahad.Production.Tests.InstalledRecoveryTests.Run(Extra);
if(args is ["--node",var nativeNode])
{
    var nodePath=Path.GetFullPath(nativeNode);using var node=File.OpenRead(nodePath);var nodeDigest=Convert.ToHexStringLower(SHA256.HashData(node));
    await Yahad.Production.Tests.InstalledRecoveryTests.RunNative(Extra,nodePath,nodeDigest);
}
var report=new{suite="windows-updater-policy",passed=results.Count-failed,failed,privateSyntheticOnly=true,installedServicesTested=false,networkRequests=false,actualAppUpdates=false,results};
Console.WriteLine(JsonSerializer.Serialize(report));return failed==0?0:1;


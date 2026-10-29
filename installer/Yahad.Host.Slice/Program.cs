using Yahad.Install;
using System.Text.Json;

// This explicit development mode never installs a service and always uses mock WhatsApp.
if(args is ["--fixture-app",var payloadRoot,var manifestFile,var settingsFile])
{
    try
    {
        using var settingsLease=WindowsFileBoundary.OpenSecret(settingsFile,SecretRole.MasterKey);
        var settings=JsonSerializer.Deserialize<NativeFixtureSettings>(settingsLease.Read())??throw new IOException("FIXTURE_SETTINGS");
        var parent=Path.GetDirectoryName(Path.GetFullPath(settingsFile))!+Path.DirectorySeparatorChar;
        foreach(var path in new[]{settings.TemporaryDirectory,settings.DataDirectory,settings.MasterKeyPath})
            if(!Path.GetFullPath(path).StartsWith(parent,StringComparison.OrdinalIgnoreCase))throw new IOException("FIXTURE_DATA_SCOPE");
        using var layout=DeveloperRuntimeLayout.Open(payloadRoot,manifestFile);
        var launcher=new NativeFixtureLauncher(layout,settings);var coordinator=new FixtureCoordinator();
        await using var lifecycle=new AppLifecycle(new("fixture"),launcher,coordinator,coordinator,TimeSpan.FromSeconds(35));
        await lifecycle.StartAsync();Console.WriteLine("{\"ready\":true}");
        var command=await Console.In.ReadLineAsync();
        if(command is not null && command!="stop")throw new IOException("FIXTURE_CONTROL");
        await lifecycle.StopAsync();Console.WriteLine("{\"stopped\":true}");return 0;
    }
    catch{Console.Error.WriteLine("NATIVE_FIXTURE_FAILED");return 1;}
}
// Deliberately cannot register a service or accept a production release. Production
// hosting must first gain ACL validation, graceful shutdown IPC and recovery gates.
if (args is ["--fixture-child", var fixtureDirectory])
{
    var exe = Environment.ProcessPath!; var directory = Path.GetFullPath(fixtureDirectory);
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
    using var job = JobProcess.Start(exe, ["--fixture-wait"], directory);
    try { return await job.WaitAsync(cancel.Token); }
    catch (OperationCanceledException) { return 0; }
}
if (args is ["--fixture-wait"]) { await Task.Delay(Timeout.Infinite); return 0; }
Console.Error.WriteLine("Native containment feasibility host. No service is installed. Use the test harness for synthetic execution.");
return 2;

internal sealed class FixtureCoordinator : IAppRecoveryBarrier,IAppUpdateAuthority
{
    public Task RequireStartAllowedAsync(AppRelease release,CancellationToken cancellation)=>Task.CompletedTask;
    public Task<AppRelease> ActivateAsync(AppRelease requested,QuiescentApp grant,CancellationToken cancellation)=>throw new IOException("FIXTURE_UPDATE_DISABLED");
}

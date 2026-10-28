using Yahad.Install;

// Deliberately cannot register a service or launch an app payload yet. Production
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

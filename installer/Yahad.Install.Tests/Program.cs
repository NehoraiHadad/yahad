using System.Diagnostics;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Yahad.Install;

if (args is ["--wait"]) { await Task.Delay(Timeout.Infinite); return 0; }
if (args is ["--exit", var requestedCode]) return int.Parse(requestedCode);
if (args is ["--hold-lock", var heldLockPath, var lockReadyPath])
{
    using var held = new FileStream(heldLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    File.WriteAllText(lockReadyPath, "ready");
    await Task.Delay(Timeout.Infinite); return 0;
}
if (args.Length > 1 && args[0] == "--echo")
{ File.WriteAllText(args[1], JsonSerializer.Serialize(args.Skip(2).ToArray())); return 0; }
if (args is ["--tree", var pidPath])
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("--wait");
    using var child = Process.Start(start)!;
    File.WriteAllText(pidPath, child.Id.ToString());
    await Task.Delay(Timeout.Infinite); return 0;
}
if (args is ["--orphan-tree", var orphanPath])
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("--wait"); using var child = Process.Start(start)!;
    File.WriteAllText(orphanPath, child.Id.ToString()); return 0;
}
if (args is ["--job-owner", var ownerPidPath])
{
    using var child = JobProcess.Start(Environment.ProcessPath!, ["--tree", ownerPidPath], AppContext.BaseDirectory);
    File.WriteAllText(ownerPidPath + ".root", child.ProcessId.ToString());
    await Task.Delay(Timeout.Infinite); return 0;
}

var root = SafeDirectory.CreatePrivateSession(Path.GetTempPath());
var valid = Path.Combine(root, "good.zip"); FixtureBundle.Create(valid);
var results = new List<object>(); var failed = 0;
async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); results.Add(new { name, status = "PASS" }); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + " " + e.GetType().Name + " " + e.Message); results.Add(new { name, status = "FAIL", reason = e.GetType().Name }); }
}
void Assert(bool value) { if (!value) throw new Exception("assertion failed"); }
async Task Reject(Func<Task> action)
{ try { await action(); } catch (IOException) { return; } catch (InvalidDataException) { return; } throw new Exception("expected rejection"); }
string Session() => SafeDirectory.CreatePrivateSession(root);
void Package(string path, BundleManifest m, params (string Name, byte[] Bytes, int? Attributes)[] files)
{
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    using (var stream = zip.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(stream, m);
    foreach (var f in files) { var e = zip.CreateEntry(f.Name); if (f.Attributes is not null) e.ExternalAttributes = f.Attributes.Value;
        using var stream = e.Open(); stream.Write(f.Bytes); }
}
var bytes = Encoding.UTF8.GetBytes("fixture"); var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
BundleManifest Manifest(string name = "app/file.txt") => new(1, "feasibility-fixture", "win-x64", "fixture-1", [new(name, bytes.Length, hash)]);
await Check("valid manifest and bytes", () => { Assert(Bundle.Inspect(valid).Manifest.Files.Length == 1); return Task.CompletedTask; });
await Check("malformed JSON is a package diagnostic", async () =>
{
    var p = Path.Combine(root, "bad-manifest.zip");
    using (var zip = ZipFile.Open(p, ZipArchiveMode.Create))
    { using var stream = zip.CreateEntry("manifest.json").Open(); stream.Write(Encoding.UTF8.GetBytes("{broken")); }
    await Reject(() => { Bundle.Inspect(p); return Task.CompletedTask; });
});
foreach (var name in new[] { "../escape", "app/../../escape", "app/file:ads", "app/CON.txt", "app/LPT1", "app/folder./file", "app//file", "app/./file", "app/evil\\file", "/app/root", "data/family.sqlite" })
    await Check("reject unsafe path " + name, async () => { var p = Path.Combine(root, Guid.NewGuid() + ".zip"); Package(p, Manifest(name), (name, bytes, null)); await Reject(() => { Bundle.Inspect(p); return Task.CompletedTask; }); });
await Check("reject corrupt bytes", async () => { var p = Path.Combine(root, "corrupt.zip"); Package(p, Manifest(), ("app/file.txt", Encoding.UTF8.GetBytes("changed"), null)); await Reject(() => { Bundle.Inspect(p); return Task.CompletedTask; }); });
await Check("reject case collision", async () => { var p = Path.Combine(root, "duplicate.zip"); Package(p, Manifest(), ("app/file.txt", bytes, null), ("APP/FILE.TXT", bytes, null)); await Reject(() => { Bundle.Inspect(p); return Task.CompletedTask; }); });
await Check("reject symlink entry", async () => { var p = Path.Combine(root, "link.zip"); Package(p, Manifest(), ("app/file.txt", bytes, unchecked((int)0xA1FF0000))); await Reject(() => { Bundle.Inspect(p); return Task.CompletedTask; }); });
await Check("reject undeclared file", async () => { var p = Path.Combine(root, "extra.zip"); Package(p, Manifest(), ("app/file.txt", bytes, null), ("app/extra", bytes, null)); await Reject(() => { Bundle.Inspect(p); return Task.CompletedTask; }); });
await Check("refuse production payload in slice", async () => { var p = Path.Combine(root, "production.zip"); Package(p, Manifest() with { Kind = "production" }, ("app/file.txt", bytes, null)); await Reject(() => { Bundle.Inspect(p); return Task.CompletedTask; }); });
await Check("refuse unsupported architecture", async () => { var p = Path.Combine(root, "arm64.zip"); Package(p, Manifest() with { Target = "win-arm64" }, ("app/file.txt", bytes, null)); await Reject(() => { Bundle.Inspect(p); return Task.CompletedTask; }); });
await Check("stage and verify repeated run", async () => { var s = Session(); await FixtureInstaller.StageAsync(valid, s, default); var payload = Path.Combine(s, "payload/app/fixture.txt"); var before = File.ReadAllBytes(payload); await FixtureInstaller.StageAsync(valid, s, default); Assert(before.SequenceEqual(File.ReadAllBytes(payload))); });
await Check("cancel and resume preserves state", async () =>
{
    var s = Session(); using var cancel = new CancellationTokenSource();
    try { await FixtureInstaller.StageAsync(valid, s, cancel.Token, fault: point => { if (point == "after-journal") { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); } }); throw new Exception("not canceled"); }
    catch (OperationCanceledException) { }
    Assert(!Directory.Exists(Path.Combine(s, "payload")));
    Assert(JsonSerializer.Deserialize<Journal>(File.ReadAllText(Path.Combine(s, "journal.json")))!.Stage == Stage.Paused);
    await FixtureInstaller.StageAsync(valid, s, default); Assert(Directory.Exists(Path.Combine(s, "payload")));
});
foreach (var faultPoint in new[] { "after-journal", "before-publish", "after-publish" })
    await Check("resume IO interruption " + faultPoint, async () =>
    {
        var s = Session(); await Reject(() => FixtureInstaller.StageAsync(valid, s, default, fault: p => { if (p == faultPoint) throw new IOException("injected disk/lock failure"); }));
        Assert(File.Exists(Path.Combine(s, "journal.json")));
        await FixtureInstaller.StageAsync(valid, s, default);
        Assert(JsonSerializer.Deserialize<Journal>(File.ReadAllText(Path.Combine(s, "journal.json")))!.Stage == Stage.Ready);
    });
await Check("never replace changed published payload", async () =>
{
    var s = Session(); await FixtureInstaller.StageAsync(valid, s, default);
    var p = Path.Combine(s, "payload/app/fixture.txt"); File.WriteAllText(p, "preserve this changed fixture");
    await Reject(() => FixtureInstaller.StageAsync(valid, s, default)); Assert(File.ReadAllText(p) == "preserve this changed fixture");
});
await Check("refuse bundle change during resume", async () =>
{
    var s = Session(); await FixtureInstaller.StageAsync(valid, s, default);
    var journal = File.ReadAllBytes(Path.Combine(s, "journal.json"));
    var p = Path.Combine(root, "different.zip"); Package(p, Manifest(), ("app/file.txt", bytes, null));
    await Reject(() => FixtureInstaller.StageAsync(p, s, default)); Assert(journal.SequenceEqual(File.ReadAllBytes(Path.Combine(s, "journal.json"))));
});
await Check("exclusive lock held by a separate process", async () =>
{
    var s = Session(); var readyPath = Path.Combine(s, "lock-ready");
    using var held = JobProcess.Start(Environment.ProcessPath!, ["--hold-lock", Path.Combine(s, "operation.lock"), readyPath], root);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!File.Exists(readyPath)) await Task.Delay(25, deadline.Token);
    await Reject(() => FixtureInstaller.StageAsync(valid, s, default)); Assert(!Directory.Exists(Path.Combine(s, "payload")));
});
await Check("refuse permissive session ACL", async () =>
{
    var s = Session(); var dir = new DirectoryInfo(s); var acl = dir.GetAccessControl();
    acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow)); dir.SetAccessControl(acl);
    await Reject(() => FixtureInstaller.StageAsync(valid, s, default));
});
await Check("refuse junction ancestor", async () =>
{
    var target = Session(); var junction = Path.Combine(root, "junction");
    var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in new[] { "/c", "mklink", "/J", junction, target }) start.ArgumentList.Add(a);
    using var p = Process.Start(start)!; await p.WaitForExitAsync(); Assert(p.ExitCode == 0);
    await Reject(() => FixtureInstaller.StageAsync(valid, junction, default));
});
async Task WaitFor(string path)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!File.Exists(path) || new FileInfo(path).Length == 0) await Task.Delay(25, deadline.Token);
}
bool Alive(int id) { try { using var p = Process.GetProcessById(id); return !p.HasExited; } catch (ArgumentException) { return false; } }
async Task Gone(params int[] ids)
{ using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (ids.Any(Alive)) await Task.Delay(25, deadline.Token); }
await Check("native argument quoting without shell", async () =>
{
    var output = Path.Combine(root, "quoted args.json"); var expected = new[] { "space name", "quote\"inside", @"C:\a b\", "שלום", "", @"\\\" };
    using var job = JobProcess.Start(Environment.ProcessPath!, new[] { "--echo", output }.Concat(expected), root);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await job.WaitAsync(deadline.Token);
    Assert(JsonSerializer.Deserialize<string[]>(File.ReadAllText(output))!.SequenceEqual(expected));
});
await Check("retain exact process exit status after early exit", async () =>
{
    using var job = JobProcess.Start(Environment.ProcessPath!, ["--exit", "19"], root);
    await Task.Delay(300);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    Assert(await job.WaitAsync(deadline.Token) == 19);
});
await Check("job disposal kills child and grandchild", async () =>
{
    var output = Path.Combine(root, "grandchild.pid"); var job = JobProcess.Start(Environment.ProcessPath!, ["--tree", output], root);
    try { await WaitFor(output); var grandchild = int.Parse(File.ReadAllText(output)); Assert(Alive(job.ProcessId) && Alive(grandchild)); job.Dispose(); await Gone(job.ProcessId, grandchild); }
    finally { job.Dispose(); }
});
await Check("root exit cannot claim empty native process tree", async () =>
{
    var output = Path.Combine(root, "orphan-tree.pid");
    using var job = JobProcess.Start(Environment.ProcessPath!, ["--orphan-tree", output], root);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    Assert(await job.WaitAsync(deadline.Token) == 0); await WaitFor(output);
    var descendant = int.Parse(File.ReadAllText(output)); Assert(Alive(descendant));
    using var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
    try { await job.WaitForTreeEmptyAsync(shortWait.Token); throw new Exception("living descendant ignored"); }
    catch (OperationCanceledException) { }
    using var child = Process.GetProcessById(descendant); child.Kill(); await child.WaitForExitAsync();
    await job.WaitForTreeEmptyAsync(deadline.Token);
});
await Check("host crash closes job and kills descendants", async () =>
{
    var output = Path.Combine(root, "crash-grandchild.pid");
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("--job-owner"); start.ArgumentList.Add(output);
    using var owner = Process.Start(start)!;
    try { await WaitFor(output); await WaitFor(output + ".root"); var ids = new[] { int.Parse(File.ReadAllText(output)), int.Parse(File.ReadAllText(output + ".root")) }; owner.Kill(); await owner.WaitForExitAsync(); await Gone(ids); }
    finally { if (!owner.HasExited) owner.Kill(); }
});
await Check("private LAN address boundaries", () =>
{
    foreach (var address in new[] { "10.1.2.3", "172.16.0.1", "172.31.255.254", "192.168.4.5" }) Assert(NetworkPlan.IsPrivateLanV4(address));
    foreach (var address in new[] { "127.0.0.1", "0.0.0.0", "169.254.1.2", "172.15.1.2", "172.32.1.2", "8.8.8.8", "224.0.0.1", "::1", "3232235777", "192.168.001.1" }) Assert(!NetworkPlan.IsPrivateLanV4(address));
    return Task.CompletedTask;
});
await Check("multiple adapters require explicit current selection", async () =>
{
    var choices = NetworkPlan.Candidates([new("ethernet", "Home", "192.168.4.10", true), new("vpn", "Work", "10.20.1.2", true), new("off", "Offline", "10.1.1.1", false), new("public", "Public", "8.8.8.8", true)]);
    Assert(choices.Count == 2); Assert(NetworkPlan.LanOrigin(choices, "ethernet", "192.168.4.10") == "https://192.168.4.10");
    await Reject(() => { NetworkPlan.LanOrigin(choices, "ethernet", "192.168.4.11"); return Task.CompletedTask; });
});
await Check("occupied loopback port is a recoverable conflict", () =>
{
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Server.ExclusiveAddressUse = true; listener.Start();
    try { var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; Assert(!NetworkPlan.CanBindLoopback(port)); }
    finally { listener.Stop(); }
    return Task.CompletedTask;
});
await NativeBoundaryTests.Run(root, Check);
await InstallerPlanningTests.Run(Check);
await AppLifecycleTests.Run(Check);
var report = JsonSerializer.Serialize(new { platform = "Windows x64", kind = "synthetic-nonprivileged", passed = results.Count - failed, failed, tests = results }, new JsonSerializerOptions { WriteIndented = true });
if (args is ["--report", var reportPath]) File.WriteAllText(reportPath, report);
Console.WriteLine($"{results.Count - failed} passed; {failed} failed. Synthetic fixtures only. Session: {root}");
return failed == 0 ? 0 : 1;

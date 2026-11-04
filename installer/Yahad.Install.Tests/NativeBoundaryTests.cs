using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Yahad.Install;

internal static class NativeBoundaryTests
{
    public static async Task Run(string parent, Func<string, Func<Task>, Task> check)
    {
        void Assert(bool value) { if (!value) throw new Exception("assertion failed"); }
        void Reject(Action action)
        { try { action(); } catch (IOException) { return; } catch (Win32Exception) { return; } throw new Exception("expected refusal"); }
        string FileFixture(out string root)
        {
            root = PrivateSession.Create(parent); var file = Path.Combine(root, "master-key");
            File.WriteAllText(file, "synthetic-private-file"); return file;
        }
        await check("native handle reads private bytes", () =>
        { var path = FileFixture(out _); using var lease = WindowsFileBoundary.OpenRead(path); Assert(Encoding.UTF8.GetString(lease.Small(4096)) == "synthetic-private-file"); return Task.CompletedTask; });
        await check("native private handle supports canonical paths beyond MAX_PATH", () =>
        {
            var root=PrivateSession.Create(parent);var directory=root;
            while(directory.Length<300)directory=Path.Combine(directory,"long-component");
            Directory.CreateDirectory(directory);var path=Path.Combine(directory,"master-key");File.WriteAllText(path,"long-private-fixture");
            using var file=WindowsFileBoundary.OpenRead(path);
            Assert(Encoding.UTF8.GetString(file.Small(4096))=="long-private-fixture");return Task.CompletedTask;
        });
        await check("native pinned ancestors and leaf refuse rename", () =>
        {
            var path = FileFixture(out var root); using var lease = WindowsFileBoundary.OpenRead(path);
            Reject(() => File.Move(path, path + ".moved")); Reject(() => Directory.Move(root, root + ".moved"));
            Assert(lease.Small(4096).Length > 0); return Task.CompletedTask;
        });
        await check("native handle rejects hardlink alias", () =>
        {
            var path = FileFixture(out var root); Assert(CreateHardLinkW(Path.Combine(root, "alias"), path, IntPtr.Zero));
            Reject(() => { using var lease = WindowsFileBoundary.OpenRead(path); }); return Task.CompletedTask;
        });
        await check("native handle bounds secret size", () =>
        { var path = FileFixture(out _); File.WriteAllBytes(path, new byte[4097]); Reject(() => { using var lease = WindowsFileBoundary.OpenRead(path); lease.Small(4096); }); return Task.CompletedTask; });
        await check("native data lease excludes and releases", () =>
        {
            var root = PrivateSession.Create(parent);
            using (var first = WindowsFileBoundary.AcquireDataLease(root)) Reject(() => { using var second = WindowsFileBoundary.AcquireDataLease(root); });
            using var next = WindowsFileBoundary.AcquireDataLease(root); return Task.CompletedTask;
        });
        foreach (var path in new[] { @"\\server\share\secret", @"C:\one\..\secret", @"C:\path\secret." })
            await check("native path refuses " + path, () => { Reject(() => WindowsFileBoundary.CanonicalLocalPath(path)); return Task.CompletedTask; });
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string path, string existing, IntPtr security);
}

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
            root = SafeDirectory.CreatePrivateSession(parent); var file = Path.Combine(root, "master-key");
            File.WriteAllText(file, "synthetic-private-file"); return file;
        }
        await check("native handle reads private bytes", () =>
        { var path = FileFixture(out _); using var lease = WindowsFileBoundary.OpenSecret(path, SecretRole.MasterKey); Assert(Encoding.UTF8.GetString(lease.Read()) == "synthetic-private-file"); return Task.CompletedTask; });
        await check("native private handle supports canonical paths beyond MAX_PATH", () =>
        {
            var root=SafeDirectory.CreatePrivateSession(parent);var directory=root;
            while(directory.Length<300)directory=Path.Combine(directory,"long-component");
            Directory.CreateDirectory(directory);var path=Path.Combine(directory,"master-key");File.WriteAllText(path,"long-private-fixture");
            using var file=WindowsFileBoundary.OpenSecret(path,SecretRole.MasterKey);
            Assert(Encoding.UTF8.GetString(file.Read())=="long-private-fixture");return Task.CompletedTask;
        });
        await check("native pinned ancestors and leaf refuse rename", () =>
        {
            var path = FileFixture(out var root); using var lease = WindowsFileBoundary.OpenSecret(path, SecretRole.MasterKey);
            Reject(() => File.Move(path, path + ".moved")); Reject(() => Directory.Move(root, root + ".moved"));
            Assert(lease.Read().Length > 0); return Task.CompletedTask;
        });
        await check("native handle refuses broad read ACL", () =>
        {
            var path = FileFixture(out _); var info = new FileInfo(path); var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow)); info.SetAccessControl(acl);
            Reject(() => { using var lease = WindowsFileBoundary.OpenSecret(path, SecretRole.MasterKey); }); return Task.CompletedTask;
        });
        await check("native bootstrap refuses application-owned capability", () =>
        { var path = FileFixture(out _); Reject(() => { using var lease = WindowsFileBoundary.OpenSecret(path, SecretRole.SetupCapability); }); return Task.CompletedTask; });
        await check("native bootstrap ACL policy accepts admin owner and read-only service", () =>
        {
            var app = WindowsIdentity.GetCurrent().User!;
            WindowsFileBoundary.ValidateAcl(new RawSecurityDescriptor($"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;{app.Value})"), app, true);
            Reject(() => WindowsFileBoundary.ValidateAcl(new RawSecurityDescriptor($"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;{app.Value})"), app, true));
            return Task.CompletedTask;
        });
        await check("native handle rejects hardlink alias", () =>
        {
            var path = FileFixture(out var root); Assert(CreateHardLinkW(Path.Combine(root, "alias"), path, IntPtr.Zero));
            Reject(() => { using var lease = WindowsFileBoundary.OpenSecret(path, SecretRole.MasterKey); }); return Task.CompletedTask;
        });
        await check("native handle bounds secret size", () =>
        { var path = FileFixture(out _); File.WriteAllBytes(path, new byte[4097]); Reject(() => { using var lease = WindowsFileBoundary.OpenSecret(path, SecretRole.MasterKey); lease.Read(); }); return Task.CompletedTask; });
        await check("native data lease excludes and releases", () =>
        {
            var root = SafeDirectory.CreatePrivateSession(parent);
            using (var first = WindowsFileBoundary.AcquireDataLease(root)) Reject(() => { using var second = WindowsFileBoundary.AcquireDataLease(root); });
            using var next = WindowsFileBoundary.AcquireDataLease(root); return Task.CompletedTask;
        });
        await check("native data lease refuses unsafe existing child ACL", () =>
        {
            var path = FileFixture(out var root); var info = new FileInfo(path); var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Write, AccessControlType.Allow)); info.SetAccessControl(acl);
            Reject(() => { using var lease = WindowsFileBoundary.AcquireDataLease(root); }); return Task.CompletedTask;
        });
        foreach (var path in new[] { @"\\server\share\secret", @"C:\one\..\secret", @"C:\path\secret:ads", @"C:\path\secret.", @"C:\path\CON.txt" })
            await check("native path refuses " + path, () => { Reject(() => WindowsFileBoundary.CanonicalLocalPath(path)); return Task.CompletedTask; });
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string path, string existing, IntPtr security);
}

using System.Security.AccessControl;
using System.Security.Principal;

namespace Yahad.Install;

public static class SafeDirectory
{
    public static void NoReparseAncestors(string path)
    {
        for (var p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
        {
            try
            {
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("REPARSE_POINT");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static string CreatePrivateSession(string parent)
    {
        NoReparseAncestors(parent);
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, "yahad-fixture-" + Guid.NewGuid().ToString("N"));
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("OWNER_MISSING");
        acl.SetOwner(user);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        // Create with the DACL already attached, not via a later permissive-to-private transition.
        new DirectoryInfo(path).Create(acl);
        File.WriteAllText(Path.Combine(path, ".yahad-fixture"), "fixture-v1");
        return path;
    }

    internal static void ValidateSession(string path)
    {
        NoReparseAncestors(path);
        NoReparseAncestors(Path.Combine(path, ".yahad-fixture"));
        if (!Directory.Exists(path) || File.ReadAllText(Path.Combine(path, ".yahad-fixture")) != "fixture-v1")
            throw new IOException("SESSION_NOT_OWNED");
        var acl = new DirectoryInfo(path).GetAccessControl();
        var user = WindowsIdentity.GetCurrent().User!;
        if (!acl.AreAccessRulesProtected || acl.GetOwner(typeof(SecurityIdentifier))?.Equals(user) != true)
            throw new IOException("SESSION_ACL");
        var allowed = new[] { user.Value, "S-1-5-18", "S-1-5-32-544" };
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !allowed.Contains(rule.IdentityReference.Value))
                throw new IOException("SESSION_ACL");
    }
}

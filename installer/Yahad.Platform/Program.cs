using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;

// Unprivileged app helper. No installation, permission repair or service control.
void Reply(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
try
{
    if (args is ["read-secret", var role, var path] && role is "master-key" or "setup-capability")
    {
        try
        {
            using var file = WindowsFileBoundary.OpenSecret(path, role == "master-key" ? SecretRole.MasterKey : SecretRole.SetupCapability);
            var bytes = file.Read();
            try { Reply(new { schema = 1, ok = true, exists = true, bytes = Convert.ToBase64String(bytes) }); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Win32Exception e) when (role == "setup-capability" && e.NativeErrorCode is 2 or 3)
        { Reply(new { schema = 1, ok = true, exists = false }); }
        return 0;
    }
    if (args is ["lock-data", var directory, var ownerText] && int.TryParse(ownerText, out var owner) && owner > 0)
    {
        using var lease = WindowsFileBoundary.AcquireDataLease(directory, owner);
        Reply(new { schema = 1, ok = true, locked = true });
        // Parent's stdin is a private lifetime pipe. EOF on parent death releases
        // the kernel lock. A bounded explicit release line permits normal shutdown.
        var line = new char[8]; var count = 0; int c;
        while ((c = Console.Read()) != -1 && c != '\n')
        { if (count == line.Length) return 2; line[count++] = (char)c; }
        return c == -1 || new string(line, 0, count) == "release" ? 0 : 2;
    }
    Reply(new { schema = 1, ok = false, code = "NATIVE_PROTOCOL" }); return 2;
}
catch
{
    // Never serialize exceptions, file paths, secret bytes or tokens.
    Reply(new { schema = 1, ok = false, code = "NATIVE_BOUNDARY_REFUSED" }); return 1;
}

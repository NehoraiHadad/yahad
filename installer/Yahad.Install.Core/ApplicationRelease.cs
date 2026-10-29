using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reflection;
namespace Yahad.Install;

// Compile the same package.json into the shared native assembly. No mutable
// installed file, environment setting or caller-provided version is trusted.
public static class ApplicationRelease
{
    public static string Version { get; } = ReadEmbeddedVersion();
    public static string SourceCommit => CompiledSourceCommit(typeof(ApplicationRelease).Assembly);
    public static string CompiledSourceCommit(Assembly assembly)
    {
        var revision = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+').Last();
        if (revision is null || !Regex.IsMatch(revision, @"\A[a-f0-9]{40}\z", RegexOptions.CultureInvariant))
            throw new IOException("APPLICATION_SOURCE_IDENTITY_MISSING");
        return revision;
    }
    private static string ReadEmbeddedVersion()
    {
        using var input = typeof(ApplicationRelease).Assembly.GetManifestResourceStream("Yahad.Application.Package.json")
            ?? throw new IOException("APPLICATION_PACKAGE_MISSING");
        if (input.Length > 65536) throw new IOException("APPLICATION_PACKAGE_BOUND");
        using var bytes = new MemoryStream(); input.CopyTo(bytes); return ParsePackageVersion(bytes.ToArray());
    }
    internal static string ParsePackageVersion(byte[] bytes)
    {
        if (bytes.Length > 65536) throw new IOException("APPLICATION_PACKAGE_BOUND");
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("APPLICATION_PACKAGE_FORMAT");
        var fields = document.RootElement.EnumerateObject().Where(p => p.Name == "version").ToArray();
        if (fields.Length != 1 || fields[0].Value.ValueKind != JsonValueKind.String) throw new IOException("APPLICATION_VERSION_FORMAT");
        var version = fields[0].Value.GetString()!;
        if (version.Length > 40 || !Regex.IsMatch(version, @"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant))
            throw new IOException("APPLICATION_VERSION_FORMAT");
        return version;
    }
}

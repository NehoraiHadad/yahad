using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yahad.Install;

public sealed record BundleFile(string Path, long Size, string Sha256);
public sealed record BundleManifest(int Schema, string Kind, string Target, string Version, BundleFile[] Files);
public sealed record VerifiedBundle(string Path, string Digest, BundleManifest Manifest);

/// <summary>Fixture-only contract. Hashes check consistency, not publisher authenticity.</summary>
public static partial class Bundle
{
    public const long MaximumBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    [GeneratedRegex(@"^app/[a-zA-Z0-9._/-]+$")]
    private static partial Regex SafeName();
    [GeneratedRegex(@"^[a-f0-9]{64}$")]
    private static partial Regex Hash();

    public static VerifiedBundle Inspect(string path)
    {
        using var stream = OpenPackage(path);
        var digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var header = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("PACKAGE_MANIFEST");
        if (header.Length > 32768) throw new InvalidDataException("PACKAGE_MANIFEST_SIZE");
        using var input = header.Open();
        using var headerBytes = new MemoryStream();
        CopyBounded(input, headerBytes, 32768, exact: false);
        BundleManifest m;
        try { m = JsonSerializer.Deserialize<BundleManifest>(headerBytes.ToArray(), Json) ?? throw new InvalidDataException("PACKAGE_MANIFEST"); }
        catch (JsonException e) { throw new InvalidDataException("PACKAGE_MANIFEST", e); }
        if (m.Schema != 1 || m.Kind != "feasibility-fixture" || m.Target != "win-x64" ||
            m.Version != "fixture-1" || m.Files is not { Length: > 0 and <= 256 })
            throw new InvalidDataException("PACKAGE_UNSUPPORTED");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json" };
        long total = 0;
        foreach (var f in m.Files)
        {
            if (f is null || string.IsNullOrEmpty(f.Path) || f.Path.Length > 240 || !SafeName().IsMatch(f.Path) ||
                f.Path.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') ||
                    p.Split('.')[0].ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" ||
                    Regex.IsMatch(p, @"^(COM|LPT)[0-9](\.|$)", RegexOptions.IgnoreCase)) ||
                f.Size < 0 || f.Size > MaximumBytes || string.IsNullOrEmpty(f.Sha256) || !Hash().IsMatch(f.Sha256) || !names.Add(f.Path))
                throw new InvalidDataException("PACKAGE_PATH_OR_HASH");
            total = checked(total + f.Size);
            if (total > MaximumBytes) throw new InvalidDataException("PACKAGE_SIZE");
        }
        if (zip.Entries.Count != names.Count) throw new InvalidDataException("PACKAGE_CONTENTS");
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            // Reject duplicate names, directories, Unix symlinks and Windows reparse points.
            if (!names.Contains(e.FullName) || !entries.Add(e.FullName) ||
                ((e.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (e.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("PACKAGE_ENTRY");
        }
        foreach (var f in m.Files)
        {
            var e = zip.GetEntry(f.Path) ?? throw new InvalidDataException("PACKAGE_CASE");
            if (e.Length != f.Size) throw new InvalidDataException("PACKAGE_LENGTH");
            using var bytes = e.Open();
            using var digestBytes = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long count = 0; int read;
            while ((read = bytes.Read(buffer)) > 0)
            {
                count = checked(count + read);
                if (count > f.Size) throw new InvalidDataException("PACKAGE_LENGTH");
                digestBytes.AppendData(buffer, 0, read);
            }
            if (count != f.Size || Convert.ToHexStringLower(digestBytes.GetHashAndReset()) != f.Sha256)
                throw new InvalidDataException("PACKAGE_HASH");
        }
        return new(System.IO.Path.GetFullPath(path), digest, m);
    }

    private static void CopyBounded(Stream input, Stream output, long maximum, bool exact)
    {
        var buffer = new byte[65536]; long total = 0; int read;
        while ((read = input.Read(buffer)) > 0)
        {
            total = checked(total + read);
            if (total > maximum) throw new InvalidDataException("PACKAGE_LENGTH");
            output.Write(buffer, 0, read);
        }
        if (exact && total != maximum) throw new InvalidDataException("PACKAGE_LENGTH");
    }

    internal static async Task CopyExactAsync(Stream input, Stream output, long size, CancellationToken cancellation)
    {
        var buffer = new byte[65536]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
        {
            total = checked(total + read);
            if (total > size) throw new InvalidDataException("PACKAGE_LENGTH");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
        }
        if (total != size) throw new InvalidDataException("PACKAGE_LENGTH");
    }

    internal static FileStream OpenPackage(string path)
    {
        SafeDirectory.NoReparseAncestors(path);
        var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (s.Length > MaximumBytes + 1024 * 1024) { s.Dispose(); throw new InvalidDataException("PACKAGE_SIZE"); }
        return s;
    }
}

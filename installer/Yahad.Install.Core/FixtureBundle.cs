using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yahad.Install;

public static class FixtureBundle
{
    public static void Create(string path)
    {
        var bytes = Encoding.UTF8.GetBytes("Yahad native installer fixture. No family data or executable payload.\n");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var manifest = new BundleManifest(1, "feasibility-fixture", "win-x64", "fixture-1",
            [new("app/fixture.txt", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))]);
        using (var header = zip.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(header, manifest);
        using var content = zip.CreateEntry("app/fixture.txt").Open(); content.Write(bytes);
    }
}

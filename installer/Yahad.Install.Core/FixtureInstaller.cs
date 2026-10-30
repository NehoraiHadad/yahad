using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Yahad.Install;

public enum Stage { Validating, Staging, Ready, Paused, Canceled, Failed }
public sealed record Progress(Stage Stage, int Completed, int Total);
public sealed record Journal(int Schema, string Kind, string Digest, Stage Stage);

/// <summary>No OS integration, app execution, live data access, or update authority.</summary>
public static class FixtureInstaller
{
    public static async Task StageAsync(string bundlePath, string session, CancellationToken cancellation,
        IProgress<Progress>? progress = null, Action<string>? fault = null)
    {
        SafeDirectory.ValidateSession(session);
        var lockPath = Path.Combine(session, "operation.lock");
        SafeDirectory.NoReparseAncestors(lockPath);
        using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var journalPath = Path.Combine(session, "journal.json");
        var destination = Path.Combine(session, "payload");
        VerifiedBundle? verified = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report(new(Stage.Validating, 0, 1));
            verified = Bundle.Inspect(bundlePath);
            if (File.Exists(journalPath))
            {
                SafeDirectory.NoReparseAncestors(journalPath);
                var previous = JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath));
                if (previous is not { Schema: 1, Kind: "feasibility-fixture" } || previous.Digest != verified.Digest)
                    throw new InvalidDataException("SESSION_BUNDLE_CHANGED");
            }
            if (Directory.Exists(destination))
            {
                VerifyStaged(destination, verified.Manifest);
                Save(session, new(1, "feasibility-fixture", verified.Digest, Stage.Ready));
                progress?.Report(new(Stage.Ready, 1, 1));
                return;
            }
            Save(session, new(1, "feasibility-fixture", verified.Digest, Stage.Staging));
            fault?.Invoke("after-journal");
            var staging = Path.Combine(session, "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            using (var input = Bundle.OpenPackage(bundlePath))
            {
                // Hold the exact inspected bytes open against replacement for the extraction period.
                if (Convert.ToHexStringLower(SHA256.HashData(input)) != verified.Digest)
                    throw new InvalidDataException("PACKAGE_CHANGED");
                input.Position = 0;
                using var zip = new ZipArchive(input, ZipArchiveMode.Read);
                var completed = 0;
                foreach (var file in verified.Manifest.Files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var target = Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    SafeDirectory.NoReparseAncestors(target);
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var source = zip.GetEntry(file.Path)!.Open();
                    await Bundle.CopyExactAsync(source, output, file.Size, cancellation);
                    output.Flush(true);
                    progress?.Report(new(Stage.Staging, ++completed, verified.Manifest.Files.Length));
                }
            }
            VerifyStaged(staging, verified.Manifest);
            cancellation.ThrowIfCancellationRequested();
            fault?.Invoke("before-publish");
            // New fixture payload only. Never overwrite even our own published payload.
            Directory.Move(staging, destination);
            fault?.Invoke("after-publish");
            Save(session, new(1, "feasibility-fixture", verified.Digest, Stage.Ready));
            progress?.Report(new(Stage.Ready, 1, 1));
        }
        catch (OperationCanceledException)
        {
            if (verified is not null) Save(session, new(1, "feasibility-fixture", verified.Digest, Stage.Paused));
            throw;
        }
        // Unexpected failures leave the last journal intact, with staging retained for inspection.
    }

    private static void VerifyStaged(string root, BundleManifest manifest)
    {
        SafeDirectory.NoReparseAncestors(root);
        // Walk one directory at a time; never recurse through an unvalidated junction.
        var pending = new Stack<string>(); pending.Push(root);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var dir))
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                SafeDirectory.NoReparseAncestors(path);
                if (Directory.Exists(path)) pending.Push(path);
                else names.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
            }
        if (!names.SetEquals(manifest.Files.Select(f => f.Path))) throw new InvalidDataException("STAGED_CONTENTS");
        foreach (var f in manifest.Files)
        {
            using var bytes = File.OpenRead(Path.Combine(root, f.Path));
            if (bytes.Length != f.Size || Convert.ToHexStringLower(SHA256.HashData(bytes)) != f.Sha256)
                throw new InvalidDataException("STAGED_HASH");
        }
    }

    private static void Save(string session, Journal value)
    {
        var path = Path.Combine(session, "journal.json");
        SafeDirectory.NoReparseAncestors(path);
        var temp = Path.Combine(session, "journal-" + Guid.NewGuid().ToString("N") + ".tmp");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
        File.Move(temp, path, true);
    }
}

using System.IO;
using Yahad.Install;
namespace Yahad.Setup;

// A staged release (9.3) as the engine reads it. The directory is not trusted: only
// the exact names of one version are read, never a glob, each file once and bounded,
// and only those bytes are verified and parsed. Which track the authenticated release
// is for this machine is ReleaseSelection's answer, the Updater's own.
internal static class ReleaseStage
{
    internal static ReleaseManifest Read(string directory,string version,byte[] key)
    {
        if(!Path.IsPathFullyQualified(directory)||!Directory.Exists(directory)||!SemVer.IsVersion(version))throw new IOException("RELEASE_SIGNATURE_INVALID");
        var manifest=ReadBounded(Path.Combine(directory,ReleaseManifest.ManifestName(version)),ReleaseManifest.MaximumBytes)??throw new IOException("RELEASE_SIGNATURE_INVALID");
        var release=ReleaseVerification.Verify(key,manifest,ReadBounded(Path.Combine(directory,ReleaseManifest.SignatureName(version)),64));
        // The names were the version's: a manifest of another release under them is not this stage.
        if(release.Version!=version)throw new IOException("RELEASE_MANIFEST_INVALID");
        return release;
    }
    // This engine applies only the track this release is for on this machine; the
    // recorded schema (4.5), never a live answer, must be one the release accepts.
    internal static void RequireTrack(ReleaseManifest release,InstallerState installed,ReleaseTrack track)
    {
        var selected=ReleaseSelection.Select(release,installed.Active.Pointer())??throw new IOException("RELEASE_NOT_NEWER");
        if(selected!=track)throw new IOException("RELEASE_TRACK_MISMATCH");
        if(!release.AcceptsSchema(installed.Active.App.DatabaseSchema))throw new IOException("RELEASE_UNSUPPORTED_FROM");
    }
    internal static byte[]? ReadBounded(string path,int maximum)
    {
        if(!File.Exists(path))return null;
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(file.Length>maximum)return null;
        var bytes=new byte[file.Length];file.ReadExactly(bytes);return bytes;
    }
}

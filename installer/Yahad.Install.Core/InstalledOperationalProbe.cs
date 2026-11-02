using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Yahad.Install;

internal sealed record OperationalProbeSelection(string Executable,string Sha256,long BootstrapEpoch,string? SystemManifestDigest);
// Shared readonly selection. Absence alone retains the separately pinned
// baseline; a malformed/present selection never falls back to an old probe.
internal static class InstalledOperationalProbe
{
    private sealed record FileRecord(string RelativePath,long Size,string Sha256);
    private sealed record ComponentRecord(SystemComponentKind Kind,string EntryPoint,string EntrySha256,FileRecord[] Files);
    private sealed record Selection(int Format,string InstallationId,long Epoch,string Version,string ManifestDigest,string? OperationalProbeDigest,
        ComponentRecord[] Components,string? PolicyDigest,string? SourceCommit);
    internal static OperationalProbeSelection Require(InstalledStateFiles state,InstalledStateFiles binaries,string installationId,string? immutableFullPolicyDigest,
        string baselineExecutable,string baselineDigest,long minimumEpoch=0,CancellationToken cancellation=default)
    {
        if(!Guid.TryParseExact(installationId,"N",out var installation)||installation.ToString("N")!=installationId)throw new IOException("OPERATIONAL_PROBE_INSTALLATION");FixedSystemComponents.Digest(baselineDigest);
        if(minimumEpoch<0)throw new IOException("OPERATIONAL_PROBE_EPOCH");
        var path=state.PathOf("system-bootstrap-current.json");
        try{_=File.GetAttributes(path);}catch(FileNotFoundException){return Baseline();}catch(DirectoryNotFoundException){return Baseline();}
        var value=NativeJson.Read<Selection>(state.Read(path,8*1024*1024));
        if(immutableFullPolicyDigest is null)throw new IOException("OPERATIONAL_PROBE_FULL_POLICY_REQUIRED");FixedSystemComponents.Digest(immutableFullPolicyDigest);
        FixedSystemComponents.Digest(value.ManifestDigest);
        if(value.Format!=1||value.InstallationId!=installationId||value.Epoch<Math.Max(1,minimumEpoch)||value.PolicyDigest!=immutableFullPolicyDigest||
            value.OperationalProbeDigest!=value.ManifestDigest||value.SourceCommit is null||!Regex.IsMatch(value.SourceCommit,"^[a-f0-9]{40}$")||
            !Regex.IsMatch(value.Version,"^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$")||value.Components is null||value.Components.Length!=5||value.Components.Select(c=>c.Kind).Distinct().Count()!=5)throw new IOException("OPERATIONAL_PROBE_SELECTION");
        var descriptors=value.Components.Select(c=>new SystemComponentDescriptor(c.Kind,FixedSystemComponents.SlotPrefix(c.Kind),c.EntryPoint,c.EntrySha256,
            c.Files.Select(f=>new SystemComponentFile(f.RelativePath,f.Size,f.Sha256)))).ToArray();
        var slot=binaries.PathOf("system-slots/"+value.ManifestDigest);var manifestBytes=binaries.Read(Path.Combine(slot,"system-manifest.json"),8*1024*1024);
        if(Hash(manifestBytes)!=value.ManifestDigest)throw new IOException("OPERATIONAL_PROBE_MANIFEST");
        var manifest=NativeJson.Read<JsonElement>(manifestBytes);
        if(manifest.GetProperty("format").GetInt32()!=2||manifest.GetProperty("kind").GetString()!="windows-system-candidate"||manifest.GetProperty("profile").GetString()!="full-system-v1"||
            manifest.GetProperty("target").GetString()!="win-x64"||manifest.GetProperty("version").GetString()!=value.Version||manifest.GetProperty("sourceCommit").GetString()!=value.SourceCommit)throw new IOException("OPERATIONAL_PROBE_SOURCE");
        var components=manifest.GetProperty("components");if(components.GetArrayLength()!=5)throw new IOException("OPERATIONAL_PROBE_COMPONENTS");
        var seen=new HashSet<SystemComponentKind>();
        foreach(var component in components.EnumerateArray())
        {
            if(!Enum.TryParse<SystemComponentKind>(component.GetProperty("kind").GetString(),false,out var kind)||!Enum.IsDefined(kind)||!seen.Add(kind))throw new IOException("OPERATIONAL_PROBE_COMPONENT_KIND");
            var descriptor=descriptors.Single(c=>c.Kind==kind);var members=component.GetProperty("files").EnumerateArray().Select(f=>new SystemComponentFile(f.GetProperty("path").GetString()!,f.GetProperty("size").GetInt64(),f.GetProperty("sha256").GetString()!)).ToArray();
            if(component.GetProperty("prefix").GetString()!=descriptor.SlotRelativeDirectory||component.GetProperty("entryPoint").GetString()!=descriptor.EntryPoint||component.GetProperty("entrySha256").GetString()!=descriptor.EntrySha256||
                !members.OrderBy(f=>f.RelativePath,StringComparer.Ordinal).Select(f=>(f.RelativePath,f.Size,f.Sha256)).SequenceEqual(descriptor.Files.OrderBy(f=>f.RelativePath,StringComparer.Ordinal).Select(f=>(f.RelativePath,f.Size,f.Sha256))))throw new IOException("OPERATIONAL_PROBE_DESCRIPTOR");
        }
        var probe=descriptors.Single(c=>c.Kind==SystemComponentKind.Probe);var root=Path.Combine(slot,probe.SlotRelativeDirectory.Replace('/',Path.DirectorySeparatorChar));
        var expected=new HashSet<string>(probe.Files.Select(f=>f.RelativePath),StringComparer.OrdinalIgnoreCase);var count=0;
        void Walk(string directory,int depth)
        {
            cancellation.ThrowIfCancellationRequested();if(depth>64)throw new IOException("OPERATIONAL_PROBE_DEPTH");using var held=binaries.PinDirectory(directory);
            foreach(var child in Directory.EnumerateFileSystemEntries(directory))
            {if(++count>50000||(File.GetAttributes(child)&FileAttributes.ReparsePoint)!=0)throw new IOException("OPERATIONAL_PROBE_CLOSURE");if(Directory.Exists(child))Walk(child,depth+1);else if(!expected.Remove(Path.GetRelativePath(root,child).Replace('\\','/')))throw new IOException("OPERATIONAL_PROBE_EXTRA_FILE");}
        }
        Walk(root,0);if(expected.Count!=0)throw new IOException("OPERATIONAL_PROBE_MISSING_FILE");
        foreach(var file in probe.Files)
        {
            cancellation.ThrowIfCancellationRequested();using var input=binaries.Open(Path.Combine(root,file.RelativePath.Replace('/',Path.DirectorySeparatorChar)));
            if(input.Length!=file.Size)throw new IOException("OPERATIONAL_PROBE_FILE_SIZE");using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[65536];long offset=0;int read;
            while((read=input.Read(offset,buffer))>0){cancellation.ThrowIfCancellationRequested();hash.AppendData(buffer.AsSpan(0,read));offset+=read;}
            if(Convert.ToHexStringLower(hash.GetHashAndReset())!=file.Sha256)throw new IOException("OPERATIONAL_PROBE_FILE_HASH");
        }
        return new(Path.Combine(root,probe.EntryPoint.Replace('/',Path.DirectorySeparatorChar)),probe.EntrySha256,value.Epoch,value.ManifestDigest);
        OperationalProbeSelection Baseline()
        {
            if(minimumEpoch>0)throw new IOException("OPERATIONAL_PROBE_SELECTION_REQUIRED");
            if(!Path.GetFullPath(baselineExecutable).StartsWith(binaries.Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("OPERATIONAL_PROBE_BASELINE_PATH");
            using var baseline=binaries.Open(baselineExecutable);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[65536];long offset=0;int read;
            while((read=baseline.Read(offset,buffer))>0){cancellation.ThrowIfCancellationRequested();hash.AppendData(buffer.AsSpan(0,read));offset+=read;}
            if(Convert.ToHexStringLower(hash.GetHashAndReset())!=baselineDigest)throw new IOException("OPERATIONAL_PROBE_BASELINE_HASH");return new(baselineExecutable,baselineDigest,0,null);
        }
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}

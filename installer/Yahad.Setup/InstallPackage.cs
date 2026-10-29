using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;

internal sealed record InstallMember(string Path,long Size,string Sha256);
internal sealed record InstallPackageManifest(int Format,string Version,string SourceCommit,string ZipSha256,string PayloadDigest,InstallMember[] Files);
internal sealed class InstallPackage:IDisposable
{
    private readonly Stream stream;private readonly ZipArchive archive;
    internal InstallPackageManifest Manifest{get;}
    internal InstallPackage(Stream zip,byte[] manifest)
    {
        stream=zip;Manifest=NativeJson.Read<InstallPackageManifest>(manifest);
        if(Manifest.Format!=1||Manifest.Version!=ApplicationRelease.Version||!System.Text.RegularExpressions.Regex.IsMatch(Manifest.SourceCommit,"^[a-f0-9]{40}$")||Manifest.Files.Length is <10 or >50000)throw new IOException("PACKAGE_FORMAT");
        Digest(Manifest.ZipSha256);Digest(Manifest.PayloadDigest);
        if(!zip.CanSeek||zip.Length>2147483648||Convert.ToHexStringLower(SHA256.HashData(zip))!=Manifest.ZipSha256)throw new IOException("PACKAGE_HASH");zip.Position=0;
        archive=new(zip,ZipArchiveMode.Read,true);
        var expected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
        foreach(var record in Manifest.Files)
        {Name(record.Path);Digest(record.Sha256);if(!expected.Add(record.Path)||record.Size is <0 or >536870912||(total+=record.Size)>2147483648)throw new IOException("PACKAGE_BOUND");}
        if(archive.Entries.Count!=expected.Count)throw new IOException("PACKAGE_MEMBERS");
        foreach(var entry in archive.Entries)
        {
            Name(entry.FullName);if(!expected.Remove(entry.FullName)||entry.Length<0||((entry.ExternalAttributes>>16)&0xf000) is not(0 or 0x8000)||((entry.ExternalAttributes&0x400)!=0))throw new IOException("PACKAGE_MEMBER_TYPE");
        }
        if(expected.Count!=0)throw new IOException("PACKAGE_MEMBERS");
        foreach(var required in new[]{"host/Yahad.ServiceHost.exe","host/Yahad.ServiceHost.runtimeconfig.json","payload/bundle.json","payload/runtime/node/node.exe","payload/runtime/ffmpeg/ffmpeg.exe","payload/runtime/caddy/caddy.exe","payload/app/src/main.ts","payload/app/dist/index.html","payload/app/native/Yahad.Platform.exe"})if(!Manifest.Files.Any(r=>r.Path==required))throw new IOException("PACKAGE_MISSING");
    }
    internal static InstallPackage Embedded()
    {
        var assembly=Assembly.GetExecutingAssembly();using var metadata=assembly.GetManifestResourceStream("Yahad.Install.Manifest")??throw new IOException("PACKAGE_NOT_EMBEDDED");
        if(metadata.Length>16777216)throw new IOException("PACKAGE_METADATA_BOUND");using var buffer=new MemoryStream();metadata.CopyTo(buffer);
        var payload=assembly.GetManifestResourceStream("Yahad.Install.Payload")??throw new IOException("PACKAGE_NOT_EMBEDDED");
        try{return new(payload,buffer.ToArray());}catch{payload.Dispose();throw;}
    }
    internal void Extract(string privateStaging,Action<int> progress,CancellationToken cancellation,Action<string>? createDirectory=null,Func<string,FileStream>? createFile=null)
    {
        SafeDirectory.NoReparseAncestors(privateStaging);var count=0;
        foreach(var record in Manifest.Files)
        {
            cancellation.ThrowIfCancellationRequested();var target=Path.GetFullPath(Path.Combine(privateStaging,record.Path.Replace('/',Path.DirectorySeparatorChar)));
            if(!target.StartsWith(Path.GetFullPath(privateStaging)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("PACKAGE_PATH");
            if(createDirectory is null)Directory.CreateDirectory(Path.GetDirectoryName(target)!);else createDirectory(Path.GetDirectoryName(target)!);SafeDirectory.NoReparseAncestors(target);
            var entry=archive.GetEntry(record.Path)??throw new IOException("PACKAGE_MEMBER");if(entry.Length!=record.Size)throw new IOException("PACKAGE_SIZE");
            using var input=entry.Open();using var output=createFile is null?new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None,1048576,FileOptions.WriteThrough):createFile(target);
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var chunk=new byte[1048576];long bytes=0;int n;
            while((n=input.Read(chunk))>0){cancellation.ThrowIfCancellationRequested();if((bytes+=n)>record.Size)throw new IOException("PACKAGE_SIZE");hash.AppendData(chunk,0,n);output.Write(chunk,0,n);}
            if(bytes!=record.Size||Convert.ToHexStringLower(hash.GetHashAndReset())!=record.Sha256)throw new IOException("PACKAGE_MEMBER_HASH");output.Flush(true);
            if(++count%100==0||count==Manifest.Files.Length)progress(count*100/Manifest.Files.Length);
        }
        if(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(privateStaging,"payload/bundle.json"))))!=Manifest.PayloadDigest)throw new IOException("PACKAGE_PAYLOAD_ANCHOR");
    }
    internal static void Name(string name)
    {
        if(name.Length is <1 or >240||!name.StartsWith("host/",StringComparison.Ordinal)&&!name.StartsWith("payload/",StringComparison.Ordinal)||name.Contains('\\')||name.Contains(':'))throw new IOException("PACKAGE_PATH");
        foreach(var part in name.Split('/'))if(part is "" or "." or ".."||part.EndsWith('.')||part.EndsWith(' ')||System.Text.RegularExpressions.Regex.IsMatch(part,"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\\..*)?$",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new IOException("PACKAGE_PATH");
    }
    private static void Digest(string value){if(!System.Text.RegularExpressions.Regex.IsMatch(value,"^[a-f0-9]{64}$"))throw new IOException("PACKAGE_DIGEST");}
    public void Dispose(){archive.Dispose();stream.Dispose();}
}

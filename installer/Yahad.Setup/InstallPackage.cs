using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;

internal sealed record InstallMember(string Path,long Size,string Sha256);
internal sealed record InstallPackageManifest(int Format,string Version,string SourceCommit,string ZipSha256,string PayloadDigest,InstallMember[] Files);
// Where Extract writes. The private default serves synthetic tests; Setup
// supplies protected directories, protected files and a no-replace commit.
internal sealed record ExtractTarget(Action<string> CreateDirectory,Func<string,FileStream> CreateFile,Action<string,string> Commit,Func<string,InstallMember,bool> ExistingMatches)
{
    internal static ExtractTarget Private{get;}=new(path=>Directory.CreateDirectory(path),path=>new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,1048576,FileOptions.WriteThrough),
        (partial,path)=>File.Move(partial,path,false),(path,record)=>{using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);return file.Length==record.Size&&Convert.ToHexStringLower(SHA256.HashData(file))==record.Sha256;});
}
// One Format-1 archive holding host/ (the service host), updater/ (the Updater
// service) and payload/ (node, the application, ffmpeg and caddy). Nothing else.
internal sealed class InstallPackage:IDisposable
{
    internal const int Format=1;
    private readonly Stream stream;private readonly ZipArchive archive;
    internal InstallPackageManifest Manifest{get;}
    internal InstallPackage(Stream zip,byte[] manifest)
    {
        stream=zip;Manifest=NativeJson.Read<InstallPackageManifest>(manifest);
        if(Manifest.Format!=Format||Manifest.Version!=ApplicationRelease.Version||!System.Text.RegularExpressions.Regex.IsMatch(Manifest.SourceCommit,"^[a-f0-9]{40}$")||Manifest.Files.Length is <10 or >50000)throw new IOException("PACKAGE_FORMAT");
        if(Manifest.SourceCommit!=ApplicationRelease.SourceCommit)throw new IOException("PACKAGE_SOURCE");
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
        foreach(var required in new[]{"payload/bundle.json","payload/runtime/node/node.exe","payload/runtime/ffmpeg/ffmpeg.exe","payload/runtime/caddy/caddy.exe","payload/app/server/main.mjs","payload/app/dist/index.html","payload/app/native/Yahad.Platform.exe","host/Yahad.ServiceHost.exe","host/Yahad.ServiceHost.runtimeconfig.json","updater/Yahad.Updater.exe","updater/Yahad.Updater.runtimeconfig.json"})
            if(!Manifest.Files.Any(r=>r.Path==required))throw new IOException("PACKAGE_MISSING");
        // The payload digest names the application bundle record itself.
        if(Manifest.Files.Single(f=>f.Path=="payload/bundle.json").Sha256!=Manifest.PayloadDigest)throw new IOException("PACKAGE_PAYLOAD_ANCHOR");
    }
    internal static InstallPackage Embedded()
    {
        var assembly=Assembly.GetExecutingAssembly();using var metadata=assembly.GetManifestResourceStream("Yahad.Install.Manifest")??throw new IOException("PACKAGE_NOT_EMBEDDED");
        if(metadata.Length>16777216)throw new IOException("PACKAGE_METADATA_BOUND");using var buffer=new MemoryStream();metadata.CopyTo(buffer);
        var payload=assembly.GetManifestResourceStream("Yahad.Install.Payload")??throw new IOException("PACKAGE_NOT_EMBEDDED");
        try{return new(payload,buffer.ToArray());}catch{payload.Dispose();throw;}
    }
    // Writes every member under root through a same-directory partial that is
    // committed by a no-replace rename, so a present member is always whole.
    // A present member is never rewritten: it must already be byte-identical.
    internal void Extract(string root,Action<int> progress,CancellationToken cancellation,ExtractTarget? target=null)
    {
        target??=ExtractTarget.Private;var count=0;var full=Path.GetFullPath(root);
        foreach(var record in Manifest.Files)
        {
            cancellation.ThrowIfCancellationRequested();var path=Path.GetFullPath(Path.Combine(full,record.Path.Replace('/',Path.DirectorySeparatorChar)));
            if(!path.StartsWith(full+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("PACKAGE_PATH");
            target.CreateDirectory(Path.GetDirectoryName(path)!);
            var entry=archive.GetEntry(record.Path)??throw new IOException("PACKAGE_MEMBER");if(entry.Length!=record.Size)throw new IOException("PACKAGE_SIZE");
            if(File.Exists(path)){if(!target.ExistingMatches(path,record))throw new IOException("INSTALL_TARGET_CHANGED");}
            else
            {
                var partial=path+".partial-"+Guid.NewGuid().ToString("N");
                try
                {
                    using(var input=entry.Open())using(var output=target.CreateFile(partial))
                    {
                        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var chunk=new byte[1048576];long bytes=0;int n;
                        while((n=input.Read(chunk))>0){cancellation.ThrowIfCancellationRequested();if((bytes+=n)>record.Size)throw new IOException("PACKAGE_SIZE");hash.AppendData(chunk,0,n);output.Write(chunk,0,n);}
                        if(bytes!=record.Size||Convert.ToHexStringLower(hash.GetHashAndReset())!=record.Sha256)throw new IOException("PACKAGE_MEMBER_HASH");output.Flush(true);
                    }
                    target.Commit(partial,path);
                }
                catch{try{File.Delete(partial);}catch(IOException){}catch(UnauthorizedAccessException){}throw;}
            }
            if(++count%100==0||count==Manifest.Files.Length)progress(count*100/Manifest.Files.Length);
        }
    }
    // An interrupted extraction leaves only members of this package and their
    // uncommitted partials. Anything else refuses; the partials are returned
    // for the caller to delete under its own ACL checks.
    internal static string[] RequireExtractedInventory(IEnumerable<string> expectedFiles,IEnumerable<string> files,IEnumerable<string> directories)
    {
        var expected=new HashSet<string>(expectedFiles,StringComparer.OrdinalIgnoreCase);var parents=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var name in expected){var slash=name.LastIndexOf('/');while(slash>0){parents.Add(name[..slash]);slash=name.LastIndexOf('/',slash-1);}}
        var partials=new List<string>();
        foreach(var file in files)
        {
            if(expected.Contains(file))continue;var marker=file.LastIndexOf(".partial-",StringComparison.Ordinal);
            if(marker<0||!expected.Contains(file[..marker])||!Guid.TryParseExact(file[(marker+9)..],"N",out var id)||id.ToString("N")!=file[(marker+9)..])throw new IOException("INSTALL_TARGET_CHANGED");
            partials.Add(file);
        }
        if(directories.Any(d=>!parents.Contains(d)))throw new IOException("INSTALL_TARGET_CHANGED");
        return partials.ToArray();
    }
    internal static void Name(string name)
    {
        if(name.Length is <1 or >240||!name.StartsWith("host/",StringComparison.Ordinal)&&!name.StartsWith("updater/",StringComparison.Ordinal)&&!name.StartsWith("payload/",StringComparison.Ordinal)||name.Contains('\\')||name.Contains(':'))throw new IOException("PACKAGE_PATH");
        foreach(var part in name.Split('/'))if(part is "" or "." or ".."||part.EndsWith('.')||part.EndsWith(' ')||System.Text.RegularExpressions.Regex.IsMatch(part,"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\\..*)?$",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new IOException("PACKAGE_PATH");
    }
    private static void Digest(string? value){if(value is null||!System.Text.RegularExpressions.Regex.IsMatch(value,"^[a-f0-9]{64}$"))throw new IOException("PACKAGE_DIGEST");}
    public void Dispose(){archive.Dispose();stream.Dispose();}
}

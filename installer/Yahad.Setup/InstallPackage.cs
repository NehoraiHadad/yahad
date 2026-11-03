using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;

internal sealed record InstallMember(string Path,long Size,string Sha256);
internal sealed record InstallPackageManifest(int Format,string Version,string SourceCommit,string ZipSha256,string PayloadDigest,InstallMember[] Files,string? SystemManifestDigest=null);
internal sealed class InstallPackage:IDisposable
{
    private readonly Stream stream;private readonly ZipArchive archive;
    internal InstallPackageManifest Manifest{get;}
    internal bool FullSystem=>Manifest.Format==2;
    internal static bool EmbeddedFullProfile()
    {
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Yahad.Install.Manifest");if(stream is null)return false;
        if(stream.Length>16777216)throw new IOException("PACKAGE_METADATA_BOUND");using var json=JsonDocument.Parse(stream);return json.RootElement.GetProperty("Format").GetInt32()==2;
    }
    internal InstallPackage(Stream zip,byte[] manifest)
    {
        stream=zip;Manifest=NativeJson.Read<InstallPackageManifest>(manifest);
        if(Manifest.Format is not(1 or 2)||Manifest.Version!=ApplicationRelease.Version||!System.Text.RegularExpressions.Regex.IsMatch(Manifest.SourceCommit,"^[a-f0-9]{40}$")||Manifest.Files.Length is <10 or >50000)throw new IOException("PACKAGE_FORMAT");
        if(FullSystem){Digest(Manifest.SystemManifestDigest!);if(Manifest.SourceCommit!=ApplicationRelease.SourceCommit)throw new IOException("PACKAGE_SOURCE");}
        else if(Manifest.SystemManifestDigest is not null||Manifest.Files.Any(f=>f.Path.StartsWith("bootstrap/",StringComparison.Ordinal)||f.Path=="system-manifest.json"))throw new IOException("PACKAGE_FORMAT");
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
        foreach(var required in new[]{"payload/bundle.json","payload/runtime/node/node.exe","payload/runtime/ffmpeg/ffmpeg.exe","payload/runtime/caddy/caddy.exe","payload/app/server/main.mjs","payload/app/dist/index.html","payload/app/native/Yahad.Platform.exe"})if(!Manifest.Files.Any(r=>r.Path==required))throw new IOException("PACKAGE_MISSING");
        if(FullSystem)
        {
            if(Manifest.Files.Any(f=>f.Path.StartsWith("host/",StringComparison.Ordinal)))throw new IOException("PACKAGE_FORMAT");
            foreach(var required in new[]{"system-manifest.json","bootstrap/authority/Yahad.Authority.exe","bootstrap/owner/Yahad.Owner.exe","bootstrap/servicehost/Yahad.ServiceHost.exe","bootstrap/servicehost/Yahad.ServiceHost.runtimeconfig.json","bootstrap/probe/node/node.exe","bootstrap/handoff/Yahad.Authority.exe"})if(!Manifest.Files.Any(r=>r.Path==required))throw new IOException("PACKAGE_MISSING");
        }
        else foreach(var required in new[]{"host/Yahad.ServiceHost.exe","host/Yahad.ServiceHost.runtimeconfig.json"})if(!Manifest.Files.Any(r=>r.Path==required))throw new IOException("PACKAGE_MISSING");
    }
    internal byte[] ReadMember(string name,int maximum)
    {
        var record=Manifest.Files.SingleOrDefault(f=>f.Path==name)??throw new IOException("PACKAGE_MEMBER");
        if(record.Size>maximum)throw new IOException("PACKAGE_MEMBER_BOUND");using var input=archive.GetEntry(name)!.Open();using var buffer=new MemoryStream();
        var bytes=new byte[65536];int count;while((count=input.Read(bytes))>0){if(buffer.Length+count>maximum||buffer.Length+count>record.Size)throw new IOException("PACKAGE_SIZE");buffer.Write(bytes,0,count);}
        var result=buffer.ToArray();if(result.Length!=record.Size||Convert.ToHexStringLower(SHA256.HashData(result))!=record.Sha256)throw new IOException("PACKAGE_MEMBER_HASH");return result;
    }
    internal static InstallPackage Embedded()
    {
        var assembly=Assembly.GetExecutingAssembly();using var metadata=assembly.GetManifestResourceStream("Yahad.Install.Manifest")??throw new IOException("PACKAGE_NOT_EMBEDDED");
        if(metadata.Length>16777216)throw new IOException("PACKAGE_METADATA_BOUND");using var buffer=new MemoryStream();metadata.CopyTo(buffer);
        var payload=assembly.GetManifestResourceStream("Yahad.Install.Payload")??throw new IOException("PACKAGE_NOT_EMBEDDED");
        try{return new(payload,buffer.ToArray());}catch{payload.Dispose();throw;}
    }
    internal RemovalTree VerifyStagingInventory(string staging,CancellationToken cancellation)
    {
        // A same-package retry may contain only expected protected staging
        // members. Extraction verifies each existing byte against its source.
        var expected=Manifest.Files.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);
        var directories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var name in expected.Keys)
        {var slash=name.LastIndexOf('/');while(slash>0){directories.Add(name[..slash]);slash=name.LastIndexOf('/',slash-1);}}
        using var root=WindowsFileBoundary.PinPreparingInstallMember(staging,true);
        var tree=RemovalTree.Inspect(staging,(path,directory)=>{cancellation.ThrowIfCancellationRequested();using var pin=WindowsFileBoundary.PinPreparingInstallMember(path,directory);});
        string Relative(string path)=>Path.GetRelativePath(staging,path).Replace('\\','/');
        RequireStagingInventory(expected.Keys,directories,tree.Files.Select(Relative),tree.Directories.Select(Relative),false);
        return tree;
    }
    internal void CleanVerifiedStaging(string staging,Action<int> progress,CancellationToken cancellation)
    {
        var tree=VerifyStagingInventory(staging,cancellation);var expected=Manifest.Files.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);
        tree.DeleteContents((path,directory)=>
        {
            if(directory)WindowsFileBoundary.DeletePreparingInstallMember(path,true);
            else{var member=expected[Path.GetRelativePath(staging,path).Replace('\\','/')];WindowsFileBoundary.DeletePreparingInstallMember(path,false,member.Size,member.Sha256);}
        },progress,cancellation);
        WindowsFileBoundary.DeletePreparingInstallMember(staging,true);
    }
    internal static string VerifyExistingStageFile(InstallMember record,long length,Func<long,byte[],int> readExisting,Stream source,CancellationToken cancellation)
    {
        if(length<0||length>record.Size)throw new IOException("INSTALL_STAGING_CHANGED");
        using var full=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);using var prefix=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sourceBytes=new byte[65536];var existingBytes=new byte[65536];long offset=0;int count;
        while((count=source.Read(sourceBytes))>0)
        {
            cancellation.ThrowIfCancellationRequested();if(offset+count>record.Size)throw new IOException("PACKAGE_SIZE");full.AppendData(sourceBytes,0,count);
            var compare=(int)Math.Min(count,Math.Max(0,length-offset));var compared=0;
            while(compared<compare)
            {
                var observed=readExisting(offset+compared,existingBytes);if(observed<=0||observed>existingBytes.Length)throw new IOException("INSTALL_STAGING_CHANGED");
                var take=Math.Min(observed,compare-compared);if(!sourceBytes.AsSpan(compared,take).SequenceEqual(existingBytes.AsSpan(0,take)))throw new IOException("INSTALL_STAGING_CHANGED");
                prefix.AppendData(existingBytes,0,take);compared+=take;
            }
            offset+=count;
        }
        if(offset!=record.Size||Convert.ToHexStringLower(full.GetHashAndReset())!=record.Sha256)throw new IOException("PACKAGE_MEMBER_HASH");
        return Convert.ToHexStringLower(prefix.GetHashAndReset());
    }
    internal static void RequireStagingInventory(IEnumerable<string> expectedFiles,IEnumerable<string> expectedDirectories,IEnumerable<string> files,IEnumerable<string> directories,bool complete=true)
    {
        bool Same(IEnumerable<string> expected,IEnumerable<string> actual)
        {var values=actual.ToArray();var allowed=new HashSet<string>(expected,StringComparer.OrdinalIgnoreCase);return values.Distinct(StringComparer.OrdinalIgnoreCase).Count()==values.Length&&(complete?allowed.SetEquals(values):allowed.IsSupersetOf(values));}
        if(!Same(expectedFiles,files)||!Same(expectedDirectories,directories))throw new IOException("INSTALL_STAGING_CHANGED");
    }
    internal void Extract(string privateStaging,Action<int> progress,CancellationToken cancellation,Action<string>? createDirectory=null,Func<string,FileStream>? createFile=null,bool resume=false)
    {
        SafeDirectory.NoReparseAncestors(privateStaging);var count=0;
        foreach(var record in Manifest.Files)
        {
            cancellation.ThrowIfCancellationRequested();var target=Path.GetFullPath(Path.Combine(privateStaging,record.Path.Replace('/',Path.DirectorySeparatorChar)));
            if(!target.StartsWith(Path.GetFullPath(privateStaging)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("PACKAGE_PATH");
            if(createDirectory is null)Directory.CreateDirectory(Path.GetDirectoryName(target)!);else createDirectory(Path.GetDirectoryName(target)!);SafeDirectory.NoReparseAncestors(target);
            var entry=archive.GetEntry(record.Path)??throw new IOException("PACKAGE_MEMBER");if(entry.Length!=record.Size)throw new IOException("PACKAGE_SIZE");
            if(resume&&File.Exists(target))
            {
                long length;string digest;
                using(var existing=WindowsFileBoundary.OpenInstallerMetadata(target))
                {length=existing.Length;using var source=entry.Open();digest=VerifyExistingStageFile(record,length,(offset,buffer)=>existing.Read(offset,buffer),source,cancellation);}
                if(length==record.Size){if(++count%100==0||count==Manifest.Files.Length)progress(count*100/Manifest.Files.Length);continue;}
                // Only a byte-for-byte prefix of this exact source member can
                // be replaced. The handle-bound delete compares it again.
                WindowsFileBoundary.DeletePreparingInstallMember(target,false,length,digest);
            }
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
        if(name.Length is <1 or >240||!name.StartsWith("host/",StringComparison.Ordinal)&&!name.StartsWith("payload/",StringComparison.Ordinal)&&name!="system-manifest.json"&&!new[]{"authority","owner","servicehost","probe","handoff"}.Any(kind=>name.StartsWith("bootstrap/"+kind+"/",StringComparison.Ordinal))||name.Contains('\\')||name.Contains(':'))throw new IOException("PACKAGE_PATH");
        foreach(var part in name.Split('/'))if(part is "" or "." or ".."||part.EndsWith('.')||part.EndsWith(' ')||System.Text.RegularExpressions.Regex.IsMatch(part,"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\\..*)?$",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new IOException("PACKAGE_PATH");
    }
    private static void Digest(string? value){if(value is null||!System.Text.RegularExpressions.Regex.IsMatch(value,"^[a-f0-9]{64}$"))throw new IOException("PACKAGE_DIGEST");}
    public void Dispose(){archive.Dispose();stream.Dispose();}
}

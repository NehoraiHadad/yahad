using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;

internal sealed record InstallMember(string Path,long Size,string Sha256);
internal sealed record PackageInfrastructure(string Version,int ContractMin,int ContractMax,string SourceCommit,string TreeSha256);
internal sealed record PackageApp(string Version,string SourceCommit,int RequiresContract,string TreeSha256,int DatabaseSchema,int[] DatabaseMigrationsFrom);
internal sealed record InstallPackageManifest(int Format,PackageInfrastructure Infrastructure,PackageApp App,string ZipSha256,InstallMember[] Files);
// Where Extract writes: protected directories, protected files and a no-replace commit.
internal sealed record ExtractTarget(Action<string> CreateDirectory,Func<string,FileStream> CreateFile,Action<string,string> Commit,Func<string,InstallMember,bool> ExistingMatches);
// One format-2 archive (docs/WINDOWS-APP-PACKAGE-PLAN.md 4.2, 5.3) holding two
// namespaces and nothing else: infra/ (infra-tree.json and the host, Updater,
// Platform, runtime, legal and setup members of the compiled infrastructure) and
// apps/ (app-tree.json and app/...). Each tree record lists exactly the members
// of its namespace. app/native/ is refused: Platform is infrastructure.
internal sealed class InstallPackage:IDisposable
{
    internal const int Format=2;
    internal const string InfraPrefix="infra/",AppPrefix="apps/",ManifestResource="Yahad.Install.Manifest",PayloadResource="Yahad.Install.Payload";
    internal static readonly string[] Required=["infra/host/Yahad.ServiceHost.exe","infra/host/Yahad.ServiceHost.runtimeconfig.json","infra/updater/Yahad.Updater.exe","infra/updater/Yahad.Updater.runtimeconfig.json",
        "infra/platform/Yahad.Platform.exe","infra/platform/Yahad.Platform.runtimeconfig.json","infra/runtime/node/node.exe","infra/runtime/caddy/caddy.exe","infra/"+Setup.InfrastructureRecord.EngineMember,
        "apps/app/package.json","apps/app/server/main.mjs","apps/app/dist/index.html","apps/app/deploy/lan-capabilities.json","apps/app/deploy/lan-help.html"];
    private readonly Stream stream;private readonly ZipArchive archive;
    internal InstallPackageManifest Manifest{get;}
    internal InstallPackage(Stream zip,byte[] manifest)
    {
        stream=zip;
        try{Manifest=NativeJson.Read<InstallPackageManifest>(manifest);}catch(JsonException){throw new IOException("PACKAGE_FORMAT");}
        var infra=Manifest.Infrastructure;var app=Manifest.App;
        if(Manifest.Format!=Format||infra is null||app is null||Manifest.Files is not {Length:>=16 and <=2*TreeRecord.MaximumMembers+2}||app.DatabaseMigrationsFrom is null||
            !ReleaseManifest.IsVersion(infra.Version)||!ReleaseManifest.IsVersion(app.Version)||!TreeRecord.IsCommit(infra.SourceCommit)||!TreeRecord.IsCommit(app.SourceCommit)||
            app.DatabaseSchema is <1 or >1024||!InfrastructureRelease.IsContract(app.RequiresContract))throw new IOException("PACKAGE_FORMAT");
        // Migrations are strictly ascending, each older than the target schema.
        for(var i=0;i<app.DatabaseMigrationsFrom.Length;i++)
            if(app.DatabaseMigrationsFrom[i]<1||app.DatabaseMigrationsFrom[i]>=app.DatabaseSchema||i>0&&app.DatabaseMigrationsFrom[i]<=app.DatabaseMigrationsFrom[i-1])throw new IOException("PACKAGE_FORMAT");
        // The infrastructure is the one compiled into this engine: a package never
        // names another, so no engine extracts an infrastructure it is not.
        if(infra.Version!=InfrastructureRelease.Version||infra.SourceCommit!=InfrastructureRelease.SourceCommit||infra.ContractMin!=InfrastructureRelease.ContractMin||
            infra.ContractMax!=InfrastructureRelease.ContractMax)throw new IOException("PACKAGE_SOURCE");
        if(!InfrastructureRelease.Supports(app.RequiresContract))throw new IOException("PACKAGE_CONTRACT");
        Digest(Manifest.ZipSha256);Digest(infra.TreeSha256);Digest(app.TreeSha256);
        if(!zip.CanSeek||zip.Length>2147483648||Convert.ToHexStringLower(SHA256.HashData(zip))!=Manifest.ZipSha256)throw new IOException("PACKAGE_HASH");zip.Position=0;
        archive=new(zip,ZipArchiveMode.Read,true);
        var expected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
        foreach(var record in Manifest.Files)
        {
            if(record is null)throw new IOException("PACKAGE_FORMAT");
            Name(record.Path);Digest(record.Sha256);if(!expected.Add(record.Path)||record.Size is <0 or >TreeRecord.MaximumMemberBytes||(total+=record.Size)>2147483648)throw new IOException("PACKAGE_BOUND");
        }
        if(archive.Entries.Count!=expected.Count)throw new IOException("PACKAGE_MEMBERS");
        foreach(var entry in archive.Entries)
        {
            Name(entry.FullName);if(!expected.Remove(entry.FullName)||entry.Length<0||((entry.ExternalAttributes>>16)&0xf000) is not(0 or 0x8000)||((entry.ExternalAttributes&0x400)!=0))throw new IOException("PACKAGE_MEMBER_TYPE");
        }
        if(expected.Count!=0)throw new IOException("PACKAGE_MEMBERS");
        foreach(var required in Required)if(!Manifest.Files.Any(r=>r.Path==required))throw new IOException("PACKAGE_MISSING");
        RequireTree(TreeKind.Infrastructure,infra.Version,infra.SourceCommit,infra.TreeSha256);
        RequireTree(TreeKind.App,app.Version,app.SourceCommit,app.TreeSha256);
    }
    internal static bool IsEmbedded=>Assembly.GetExecutingAssembly().GetManifestResourceInfo(PayloadResource) is not null;
    // What the window names: the app this image installs, read from the manifest alone
    // (the archive is verified only when it is installed), or, for the payload-free
    // engine, the infrastructure it is.
    internal static string DisplayVersion()
    {
        using var metadata=Assembly.GetExecutingAssembly().GetManifestResourceStream(ManifestResource);
        if(metadata is null)return InfrastructureRelease.Version;
        if(metadata.Length>16777216)throw new IOException("PACKAGE_METADATA_BOUND");using var buffer=new MemoryStream();metadata.CopyTo(buffer);
        var version=NativeJson.Read<InstallPackageManifest>(buffer.ToArray()).App?.Version;
        return version is not null&&ReleaseManifest.IsVersion(version)?version:throw new IOException("PACKAGE_FORMAT");
    }
    internal static InstallPackage Embedded()
    {
        var assembly=Assembly.GetExecutingAssembly();using var metadata=assembly.GetManifestResourceStream(ManifestResource)??throw new IOException("PACKAGE_NOT_EMBEDDED");
        if(metadata.Length>16777216)throw new IOException("PACKAGE_METADATA_BOUND");using var buffer=new MemoryStream();metadata.CopyTo(buffer);
        var payload=assembly.GetManifestResourceStream(PayloadResource)??throw new IOException("PACKAGE_NOT_EMBEDDED");
        try{return new(payload,buffer.ToArray());}catch{payload.Dispose();throw;}
    }
    internal static string Prefix(TreeKind kind)=>kind==TreeKind.Infrastructure?InfraPrefix:AppPrefix;
    // The members of one namespace, relative to its installed tree root.
    internal InstallMember[] Members(TreeKind kind)
    {var prefix=Prefix(kind);return Manifest.Files.Where(f=>f.Path.StartsWith(prefix,StringComparison.Ordinal)).Select(f=>f with{Path=f.Path[prefix.Length..]}).ToArray();}
    // The tree record is the archive's own member: its bytes are the ones the manifest
    // names, it parses, it carries the manifest's identity, and it lists exactly the
    // other members of its namespace with their sizes and digests.
    private void RequireTree(TreeKind kind,string version,string commit,string treeSha256)
    {
        var name=Prefix(kind)+TreeRecord.RecordName(kind);
        var member=Manifest.Files.SingleOrDefault(f=>f.Path==name)??throw new IOException("PACKAGE_MISSING");
        if(member.Sha256!=treeSha256||member.Size>TreeRecord.MaximumBytes)throw new IOException("PACKAGE_TREE_MISMATCH");
        var entry=archive.GetEntry(name)??throw new IOException("PACKAGE_MEMBER");
        byte[] bytes;using(var input=entry.Open())using(var buffer=new MemoryStream()){input.CopyTo(buffer);bytes=buffer.ToArray();}
        if(bytes.Length!=member.Size||TreeRecord.Sha256(bytes)!=treeSha256)throw new IOException("PACKAGE_TREE_MISMATCH");
        TreeRecord tree;try{tree=TreeRecord.Parse(bytes,kind);}catch(IOException){throw new IOException("PACKAGE_TREE_MISMATCH");}
        if(tree.Version!=version||tree.SourceCommit!=commit)throw new IOException("PACKAGE_TREE_MISMATCH");
        var others=Members(kind).Where(f=>f.Path!=TreeRecord.RecordName(kind)).ToDictionary(f=>f.Path,StringComparer.Ordinal);
        if(others.Count!=tree.Files.Length||tree.Files.Any(f=>!others.TryGetValue(f.Path,out var o)||o.Size!=f.Size||o.Sha256!=f.Sha256))throw new IOException("PACKAGE_TREE_MISMATCH");
    }
    // What the installer records for each tree this package installs.
    internal InfrastructureRecord InfrastructureRecord()
    {
        var infra=Manifest.Infrastructure;var files=Members(TreeKind.Infrastructure);
        return new(infra.Version,infra.ContractMin,infra.ContractMax,infra.SourceCommit,files.Single(f=>f.Path==Setup.InfrastructureRecord.EngineMember).Sha256,infra.TreeSha256,files);
    }
    internal AppRecord AppRecord(){var app=Manifest.App;return new(app.Version,app.SourceCommit,app.RequiresContract,app.TreeSha256,app.DatabaseSchema,Members(TreeKind.App));}
    internal InstalledPair Pair()=>new(InfrastructureRecord(),AppRecord());
    // A kept database is acceptable only at the target schema or at one this app
    // migrates from (8.3).
    internal bool AcceptsSchema(int schema)=>schema==Manifest.App.DatabaseSchema||Manifest.App.DatabaseMigrationsFrom.Contains(schema);
    // Writes every member of one namespace under root through a same-directory partial
    // that is committed by a no-replace rename, so a present member is always whole.
    // A present member is never rewritten: it must already be byte-identical.
    internal void Extract(TreeKind kind,string root,Action<int> progress,CancellationToken cancellation,ExtractTarget target)
    {
        var count=0;var full=Path.GetFullPath(root);var prefix=Prefix(kind);var members=Members(kind);
        foreach(var record in members)
        {
            cancellation.ThrowIfCancellationRequested();var path=Path.GetFullPath(Path.Combine(full,record.Path.Replace('/',Path.DirectorySeparatorChar)));
            if(!path.StartsWith(full+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("PACKAGE_PATH");
            target.CreateDirectory(Path.GetDirectoryName(path)!);
            var entry=archive.GetEntry(prefix+record.Path)??throw new IOException("PACKAGE_MEMBER");if(entry.Length!=record.Size)throw new IOException("PACKAGE_SIZE");
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
            if(++count%100==0||count==members.Length)progress(count*100/members.Length);
        }
    }
    // An interrupted extraction leaves only expected members and their uncommitted
    // partials. Anything else refuses; the partials are returned for the caller to
    // delete under its own ACL checks.
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
    // infra/<infrastructure member or infra-tree.json>, apps/<app member or app-tree.json>.
    internal static void Name(string name)
    {
        if(name is null||!MemberNames.Valid(name))throw new IOException("PACKAGE_PATH");
        foreach(var kind in new[]{TreeKind.Infrastructure,TreeKind.App})
        {
            var prefix=Prefix(kind);if(!name.StartsWith(prefix,StringComparison.Ordinal))continue;
            var member=name[prefix.Length..];if(member==TreeRecord.RecordName(kind)||TreeRecord.InTree(kind,member))return;
        }
        throw new IOException("PACKAGE_PATH");
    }
    private static void Digest(string? value){if(!TreeRecord.IsDigest(value))throw new IOException("PACKAGE_DIGEST");}
    public void Dispose(){archive.Dispose();stream.Dispose();}
}

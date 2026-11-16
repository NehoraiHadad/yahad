using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Yahad.Install;
namespace Yahad.Setup;

// yahad-windows-app-<v>.zip (docs/WINDOWS-APP-PACKAGE-PLAN.md 5.2): exactly
// app-tree.json and the members it lists under app/, nothing else. Its bytes are the
// signed manifest's before the zip is opened, every name obeys the install package's
// rules, and no member is code the Runtime could load natively: the server is one
// self-contained ESM file. The required members are what the Runtime starts and the
// LAN help page reads (NativeLanInstall.RequirePayload), plus the legal record.
internal sealed class AppPackage:IDisposable
{
    internal static readonly string[] Required=["app/package.json","app/server/main.mjs","app/dist/index.html","app/deploy/lan-capabilities.json","app/deploy/lan-help.html",
        "app/legal/server-bundle.json","app/legal/ThirdPartyNotices-server.txt"];
    private static readonly string[] ForbiddenDirectories=["app/src/","app/node_modules/","app/native/"];
    private static readonly string[] ForbiddenExtensions=[".node",".exe",".dll",".ps1",".cmd",".bat"];
    private const string ForbiddenFile="app/package-lock.json";
    private readonly Stream stream;private readonly ZipArchive archive;
    // What the installer records for the tree: the signed identity and the members,
    // the tree record among them.
    internal AppRecord App{get;}
    internal long Bytes=>App.Files.Sum(f=>f.Size);
    internal static AppPackage Open(string path,ReleaseManifest release)
    {
        var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        try{return new(file,release);}catch{file.Dispose();throw;}
    }
    // The stream is read once for the digest and then opened as the zip, so the bytes
    // checked are the bytes read.
    internal AppPackage(Stream zip,ReleaseManifest release)
    {
        stream=zip;var signed=release.App;
        if(!zip.CanSeek||zip.Length!=signed.Archive.Bytes)throw new IOException("RELEASE_APP_SIZE");
        if(Convert.ToHexStringLower(SHA256.HashData(zip))!=signed.Archive.Sha256)throw new IOException("RELEASE_APP_HASH");zip.Position=0;
        try{archive=new(zip,ZipArchiveMode.Read,true);}catch(InvalidDataException){throw new IOException("APP_PACKAGE_ZIP");}
        try
        {
            if(archive.Entries.Count is <2 or >TreeRecord.MaximumMembers+1)throw new IOException("APP_PACKAGE_BOUND");
            // Paths compare ignoring case: two spellings are one Windows path.
            var folded=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
            foreach(var entry in archive.Entries)
            {
                Name(entry.FullName);if(!folded.Add(entry.FullName))throw new IOException("APP_PACKAGE_COLLISION");
                if(!ArchiveExtraction.RegularFile(entry))throw new IOException("APP_PACKAGE_MEMBER_TYPE");
                if(entry.Length>TreeRecord.MaximumMemberBytes||(total+=entry.Length)>TreeRecord.MaximumTotalBytes)throw new IOException("APP_PACKAGE_BOUND");
            }
            var tree=RequireTree(release);
            // The member set is the record's, exactly and ordinally, sizes included.
            var listed=tree.Files.ToDictionary(f=>f.Path,StringComparer.Ordinal);
            if(archive.Entries.Count!=listed.Count+1||archive.Entries.Any(e=>e.FullName!=InstalledLayout.AppTreeName&&(!listed.TryGetValue(e.FullName,out var f)||f.Size!=e.Length)))
                throw new IOException("APP_PACKAGE_MEMBERS");
            foreach(var required in Required)if(!listed.ContainsKey(required))throw new IOException("APP_PACKAGE_MISSING");
            // Whatever its name, no member is a PE image.
            var head=new byte[2];
            foreach(var entry in archive.Entries)
            {
                using var input=entry.Open();var read=0;int n;
                while(read<2&&(n=input.Read(head,read,2-read))>0)read+=n;
                if(read==2&&head[0]==(byte)'M'&&head[1]==(byte)'Z')throw new IOException("APP_PACKAGE_PE");
            }
            var record=archive.GetEntry(InstalledLayout.AppTreeName)!;
            App=new(release.Version,release.SourceCommit,signed.RequiresContract,signed.TreeSha256,signed.DatabaseSchema,
                [..tree.Files.Select(f=>new InstallMember(f.Path,f.Size,f.Sha256)),new(InstalledLayout.AppTreeName,record.Length,signed.TreeSha256)]);
        }
        catch{archive.Dispose();throw;}
    }
    // The record is the archive's own member, the bytes the manifest names, and it
    // carries the release's identity.
    private TreeRecord RequireTree(ReleaseManifest release)
    {
        var entry=archive.GetEntry(InstalledLayout.AppTreeName)??throw new IOException("APP_PACKAGE_MISSING");
        if(entry.Length>TreeRecord.MaximumBytes)throw new IOException("APP_PACKAGE_TREE_MISMATCH");
        byte[] bytes;using(var input=entry.Open())using(var buffer=new MemoryStream()){input.CopyTo(buffer);bytes=buffer.ToArray();}
        if(bytes.Length!=entry.Length||TreeRecord.Sha256(bytes)!=release.App.TreeSha256)throw new IOException("APP_PACKAGE_TREE_MISMATCH");
        TreeRecord tree;try{tree=TreeRecord.Parse(bytes,TreeKind.App);}catch(IOException){throw new IOException("APP_PACKAGE_TREE_MISMATCH");}
        if(tree.Version!=release.Version||tree.SourceCommit!=release.SourceCommit)throw new IOException("APP_PACKAGE_TREE_MISMATCH");
        return tree;
    }
    // app-tree.json or app/<member>: the install package's name rules, then the refused
    // source, dependency, native and executable members.
    internal static void Name(string name)
    {
        if(name is null||!MemberNames.Valid(name))throw new IOException("APP_PACKAGE_PATH");
        if(name==InstalledLayout.AppTreeName)return;
        if(ForbiddenDirectories.Any(d=>name.StartsWith(d,StringComparison.OrdinalIgnoreCase))||string.Equals(name,ForbiddenFile,StringComparison.OrdinalIgnoreCase)||
            ForbiddenExtensions.Any(e=>name.EndsWith(e,StringComparison.OrdinalIgnoreCase)))throw new IOException("APP_PACKAGE_FORBIDDEN");
        if(!TreeRecord.InTree(TreeKind.App,name))throw new IOException("APP_PACKAGE_PATH");
    }
    // Into the tree root (apps\<v>), each member size- and hash-checked as it is written.
    internal void Extract(string root,Action<int> progress,CancellationToken cancellation,ExtractTarget target)
        =>ArchiveExtraction.Extract(archive,"",App.Files,root,progress,cancellation,target,"APP_PACKAGE_");
    public void Dispose(){archive.Dispose();stream.Dispose();}
}

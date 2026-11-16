using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Yahad.Install;
namespace Yahad.Setup;

// yahad-windows-app-<v>.zip (docs/WINDOWS-APP-PACKAGE-PLAN.md 5.2): exactly
// app-tree.json and the members it lists under app/, nothing else. Its bytes are the
// signed manifest's before the zip is opened, every name obeys the install package's
// rules, and every member obeys the app member policy (AppMemberPolicy), the one the
// install package's apps/ namespace obeys too.
internal sealed class AppPackage:IDisposable
{
    private const string Codes="APP_PACKAGE_";
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
            foreach(var required in AppMemberPolicy.Required)if(!listed.ContainsKey(required))throw new IOException("APP_PACKAGE_MISSING");
            foreach(var entry in archive.Entries)AppMemberPolicy.RequireNotImage(entry,Codes);
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
    // app-tree.json or app/<member>: the install package's name rules, then the app
    // member policy.
    internal static void Name(string name)
    {
        if(name is null||!MemberNames.Valid(name))throw new IOException(Codes+"PATH");
        if(name!=InstalledLayout.AppTreeName)AppMemberPolicy.RequireName(name,Codes);
    }
    // Into the tree root (apps\<v>), each member size- and hash-checked as it is written.
    internal void Extract(string root,Action<int> progress,CancellationToken cancellation,ExtractTarget target)
        =>ArchiveExtraction.Extract(archive,"",App.Files,root,progress,cancellation,target,"APP_PACKAGE_");
    public void Dispose(){archive.Dispose();stream.Dispose();}
}

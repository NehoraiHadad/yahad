using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
namespace Yahad.Setup;

internal sealed record InstallMember(string Path,long Size,string Sha256);
internal sealed record PackageInfrastructure(string Version,int ContractMin,int ContractMax,string SourceCommit,string TreeSha256);
internal sealed record PackageApp(string Version,string SourceCommit,int RequiresContract,string TreeSha256,int DatabaseSchema,int[] DatabaseMigrationsFrom);
// The oldest installed pair this release moves from: the release manifest's
// minimumFrom (5.1), carried by the Setup for its manual path.
internal sealed record PackageMinimumFrom(string App,string Infrastructure);
internal sealed record InstallPackageManifest(int Format,PackageInfrastructure Infrastructure,PackageApp App,PackageMinimumFrom MinimumFrom,string ZipSha256,InstallMember[] Files)
{
    // The free space an install requires on the binary root's drive: the extracted
    // trees with room for their partials and a later upgrade, plus 512 MiB. The
    // install check and the DISK_SPACE text both name this one number.
    internal long RequiredFreeBytes()=>checked(Files.Sum(f=>f.Size)*3+536870912L);
}
// Where Extract writes: protected directories, protected files and a no-replace commit.
internal sealed record ExtractTarget(Action<string> CreateDirectory,Func<string,FileStream> CreateFile,Action<string,string> Commit,Func<string,InstallMember,bool> ExistingMatches);
// One format-2 archive (docs/WINDOWS-APP-PACKAGE-PLAN.md 4.2, 5.3) holding two
// namespaces and nothing else: infra/ (infra-tree.json and the host, Updater,
// Platform, runtime, legal and setup members of the compiled infrastructure) and
// apps/ (app-tree.json and app/...). Each tree record lists exactly the members
// of its namespace. The apps/ namespace obeys the app member policy (AppMemberPolicy),
// the app archive's own, so an app is accepted or refused alike whichever package
// carries it. The required members are installer/windows/setup_package.py's
// REQUIRED_MEMBERS: the reader refuses what the writer would.
// The engine member (infra/setup/Yahad.Setup.exe) is listed in Files and in
// infra-tree.json but is not an archive entry: its bytes are the Setup image's own
// prefix (SetupImage), checked against the manifest's size and digest when the
// package opens and again as each copy is written.
internal sealed class InstallPackage:IDisposable
{
    internal const int Format=2;
    internal const string InfraPrefix="infra/",AppPrefix="apps/";
    internal static readonly string EnginePath=InfraPrefix+Setup.InfrastructureRecord.EngineMember;
    private const string Codes="PACKAGE_";
    internal static readonly string[] Required=["infra/host/Yahad.ServiceHost.exe","infra/updater/Yahad.Updater.exe",
        "infra/platform/Yahad.Platform.exe","infra/runtime/node/node.exe","infra/runtime/caddy/caddy.exe","infra/"+Setup.InfrastructureRecord.EngineMember,
        ..AppMemberPolicy.Required.Select(m=>AppPrefix+m)];
    private readonly Stream stream;private readonly ZipArchive archive;private readonly Func<Stream> engine;private SetupImage? image;
    internal InstallPackageManifest Manifest{get;}
    // engine opens a fresh read of the engine member's bytes each time it is called.
    internal InstallPackage(Stream zip,byte[] manifest,Func<Stream> engine)
    {
        stream=zip;this.engine=engine;
        try{Manifest=NativeJson.Read(manifest,SetupJsonContext.Default.InstallPackageManifest);}catch(JsonException){throw new IOException("PACKAGE_FORMAT");}
        var infra=Manifest.Infrastructure;var app=Manifest.App;var minimum=Manifest.MinimumFrom;
        if(Manifest.Format!=Format||infra is null||app is null||minimum is null||Manifest.Files is not {Length:>=16 and <=2*TreeRecord.MaximumMembers+2}||app.DatabaseMigrationsFrom is null||
            !SemVer.IsVersion(infra.Version)||!SemVer.IsVersion(app.Version)||!TreeRecord.IsCommit(infra.SourceCommit)||!TreeRecord.IsCommit(app.SourceCommit)||
            app.DatabaseSchema is <1 or >1024||!InfrastructureRelease.IsContract(app.RequiresContract)||
            // A release may not admit a source newer than itself.
            minimum.App is null||minimum.Infrastructure is null||!SemVer.IsVersion(minimum.App)||!SemVer.IsVersion(minimum.Infrastructure)||
            SemVer.Compare(minimum.App,app.Version)>0||SemVer.Compare(minimum.Infrastructure,infra.Version)>0)throw new IOException("PACKAGE_FORMAT");
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
        // One archive entry per manifest path, matched exactly and ordinally, as Extract
        // reads it; names that differ only in case are one Windows path and refused.
        var expected=new HashSet<string>(StringComparer.Ordinal);var folded=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
        foreach(var record in Manifest.Files)
        {
            if(record is null)throw new IOException("PACKAGE_FORMAT");
            Name(record.Path);Digest(record.Sha256);if(!folded.Add(record.Path)||!expected.Add(record.Path)||record.Size is <0 or >TreeRecord.MaximumMemberBytes||(total+=record.Size)>2147483648)throw new IOException("PACKAGE_BOUND");
        }
        // Every member but the engine is an archive entry.
        if(!expected.Remove(EnginePath))throw new IOException("PACKAGE_MISSING");
        if(archive.Entries.Count!=expected.Count)throw new IOException("PACKAGE_MEMBERS");
        foreach(var entry in archive.Entries)
        {
            Name(entry.FullName);if(!expected.Remove(entry.FullName)||!ArchiveExtraction.RegularFile(entry))throw new IOException("PACKAGE_MEMBER_TYPE");
        }
        if(expected.Count!=0)throw new IOException("PACKAGE_MEMBERS");
        foreach(var required in Required)if(!Manifest.Files.Any(r=>r.Path==required))throw new IOException("PACKAGE_MISSING");
        var engineRecord=Manifest.Files.Single(r=>r.Path==EnginePath);
        using(var input=engine())if(input.Length!=engineRecord.Size||Convert.ToHexStringLower(SHA256.HashData(input))!=engineRecord.Sha256)throw new IOException("PACKAGE_ENGINE");
        foreach(var entry in archive.Entries)if(entry.FullName.StartsWith(AppPrefix,StringComparison.Ordinal))AppMemberPolicy.RequireNotImage(entry,Codes);
        RequireTree(TreeKind.Infrastructure,infra.Version,infra.SourceCommit,infra.TreeSha256);
        RequireTree(TreeKind.App,app.Version,app.SourceCommit,app.TreeSha256);
        // Each list the installer record will carry fits its bound (InstallerState).
        foreach(var kind in new[]{TreeKind.Infrastructure,TreeKind.App})if(!InstallerState.WithinListBound(Members(kind)))throw new IOException("PACKAGE_BOUND");
    }
    // The running image carries a package exactly when it ends in a Setup image trailer;
    // the installed engine (the bare prefix) does not.
    internal static bool IsEmbedded{get{using var image=SetupImage.OpenRunning();return image is not null;}}
    // The app this image installs, read from the manifest alone (the archive is verified
    // only when it is installed).
    internal static string DisplayVersion()
    {
        using var image=SetupImage.OpenRunning()??throw new IOException("PACKAGE_NOT_EMBEDDED");
        var version=NativeJson.Read(image.ReadManifest(),SetupJsonContext.Default.InstallPackageManifest).App?.Version;
        return version is not null&&SemVer.IsVersion(version)?version:throw new IOException("PACKAGE_FORMAT");
    }
    // The free space this image's install requires, read from the manifest alone.
    internal static long RequiredFreeBytes()
    {
        using var image=SetupImage.OpenRunning()??throw new IOException("PACKAGE_NOT_EMBEDDED");
        return NativeJson.Read(image.ReadManifest(),SetupJsonContext.Default.InstallPackageManifest).RequiredFreeBytes();
    }
    // The package of the running image, read through the one handle that proved it is
    // that image; the package holds the handle until it is disposed.
    internal static InstallPackage Embedded()=>From(SetupImage.OpenRunning());
    internal static InstallPackage FromImage(string path)=>From(SetupImage.Open(path));
    private static InstallPackage From(SetupImage? image)
    {
        if(image is null)throw new IOException("PACKAGE_NOT_EMBEDDED");
        try{return new(image.Zip(),image.ReadManifest(),image.Engine){image=image};}catch{image.Dispose();throw;}
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
    // The manual path and a reinstall over a kept record move only from a pair this
    // release supports, as selection does for the signed manifest (ReleaseSelection).
    internal void RequireFrom(InstalledPair installed)
    {
        if(SemVer.Compare(installed.App.Version,Manifest.MinimumFrom.App)<0||SemVer.Compare(installed.Infrastructure.Version,Manifest.MinimumFrom.Infrastructure)<0)
            throw new IOException("RELEASE_UNSUPPORTED_FROM");
    }
    internal void Extract(TreeKind kind,string root,Action<int> progress,CancellationToken cancellation,ExtractTarget target)
        =>ArchiveExtraction.Extract(record=>kind==TreeKind.Infrastructure&&record.Path==Setup.InfrastructureRecord.EngineMember?engine():ArchiveExtraction.Entry(archive,Prefix(kind),record,Codes),
            Members(kind),root,progress,cancellation,target,Codes);
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
            var member=name[prefix.Length..];if(member==TreeRecord.RecordName(kind))return;
            if(kind==TreeKind.App){AppMemberPolicy.RequireName(member,Codes);return;}
            if(TreeRecord.InTree(kind,member))return;
        }
        throw new IOException("PACKAGE_PATH");
    }
    private static void Digest(string? value){if(!TreeRecord.IsDigest(value))throw new IOException("PACKAGE_DIGEST");}
    public void Dispose(){archive.Dispose();stream.Dispose();image?.Dispose();}
}
// The member rules both archives share: the install package and the app archive (5.2).
internal static class ArchiveExtraction
{
    // A regular file: no directory, symbolic link or reparse entry.
    internal static bool RegularFile(ZipArchiveEntry entry)=>entry.Length>=0&&((entry.ExternalAttributes>>16)&0xf000) is 0 or 0x8000&&(entry.ExternalAttributes&0x400)==0;
    // The bytes of one member, an entry of the record's size.
    internal static Stream Entry(ZipArchive archive,string prefix,InstallMember record,string codes)
    {
        var entry=archive.GetEntry(prefix+record.Path)??throw new IOException(codes+"MEMBER");if(entry.Length!=record.Size)throw new IOException(codes+"SIZE");
        return entry.Open();
    }
    internal static void Extract(ZipArchive archive,string prefix,InstallMember[] members,string root,Action<int> progress,CancellationToken cancellation,ExtractTarget target,string codes)
        =>Extract(record=>Entry(archive,prefix,record,codes),members,root,progress,cancellation,target,codes);
    // Writes every member of one namespace under root through a same-directory partial
    // that is committed by a no-replace rename, so a present member is always whole.
    // A present member is never rewritten: it must already be byte-identical. open reads
    // one member's bytes; codes is the refusal prefix of the package being read.
    internal static void Extract(Func<InstallMember,Stream> open,InstallMember[] members,string root,Action<int> progress,CancellationToken cancellation,ExtractTarget target,string codes)
    {
        var count=0;var full=Path.GetFullPath(root);
        foreach(var record in members)
        {
            cancellation.ThrowIfCancellationRequested();var path=Path.GetFullPath(Path.Combine(full,record.Path.Replace('/',Path.DirectorySeparatorChar)));
            if(!path.StartsWith(full+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException(codes+"PATH");
            target.CreateDirectory(Path.GetDirectoryName(path)!);
            if(File.Exists(path)){if(!target.ExistingMatches(path,record))throw new IOException("INSTALL_TARGET_CHANGED");}
            else
            {
                var partial=path+".partial-"+Guid.NewGuid().ToString("N");
                try
                {
                    using(var input=open(record))using(var output=target.CreateFile(partial))
                    {
                        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var chunk=new byte[1048576];long bytes=0;int n;
                        while((n=input.Read(chunk))>0){cancellation.ThrowIfCancellationRequested();if((bytes+=n)>record.Size)throw new IOException(codes+"SIZE");hash.AppendData(chunk,0,n);output.Write(chunk,0,n);}
                        if(bytes!=record.Size||Convert.ToHexStringLower(hash.GetHashAndReset())!=record.Sha256)throw new IOException(codes+"MEMBER_HASH");output.Flush(true);
                    }
                    target.Commit(partial,path);
                }
                catch{try{File.Delete(partial);}catch(IOException){}catch(UnauthorizedAccessException){}throw;}
            }
            if(++count%100==0||count==members.Length)progress(count*100/members.Length);
        }
    }
}
// The distributable Setup: the payload-free engine image, then its install package.
//   [engine][install.zip][install-manifest.json][trailer]
// The trailer is the last 40 bytes: Magic, then the three part lengths as little-endian
// int64, which with the trailer sum to exactly the file length. The engine part is the
// bytes of the engine member, so the package does not carry the engine twice
// (installer/windows/setup_package.py write_image writes this layout).
// One handle, opened without write or delete sharing, serves every part: while it is
// open the file can be neither rewritten nor renamed, so every read is of one file.
internal sealed class SetupImage:IDisposable
{
    internal const int TrailerLength=40;
    internal static ReadOnlySpan<byte> Magic=>"Yahad.SetupImage"u8;
    private readonly SafeFileHandle file;private readonly long engineLength,zipLength,manifestLength;
    private SetupImage(SafeFileHandle file,long engine,long zip,long manifest){this.file=file;engineLength=engine;zipLength=zip;manifestLength=manifest;}
    // The running process's own image. Environment.ProcessPath names whatever file has
    // that path now, which an unelevated process may have renamed away and replaced
    // after the elevation prompt; the image is accepted only when the opened file is
    // the one this process was loaded from (LoadedImage).
    internal static SetupImage? OpenRunning()
        =>Open(Environment.ProcessPath??throw new IOException("PACKAGE_NOT_EMBEDDED"),requireLoaded:true);
    // null when the file carries no trailer; a trailer whose lengths do not describe the
    // file is refused.
    internal static SetupImage? Open(string path)=>Open(path,requireLoaded:false);
    private static SetupImage? Open(string path,bool requireLoaded)
    {
        var handle=File.OpenHandle(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        try
        {
            if(requireLoaded&&!LoadedImage.Is(handle))throw new IOException("PACKAGE_IMAGE_IDENTITY");
            var length=RandomAccess.GetLength(handle);if(length<TrailerLength){handle.Dispose();return null;}
            Span<byte> trailer=stackalloc byte[TrailerLength];ReadExactly(handle,trailer,length-TrailerLength);
            if(!trailer[..Magic.Length].SequenceEqual(Magic)){handle.Dispose();return null;}
            var engine=BinaryPrimitives.ReadInt64LittleEndian(trailer[16..]);var zip=BinaryPrimitives.ReadInt64LittleEndian(trailer[24..]);var manifest=BinaryPrimitives.ReadInt64LittleEndian(trailer[32..]);
            if(engine is <1 or >TreeRecord.MaximumMemberBytes||zip is <1 or >2147483648||manifest is <1 or >16777216||engine+zip+manifest+TrailerLength!=length)throw new IOException("PACKAGE_IMAGE");
            return new(handle,engine,zip,manifest);
        }
        catch{handle.Dispose();throw;}
    }
    internal Stream Engine()=>new ImagePart(file,0,engineLength);
    internal Stream Zip()=>new ImagePart(file,engineLength,zipLength);
    internal byte[] ReadManifest(){var bytes=new byte[manifestLength];ReadExactly(file,bytes,engineLength+zipLength);return bytes;}
    private static void ReadExactly(SafeFileHandle handle,Span<byte> buffer,long offset)
    {
        while(buffer.Length>0){var n=RandomAccess.Read(handle,buffer,offset);if(n<=0)throw new IOException("PACKAGE_IMAGE");buffer=buffer[n..];offset+=n;}
    }
    public void Dispose()=>file.Dispose();
}
// Whether an open file is the image this process was loaded from. Names cannot say:
// a case-sensitive directory holds Setup.exe and setup.exe, and a file has as many
// names as it has hard links. So the file at the name the memory manager reports for
// the main module (which follows renames) is opened and the two are compared by
// volume and file ID. That name is read again while the second handle, which denies
// rename and delete, is open: unchanged, it still names the image, and the image is
// the file that handle holds. A file ID names a file, not one of its data streams:
// the running image's default stream can be renamed to a named stream and a new
// default stream written, so both the mapped name and the held handle must be the
// default stream (no ":stream" in the last component; NTFS names hold no ':').
internal static class LoadedImage
{
    internal static bool Is(SafeFileHandle file)=>Is(file,MappedName);
    // mapped reports the main module's NT path; tests stand in for the memory manager.
    internal static bool Is(SafeFileHandle file,Func<string?> mapped)
    {
        var name=mapped();if(name is null||NamesStream(name)||NtName(file) is not {} held||NamesStream(held))return false;
        SafeFileHandle named;
        try{named=File.OpenHandle(@"\\?\GLOBALROOT"+name,FileMode.Open,FileAccess.Read,FileShare.Read);}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){return false;}
        using(named)return Identity(file) is {} opened&&Identity(named)==opened&&mapped()==name;
    }
    private static bool NamesStream(string ntName)=>ntName.AsSpan(ntName.LastIndexOf('\\')+1).Contains(':');
    // The NT path of a handle's file, as GetMappedFileName reports one.
    internal static string? NtName(SafeFileHandle file)
    {
        var buffer=new char[32768];var n=GetFinalPathNameByHandleW(file,buffer,(uint)buffer.Length,VolumeNameNt);
        return n is >0 and <32768?new string(buffer,0,(int)n):null;
    }
    private static string? MappedName()
    {
        var buffer=new char[32768];var n=GetMappedFileNameW(GetCurrentProcess(),GetModuleHandleW(null),buffer,(uint)buffer.Length);
        return n is >0 and <32768?new string(buffer,0,(int)n):null;
    }
    // The 128-bit file ID where the file system has one (NTFS, ReFS), else the volume
    // serial and 64-bit file index (FAT, exFAT); both handles take the same branch.
    private static string? Identity(SafeFileHandle file)
    {
        var id=new byte[24];if(GetFileInformationByHandleEx(file,FileIdInfo,id,(uint)id.Length))return "id:"+Convert.ToHexString(id);
        var info=new byte[52];return GetFileInformationByHandle(file,info)?"index:"+Convert.ToHexString(info,28,4)+Convert.ToHexString(info,44,8):null;
    }
    private const uint VolumeNameNt=2;private const int FileIdInfo=18;
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file,[Out] char[] path,uint length,uint flags);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandleEx(SafeFileHandle file,int kind,[Out] byte[] info,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandle(SafeFileHandle file,[Out] byte[] info);
    [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll",EntryPoint="K32GetMappedFileNameW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetMappedFileNameW(IntPtr process,IntPtr address,[Out] char[] name,uint length);
}
// A read-only, seekable window [start, start+length) of an image's handle, read by
// offset; the image owns the handle.
internal sealed class ImagePart(SafeFileHandle file,long start,long length):Stream
{
    private long position;
    public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;
    public override long Length=>length;
    public override long Position{get=>position;set=>position=value>=0?value:throw new ArgumentOutOfRangeException(nameof(value));}
    public override int Read(byte[] buffer,int offset,int count)=>Read(buffer.AsSpan(offset,count));
    public override int Read(Span<byte> buffer)
    {
        if(position>=length||buffer.Length==0)return 0;
        var n=RandomAccess.Read(file,buffer[..(int)Math.Min(buffer.Length,length-position)],start+position);position+=n;return n;
    }
    public override long Seek(long offset,SeekOrigin origin)=>Position=origin switch{SeekOrigin.Begin=>offset,SeekOrigin.Current=>position+offset,SeekOrigin.End=>length+offset,_=>throw new ArgumentOutOfRangeException(nameof(origin))};
    public override void Flush(){}
    public override void SetLength(long value)=>throw new NotSupportedException();
    public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}

using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Yahad.Install;

// Versioned staging only. Live service/helper paths, activation, source
// enrollment and family data are outside this writer's fixed relative namespace.
internal sealed class InstalledSystemSlots
{
    private readonly InstalledStateFiles files;
    private readonly Func<long> availableBytes;
    private readonly long allocationUnit;
    private readonly Action<string>? fault;
    internal string BinaryRoot=>files.Root;
    internal InstalledSystemSlots(InstalledStateFiles binaryFiles,Func<long>? availableBytes=null,Action<string>? fault=null)
    {
        if(binaryFiles.IsSingleService||binaryFiles.IsRuntimeProfile)throw Error("AUTHORITY_REQUIRED");
        if(binaryFiles.Roles is not null&&!string.Equals(binaryFiles.Root,InstalledUpdaterLayout.BinaryRoot,StringComparison.OrdinalIgnoreCase))throw Error("BINARY_ROOT");
        files=binaryFiles;this.fault=fault;this.availableBytes=availableBytes??(()=>new DriveInfo(Path.GetPathRoot(files.Root)!).AvailableFreeSpace);
        if(!GetDiskFreeSpaceW(Path.GetPathRoot(files.Root)!,out var sectors,out var bytes,out _,out _))throw Error("VOLUME_GEOMETRY");allocationUnit=(long)sectors*bytes;if(allocationUnit is <512 or >2*1024*1024)throw Error("VOLUME_GEOMETRY");
    }
    internal string SlotRoot(string systemDigest){FixedSystemComponents.Digest(systemDigest);return files.PathOf(FixedSystemComponents.SlotsDirectory+"/"+systemDigest);}
    internal byte[] ReadManifest(string digest)=>files.Read(Child(SlotRoot(digest),"system-manifest.json"),8*1024*1024);
    internal Stream OpenFile(string digest,SystemComponentDescriptor component,SystemComponentFile file)
    {
        if(!component.Files.Any(f=>f.RelativePath==file.RelativePath&&f.Size==file.Size&&f.Sha256==file.Sha256))throw Error("FILE_SCOPE");
        return new PinnedStream(files.Open(Child(SlotRoot(digest),component.SlotRelativeDirectory+"/"+file.RelativePath)));
    }
    internal string Stage(string digest,byte[] systemManifest,IReadOnlyList<SystemComponentDescriptor> components,Func<string,Stream> openMember,CancellationToken cancellation)
    {
        FixedSystemComponents.Digest(digest);if(Hash(systemManifest)!=digest||systemManifest.Length is <1 or >8*1024*1024)throw Error("MANIFEST");
        RequireComponents(components);files.ValidateRoot();var parent=files.PathOf(FixedSystemComponents.SlotsDirectory);files.EnsureDirectory(parent);
        var recovery=new NativeIntakeRecovery(files,"system-slots/.intake-recovery",fault,bytes=>RequireSpace(Allocated(bytes)+allocationUnit));recovery.Reconcile(cancellation);
        if(Directory.EnumerateFileSystemEntries(parent,".staging-*").Any())throw Error("RECONCILIATION_REQUIRED");
        var destination=SlotRoot(digest);
        if(Directory.Exists(destination)){Verify(digest,systemManifest,components,cancellation);return destination;}
        var needed=Allocated(systemManifest.LongLength);var directories=new HashSet<string>(StringComparer.Ordinal);
        foreach(var component in components)foreach(var file in component.Files){needed=checked(needed+Allocated(file.Size));var name=component.SlotRelativeDirectory+"/"+file.RelativePath;for(var at=name.IndexOf('/');at>=0;at=name.IndexOf('/',at+1))directories.Add(name[..at]);}
        RequireSpace(checked(needed+(directories.Count+4L)*allocationUnit));
        var members=components.SelectMany(c=>c.Files.Select(f=>new NativeIntakeMember(c.SlotRelativeDirectory+"/"+f.RelativePath,f.Size,f.Sha256))).Append(new("system-manifest.json",systemManifest.Length,digest));
        var staging=recovery.Stage("system",digest,members);
        foreach(var component in components)foreach(var file in component.Files)
        {
            cancellation.ThrowIfCancellationRequested();var relative=component.SlotRelativeDirectory+"/"+file.RelativePath;var path=Child(staging,relative);Parents(staging,Path.GetDirectoryName(path)!);
            RequireSpace(Allocated(file.Size)+allocationUnit);using var input=openMember(relative);using var output=files.Create(path);Copy(input,output,file.Size,file.Sha256,cancellation,bytes=>RequireSpace(Allocated(bytes)+allocationUnit));output.Flush(true);
        }
        Write(Child(staging,"system-manifest.json"),systemManifest);
        VerifyDirectory(staging,systemManifest,components,cancellation);cancellation.ThrowIfCancellationRequested();
        fault?.Invoke("system_slot_verified");recovery.PublishedDirectory(staging,destination);fault?.Invoke("system_slot_published");
        return destination;
    }
    internal void Verify(string digest,byte[] manifest,IReadOnlyList<SystemComponentDescriptor> components,CancellationToken cancellation)
    {FixedSystemComponents.Digest(digest);if(Hash(manifest)!=digest)throw Error("MANIFEST");RequireComponents(components);VerifyDirectory(SlotRoot(digest),manifest,components,cancellation);}
    private void VerifyDirectory(string root,byte[] manifest,IReadOnlyList<SystemComponentDescriptor> components,CancellationToken cancellation)
    {
        using var pin=files.PinDirectory(root);if(!files.Read(Child(root,"system-manifest.json"),8*1024*1024).SequenceEqual(manifest))throw Error("MANIFEST");
        var expected=new Dictionary<string,SystemComponentFile>(StringComparer.Ordinal);
        foreach(var component in components)foreach(var file in component.Files)expected.Add(component.SlotRelativeDirectory+"/"+file.RelativePath,file);
        var directories=new HashSet<string>(StringComparer.Ordinal);foreach(var name in expected.Keys)for(var at=name.IndexOf('/');at>=0;at=name.IndexOf('/',at+1))directories.Add(name[..at]);
        var observed=new HashSet<string>(StringComparer.Ordinal);var count=0;
        void Walk(string directory,int depth)
        {
            cancellation.ThrowIfCancellationRequested();if(depth>64)throw Error("DEPTH");using var held=files.PinDirectory(directory);
            foreach(var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellation.ThrowIfCancellationRequested();if(++count>150000||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw Error("CLOSURE");var relative=Path.GetRelativePath(root,path).Replace('\\','/');
                if(Directory.Exists(path)){if(!directories.Contains(relative))throw Error("CLOSURE");Walk(path,depth+1);}else if(!observed.Add(relative))throw Error("CLOSURE");
            }
        }
        Walk(root,0);if(!observed.SetEquals(expected.Keys.Append("system-manifest.json")))throw Error("CLOSURE");
        foreach(var pair in expected){using var input=new PinnedStream(files.Open(Child(root,pair.Key)));Copy(input,Stream.Null,pair.Value.Size,pair.Value.Sha256,cancellation);}
    }
    private static void RequireComponents(IReadOnlyList<SystemComponentDescriptor> components)
    {if(components.Count!=4||components.Select(c=>c.Kind).Distinct().Count()!=4||components.Sum(c=>c.Files.Count)>50000||components.Sum(c=>c.Files.Sum(f=>f.Size))>2L*1024*1024*1024)throw Error("COMPONENTS");}
    private string Child(string root,string relative)
    {
        var path=files.PathOf(Path.GetRelativePath(files.Root,Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar))));
        if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw Error("PATH");return path;
    }
    private void Parents(string root,string directory)
    {if(directory==root)return;Parents(root,Path.GetDirectoryName(directory)!);if(Directory.Exists(directory)){using var held=files.PinDirectory(directory);}else files.CreateDirectory(directory);}
    private void RequireSpace(long additional){var free=availableBytes();if(free<0||free<checked(512L*1024*1024+additional))throw Error("DISK_SPACE");}
    private long Allocated(long bytes)=>checked(((Math.Max(1,bytes)+allocationUnit-1)/allocationUnit)*allocationUnit);
    private void Write(string path,byte[] bytes){RequireSpace(Allocated(bytes.LongLength)+allocationUnit);using var output=files.Create(path);output.Write(bytes);output.Flush(true);}
    internal static void Copy(Stream input,Stream output,long size,string digest,CancellationToken cancellation,Action<long>? beforeWrite=null)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[65536];long total=0;
        while(true){cancellation.ThrowIfCancellationRequested();var read=input.Read(buffer);if(read==0)break;if((total+=read)>size)throw Error("FILE_BOUND");hash.AppendData(buffer.AsSpan(0,read));beforeWrite?.Invoke(read);output.Write(buffer,0,read);}
        if(total!=size||Convert.ToHexStringLower(hash.GetHashAndReset())!=digest)throw Error("FILE_BYTES");
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static IOException Error(string code)=>new("SYSTEM_SLOTS_"+code);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool MoveFileExW(string from,string to,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetDiskFreeSpaceW(string root,out uint sectors,out uint bytes,out uint availableClusters,out uint totalClusters);
    internal sealed class PinnedStream(WindowsFileBoundary.PrivateReadLease lease):Stream
    {
        private long position;public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;public override long Length=>lease.Length;
        public override long Position{get=>position;set{if(value<0)throw Error("SEEK");position=value;}}
        public override int Read(byte[] buffer,int offset,int count)=>Read(buffer.AsSpan(offset,count));public override int Read(Span<byte> bytes){var read=lease.Read(position,bytes);position+=read;return read;}
        public override long Seek(long offset,SeekOrigin origin){Position=checked((origin switch{SeekOrigin.Begin=>0,SeekOrigin.Current=>position,SeekOrigin.End=>Length,_=>throw new ArgumentOutOfRangeException(nameof(origin))})+offset);return position;}
        public override void Flush(){}public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();public override void SetLength(long length)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing)lease.Dispose();base.Dispose(disposing);}
    }
}

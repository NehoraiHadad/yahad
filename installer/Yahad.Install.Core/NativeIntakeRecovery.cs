using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Yahad.Install;

internal sealed record NativeIntakeMember(string Path,long Bytes,string Sha256);

// Called only while the caller holds the existing intake exclusive lease. This
// journal owns generated, unfinished artifacts; it cannot address family data,
// backups, a selected payload, or security/enrollment metadata. No deletion API.
internal sealed partial class NativeIntakeRecovery
{
    private const long MaximumBytes=2L*1024*1024*1024,MaximumResidueBytes=8L*1024*1024*1024;
    private readonly InstalledStateFiles files;
    private readonly string journal;
    private readonly Action<string>? fault;
    private readonly Action<long>? requireSpace;
    private readonly Dictionary<string,Record> pending=new(StringComparer.Ordinal);
    private readonly Dictionary<string,(string Digest,long Bytes)> unchangedPointers=new(StringComparer.Ordinal);
    private sealed record Record(int Format,string Id,string Kind,string Source,string Digest,long Bytes,string Temporary,string Destination,NativeIntakeMember[] Members);
    private sealed record Identity(uint Volume,uint High,uint Low);

    internal NativeIntakeRecovery(InstalledStateFiles files,string journalRelative="intake/recovery",Action<string>? fault=null,Action<long>? requireSpace=null)
    {
        if(journalRelative is not("intake/recovery" or "system-slots/.intake-recovery"))throw Error();
        this.files=files;this.fault=fault;this.requireSpace=requireSpace;journal=files.PathOf(journalRelative);
        files.ValidateRoot();if(Directory.Exists(journal))using(files.PinDirectory(journal)){}
    }
    internal void Reconcile(CancellationToken cancellation)
    {
        pending.Clear();if(!Directory.Exists(journal))return;using var held=files.PinDirectory(journal);ReconcileMetadata(cancellation);var count=0;
        foreach(var path in Directory.EnumerateFileSystemEntries(journal))
        {
            cancellation.ThrowIfCancellationRequested();if(++count>2048)throw Error();
            var name=Path.GetFileName(path);if(!name.StartsWith("operation-",StringComparison.Ordinal))continue;
            if(!name.EndsWith(".json",StringComparison.Ordinal)||name.Length!=47)throw Error();
            var record=NativeJson.Read<Record>(files.Read(path,8*1024*1024));Validate(record);
            if(name!="operation-"+record.Id+".json")throw Error();
            var temporary=files.PathOf(record.Temporary);var exists=File.Exists(temporary)||Directory.Exists(temporary);
            if(File.Exists(Done(record.Id)))
            {
                RequireDone(record.Id);
                if(exists)throw Error();var retained=RetainedPath(record);
                if(File.Exists(retained)||Directory.Exists(retained))
                {using(var original=Open(retained,IsDirectory(record)))RequireIdentity(record,original,false);if(IsDirectory(record))VerifyPrefix(record,retained,cancellation);}
                else TryArchivePublished(record,cancellation);
                continue;
            }
            if(!exists)
            {
                // Publication can precede its completion marker. Adoption here
                // only closes the journal; the normal verifier rehashes closure.
                var destination=files.PathOf(record.Destination);
                var retained=RetainedPath(record);
                if(File.Exists(retained)||Directory.Exists(retained))
                {using var original=Open(retained,IsDirectory(record));RequireIdentity(record,original,false);Complete(record);}
                else if(!File.Exists(Pin(record.Id)))
                {if(record.Kind=="download"&&!File.Exists(destination))pending.Add(record.Id,record);else Complete(record);}
                else if(File.Exists(destination)||Directory.Exists(destination))
                VerifyAndCompletePublished(record,destination,cancellation);
                else throw Error();
                continue;
            }
            using(var identity=Open(temporary,IsDirectory(record)))RequireIdentity(record,identity,true);
            if(record.Kind=="download")
            {using var input=files.Open(temporary);if(input.Length>record.Bytes)throw Error();pending.Add(record.Id,record);continue;}
            if(IsDirectory(record))VerifyPrefix(record,temporary,cancellation);
            using var handle=Open(temporary,IsDirectory(record));RequireIdentity(record,handle,false);
            if(!IsDirectory(record)&&Length(handle)>record.Bytes)throw Error();
            Retain(record,handle);Complete(record);
        }
        if(pending.Values.Count(r=>r.Kind=="download")>2)throw Error();
        CheckResidue(cancellation);
    }
    internal bool KnowsDownload(string path)=>pending.Values.Any(r=>r.Kind=="download"&&files.PathOf(r.Temporary)==path);
    internal long ReusableDownloadBytes(string source,string digest,long bytes)
    {
        var record=pending.Values.SingleOrDefault(r=>r.Kind=="download"&&r.Source==source&&r.Digest==digest&&r.Bytes==bytes);
        if(record is null||!File.Exists(files.PathOf(record.Temporary)))return 0;
        using var handle=Open(files.PathOf(record.Temporary),false);RequireIdentity(record,handle,false);return Length(handle);
    }

    internal OwnedFile Download(string source,string digest,long bytes)
    {
        Digest(source);Digest(digest);if(bytes is <1 or >MaximumBytes)throw Error();
        var record=pending.Values.SingleOrDefault(r=>r.Kind=="download"&&r.Source==source&&r.Digest==digest&&r.Bytes==bytes);
        if(record is null&&pending.Values.Count(r=>r.Kind=="download")>=2)throw Error();
        if(record is null)record=Begin("download",source,digest,bytes,[],"intake/archive-"+digest+".zip");
        var path=files.PathOf(record.Temporary);
        if(!File.Exists(path))
        {
            using var created=files.Create(path);fault?.Invoke("recovery_object_created");WritePin(record,Id(created.SafeFileHandle));
        }
        var handle=Open(path,false);
        try
        {
            RequireIdentity(record,handle,true);if(Length(handle)>record.Bytes)throw Error();
            var stream=new FileStream(handle,FileAccess.ReadWrite,65536,false);
            // Exact original native identity, ACL, no-follow and exclusive handle
            // are proven before replacing ONLY these unfinished download bytes.
            stream.Position=0;stream.SetLength(0);stream.Flush(true);fault?.Invoke("recovery_download_reset");
            return new(this,record.Id,stream);
        }
        catch{handle.Dispose();throw;}
    }
    internal string Stage(string kind,string manifestDigest,IEnumerable<NativeIntakeMember> members)
    {
        if(kind is not("payload" or "system"))throw Error();var list=members.ToArray();
        var destination=(kind=="payload"?"payloads/":"system-slots/")+manifestDigest;
        var record=Begin(kind,manifestDigest,manifestDigest,list.Sum(m=>m.Bytes),list,destination);
        var path=files.PathOf(record.Temporary);files.CreateDirectory(path);fault?.Invoke("recovery_object_created");
        using var handle=Open(path,true);WritePin(record,Id(handle));return path;
    }
    internal void PublishedDirectory(string staging,string destination,CancellationToken cancellation=default)
    {
        var record=pending.Values.SingleOrDefault(r=>files.PathOf(r.Temporary)==staging&&files.PathOf(r.Destination)==destination)??throw Error();
        VerifyPrefix(record,staging,cancellation,true);using var handle=Open(staging,true);RequireIdentity(record,handle,false);Rename(handle,destination,false);fault?.Invoke("recovery_"+record.Kind+"_published");Complete(record,true);
    }
    internal string Pointer(string name,byte[] bytes)
    {
        if(name is not("available.json" or "system-available.json")||bytes.Length is <1 or >4096)throw Error();
        var destination=files.PathOf("intake/"+name);
        if(File.Exists(destination)&&files.Read(destination,4096).SequenceEqual(bytes))
        {unchangedPointers[destination]=(Hash(bytes),bytes.LongLength);return destination;}
        var digest=Hash(bytes);var record=Begin("pointer",digest,digest,bytes.Length,[],"intake/"+name);
        var path=files.PathOf(record.Temporary);using var created=files.Create(path);fault?.Invoke("recovery_object_created");WritePin(record,Id(created.SafeFileHandle));created.Write(bytes);created.Flush(true);return path;
    }
    internal void PublishedPointer(string temporary)
    {
        if(unchangedPointers.TryGetValue(temporary,out var same))
        {var current=files.Read(temporary,4096);if(current.LongLength!=same.Bytes||Hash(current)!=same.Digest)throw Error();return;}
        var record=pending.Values.SingleOrDefault(r=>r.Kind=="pointer"&&files.PathOf(r.Temporary)==temporary)??throw Error();
        using var handle=Open(temporary,false);RequireIdentity(record,handle,false);
        if(Length(handle)!=record.Bytes||Hash(handle,record.Bytes)!=record.Digest)throw Error();
        Rename(handle,files.PathOf(record.Destination),true);fault?.Invoke("recovery_pointer_published");Complete(record,true);
    }
    private Record Begin(string kind,string source,string digest,long bytes,NativeIntakeMember[] members,string destination)
    {
        if(!Directory.Exists(journal)){requireSpace?.Invoke(4096);files.CreateDirectory(journal);}
        CheckResidue(default);if(Directory.EnumerateFiles(journal,"operation-*.json").Take(512).Count()>=512)throw Error();
        var id=Guid.NewGuid().ToString("N");var temporary=kind switch
        {"download"=>"intake/download-"+id+".zip.partial","payload"=>"payloads/.intake-"+id,"system"=>"system-slots/.staging-"+id,"pointer"=>destination+".partial-"+id,_=>throw Error()};
        var record=new Record(1,id,kind,source,digest,bytes,temporary,destination,members);Validate(record);
        Write(Path.Combine(journal,"operation-"+id+".json"),JsonSerializer.SerializeToUtf8Bytes(record));pending.Add(id,record);fault?.Invoke("recovery_intent_flushed");return record;
    }
    private void Validate(Record r)
    {
        if(r.Format!=1||!Guid.TryParseExact(r.Id,"N",out var id)||id.ToString("N")!=r.Id||r.Members is null||r.Members.Length>50001||r.Bytes is <1 or >MaximumBytes)throw Error();Digest(r.Source);Digest(r.Digest);
        var temporary=r.Kind switch
        {"download"=>"intake/download-"+r.Id+".zip.partial","payload"=>"payloads/.intake-"+r.Id,"system"=>"system-slots/.staging-"+r.Id,"pointer"=>r.Destination+".partial-"+r.Id,_=>throw Error()};
        var expected=r.Kind switch{"download"=>"intake/archive-"+r.Digest+".zip","payload"=>"payloads/"+r.Digest,"system"=>"system-slots/"+r.Digest,"pointer"=>r.Destination,_=>throw Error()};
        if(r.Temporary!=temporary||r.Destination!=expected||r.Kind=="pointer"&&r.Destination is not("intake/available.json" or "intake/system-available.json")||(r.Kind=="system")!=journal.EndsWith(".intake-recovery",StringComparison.Ordinal))throw Error();
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
        foreach(var member in r.Members){PathMember(member.Path);Digest(member.Sha256);if(member.Bytes is <0 or >512L*1024*1024||!seen.Add(member.Path))throw Error();total=checked(total+member.Bytes);}
        if(IsDirectory(r)?r.Members.Length<1||total!=r.Bytes:r.Members.Length!=0)throw Error();
    }
    private void RequireIdentity(Record record,SafeFileHandle handle,bool allowEmptyIntent)
    {
        var pin=Pin(record.Id);if(!File.Exists(pin))
        {
            // A crash after generated-object creation but before its ID flush may
            // own an EMPTY artifact only. No byte can be written before this pin.
            if(!allowEmptyIntent||(IsDirectory(record)?Directory.EnumerateFileSystemEntries(files.PathOf(record.Temporary)).Any():Length(handle)!=0))throw Error();
            WritePin(record,Id(handle));
        }
        if(NativeJson.Read<Identity>(files.Read(pin,1024))!=Id(handle))throw Error();
    }
    private void WritePin(Record record,Identity identity)
    {Write(Pin(record.Id),JsonSerializer.SerializeToUtf8Bytes(identity));fault?.Invoke("recovery_identity_flushed");}
    private void Complete(Record record,bool verifiedPublication=false){if(!File.Exists(Done(record.Id)))Write(Done(record.Id),[1]);else RequireDone(record.Id);pending.Remove(record.Id);if(verifiedPublication)ArchiveMetadata(record);}
    private string Pin(string id)=>Path.Combine(journal,"identity-"+id+".json");
    private string Done(string id)=>Path.Combine(journal,"complete-"+id+".bin");
    private static bool IsDirectory(Record record)=>record.Kind is "payload" or "system";
    private void VerifyPrefix(Record record,string root,CancellationToken cancellation,bool complete=false)
    {
        var expected=record.Members.ToDictionary(m=>m.Path,StringComparer.Ordinal);var directories=new HashSet<string>(StringComparer.Ordinal);
        foreach(var member in expected.Keys)for(var at=member.IndexOf('/');at>=0;at=member.IndexOf('/',at+1))directories.Add(member[..at]);
        var count=0;var observed=new HashSet<string>(StringComparer.Ordinal);
        void Walk(string current,int depth)
        {
            cancellation.ThrowIfCancellationRequested();if(depth>64)throw Error();using var held=files.PinDirectory(current);
            foreach(var path in Directory.EnumerateFileSystemEntries(current))
            {
                if(++count>150000||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw Error();var relative=Path.GetRelativePath(root,path).Replace('\\','/');
                if(Directory.Exists(path)){if(!directories.Contains(relative))throw Error();Walk(path,depth+1);}
                else {if(!expected.TryGetValue(relative,out var member)||!observed.Add(relative))throw Error();using var input=files.Open(path);if(input.Length>member.Bytes||complete&&(input.Length!=member.Bytes||(input.Length==0?Hash([]):input.Sha256(member.Bytes))!=member.Sha256))throw Error();}
            }
        }
        Walk(root,0);if(complete&&!observed.SetEquals(expected.Keys))throw Error();
    }
    private void Retain(Record record,SafeFileHandle handle)
    {
        CheckResidue(default,true,record.Bytes);Rename(handle,RetainedPath(record),false);fault?.Invoke("recovery_residue_retained");
    }
    private string RetainedPath(Record record)=>IsDirectory(record)?Path.Combine(Path.GetDirectoryName(files.PathOf(record.Temporary))!,".retained-"+record.Id):Path.Combine(journal,"retained-"+record.Id);
    private void CheckResidue(CancellationToken cancellation,bool reserve=false,long additional=0)
    {
        var roots=new List<string>();foreach(var relative in new[]{"payloads","system-slots"}){var path=files.PathOf(relative);if(Directory.Exists(path))roots.AddRange(Directory.EnumerateFileSystemEntries(path,".retained-*"));}
        if(Directory.Exists(journal))roots.AddRange(Directory.EnumerateFileSystemEntries(journal,"retained-*"));if(roots.Count>8||reserve&&roots.Count>=8)throw Error();long total=additional;var count=0;
        void Walk(string path,int depth)
        {
            cancellation.ThrowIfCancellationRequested();if(depth>64||++count>300000||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw Error();
            if(Directory.Exists(path)){using var held=files.PinDirectory(path);foreach(var member in Directory.EnumerateFileSystemEntries(path))Walk(member,depth+1);}
            else {using var input=files.Open(path);total=checked(total+input.Length);if(total>MaximumResidueBytes)throw Error();}
        }
        foreach(var root in roots)Walk(root,0);
    }
    private SafeFileHandle Open(string path,bool directory)
    {
        using var parent=files.PinDirectory(Path.GetDirectoryName(path)!);
        var handle=Native.CreateFileW(@"\\?\"+path,directory?0x30080u:0xC0030080u,0,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
        try
        {
            if(handle.IsInvalid||!Native.GetFileInformationByHandle(handle,out var info)||(info.Attributes&0x400)!=0||((info.Attributes&0x10)!=0)!=directory||!directory&&info.Links!=1)throw Error();
            if(Native.GetSecurityInfo(handle,1,5,out _,out _,out _,out _,out var descriptor)!=0)throw Error();
            try{var length=Native.GetSecurityDescriptorLength(descriptor);if(length is 0 or >65536)throw Error();var bytes=new byte[(int)length];Marshal.Copy(descriptor,bytes,0,bytes.Length);var acl=new RawSecurityDescriptor(bytes,0);if(files.Roles is {} roles)roles.Validate(acl,files.Role(path),directory);else WindowsFileBoundary.ValidateAcl(acl,WindowsIdentity.GetCurrent().User??throw Error(),false);}
            finally{Native.LocalFree(descriptor);}
            return handle;
        }
        catch{handle.Dispose();throw;}
    }
    private void Rename(SafeFileHandle handle,string destination,bool replace)
    {
        using var parent=files.PinDirectory(Path.GetDirectoryName(destination)!);var encoded=Encoding.Unicode.GetBytes(@"\\?\"+destination);var offset=IntPtr.Size==8?20:12;var size=offset+encoded.Length+8;var buffer=Marshal.AllocHGlobal(size);
        try{for(var at=0;at<size;at++)Marshal.WriteByte(buffer,at,0);Marshal.WriteInt32(buffer,0,replace?1:0);Marshal.WriteInt32(buffer,IntPtr.Size==8?16:8,encoded.Length);Marshal.Copy(encoded,0,buffer+offset,encoded.Length);if(!Native.SetFileInformationByHandle(handle,3,buffer,size))throw new IOException("NATIVE_INTAKE_PUBLICATION",new System.ComponentModel.Win32Exception());}
        finally{Marshal.FreeHGlobal(buffer);}
    }
    private static Identity Id(SafeFileHandle handle){if(!Native.GetFileInformationByHandle(handle,out var info))throw Error();return new(info.Volume,info.IndexHigh,info.IndexLow);}
    private static long Length(SafeFileHandle handle){if(!Native.GetFileInformationByHandle(handle,out var info))throw Error();return ((long)info.SizeHigh<<32)|info.SizeLow;}
    private static string Hash(SafeFileHandle handle,long bytes){using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[65536];long offset=0;while(offset<bytes){var read=RandomAccess.Read(handle,buffer.AsSpan(0,(int)Math.Min(buffer.Length,bytes-offset)),offset);if(read==0)throw Error();hash.AppendData(buffer.AsSpan(0,read));offset+=read;}return Convert.ToHexStringLower(hash.GetHashAndReset());}
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Digest(string digest){if(string.IsNullOrEmpty(digest)||digest.Length!=64||digest.Any(c=>c is not(>='a' and <='f' or >='0' and <='9')))throw Error();}
    private static void PathMember(string path){if(string.IsNullOrEmpty(path)||path.Length>220||path.Split('/').Any(p=>p is "" or "." or ".."||p.Any(c=>c is ':' or '\\'||char.IsControl(c))))throw Error();}
    private void Write(string path,byte[] bytes)=>WriteMetadata(path,bytes);
    private static IOException Error()=>new("NATIVE_INTAKE_MAINTENANCE_REQUIRED");
    internal sealed class OwnedFile : IDisposable
    {
        private readonly NativeIntakeRecovery owner;private readonly string id;internal FileStream Stream{get;}
        internal OwnedFile(NativeIntakeRecovery owner,string id,FileStream stream){this.owner=owner;this.id=id;Stream=stream;}
        internal void Publish(){var record=owner.pending[id];Stream.Flush(true);if(Stream.Length!=record.Bytes||Hash(Stream.SafeFileHandle,record.Bytes)!=record.Digest)throw Error();owner.Rename(Stream.SafeFileHandle,owner.files.PathOf(record.Destination),false);owner.fault?.Invoke("recovery_download_published");owner.Complete(record,true);}
        public void Dispose()=>Stream.Dispose();
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]internal struct FileInfo{public uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInfo info);
        [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool SetFileInformationByHandle(SafeFileHandle handle,int kind,IntPtr value,int bytes);
        [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool FlushFileBuffers(SafeFileHandle handle);
        [DllImport("advapi32.dll",SetLastError=true)]internal static extern uint GetSecurityInfo(SafeFileHandle handle,uint type,uint flags,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
        [DllImport("advapi32.dll")]internal static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("kernel32.dll")]internal static extern IntPtr LocalFree(IntPtr descriptor);
    }
}

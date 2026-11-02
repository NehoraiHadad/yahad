using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.Win32.SafeHandles;

namespace Yahad.Install;

// Owns duplicates of the ACTUAL admitted generation, surviving SCM stop and
// lifecycle object disposal. It never grants success from an empty invented job
// or a caller Boolean, and never terminates processes as part of observation.
internal sealed class RetainedRuntimeQuiescence:IDisposable
{
    private readonly SafeProcessHandle host;private readonly SafeFileHandle job;private bool disposed;
    internal int RootProcessId{get;}
    internal RetainedRuntimeQuiescence Duplicate()=>FromRetainedHostAndJob(host,job,RootProcessId);
    private RetainedRuntimeQuiescence(SafeProcessHandle host,SafeFileHandle job,int processId){this.host=host;this.job=job;RootProcessId=processId;}
    internal NativeRuntimeTransfer DuplicateNativeHandlesForProcess(SafeProcessHandle target)
    {
        ObjectDisposedException.ThrowIf(disposed,this);var receiver=NativeCustodyTransfer.RequireTarget(target);
        var handles=NativeCustodyTransfer.DuplicateTo(target,[host,job]);
        return new(receiver.Pid,receiver.Created,RootProcessId,NativeCustodyTransfer.Created(host),handles[0],handles[1]);
    }
    internal static RetainedRuntimeQuiescence AdoptTransferred(NativeRuntimeTransfer transfer)
    {
        NativeCustodyTransfer.RequireReceiver(transfer.TargetPid,transfer.TargetCreated);
        if(transfer.Host==transfer.Job)throw new IOException("CUSTODY_DUPLICATE_RUNTIME_HANDLE");
        using var host=new SafeProcessHandle(NativeCustodyTransfer.Pointer(transfer.Host),true);
        using var job=new SafeFileHandle(NativeCustodyTransfer.Pointer(transfer.Job),true);
        if(NativeCustodyTransfer.Created(host)!=transfer.RootCreated)throw new IOException("CUSTODY_RUNTIME_GENERATION");
        return FromRetainedHostAndJob(host,job,transfer.RootPid);
    }
    internal static RetainedRuntimeQuiescence FromRetainedHostAndJob(SafeProcessHandle host,SafeFileHandle job,int rootPid)
    {
        if(rootPid<1||host.IsInvalid||host.IsClosed||job.IsInvalid||job.IsClosed)throw new IOException("RECOVERY_RUNTIME_OWNERSHIP");
        var process=GetCurrentProcess();IntPtr hostCopy=IntPtr.Zero,jobCopy=IntPtr.Zero;var hostAdded=false;var jobAdded=false;
        try
        {
            host.DangerousAddRef(ref hostAdded);job.DangerousAddRef(ref jobAdded);
            if(!DuplicateHandle(process,host.DangerousGetHandle(),process,out hostCopy,0,false,2)||!DuplicateHandle(process,job.DangerousGetHandle(),process,out jobCopy,0,false,2))throw new Win32Exception();
            if(GetProcessId(hostCopy)!=(uint)rootPid)throw new IOException("RECOVERY_RUNTIME_IDENTITY");
            if(!IsProcessInJob(hostCopy,jobCopy,out var inside)||!inside)throw new IOException("RECOVERY_RUNTIME_JOB_BINDING");
            var result=new RetainedRuntimeQuiescence(new(hostCopy,true),new(jobCopy,true),rootPid);hostCopy=jobCopy=IntPtr.Zero;return result;
        }
        finally{if(hostCopy!=IntPtr.Zero)CloseHandle(hostCopy);if(jobCopy!=IntPtr.Zero)CloseHandle(jobCopy);if(jobAdded)job.DangerousRelease();if(hostAdded)host.DangerousRelease();}
    }
    internal async Task RequireAsync(CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();var wait=WaitForSingleObject(host,0);
            if(wait==uint.MaxValue)throw new Win32Exception();
            var accounting=JobAccounting.Read(job);if(wait==0&&accounting.TreeEmpty)return;
            await Task.Delay(20,cancellation);
        }
    }
    public void Dispose(){if(disposed)return;host.Dispose();job.Dispose();disposed=true;}
    [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool DuplicateHandle(IntPtr source,IntPtr handle,IntPtr target,out IntPtr duplicate,uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint options);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint GetProcessId(IntPtr process);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsProcessInJob(IntPtr process,IntPtr job,[MarshalAs(UnmanagedType.Bool)]out bool inside);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeProcessHandle process,uint milliseconds);
    [DllImport("kernel32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CloseHandle(IntPtr handle);
}

// One encoded-byte budget for preflight, serialization and receipt intake.
// Base64 is ASCII without JSON control characters; explicit outer encoding makes
// the exact envelope length independent of digest/base64 character values.
internal static class RecoveryReceiptEncoding
{
    internal const int MaximumBytes=4*1024*1024;
    private static readonly JsonSerializerOptions Outer=new(){Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    private static readonly int Overhead=JsonSerializer.SerializeToUtf8Bytes(new RecoveryEnvelope("",new string('0',64)),Outer).Length;
    internal static int MaximumPayloadBytes=>((MaximumBytes-Overhead)/4)*3;
    internal static int EncodedSize(int payloadBytes)
    {
        if(payloadBytes<0||payloadBytes>MaximumPayloadBytes)throw new IOException("RECOVERY_RECEIPT_BOUND");
        return Overhead+((payloadBytes+2)/3)*4;
    }
    internal static byte[] Serialize(byte[] payload,string mac)
    {
        var expected=EncodedSize(payload.Length);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new RecoveryEnvelope(Convert.ToBase64String(payload),mac),Outer);
        if(bytes.Length!=expected||bytes.Length>MaximumBytes)throw new IOException("RECOVERY_RECEIPT_BOUND");
        return bytes;
    }
    internal static int Preflight(RecoveryManifest manifest)=>EncodedSize(JsonSerializer.SerializeToUtf8Bytes(manifest).Length);
}

public sealed record NativeBackupResult(string BackupId,string ReceiptDigest,int ObservedSchema);
public sealed record NativeRestoreResult(string OperationId,string RestoredDirectory,int ObservedSchema,bool ReconciledPublication);
internal sealed record RecoveryFile(string Name,long Bytes,string Sha256,long EncryptedBytes,string EncryptedSha256);
internal sealed record RecoveryManifest(int Format,string BackupId,string ContextDigest,int Schema,RecoveryFile[] Files);
internal sealed record RecoveryEnvelope(string Payload,string Mac);
internal sealed record ProbeScratchOwner(int Format,string DirectoryName,string DataPathDigest,string Purpose,
    int OwnerProcessId,string LeaseGeneration);
internal sealed record RecoveryRecord(int Format,int Sequence,string OperationId,string BackupId,string ReceiptDigest,
    string ContextDigest,int MinimumSchema,int MaximumSchema,string Phase,string? Attempt,string PreviousDigest,
    string DataPathDigest,int ObservedJobRootProcessId,string LeaseGeneration);

/// <summary>
/// The original private fixture and the role-rooted installed adapter share this
/// encrypted backup and restore engine. Installed entry is internal and requires
/// the retained observed app job; no caller Boolean can establish quiescence.
/// Every restore publishes a NEW directory; activation/live-file replacement is absent.
/// The caller must retain the actual owned app job. There is no Boolean quiescence API.
/// </summary>
public sealed class NativeRecoveryFixture : IDisposable
{
    private const int Chunk=1024*1024, MaximumFiles=10000;
    private const long MaximumFile=256L*1024*1024, MaximumTotal=2L*1024*1024*1024;
    private readonly string root,data,context;
    private readonly InstalledStateFiles? files;
    private readonly Dictionary<string,InstalledStateFiles> staging=new(StringComparer.OrdinalIgnoreCase);
    private readonly string leaseGeneration=Guid.NewGuid().ToString("N");
    private string DataPathDigest=>Hash(Encoding.UTF8.GetBytes(data.ToUpperInvariant()));
    private readonly RetainedRuntimeQuiescence appJob;
    private readonly NativeSqliteInspector inspector;
    private readonly IDisposable rootPin;
    private readonly WindowsFileBoundary.DataLease lease;
    private readonly WindowsFileBoundary.PrivateReadLease keyFile,policyFile,pinFile;
    private readonly byte[] encryption,authentication;
    private readonly SemaphoreSlim serial=new(1,1);
    private bool disposed;private ProbeOwnershipException? pendingProbe;private string? pendingProbeScratch;
    public int ReconciledProbeResidues { get; private set; }
    // Installed catalog roots are exactly datasets/{canonical DataId}. Using
    // that existing identity avoids the extra 32 path characters from a second
    // full path hash. SQLite's Windows WAL path must fit with its -wal/-shm
    // suffixes. Ownership receipts still authenticate the FULL DataPathDigest;
    // a residue for another selected data path is never adopted or deleted.
    private string ProbeRoot=>files is null?Path.Combine(root,".recovery-probes"):Path.Combine(files.ProbeTemporary,"recovery-"+Id(Path.GetFileName(root)));
    private NativeRecoveryFixture(string root,string data,RetainedRuntimeQuiescence job,NativeSqliteInspector inspector,
        IDisposable rootPin,WindowsFileBoundary.DataLease lease,WindowsFileBoundary.PrivateReadLease keyFile,
        WindowsFileBoundary.PrivateReadLease policyFile,WindowsFileBoundary.PrivateReadLease pinFile,byte[] key,InstalledStateFiles? files=null)
    {
        this.root=root;this.data=data;appJob=job;this.inspector=inspector;this.rootPin=rootPin;this.lease=lease;this.files=files;
        this.keyFile=keyFile;this.policyFile=policyFile;this.pinFile=pinFile;
        encryption=HKDF.DeriveKey(HashAlgorithmName.SHA256,key,32,info:Encoding.UTF8.GetBytes("yahad-fixture-backup-encryption-v1"));
        authentication=HKDF.DeriveKey(HashAlgorithmName.SHA256,key,32,info:Encoding.UTF8.GetBytes("yahad-fixture-backup-authentication-v1"));
        context=Hash(Encoding.UTF8.GetBytes(Hash(key)+":"+Digest(policyFile)+":"+Digest(pinFile)));
    }
    // SourceDataId/restoreOperation come only from the independently verified
    // activation. A recovered dataset retains the original encrypted catalog.
    internal static async Task<NativeRecoveryFixture> OpenInstalledAsync(InstalledStateFiles files,string sourceDataId,
        JobProcess retainedOwnedAppJob,NativeSqliteInspector inspector,string? restoreOperation=null,CancellationToken cancellation=default)
    {
        var owned=retainedOwnedAppJob.AcquireRecoveryQuiescence();
        try{return await OpenInstalledAsync(files,sourceDataId,owned,inspector,restoreOperation,cancellation);}
        catch{owned.Dispose();throw;}
    }
    internal static async Task<NativeRecoveryFixture> OpenInstalledAsync(InstalledStateFiles files,string sourceDataId,
        RetainedRuntimeQuiescence retainedOwnedAppJob,NativeSqliteInspector inspector,string? restoreOperation=null,CancellationToken cancellation=default,WindowsFileBoundary.DataLease? retainedSourceLease=null)
    {
        if(files.IsSingleService)throw new IOException("RECOVERY_SEPARATE_AUTHORITY_REQUIRED");
        var root=files.PathOf("datasets/"+Id(sourceDataId));
        var selected=restoreOperation is null?root:Path.Combine(root,"restore-"+Id(restoreOperation),"completed");
        var data=Path.Combine(selected,"data");
        await retainedOwnedAppJob.RequireAsync(cancellation);
        var rootPin=files.PinDirectory(root);WindowsFileBoundary.DataLease? lease=null;
        WindowsFileBoundary.PrivateReadLease? key=null,policy=null,pins=null;byte[]? bytes=null,raw=null;
        try
        {
            lease=retainedSourceLease?.DuplicateForDirectory(data)??files.Lease(data);await retainedOwnedAppJob.RequireAsync(cancellation);
            key=files.Open(restoreOperation is null?Path.Combine(selected,"master-key"):Path.Combine(selected,"identity","master-key"));
            policy=files.Open(restoreOperation is null?Path.Combine(selected,"update-policy"):Path.Combine(selected,"policy","update-policy"));
            pins=files.Open(restoreOperation is null?Path.Combine(selected,"update-pin"):Path.Combine(selected,"policy","update-pin"));
            bytes=key.Small(4096);var start=0;var end=bytes.Length;
            while(start<end&&bytes[start] is 9 or 10 or 13 or 32)start++;
            while(end>start&&bytes[end-1] is 9 or 10 or 13 or 32)end--;
            raw=new byte[32];
            if(Base64.DecodeFromUtf8(bytes.AsSpan(start,end-start),raw,out var consumed,out var written)!=OperationStatus.Done||written!=32||consumed!=end-start||policy.Length>1024*1024||pins.Length>1024*1024)throw new IOException("RECOVERY_IDENTITY_BOUND");
            var recovery=new NativeRecoveryFixture(root,data,retainedOwnedAppJob,inspector,rootPin,lease,key,policy,pins,raw,files);
            try{recovery.ReconciledProbeResidues=recovery.ReconcileProbeResidues();return recovery;}
            catch{recovery.Dispose();throw;}
        }
        catch{pins?.Dispose();policy?.Dispose();key?.Dispose();lease?.Dispose();rootPin.Dispose();throw;}
        finally{if(bytes is not null)CryptographicOperations.ZeroMemory(bytes);if(raw is not null)CryptographicOperations.ZeroMemory(raw);}
    }
    private bool PrivateScratch(string path)=>path.StartsWith(ProbeRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private InstalledStateFiles? Storage(string path)=>staging.FirstOrDefault(x=>path.Equals(x.Key,StringComparison.OrdinalIgnoreCase)||path.StartsWith(x.Key+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)).Value??files;
    private string PrivateProbeRoot(string path)=>Path.Combine(ProbeRoot,Path.GetRelativePath(ProbeRoot,path).Split(Path.DirectorySeparatorChar)[0]);
    private IDisposable Pin(string path)=>PrivateScratch(path)&&files is not null?WindowsFileBoundary.PinOwnedPrivateProbeDirectory(PrivateProbeRoot(path),path):Storage(path)?.PinDirectory(path)??WindowsFileBoundary.PinPrivateDirectory(path);
    private WindowsFileBoundary.PrivateReadLease Open(string path)=>PrivateScratch(path)&&files is not null?WindowsFileBoundary.OpenOwnedPrivateProbeRead(PrivateProbeRoot(path),path):Storage(path)?.Open(path)??WindowsFileBoundary.OpenPrivateRead(path);
    private FileStream Create(string path)=>PrivateScratch(path)?new(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough):Storage(path)?.Create(path)??new(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
    private void MakeDirectory(string path)
    {
        if(files is null||PrivateScratch(path)){Directory.CreateDirectory(path);return;}
        if(Directory.Exists(path)){using var existing=Pin(path);return;}
        var parent=Path.GetDirectoryName(path)!;if(!Directory.Exists(parent))MakeDirectory(parent);
        Storage(path)!.CreateDirectory(path);
    }
    public static async Task<NativeRecoveryFixture> OpenAsync(string privateFixtureRoot,JobProcess retainedOwnedAppJob,
        NativeSqliteInspector inspector,CancellationToken cancellation=default)
    {
        var root=Path.GetFullPath(privateFixtureRoot);SafeDirectory.ValidateSession(root);
        var data=Path.Combine(root,"data");
        await retainedOwnedAppJob.WaitForTreeEmptyAsync(cancellation);
        var rootPin=WindowsFileBoundary.PinPrivateDirectory(root);
        WindowsFileBoundary.DataLease? lease=null;
        WindowsFileBoundary.PrivateReadLease? key=null,policy=null,pins=null;
        byte[]? bytes=null,raw=null;RetainedRuntimeQuiescence? observed=null;
        try
        {
            lease=WindowsFileBoundary.AcquireDataLease(data);
            await retainedOwnedAppJob.WaitForTreeEmptyAsync(cancellation);
            key=WindowsFileBoundary.OpenPrivateRead(Path.Combine(root,"master-key"));
            policy=WindowsFileBoundary.OpenPrivateRead(Path.Combine(root,"update-policy"));
            pins=WindowsFileBoundary.OpenPrivateRead(Path.Combine(root,"update-pin"));
            bytes=key.Small(4096);var start=0;var end=bytes.Length;
            while(start<end&&bytes[start] is 9 or 10 or 13 or 32)start++;
            while(end>start&&bytes[end-1] is 9 or 10 or 13 or 32)end--;
            raw=new byte[32];
            if(Base64.DecodeFromUtf8(bytes.AsSpan(start,end-start),raw,out _,out var written)!=OperationStatus.Done||written!=32||policy.Length>1024*1024||pins.Length>1024*1024)throw new IOException("RECOVERY_IDENTITY_BOUND");
            observed=retainedOwnedAppJob.AcquireRecoveryQuiescence();
            var recovery=new NativeRecoveryFixture(root,data,observed,inspector,rootPin,lease,key,policy,pins,raw);
            try{recovery.ReconciledProbeResidues=recovery.ReconcileProbeResidues();return recovery;}
            catch{recovery.Dispose();throw;}
        }
        catch {observed?.Dispose();pins?.Dispose();policy?.Dispose();key?.Dispose();lease?.Dispose();rootPin.Dispose();throw;}
        finally {if(bytes is not null)CryptographicOperations.ZeroMemory(bytes);if(raw is not null)CryptographicOperations.ZeroMemory(raw);}
    }
    private static string Id(string id)=>Guid.TryParseExact(id,"N",out var parsed)&&parsed.ToString("N")==id?id:throw new IOException("RECOVERY_IDENTIFIER");
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Digest(WindowsFileBoundary.PrivateReadLease file)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[Chunk];long offset=0;
        try{int count;while((count=file.Read(offset,buffer))>0){hash.AppendData(buffer,0,count);offset+=count;}return Convert.ToHexStringLower(hash.GetHashAndReset());}
        finally{CryptographicOperations.ZeroMemory(buffer);}
    }
    private async Task ObserveAsync(CancellationToken cancellation)
    {ObjectDisposedException.ThrowIf(disposed,this);await appJob.RequireAsync(cancellation);}
    internal WindowsFileBoundary.DataLease DuplicateDataLease(string expectedDirectory)
    {ObjectDisposedException.ThrowIf(disposed,this);return lease.DuplicateForDirectory(expectedDirectory);}
    internal bool OwnsRuntimeObservation(RetainedRuntimeQuiescence observation)=>ReferenceEquals(appJob,observation);
    internal async Task<int> InspectCurrentSchemaAsync(CancellationToken cancellation)
    {await ObserveAsync(cancellation);using var source=Open(Path.Combine(data,"app.sqlite"));return await ProbeCopyAsync(source,"backup-validation",null,cancellation);}
    internal async Task RequireCurrentOperatorAsync(string familyId,string parentUserId,CancellationToken cancellation)
    {
        await ObserveAsync(cancellation);using var source=Open(Path.Combine(data,"app.sqlite"));
        _=await ProbeCopyAsync(source,"operator-validation",null,cancellation,async(database,scratch,token)=>
        {await inspector.RequireOperatorAsync(database,scratch,familyId,parentUserId,token,files is not null);return 1;});
    }
    internal async Task<int> InspectRestoredSchemaAsync(string cloneOperationId,CancellationToken cancellation)
    {
        await ObserveAsync(cancellation);var path=Path.Combine(root,"restore-"+Id(cloneOperationId),"completed","data","app.sqlite");
        using var source=Open(path);return await ProbeCopyAsync(source,"restore-validation",null,cancellation);
    }
    internal async Task RequireSourceMatchesBackupAsync(string backupId,string receiptDigest,int expectedSchema,CancellationToken cancellation)
    {
        await ObserveAsync(cancellation);var verified=VerifyBackup(backupId);
        if(verified.Digest!=receiptDigest||verified.Manifest.Schema!=expectedSchema)throw new IOException("RECOVERY_ADOPTED_BACKUP_BINDING");
        var sources=Sources();
        try
        {
            var all=sources.Concat(new[]{("identity/master-key",keyFile),("policy/update-policy",policyFile),("policy/update-pin",pinFile)}).ToArray();
            if(!all.Select(x=>x.Item1).ToHashSet(StringComparer.Ordinal).SetEquals(verified.Manifest.Files.Select(x=>x.Name)))throw new IOException("RECOVERY_ADOPTED_SOURCE_MEMBERS");
            foreach(var member in all)
            {
                cancellation.ThrowIfCancellationRequested();var expected=verified.Manifest.Files.Single(x=>x.Name==member.Item1);
                if(member.Item2.Length!=expected.Bytes||Digest(member.Item2)!=expected.Sha256)throw new IOException("RECOVERY_ADOPTED_SOURCE_CHANGED");
            }
        }
        finally{foreach(var source in sources)source.File.Dispose();}
    }
    private static string Child(string parent,string name)
    {
        if(name.Length>200||name.Contains('\\')||name.Split('/').Any(p=>p is "" or "." or ".."||p.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new IOException("RECOVERY_MEMBER_PATH");
        var path=Path.Combine(parent,name.Replace('/',Path.DirectorySeparatorChar));
        WindowsFileBoundary.CanonicalLocalPath(path);return path;
    }
    private List<(string Name,WindowsFileBoundary.PrivateReadLease File)> Sources()
    {
        var sources=new List<(string,WindowsFileBoundary.PrivateReadLease)>();
        try
        {
            var visited=0;
            void Walk(string directory,int depth=0)
            {
                if(depth>64)throw new IOException("RECOVERY_TREE_DEPTH");
                using var pin=Pin(directory);
                foreach(var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
                {
                    if(++visited>100000)throw new IOException("RECOVERY_TREE_BOUND");
                    if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("RECOVERY_REPARSE");
                    if(Directory.Exists(path)){Walk(path,depth+1);continue;}
                    var relative=Path.GetRelativePath(data,path).Replace('\\','/');
                    if(relative=="process.lock")continue;
                    if(relative is "app.sqlite-wal" or "app.sqlite-shm" or "app.sqlite-journal")throw new IOException("RECOVERY_SQLITE_SIDECAR");
                    sources.Add(("data/"+relative,Open(path)));
                    if(sources.Count>MaximumFiles-3)throw new IOException("RECOVERY_FILE_COUNT");
                }
            }
            Walk(data);
            if(!sources.Any(s=>s.Item1=="data/app.sqlite"))throw new IOException("RECOVERY_DATABASE_MISSING");
            long total=0;foreach(var source in sources){if(source.Item2.Length>MaximumFile||(total+=source.Item2.Length)>MaximumTotal)throw new IOException("RECOVERY_SIZE_BOUND");}
            return sources;
        }
        catch{foreach(var source in sources)source.Item2.Dispose();throw;}
    }
    private byte[] Envelope<T>(T value,string domain)
    {
        var payload=JsonSerializer.SerializeToUtf8Bytes(value);
        RecoveryReceiptEncoding.EncodedSize(payload.Length);
        return RecoveryReceiptEncoding.Serialize(payload,
            Convert.ToHexStringLower(HMACSHA256.HashData(authentication,Encoding.UTF8.GetBytes(domain+":"+Convert.ToBase64String(payload)))));
    }
    private T Authenticate<T>(byte[] bytes,string domain)
    {
        var envelope=NativeJson.Read<RecoveryEnvelope>(bytes);
        byte[] expected=HMACSHA256.HashData(authentication,Encoding.UTF8.GetBytes(domain+":"+envelope.Payload)),supplied;
        try{supplied=Convert.FromHexString(envelope.Mac);}catch(FormatException){throw new IOException("RECOVERY_AUTHENTICATION");}
        if(!CryptographicOperations.FixedTimeEquals(expected,supplied))throw new IOException("RECOVERY_AUTHENTICATION");
        return NativeJson.Read<T>(Convert.FromBase64String(envelope.Payload));
    }
    private void Durable(string destination,byte[] bytes,Action<string>? fault=null)
    {
        var partial=destination+".partial-"+Guid.NewGuid().ToString("N");
        using(var stream=Create(partial))
        {stream.Write(bytes);fault?.Invoke("before-durable-flush");stream.Flush(true);}
        if(!MoveFileExW(@"\\?\"+partial,@"\\?\"+destination,8))throw new Win32Exception(); // never replace an existing destination
    }
    private static int FullChunk(WindowsFileBoundary.PrivateReadLease source,long offset,byte[] buffer)
    {
        var wanted=(int)Math.Min(buffer.Length,source.Length-offset);var read=0;
        while(read<wanted){var count=source.Read(offset+read,buffer.AsSpan(read,wanted-read));if(count==0)throw new IOException("RECOVERY_SHORT_READ");read+=count;}
        return read;
    }
    private RecoveryFile Encrypt(string backupId,string name,WindowsFileBoundary.PrivateReadLease source,string destination,Action<string>? fault,CancellationToken cancellation)
    {
        var plain=new byte[Chunk];using var aes=new AesGcm(encryption,16);long offset=0,index=0;
        using var plainHash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            using(var output=Create(destination))
            {
                int count;while((count=FullChunk(source,offset,plain))>0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var nonce=RandomNumberGenerator.GetBytes(12);var tag=new byte[16];var cipher=new byte[count];
                    aes.Encrypt(nonce,plain.AsSpan(0,count),cipher,tag,Aad(backupId,name,index++,source.Length));
                    var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,count);
                    output.Write(header);output.Write(nonce);output.Write(tag);output.Write(cipher);
                    plainHash.AppendData(plain,0,count);offset+=count;fault?.Invoke("backup-chunk-written");
                }
                if(offset!=source.Length)throw new IOException("RECOVERY_SOURCE_CHANGED");output.Flush(true);
            }
            using var file=Open(destination);
            return new(name,offset,Convert.ToHexStringLower(plainHash.GetHashAndReset()),file.Length,Digest(file));
        }
        finally{CryptographicOperations.ZeroMemory(plain);}
    }
    private static byte[] Aad(string id,string name,long index,long length)=>Encoding.UTF8.GetBytes(FormattableString.Invariant($"yahad-backup-v1\n{id}\n{name}\n{index}\n{length}"));
    private void Decrypt(string backupId,RecoveryFile member,string source,string destination,Action<string>? fault,CancellationToken cancellation)
    {
        MakeDirectory(Path.GetDirectoryName(destination)!);
        using var input=Open(source);
        if(input.Length!=member.EncryptedBytes||Digest(input)!=member.EncryptedSha256)throw new IOException("RECOVERY_CIPHER_HASH");
        using var aes=new AesGcm(encryption,16);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var output=Create(destination);
        long position=0,total=0,index=0;
        byte[] Read(int count){var bytes=new byte[count];var read=0;while(read<count){var n=input.Read(position,bytes.AsSpan(read));if(n==0)throw new IOException("RECOVERY_CIPHER_TRUNCATED");read+=n;position+=n;}return bytes;}
        while(position<input.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            var count=BinaryPrimitives.ReadInt32LittleEndian(Read(4));
            if(count<1||count>Chunk||total+count>member.Bytes)throw new IOException("RECOVERY_CHUNK_BOUND");
            var nonce=Read(12);var tag=Read(16);var cipher=Read(count);var plain=new byte[count];
            try{aes.Decrypt(nonce,cipher,tag,plain,Aad(backupId,member.Name,index++,member.Bytes));output.Write(plain);hash.AppendData(plain);total+=count;fault?.Invoke("restore-chunk-written");}
            finally{CryptographicOperations.ZeroMemory(plain);}
        }
        if(total!=member.Bytes||Convert.ToHexStringLower(hash.GetHashAndReset())!=member.Sha256)throw new IOException("RECOVERY_PLAIN_HASH");
        output.Flush(true);
    }
    public async Task<NativeBackupResult> BackupAsync(Action<string>? fault=null,CancellationToken cancellation=default)
    {
        await serial.WaitAsync(cancellation);
        var sources=new List<(string Name,WindowsFileBoundary.PrivateReadLease File)>();
        try
        {
            await ObserveAsync(cancellation);sources=Sources();
            var total=sources.Sum(s=>s.File.Length)+keyFile.Length+policyFile.Length+pinFile.Length;
            if(total>MaximumTotal)throw new IOException("RECOVERY_SIZE_BOUND");
            if(new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace<total+sources.Single(s=>s.Name=="data/app.sqlite").File.Length+16L*1024*1024)throw new IOException("RECOVERY_FREE_SPACE");
            var all=sources.Concat(new[]{("identity/master-key",keyFile),("policy/update-policy",policyFile),("policy/update-pin",pinFile)}).ToArray();
            foreach(var member in all)Child(root,member.Item1); // enforce path limits before any payload writes
            var database=sources.Single(s=>s.Name=="data/app.sqlite").File;
            // Plaintext validation staging is never a child of an exportable backup.
            // The backup directory is not created until validation/cleanup succeeds.
            var schema=await ProbeCopyAsync(database,"backup-validation",fault,cancellation);
            cancellation.ThrowIfCancellationRequested();
            var id=Guid.NewGuid().ToString("N");
            var placeholder=new string('0',64);
            var projected=all.Select(s=>new RecoveryFile(s.Item1,s.Item2.Length,placeholder,
                s.Item2.Length+((s.Item2.Length+Chunk-1)/Chunk)*32,placeholder)).ToArray();
            var receiptBytes=RecoveryReceiptEncoding.Preflight(new RecoveryManifest(1,id,context,schema,projected));
            fault?.Invoke("backup-receipt-preflight-complete");
            var directory=Path.Combine(root,"backup-"+id);MakeDirectory(directory);
            var members=new List<RecoveryFile>();
            for(var i=0;i<all.Length;i++){cancellation.ThrowIfCancellationRequested();members.Add(Encrypt(id,all[i].Item1,all[i].Item2,Path.Combine(directory,$"{i:D5}.bin"),fault,cancellation));}
            await ObserveAsync(cancellation);
            var observed=FilesBelow(data).Where(n=>n!="process.lock").Select(n=>"data/"+n).ToHashSet(StringComparer.Ordinal);
            if(!observed.SetEquals(sources.Select(s=>s.Name)))throw new IOException("RECOVERY_SOURCE_MEMBERS_CHANGED");
            var manifest=new RecoveryManifest(1,id,context,schema,members.ToArray());
            var bytes=Envelope(manifest,"backup");
            if(bytes.Length!=receiptBytes)throw new IOException("RECOVERY_RECEIPT_PREFLIGHT_CHANGED");
            fault?.Invoke("before-backup-receipt");
            Durable(Path.Combine(directory,"receipt.json"),bytes,fault);
            fault?.Invoke("after-backup-receipt");
            VerifyBackup(id);return new(id,Hash(bytes),schema);
        }
        finally{foreach(var source in sources)source.File.Dispose();serial.Release();}
    }
    private void CopyPlain(WindowsFileBoundary.PrivateReadLease source,string destination)
    {
        var buffer=new byte[Chunk];long offset=0;
        try{using var output=Create(destination);int count;while((count=source.Read(offset,buffer))>0){output.Write(buffer,0,count);offset+=count;}output.Flush(true);}
        finally{CryptographicOperations.ZeroMemory(buffer);}
    }
    private (RecoveryManifest Manifest,string Digest) VerifyBackup(string backupId)
    {
        var directory=Path.Combine(root,"backup-"+Id(backupId));using var pin=Pin(directory);
        using var receipt=Open(Path.Combine(directory,"receipt.json"));var bytes=receipt.Small(RecoveryReceiptEncoding.MaximumBytes);
        var manifest=Authenticate<RecoveryManifest>(bytes,"backup");
        if(manifest.Format!=1||manifest.BackupId!=backupId||manifest.ContextDigest!=context||manifest.Schema<1||manifest.Files.Length is <4 or >MaximumFiles)throw new IOException("RECOVERY_BACKUP_IDENTITY");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
        for(var i=0;i<manifest.Files.Length;i++)
        {
            var member=manifest.Files[i];Child(root,member.Name);
            if(!names.Add(member.Name)||!(member.Name.StartsWith("data/",StringComparison.Ordinal)||member.Name is "identity/master-key" or "policy/update-policy" or "policy/update-pin")||member.Name is "data/process.lock" or "data/app.sqlite-wal" or "data/app.sqlite-shm" or "data/app.sqlite-journal"||member.Bytes<0||member.Bytes>MaximumFile||(total+=member.Bytes)>MaximumTotal)throw new IOException("RECOVERY_MANIFEST_BOUND");
            using var file=Open(Path.Combine(directory,$"{i:D5}.bin"));
            if(file.Length!=member.EncryptedBytes||Digest(file)!=member.EncryptedSha256)throw new IOException("RECOVERY_CIPHER_HASH");
        }
        if(!new[]{"data/app.sqlite","identity/master-key","policy/update-policy","policy/update-pin"}.All(names.Contains))throw new IOException("RECOVERY_BACKUP_INCOMPLETE");
        var expectedNames=Enumerable.Range(0,manifest.Files.Length).Select(i=>$"{i:D5}.bin").Append("receipt.json").ToHashSet(StringComparer.Ordinal);
        if(!expectedNames.SetEquals(FilesBelow(directory,MaximumFiles+1)))throw new IOException("RECOVERY_BACKUP_MEMBERS");
        return(manifest,Hash(bytes));
    }
    private async Task<int> ProbeCopyAsync(WindowsFileBoundary.PrivateReadLease source,string purpose,
        Action<string>? fault,CancellationToken cancellation,Func<string,string,CancellationToken,Task<int>>? inspect=null)
    {
        MakeDirectory(ProbeRoot);
        using var parentPin=Pin(ProbeRoot);
        var scratch=SafeDirectory.CreatePrivateSession(ProbeRoot);
        // Commit authenticated ownership BEFORE copying any plaintext. A crash
        // after this point is reconciled on the next offline admission.
        Durable(Path.Combine(scratch,"owner.json"),Envelope(new ProbeScratchOwner(1,Path.GetFileName(scratch),
            DataPathDigest,purpose,Environment.ProcessId,leaseGeneration),"probe-scratch"));
        try
        {
            using var gate=files is null?WindowsFileBoundary.AcquireDataLease(scratch):WindowsFileBoundary.AcquireOwnedPrivateProbeLease(scratch);
            var copied=Path.Combine(scratch,"app.sqlite");CopyPlain(source,copied);
            fault?.Invoke("probe-copy-complete");cancellation.ThrowIfCancellationRequested();
            if(inspect is not null)return await inspect(copied,scratch,cancellation);
            return files is null?await inspector.InspectAsync(copied,scratch,cancellation):await inspector.InspectOwnedPrivateProbeAsync(copied,scratch,cancellation);
        }
        catch(ProbeOwnershipException owned){pendingProbe=owned;pendingProbeScratch=scratch;owned.KeepAlive(this);throw;}
        finally {if(pendingProbeScratch!=scratch)CleanupProbe(scratch);}
    }
    private int ReconcileProbeResidues()
    {
        if(!Directory.Exists(ProbeRoot))return 0;
        using var parentPin=Pin(ProbeRoot);var count=0;
        var entries=Directory.EnumerateFileSystemEntries(ProbeRoot).ToArray();
        if(entries.Length>128)throw new IOException("RECOVERY_PROBE_RESIDUE_BOUND");
        foreach(var path in entries){CleanupProbe(path);count++;}return count;
    }
    private void CleanupProbe(string directory)
    {
        var path=Path.GetFullPath(directory);var name=Path.GetFileName(path);
        if(!string.Equals(Path.GetDirectoryName(path),ProbeRoot,StringComparison.OrdinalIgnoreCase)||
            !name.StartsWith("yahad-fixture-",StringComparison.Ordinal)||!Guid.TryParseExact(name[14..],"N",out _))
            throw new IOException("RECOVERY_PROBE_SCOPE");
        string[] members;
        bool Metadata(string file)=>file is ".yahad-fixture" or "owner.json" or "process.lock"||
            file.StartsWith("owner.json.partial-",StringComparison.Ordinal)&&Guid.TryParseExact(file[19..],"N",out _);
        using(var gate=files is null?WindowsFileBoundary.AcquireDataLease(path):WindowsFileBoundary.AcquireOwnedPrivateProbeLease(path))
        {
            if(File.Exists(Path.Combine(path,".yahad-fixture")))SafeDirectory.ValidateSession(path);
            if(Directory.EnumerateDirectories(path).Any())throw new IOException("RECOVERY_PROBE_UNEXPECTED_MEMBER");
            members=FilesBelow(path).ToArray();
            var ownerPath=Path.Combine(path,"owner.json");
            if(File.Exists(ownerPath))
            {
                using var owner=Open(ownerPath);
                var record=Authenticate<ProbeScratchOwner>(owner.Small(8192),"probe-scratch");
                if(record.Format!=1||record.DirectoryName!=name||record.DataPathDigest!=DataPathDigest||
                    record.OwnerProcessId<1||!Guid.TryParseExact(record.LeaseGeneration,"N",out _)||
                    record.Purpose is not("backup-validation" or "restore-validation" or "operator-validation"))throw new IOException("RECOVERY_PROBE_OWNERSHIP");
            }
            else if(members.Any(file=>!Metadata(file)||file.StartsWith("owner.json.partial-",StringComparison.Ordinal)))throw new IOException("RECOVERY_PROBE_OWNERSHIP_MISSING");
            foreach(var file in members)
            {
                if(Metadata(file))continue;
                if(file is not("app.sqlite" or "app.sqlite-wal" or "app.sqlite-shm" or "app.sqlite-journal")&&
                    !(file.StartsWith("sqlite-observation-",StringComparison.Ordinal)&&file.EndsWith(".json",StringComparison.Ordinal)&&Guid.TryParseExact(file[19..^5],"N",out _)))
                    throw new IOException("RECOVERY_PROBE_UNEXPECTED_MEMBER");
                // Check actual read handles before changing the catalog. In
                // particular, a locked DB must not cause its ownership record
                // to be deleted first by an unordered recursive directory delete.
                using var readable=Open(Path.Combine(path,file));
            }
            foreach(var file in members.Where(file=>!Metadata(file)))File.Delete(Path.Combine(path,file));
        }
        // All plaintext is gone before metadata removal. A crash now leaves
        // either an authenticated owned scope or only recognized metadata in
        // the pinned private catalog; the latter contains no payload to export.
        SafeDirectory.NoReparseAncestors(path);
        foreach(var file in members.Where(file=>Metadata(file)&&file!="owner.json"))File.Delete(Path.Combine(path,file));
        File.Delete(Path.Combine(path,"owner.json"));
        Directory.Delete(path,false);
    }

    private List<string> FilesBelow(string root,int maximumFiles=MaximumFiles)
    {
        var names=new List<string>();var visited=0;
        void Walk(string directory,int depth)
        {
            if(depth>64)throw new IOException("RECOVERY_TREE_DEPTH");
            using var pin=Pin(directory);
            foreach(var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if(++visited>100000)throw new IOException("RECOVERY_TREE_BOUND");
                if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("RECOVERY_REPARSE");
                if(Directory.Exists(path))Walk(path,depth+1);
                else{names.Add(Path.GetRelativePath(root,path).Replace('\\','/'));if(names.Count>maximumFiles)throw new IOException("RECOVERY_FILE_COUNT");}
            }
        }
        Walk(root,0);return names;
    }
    private async Task ValidateRestored(string directory,RecoveryManifest manifest,int minimum,int maximum,CancellationToken cancellation)
    {
        using var pin=Pin(directory);
        var names=FilesBelow(directory,MaximumFiles+1).Where(n=>n!="data/process.lock").ToHashSet(StringComparer.Ordinal);
        if(!names.SetEquals(manifest.Files.Select(f=>f.Name)))throw new IOException("RECOVERY_RESTORED_MEMBERS");
        var gate=Child(directory,"data/process.lock");if(File.Exists(gate)){using var metadata=Open(gate);if(metadata.Length!=0)throw new IOException("RECOVERY_RESTORED_LEASE_METADATA");}
        foreach(var member in manifest.Files){using var file=Open(Child(directory,member.Name));if(file.Length!=member.Bytes||Digest(file)!=member.Sha256)throw new IOException("RECOVERY_RESTORED_HASH");}
        using var source=Open(Child(directory,"data/app.sqlite"));
        var schema=await ProbeCopyAsync(source,"restore-validation",null,cancellation);
        if(schema!=manifest.Schema||schema<minimum||schema>maximum)throw new IOException("RECOVERY_SCHEMA_INCOMPATIBLE");
    }
    private List<RecoveryRecord> ReadJournal(string operation,out string previousDigest)
    {
        var directory=Path.Combine(root,"restore-"+Id(operation));using var pin=Pin(directory);
        var records=new List<RecoveryRecord>();previousDigest="";
        var files=Directory.EnumerateFiles(directory,"*.json").Order(StringComparer.Ordinal).ToArray();
        if(files.Length is <1 or >128)throw new IOException("RECOVERY_JOURNAL_BOUND");
        foreach(var path in files)
        {
            if(Path.GetFileName(path)!=$"{records.Count+1:D5}.json")throw new IOException("RECOVERY_JOURNAL_SEQUENCE");
            using var file=Open(path);var bytes=file.Small(16384);
            var record=Authenticate<RecoveryRecord>(bytes,"journal");
            if(record.Format!=1||record.OperationId!=operation||record.ContextDigest!=context||record.Sequence!=records.Count+1||record.PreviousDigest!=previousDigest||record.MinimumSchema<1||record.MaximumSchema<record.MinimumSchema||record.DataPathDigest!=DataPathDigest||record.ObservedJobRootProcessId<1||!Guid.TryParseExact(record.LeaseGeneration,"N",out _)||record.Phase is not("planned" or "extracting" or "validated" or "complete"))throw new IOException("RECOVERY_JOURNAL_INVALID");
            if(records.Count>0){var first=records[0];if(record.BackupId!=first.BackupId||record.ReceiptDigest!=first.ReceiptDigest||record.MinimumSchema!=first.MinimumSchema||record.MaximumSchema!=first.MaximumSchema||records[^1].Phase=="complete"||record.Phase=="planned")throw new IOException("RECOVERY_JOURNAL_BINDING");}
            else if(record.Phase!="planned")throw new IOException("RECOVERY_JOURNAL_PLAN");
            if(record.Attempt is not null)Id(record.Attempt);
            records.Add(record);previousDigest=Hash(bytes);
        }
        return records;
    }
    public async Task<NativeRestoreResult> RestoreToNewDirectoryAsync(string backupId,int minimumSchema,int maximumSchema,
        string? operationId=null,Action<string>? fault=null,CancellationToken cancellation=default)
        =>await RestoreCoreAsync(backupId,minimumSchema,maximumSchema,operationId,false,fault,cancellation);
    internal Task<NativeRestoreResult> RestorePlannedToNewDirectoryAsync(string backupId,int minimumSchema,int maximumSchema,
        string operationId,Action<string>? fault=null,CancellationToken cancellation=default)
        =>RestoreCoreAsync(backupId,minimumSchema,maximumSchema,Id(operationId),true,fault,cancellation);
    private async Task<NativeRestoreResult> RestoreCoreAsync(string backupId,int minimumSchema,int maximumSchema,
        string? operationId,bool createPlanned,Action<string>? fault,CancellationToken cancellation)
    {
        await serial.WaitAsync(cancellation);
        try
        {
            await ObserveAsync(cancellation);
            var verified=VerifyBackup(backupId);var manifest=verified.Manifest;
            if(minimumSchema<1||maximumSchema<minimumSchema||manifest.Schema<minimumSchema||manifest.Schema>maximumSchema)throw new IOException("RECOVERY_SCHEMA_INCOMPATIBLE");
            var operation=operationId is null?Guid.NewGuid().ToString("N"):Id(operationId);
            var directory=Path.Combine(root,"restore-"+operation);var completed=Path.Combine(directory,"completed");
            if(files is not null)this.staging[directory]=files.ForRecoveryStaging(directory);
            if(files is not null)this.staging[directory]=files.ForRecoveryStaging(directory);
            List<RecoveryRecord> records;string previous;
            if(operationId is null||createPlanned&&!Directory.Exists(directory))
            {
                MakeDirectory(directory);records=[];previous="";
            }
            else
            {
                records=ReadJournal(operation,out previous);var first=records[0];
                if(first.BackupId!=backupId||first.ReceiptDigest!=verified.Digest||first.MinimumSchema!=minimumSchema||first.MaximumSchema!=maximumSchema)throw new IOException("RECOVERY_RETRY_BINDING");
            }
            using var directoryPin=Pin(directory);
            void Append(string phase,string? attempt)
            {
                if(records.Count>=128)throw new IOException("RECOVERY_JOURNAL_BOUND");
                var record=new RecoveryRecord(1,records.Count+1,operation,backupId,verified.Digest,context,minimumSchema,maximumSchema,phase,attempt,previous,DataPathDigest,appJob.RootProcessId,leaseGeneration);
                var bytes=Envelope(record,"journal");Durable(Path.Combine(directory,$"{record.Sequence:D5}.json"),bytes,fault);
                records.Add(record);previous=Hash(bytes);fault?.Invoke("journal-"+phase);
            }
            if(records.Count==0)Append("planned",null);
            if(Directory.Exists(completed))
            {
                await ValidateRestored(completed,manifest,minimumSchema,maximumSchema,cancellation);await ObserveAsync(cancellation);
                if(records[^1].Phase!="complete")Append("complete",records[^1].Attempt);
                return new(operation,completed,manifest.Schema,true);
            }
            if(records[^1].Phase=="complete")throw new IOException("RECOVERY_COMPLETED_OUTPUT_MISSING");
            if(records.Count(r=>r.Phase=="extracting")>=3)throw new IOException("RECOVERY_ATTEMPTS_QUARANTINED");
            var requiredBytes=manifest.Files.Sum(f=>f.Bytes)+16L*1024*1024;
            if(new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace<requiredBytes)throw new IOException("RECOVERY_FREE_SPACE");
            var attempt=Guid.NewGuid().ToString("N");Append("extracting",attempt);
            var staging=Path.Combine(directory,"staging-"+attempt);MakeDirectory(staging);
            for(var i=0;i<manifest.Files.Length;i++)
            {
                cancellation.ThrowIfCancellationRequested();
                Decrypt(backupId,manifest.Files[i],Path.Combine(root,"backup-"+backupId,$"{i:D5}.bin"),Child(staging,manifest.Files[i].Name),fault,cancellation);
            }
            await ValidateRestored(staging,manifest,minimumSchema,maximumSchema,cancellation);
            Append("validated",attempt);await ObserveAsync(cancellation);fault?.Invoke("before-restore-publication");
            // Same private parent, generated names, no destination replacement. No live path is involved.
            if(!MoveFileExW(@"\\?\"+staging,@"\\?\"+completed,8))throw new Win32Exception();
            fault?.Invoke("after-restore-publication");
            await ValidateRestored(completed,manifest,minimumSchema,maximumSchema,cancellation);
            Append("complete",attempt);return new(operation,completed,manifest.Schema,false);
        }
        finally{serial.Release();}
    }
    // Publishes only this verified NEW output's final ACLs. Existing active data
    // is never replaced or relabelled. Activation remains a separate protected
    // writer operation after this method returns and after an owner decision.
    internal async Task<NativeRestoreResult> PublishInstalledRestoreAsync(string backupId,string operationId,
        int minimumSchema,int maximumSchema,Action<string>? fault=null,CancellationToken cancellation=default)
    {
        if(files is null)throw new IOException("RECOVERY_INSTALLED_STORAGE_REQUIRED");
        await serial.WaitAsync(cancellation);
        try
        {
            await ObserveAsync(cancellation);var verified=VerifyBackup(backupId);
            var directory=Path.Combine(root,"restore-"+Id(operationId));var completed=Path.Combine(directory,"completed");
            var view=files.ForRecoveryStaging(directory);staging[directory]=view;
            var records=ReadJournal(operationId,out _);var first=records[0];
            if(records[^1].Phase!="complete"||first.BackupId!=backupId||first.ReceiptDigest!=verified.Digest||first.MinimumSchema!=minimumSchema||first.MaximumSchema!=maximumSchema)throw new IOException("RECOVERY_PUBLICATION_JOURNAL");
            await ValidateRestored(completed,verified.Manifest,minimumSchema,maximumSchema,cancellation);
            await ObserveAsync(cancellation);fault?.Invoke("before-recovery-role-publication");
            var gate=Path.Combine(completed,"data","process.lock");
            if(!File.Exists(gate)){using var metadata=view.Create(gate);metadata.Flush(true);}
            view.PromoteRecoveryDirectory(completed,fault);await ObserveAsync(cancellation);
            // Verify through the NORMAL installed role map, not the staging
            // view. Interrupted per-object promotion can be retried unchanged.
            var retained=new List<IDisposable>();
            try
            {
                var schema=NativeRecoveryAdmission.VerifyCompleted(root,data,backupId,operationId,verified.Digest,completed,
                    Path.Combine(completed,"identity","master-key"),Path.Combine(completed,"policy","update-policy"),Path.Combine(completed,"policy","update-pin"),retained,files);
                return new(operationId,completed,schema,true);
            }
            finally{for(var i=retained.Count-1;i>=0;i--)retained[i].Dispose();}
        }
        finally{serial.Release();}
    }
    public void Dispose()
    {
        serial.Wait();try{if(disposed)return;if(pendingProbe is not null){if(!pendingProbe.QuiescenceConfirmed)throw new IOException("RECOVERY_PROBE_QUIESCENCE_UNCONFIRMED");if(pendingProbeScratch is not null)CleanupProbe(pendingProbeScratch);pendingProbe=null;pendingProbeScratch=null;}disposed=true;pinFile.Dispose();policyFile.Dispose();keyFile.Dispose();lease.Dispose();rootPin.Dispose();appJob.Dispose();CryptographicOperations.ZeroMemory(encryption);CryptographicOperations.ZeroMemory(authentication);}
        finally{serial.Release();}
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool MoveFileExW(string existing,string destination,uint flags);
}

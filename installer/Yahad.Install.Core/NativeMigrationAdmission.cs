using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Yahad.Install;

internal sealed record MigrationFile(string Name,long Bytes,string Sha256);
internal sealed record MigrationReceipt(int Format,OfflineMigrationPlan Plan,string Nonce,int ObservedSchema,
    MigrationSandboxObservation Actor,MigrationFile[] Files);
internal sealed record MigrationEnvelope(string Payload,string Mac);

// An authority-produced capability, not a child result DTO or a caller flag.
internal sealed class VerifiedMigration
{
    internal OfflineMigrationPlan Plan{get;}internal string ReceiptDigest{get;}internal string ReceiptPath{get;}internal int ObservedSchema{get;}
    private VerifiedMigration(OfflineMigrationPlan plan,string digest,string receipt,int schema){Plan=plan;ReceiptDigest=digest;ReceiptPath=receipt;ObservedSchema=schema;}
    internal static VerifiedMigration FromVerifiedReceipt(OfflineMigrationPlan plan,string digest,string path,int schema)=>new(plan,digest,path,schema);
}

internal static class NativeMigrationAdmission
{
    private const int MaximumFiles=10000,MaximumReceipt=4*1024*1024;
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string PathOf(InstalledStateFiles files,OfflineMigrationPlan plan)=>files.PathOf("datasets/"+plan.SourceDataId+"/restore-"+plan.CloneOperationId+"/migration.receipt");
    internal static VerifiedMigrationSpec SpecForInstalled(InstalledStateFiles files,InstalledMigration installed)
        =>BuildSpec(files,installed.Plan,installed.Nonce,installed.NodeDigest,installed.RuntimeSid,installed.AppContainerSid,installed.SourceRestoreOperationId);
    internal static VerifiedMigrationSpec SpecForLaunch(InstalledStateFiles files,MigrationLaunchDescriptor launch)
        =>BuildSpec(files,launch.Plan,launch.Nonce,launch.NodeDigest,launch.RuntimeSid,launch.AppContainerSid,launch.SourceRestoreOperationId);
    private static VerifiedMigrationSpec BuildSpec(InstalledStateFiles files,OfflineMigrationPlan plan,string nonce,string nodeDigest,string runtimeSid,string appContainerSid,string? sourceRestoreOperationId)
    {
        plan.Validate();var workspace=files.PathOf("migration-staging/"+plan.OperationId);var payload=Path.Combine(workspace,"payload");
        var clone=files.PathOf("datasets/"+plan.SourceDataId+"/restore-"+plan.CloneOperationId+"/completed");
        if(sourceRestoreOperationId is { } sourceOperation&&(!Guid.TryParseExact(sourceOperation,"N",out var id)||id.ToString("N")!=sourceOperation))throw new IOException("MIGRATION_SOURCE_ID");
        var source=sourceRestoreOperationId is { } operation?files.PathOf("datasets/"+plan.SourceDataId+"/restore-"+operation+"/completed/data"):files.PathOf("datasets/"+plan.SourceDataId+"/data");
        var authority=files.Roles?.Authority.Value??"S-1-5-80-2";var runtime=files.Roles?.Runtime.Value??runtimeSid;
        if(runtime!=runtimeSid)throw new IOException("MIGRATION_RUNTIME_ROLE_CHANGED");
        return new(plan,Path.Combine(payload,"runtime","node","node.exe"),nodeDigest,Path.Combine(payload,"app","src","native-migrate-v1.ts"),payload,clone,Path.Combine(clone,"data"),
            Path.Combine(workspace,"work","request.json"),Path.Combine(workspace,"work","result.json"),Path.Combine(workspace,"temp"),runtime,appContainerSid,nonce,source,files.Root,authority);
    }
    private static string Key(VerifiedMigrationSpec spec)=>Path.Combine(spec.CloneRoot,"identity","master-key");
    private static string Policy(VerifiedMigrationSpec spec)=>Path.Combine(spec.CloneRoot,"policy","update-policy");
    private static string Pin(VerifiedMigrationSpec spec)=>Path.Combine(spec.CloneRoot,"policy","update-pin");
    private static void RequireIdentity(InstalledStateFiles files,VerifiedMigrationSpec spec)
    {
        if(Hash(files.Read(Key(spec),4096))!=spec.Plan.MasterKeyDigest||Hash(files.Read(Policy(spec),1024*1024))!=spec.Plan.PolicyDigest||
            Hash(files.Read(Pin(spec),1024*1024))!=spec.Plan.PinDigest)throw new IOException("MIGRATION_COPIED_IDENTITY_CHANGED");
    }
    internal static void RequireBaseline(InstalledStateFiles files,VerifiedMigrationSpec spec)
    {
        spec.Validate();if(!VerifiedMigrationSpec.Same(files.Root,spec.ControlRoot))throw new IOException("MIGRATION_ROOT_BINDING");RequireIdentity(files,spec);
        var pins=new List<IDisposable>();
        try
        {
            var schema=NativeRecoveryAdmission.VerifyCompleted(files.PathOf("datasets/"+spec.Plan.SourceDataId),spec.SourceDataDirectory,
                spec.Plan.BackupId,spec.Plan.CloneOperationId,spec.Plan.BackupReceiptDigest,spec.CloneRoot,Key(spec),Policy(spec),Pin(spec),pins,files);
            if(schema!=spec.Plan.SourceSchema)throw new IOException("MIGRATION_BASELINE_SCHEMA");
        }
        finally{foreach(var item in pins)item.Dispose();}
    }
    private static byte[] AuthenticationKey(InstalledStateFiles files,VerifiedMigrationSpec spec)
    {
        var encoded=files.Read(Key(spec),4096);var raw=new byte[32];
        try
        {
            var start=0;var end=encoded.Length;while(start<end&&encoded[start] is 9 or 10 or 13 or 32)start++;while(end>start&&encoded[end-1] is 9 or 10 or 13 or 32)end--;
            if(Base64.DecodeFromUtf8(encoded.AsSpan(start,end-start),raw,out var consumed,out var written)!=OperationStatus.Done||consumed!=end-start||written!=32)throw new IOException("MIGRATION_KEY_FORMAT");
            return HKDF.DeriveKey(HashAlgorithmName.SHA256,raw,32,info:Encoding.UTF8.GetBytes("yahad-offline-migration-receipt-v1"));
        }
        finally{CryptographicOperations.ZeroMemory(encoded);CryptographicOperations.ZeroMemory(raw);}
    }
    private static MigrationFile[] Inventory(InstalledStateFiles files,string root,bool includeData=true)
    {
        var result=new List<MigrationFile>();var visited=0;long total=0;
        void Walk(string directory,int depth)
        {
            if(depth>64)throw new IOException("MIGRATION_TREE_DEPTH");using var pinned=files.PinDirectory(directory);
            foreach(var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                if(++visited>100000||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("MIGRATION_TREE_BOUND");
                if(Directory.Exists(path)){if(includeData||!VerifiedMigrationSpec.Same(path,Path.Combine(root,"data")))Walk(path,depth+1);continue;}
                var name=Path.GetRelativePath(root,path).Replace('\\','/');if(name=="data/process.lock")continue;
                if(name.Length>200||name.Split('/').Any(x=>x is "" or "." or "..")||
                    !(name.StartsWith("data/",StringComparison.Ordinal)||name is "identity/master-key" or "policy/update-policy" or "policy/update-pin")||
                    name is "data/app.sqlite-wal" or "data/app.sqlite-shm" or "data/app.sqlite-journal"||result.Count>=MaximumFiles)throw new IOException("MIGRATION_MEMBER_SCOPE");
                using var source=files.Open(path);if(source.Length<0||source.Length>256L*1024*1024||(total+=source.Length)>2L*1024*1024*1024)throw new IOException("MIGRATION_MEMBER_BOUND");
                using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[1024*1024];long position=0;
                try{int read;while((read=source.Read(position,buffer))>0){hash.AppendData(buffer,0,read);position+=read;}}
                finally{CryptographicOperations.ZeroMemory(buffer);}
                if(position!=source.Length)throw new IOException("MIGRATION_MEMBER_CHANGED");result.Add(new(name,position,Convert.ToHexStringLower(hash.GetHashAndReset())));
            }
        }
        Walk(root,0);var required=includeData?new[]{"data/app.sqlite","identity/master-key","policy/update-policy","policy/update-pin"}:new[]{"identity/master-key","policy/update-policy","policy/update-pin"};
        if(!required.All(name=>result.Any(f=>f.Name==name)))throw new IOException("MIGRATION_CLOSURE_MISSING");
        return result.OrderBy(f=>f.Name,StringComparer.Ordinal).ToArray();
    }
    internal static async Task<VerifiedMigration> VerifyAndPublishAsync(InstalledStateFiles files,NativeRecoveryFixture source,VerifiedMigrationSpec spec,
        MigrationSandboxObservation actor,CancellationToken cancellation)
    {
        spec.Validate();NativeOfflineMigration.RequireObserved(spec,actor);RequireIdentity(files,spec);
        var child=NativeJson.Read<MigrationChildResult>(files.Read(spec.ResultFile,8192));
        if(child!=new MigrationChildResult(1,spec.Plan.OperationId,spec.Nonce,spec.Plan.SourceSchema,spec.Plan.TargetSchema,spec.Plan.PayloadDigest))throw new IOException("MIGRATION_RESULT_BINDING");
        var held=new List<IDisposable>();
        try
        {
            // Reuse the existing authenticated backup/restore journal and exact
            // copied keys/policy verification. Only data may have migrated.
            var baseline=NativeRecoveryAdmission.VerifyCommittedEvidence(files.PathOf("datasets/"+spec.Plan.SourceDataId),spec.SourceDataDirectory,
                spec.Plan.BackupId,spec.Plan.CloneOperationId,spec.Plan.BackupReceiptDigest,spec.CloneRoot,Key(spec),Policy(spec),Pin(spec),held,files);
            if(baseline!=spec.Plan.SourceSchema)throw new IOException("MIGRATION_SOURCE_SCHEMA");
            var schema=await source.InspectRestoredSchemaAsync(spec.Plan.CloneOperationId,cancellation);
            if(schema!=spec.Plan.TargetSchema)throw new IOException("MIGRATION_TARGET_SCHEMA");
            var members=Inventory(files,spec.CloneRoot);var receipt=new MigrationReceipt(1,spec.Plan,spec.Nonce,schema,actor,members);var payload=JsonSerializer.SerializeToUtf8Bytes(receipt);
            var key=AuthenticationKey(files,spec);
            byte[] bytes;try{bytes=JsonSerializer.SerializeToUtf8Bytes(new MigrationEnvelope(Convert.ToBase64String(payload),Convert.ToHexStringLower(HMACSHA256.HashData(key,payload))));}
            finally{CryptographicOperations.ZeroMemory(key);}
            if(bytes.Length>MaximumReceipt)throw new IOException("MIGRATION_RECEIPT_BOUND");
            var destination=PathOf(files,spec.Plan);
            if(File.Exists(destination)){if(!files.Read(destination,MaximumReceipt).SequenceEqual(bytes))throw new IOException("MIGRATION_RECEIPT_CONFLICT");}
            else{using var output=files.Create(destination);output.Write(bytes);output.Flush(true);}
            return VerifiedMigration.FromVerifiedReceipt(spec.Plan,Hash(bytes),destination,schema);
        }
        finally{foreach(var item in held)item.Dispose();}
    }
    internal static VerifiedMigration VerifyCompleted(InstalledStateFiles files,VerifiedMigrationSpec spec,string expectedDigest)
        =>Verify(files,spec,expectedDigest,true);
    internal static VerifiedMigration VerifyCommittedEvidence(InstalledStateFiles files,VerifiedMigrationSpec spec,string expectedDigest)
        =>Verify(files,spec,expectedDigest,false);
    private static VerifiedMigration Verify(InstalledStateFiles files,VerifiedMigrationSpec spec,string expectedDigest,bool originalData)
    {
        spec.Validate();OfflineMigrationPlan.Digest(expectedDigest);RequireIdentity(files,spec);var bytes=files.Read(PathOf(files,spec.Plan),MaximumReceipt);
        if(Hash(bytes)!=expectedDigest)throw new IOException("MIGRATION_RECEIPT_DIGEST");var envelope=NativeJson.Read<MigrationEnvelope>(bytes);var payload=Convert.FromBase64String(envelope.Payload);var key=AuthenticationKey(files,spec);
        try{if(!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key,payload),Convert.FromHexString(envelope.Mac)))throw new IOException("MIGRATION_RECEIPT_MAC");}
        finally{CryptographicOperations.ZeroMemory(key);}
        var receipt=NativeJson.Read<MigrationReceipt>(payload);
        if(receipt.Format!=1||receipt.Plan!=spec.Plan||receipt.Nonce!=spec.Nonce||receipt.ObservedSchema!=spec.Plan.TargetSchema||receipt.Files.Length is <4 or >MaximumFiles)throw new IOException("MIGRATION_RECEIPT_SCOPE");
        NativeOfflineMigration.RequireObserved(spec,receipt.Actor);
        var retained=new List<IDisposable>();
        try
        {
            var baseline=NativeRecoveryAdmission.VerifyCommittedEvidence(files.PathOf("datasets/"+spec.Plan.SourceDataId),spec.SourceDataDirectory,
                spec.Plan.BackupId,spec.Plan.CloneOperationId,spec.Plan.BackupReceiptDigest,spec.CloneRoot,Key(spec),Policy(spec),Pin(spec),retained,files);
            if(baseline!=spec.Plan.SourceSchema)throw new IOException("MIGRATION_SOURCE_SCHEMA");
        }
        finally{foreach(var item in retained)item.Dispose();}
        var actual=Inventory(files,spec.CloneRoot,originalData);var expected=receipt.Files;
        if(!originalData)expected=expected.Where(f=>!f.Name.StartsWith("data/",StringComparison.Ordinal)).ToArray();
        if(!actual.SequenceEqual(expected))throw new IOException("MIGRATION_POST_DATA_CHANGED");
        return VerifiedMigration.FromVerifiedReceipt(spec.Plan,expectedDigest,PathOf(files,spec.Plan),receipt.ObservedSchema);
    }
}

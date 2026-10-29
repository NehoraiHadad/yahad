using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Yahad.Install;

// Read-only bridge to the actual encrypted backup and chained restore journal
// format emitted by NativeRecoveryFixture. No caller-supplied "verified" flags.
internal static class NativeRecoveryAdmission
{
    internal static int VerifyCompleted(string catalog,string sourceData,string backupId,string operationId,
        string expectedReceipt,string candidate,string masterKey,string policy,string updatePin,ICollection<IDisposable> retained)
        => Verify(catalog,sourceData,backupId,operationId,expectedReceipt,candidate,masterKey,policy,updatePin,retained,true);
    // Only an independently protected committed selection may consume evidence
    // without requiring active mutable data to equal the original restore.
    internal static int VerifyCommittedEvidence(string catalog,string sourceData,string backupId,string operationId,
        string expectedReceipt,string candidate,string masterKey,string policy,string updatePin,ICollection<IDisposable> retained)
        => Verify(catalog,sourceData,backupId,operationId,expectedReceipt,candidate,masterKey,policy,updatePin,retained,false);
    private static int Verify(string catalog,string sourceData,string backupId,string operationId,string expectedReceipt,
        string candidate,string masterKey,string policy,string updatePin,ICollection<IDisposable> retained,bool requireOriginalPlaintext)
    {
        void Id(string value){if(!Guid.TryParseExact(value,"N",out var id)||id.ToString("N")!=value)throw new IOException("RECOVERY_ADMISSION_ID");}
        Id(backupId);Id(operationId);
        WindowsFileBoundary.PrivateReadLease Pin(string path){var p=WindowsFileBoundary.OpenPrivateRead(path);retained.Add(p);return p;}
        byte[] Read(string path,int bound)=>Pin(path).Small(bound);
        string Hash(byte[] value)=>Convert.ToHexStringLower(SHA256.HashData(value));
        string Digest(WindowsFileBoundary.PrivateReadLease file)
        {using var h=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var b=new byte[1024*1024];long offset=0;try{int n;while((n=file.Read(offset,b))>0){h.AppendData(b,0,n);offset+=n;}return Convert.ToHexStringLower(h.GetHashAndReset());}finally{CryptographicOperations.ZeroMemory(b);}}
        var encoded=Read(masterKey,4096);var raw=new byte[32];
        try
        {
            var start=0;var end=encoded.Length;while(start<end&&encoded[start] is 9 or 10 or 13 or 32)start++;while(end>start&&encoded[end-1] is 9 or 10 or 13 or 32)end--;
            if(Base64.DecodeFromUtf8(encoded.AsSpan(start,end-start),raw,out var consumed,out var written)!=OperationStatus.Done||written!=32||consumed!=end-start)
            {CryptographicOperations.ZeroMemory(raw);throw new IOException("RECOVERY_ADMISSION_KEY");}
        }
        finally{CryptographicOperations.ZeroMemory(encoded);}
        var key=HKDF.DeriveKey(HashAlgorithmName.SHA256,raw,32,info:Encoding.UTF8.GetBytes("yahad-fixture-backup-authentication-v1"));
        try
        {
            var context=Hash(Encoding.UTF8.GetBytes(Hash(raw)+":"+Digest(Pin(policy))+":"+Digest(Pin(updatePin))));
            T Auth<T>(byte[] bytes,string purpose)
            {
                var envelope=NativeJson.Read<RecoveryEnvelope>(bytes);
                var expected=HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(purpose+":"+envelope.Payload));
                var mac=Convert.FromHexString(envelope.Mac);
                if(!CryptographicOperations.FixedTimeEquals(expected,mac))throw new IOException("RECOVERY_ADMISSION_MAC");
                return NativeJson.Read<T>(Convert.FromBase64String(envelope.Payload));
            }
            var backup=Path.Combine(catalog,"backup-"+backupId);retained.Add(WindowsFileBoundary.PinPrivateDirectory(backup));
            var receipt=Read(Path.Combine(backup,"receipt.json"),RecoveryReceiptEncoding.MaximumBytes);
            if(Hash(receipt)!=expectedReceipt)throw new IOException("RECOVERY_ADMISSION_RECEIPT_ID");
            var manifest=Auth<RecoveryManifest>(receipt,"backup");
            if(manifest.Format!=1||manifest.BackupId!=backupId||manifest.ContextDigest!=context||manifest.Schema is <1 or >100000||manifest.Files.Length is <4 or >10000)throw new IOException("RECOVERY_ADMISSION_MANIFEST");
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
            for(var i=0;i<manifest.Files.Length;i++)
            {
                var f=manifest.Files[i];Member(f.Name);
                if(!names.Add(f.Name)||!(f.Name.StartsWith("data/",StringComparison.Ordinal)||f.Name is "identity/master-key" or "policy/update-policy" or "policy/update-pin")||
                    f.Name is "data/process.lock" or "data/app.sqlite-wal" or "data/app.sqlite-shm" or "data/app.sqlite-journal"||f.Bytes<0||f.Bytes>256L*1024*1024||(total+=f.Bytes)>2L*1024*1024*1024)throw new IOException("RECOVERY_ADMISSION_MEMBERS");
                var cipher=Pin(Path.Combine(backup,$"{i:D5}.bin"));
                if(cipher.Length!=f.EncryptedBytes||Digest(cipher)!=f.EncryptedSha256)throw new IOException("RECOVERY_ADMISSION_CIPHER");
                if(requireOriginalPlaintext||!f.Name.StartsWith("data/",StringComparison.Ordinal))
                {
                    var plain=Pin(Path.Combine(candidate,f.Name.Replace('/',Path.DirectorySeparatorChar)));
                    if(plain.Length!=f.Bytes||Digest(plain)!=f.Sha256)throw new IOException("RECOVERY_ADMISSION_RESTORED_BYTES");
                }
            }
            if(!new[]{"data/app.sqlite","identity/master-key","policy/update-policy","policy/update-pin"}.All(names.Contains)||
                requireOriginalPlaintext&&!names.SetEquals(Files(candidate).Where(n=>n!="data/process.lock"))||
                !Enumerable.Range(0,manifest.Files.Length).Select(i=>$"{i:D5}.bin").Append("receipt.json").ToHashSet(StringComparer.Ordinal).SetEquals(Files(backup)))throw new IOException("RECOVERY_ADMISSION_CLOSURE");
            var journal=Path.Combine(catalog,"restore-"+operationId);retained.Add(WindowsFileBoundary.PinPrivateDirectory(journal));
            if(!string.Equals(candidate,Path.Combine(journal,"completed"),StringComparison.OrdinalIgnoreCase))throw new IOException("RECOVERY_ADMISSION_SELECTION");
            var paths=Directory.GetFiles(journal,"*.json").Order(StringComparer.Ordinal).ToArray();
            if(paths.Length is <4 or >128||Directory.EnumerateFiles(journal).Any(p=>Path.GetFileName(p).Contains(".partial",StringComparison.OrdinalIgnoreCase)))throw new IOException("RECOVERY_ADMISSION_PENDING");
            string previous="";RecoveryRecord? first=null,last=null;var sourceDigest=Hash(Encoding.UTF8.GetBytes(sourceData.ToUpperInvariant()));
            for(var i=0;i<paths.Length;i++)
            {
                if(Path.GetFileName(paths[i])!=$"{i+1:D5}.json")throw new IOException("RECOVERY_ADMISSION_SEQUENCE");
                var bytes=Read(paths[i],16384);var record=Auth<RecoveryRecord>(bytes,"journal");
                if(record.Format!=1||record.Sequence!=i+1||record.OperationId!=operationId||record.BackupId!=backupId||record.ReceiptDigest!=expectedReceipt||
                    record.ContextDigest!=context||record.PreviousDigest!=previous||record.DataPathDigest!=sourceDigest||record.ObservedJobRootProcessId<1||
                    record.MinimumSchema<1||record.MaximumSchema<record.MinimumSchema||record.Phase is not("planned" or "extracting" or "validated" or "complete"))throw new IOException("RECOVERY_ADMISSION_JOURNAL");
                Id(record.LeaseGeneration);if(record.Attempt is not null)Id(record.Attempt);
                if(i==0){if(record.Phase!="planned"||record.Attempt is not null)throw new IOException("RECOVERY_ADMISSION_PLAN");first=record;}
                else if(last!.Phase=="complete"||record.Phase=="planned"||record.MinimumSchema!=first!.MinimumSchema||record.MaximumSchema!=first.MaximumSchema)throw new IOException("RECOVERY_ADMISSION_TRANSITION");
                if(record.Phase=="validated"&&(last?.Phase!="extracting"||record.Attempt!=last.Attempt)||record.Phase=="complete"&&(last?.Phase!="validated"||record.Attempt!=last.Attempt))throw new IOException("RECOVERY_ADMISSION_TRANSITION");
                if((record.Phase is "extracting" or "validated" or "complete")&&record.Attempt is null)throw new IOException("RECOVERY_ADMISSION_ATTEMPT");
                previous=Hash(bytes);last=record;
            }
            if(last!.Phase!="complete"||manifest.Schema<last.MinimumSchema||manifest.Schema>last.MaximumSchema)throw new IOException("RECOVERY_ADMISSION_INCOMPLETE");
            return manifest.Schema;
        }
        finally{CryptographicOperations.ZeroMemory(raw);CryptographicOperations.ZeroMemory(key);}
    }
    private static void Member(string name)
    {if(name.Length is <1 or >200||name.Contains('\\')||name.Split('/').Any(p=>p is "" or "." or ".."||p.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new IOException("RECOVERY_ADMISSION_PATH");}
    private static string[] Files(string root)
    {
        var files=new List<string>();
        void Walk(string path,int depth)
        {if(depth>64)throw new IOException("RECOVERY_ADMISSION_DEPTH");using var pin=WindowsFileBoundary.PinPrivateDirectory(path);foreach(var entry in Directory.EnumerateFileSystemEntries(path)){if((File.GetAttributes(entry)&FileAttributes.ReparsePoint)!=0)throw new IOException("RECOVERY_ADMISSION_REPARSE");if(Directory.Exists(entry))Walk(entry,depth+1);else{files.Add(Path.GetRelativePath(root,entry).Replace('\\','/'));if(files.Count>10001)throw new IOException("RECOVERY_ADMISSION_COUNT");}}}
        Walk(root,0);return files.ToArray();
    }
}

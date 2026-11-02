using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Yahad.Install;

internal sealed partial class NativeIntakeRecovery
{
    private sealed record ArchiveProof(int Format,string Id,string OperationDigest,string IdentityDigest,string CompletionDigest);
    private string OperationPath(string id)=>Path.Combine(journal,"operation-"+id+".json");
    private string ArchiveProofPath(string id)=>Path.Combine(journal,"archive-"+id+".json");
    private string AuditRoot=>journal+"-audit";
    private static bool CanonicalId(string id)=>Guid.TryParseExact(id,"N",out var parsed)&&parsed.ToString("N")==id;
    private static (string Kind,string Id) MetadataName(string name)
    {
        var match=Regex.Match(name,"^(operation|identity|complete|archive)-([a-f0-9]{32})\\.(json|bin)$",RegexOptions.CultureInvariant);
        if(!match.Success||!CanonicalId(match.Groups[2].Value)||(match.Groups[1].Value=="complete")!=(match.Groups[3].Value=="bin"))throw Error();
        return(match.Groups[1].Value,match.Groups[2].Value);
    }
    private void WriteMetadata(string path,byte[] bytes)
    {
        if(Path.GetDirectoryName(path)!=journal||bytes.Length is <1 or >8*1024*1024)throw Error();
        var metadata=MetadataName(Path.GetFileName(path));requireSpace?.Invoke(bytes.LongLength+8192);
        var temporary=Path.Combine(journal,"metadata-"+metadata.Kind+"-"+metadata.Id+"-"+Guid.NewGuid().ToString("N")+".partial");
        Identity identity;
        using(var output=files.Create(temporary))
        {
            identity=Id(output.SafeFileHandle);fault?.Invoke("metadata_"+metadata.Kind+"_created");
            var first=Math.Max(1,bytes.Length/2);output.Write(bytes.AsSpan(0,first));fault?.Invoke("metadata_"+metadata.Kind+"_during_write");
            output.Write(bytes.AsSpan(first));fault?.Invoke("metadata_"+metadata.Kind+"_before_flush");output.Flush(true);fault?.Invoke("metadata_"+metadata.Kind+"_before_rename");
        }
        using var owned=Open(temporary,false);if(Id(owned)!=identity||Length(owned)!=bytes.Length||Hash(owned,bytes.Length)!=Hash(bytes))throw Error();
        Rename(owned,path,false);if(!Native.FlushFileBuffers(owned))throw Error();fault?.Invoke("metadata_"+metadata.Kind+"_published");
    }
    private void ReconcileMetadata(CancellationToken cancellation)
    {
        // Unpublished metadata never authorized object bytes. Preserve it, and
        // recover only from a durable strict intent plus an exact empty object.
        foreach(var path in Directory.EnumerateFileSystemEntries(journal,"metadata-*.partial").Take(65).ToArray())
        {
            cancellation.ThrowIfCancellationRequested();var match=Regex.Match(Path.GetFileName(path),"^metadata-(operation|identity|complete|archive)-([a-f0-9]{32})-([a-f0-9]{32})\\.partial$",RegexOptions.CultureInvariant);
            if(!match.Success||!CanonicalId(match.Groups[2].Value)||!CanonicalId(match.Groups[3].Value))throw Error();
            var kind=match.Groups[1].Value;var id=match.Groups[2].Value;
            if(kind=="operation")
            {
                if(File.Exists(OperationPath(id))||HasGeneratedObject(id)||File.Exists(Pin(id))||File.Exists(Done(id)))throw Error();
            }
            else
            {
                var record=ReadStrictOperation(id);
                if(kind=="identity")
                {if(File.Exists(Pin(id)))throw Error();RequireEmptyGenerated(record);}
                else if(kind=="complete")RequireReconstructibleCompletion(record);
                else {RequireDone(id);if(!File.Exists(Pin(id)))throw Error();}
            }
            RetainMetadata(path,cancellation);
        }
        foreach(var path in Directory.EnumerateFiles(journal,"operation-*.json").ToArray())
        {
            cancellation.ThrowIfCancellationRequested();var metadata=MetadataName(Path.GetFileName(path));if(metadata.Kind!="operation")throw Error();
            var bytes=ReadRaw(path,8*1024*1024);if(!TornJson(bytes))continue;
            if(HasGeneratedObject(metadata.Id)||File.Exists(Pin(metadata.Id))||File.Exists(Done(metadata.Id))||File.Exists(ArchiveProofPath(metadata.Id)))throw Error();
            RetainMetadata(path,cancellation);
        }
        foreach(var path in Directory.EnumerateFiles(journal,"identity-*.json").ToArray())
        {
            cancellation.ThrowIfCancellationRequested();var metadata=MetadataName(Path.GetFileName(path));if(metadata.Kind!="identity")throw Error();
            if(!TornJson(ReadRaw(path,1024)))continue;var record=ReadStrictOperation(metadata.Id);RequireEmptyGenerated(record);RetainMetadata(path,cancellation);
        }
        foreach(var path in Directory.EnumerateFiles(journal,"complete-*.bin").ToArray())
        {
            cancellation.ThrowIfCancellationRequested();var metadata=MetadataName(Path.GetFileName(path));var bytes=ReadRaw(path,16);
            if(bytes.SequenceEqual(new byte[]{1}))continue;if(bytes.Length!=0)throw Error();var record=ReadStrictOperation(metadata.Id);RequireReconstructibleCompletion(record);RetainMetadata(path,cancellation);
        }
        foreach(var path in Directory.EnumerateFiles(journal,"archive-*.json").ToArray())
        {cancellation.ThrowIfCancellationRequested();var proof=NativeJson.Read<ArchiveProof>(files.Read(path,4096));if(MetadataName(Path.GetFileName(path)).Id!=proof.Id)throw Error();ResumeArchive(proof);}
        CheckMetadataResidue();
    }
    private Record ReadStrictOperation(string id)
    {var record=NativeJson.Read<Record>(files.Read(OperationPath(id),8*1024*1024));Validate(record);if(record.Id!=id)throw Error();return record;}
    private static bool TornJson(byte[] bytes)
    {
        try{using var parsed=JsonDocument.Parse(bytes);return false;}
        catch(JsonException)
        {
            // A valid incomplete JSON prefix is recoverable; arbitrary malformed
            // complete metadata is not silently classified as a torn write.
            try{var reader=new Utf8JsonReader(bytes,false,default);while(reader.Read()){}return true;}catch(JsonException){return false;}
        }
    }
    private bool HasGeneratedObject(string id)
    {
        foreach(var relative in new[]{"intake/download-"+id+".zip.partial","payloads/.intake-"+id,"payloads/.retained-"+id,"system-slots/.staging-"+id,"system-slots/.retained-"+id,"intake/available.json.partial-"+id,"intake/system-available.json.partial-"+id})
        {var path=files.PathOf(relative);if(File.Exists(path)||Directory.Exists(path))return true;}
        return File.Exists(Path.Combine(journal,"retained-"+id))||Directory.Exists(Path.Combine(journal,"retained-"+id));
    }
    private void RequireEmptyGenerated(Record record)
    {
        var temporary=files.PathOf(record.Temporary);if(!File.Exists(temporary)&&!Directory.Exists(temporary))throw Error();
        if(IsDirectory(record))
        {using var held=files.PinDirectory(temporary);if(Directory.EnumerateFileSystemEntries(temporary).Any())throw Error();}
        else {using var input=files.Open(temporary);if(input.Length!=0)throw Error();}
        // Same role/no-follow/native kind proof before any malformed ID metadata
        // can be retired. A nonempty unproven object is never adopted or reset.
        using var original=Open(temporary,IsDirectory(record));
    }
    private void RequireReconstructibleCompletion(Record record)
    {
        var temporary=files.PathOf(record.Temporary);if(File.Exists(temporary)||Directory.Exists(temporary))throw Error();
        if(!File.Exists(Pin(record.Id))){if(HasGeneratedObject(record.Id))throw Error();return;}
        var retained=RetainedPath(record);var destination=File.Exists(retained)||Directory.Exists(retained)?retained:files.PathOf(record.Destination);
        using var original=Open(destination,IsDirectory(record));RequireIdentity(record,original,false);
    }
    private byte[] ReadRaw(string path,int bound)
    {
        using var input=files.Open(path);if(input.Length<0||input.Length>bound)throw Error();return input.Length==0?[]:input.Small(bound);
    }
    private void RetainMetadata(string path,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();using var owned=Open(path,false);var bytes=Length(owned);if(bytes>8*1024*1024)throw Error();CheckMetadataResidue(true,bytes);
        Rename(owned,Path.Combine(journal,"metadata-retained-"+Guid.NewGuid().ToString("N")),false);
    }
    private void CheckMetadataResidue(bool reserve=false,long additional=0)
    {
        long total=additional;var count=0;foreach(var path in Directory.EnumerateFileSystemEntries(journal,"metadata-retained-*"))
        {
            var suffix=Path.GetFileName(path)[18..];if(!CanonicalId(suffix)||++count>(reserve?31:32))throw Error();using var input=files.Open(path);total=checked(total+input.Length);if(total>64L*1024*1024)throw Error();
        }
    }
    private void RequireDone(string id)
    {if(!ReadRaw(Done(id),16).SequenceEqual(new byte[]{1}))throw Error();}
    private void VerifyAndCompletePublished(Record record,string destination,CancellationToken cancellation)
    {
        if(IsDirectory(record))VerifyPrefix(record,destination,cancellation,true);
        using var original=Open(destination,IsDirectory(record));RequireIdentity(record,original,false);
        if(!IsDirectory(record)&&(Length(original)!=record.Bytes||Hash(original,record.Bytes)!=record.Digest))throw Error();Complete(record,true);
    }
    private void TryArchivePublished(Record record,CancellationToken cancellation)
    {
        if(!File.Exists(Pin(record.Id)))return;var destination=files.PathOf(record.Destination);
        if(!File.Exists(destination)&&!Directory.Exists(destination))return;
        // A later pointer may have replaced this exact destination. Preserve its
        // older evidence; only the actual original published identity can compact.
        using(var original=Open(destination,IsDirectory(record)))
        {if(NativeJson.Read<Identity>(files.Read(Pin(record.Id),1024))!=Id(original))return;}
        VerifyAndCompletePublished(record,destination,cancellation);
    }
    // Only reached immediately after complete closure/whole-file verification
    // and same-handle publication, or re-proving that exact published identity.
    // Audit metadata moves out of the active journal; no artifact/data is deleted.
    private void ArchiveMetadata(Record record)
    {
        RequireDone(record.Id);var operation=ReadRaw(OperationPath(record.Id),8*1024*1024);if(!operation.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(record)))throw Error();
        var identity=ReadRaw(Pin(record.Id),1024);_=NativeJson.Read<Identity>(identity);
        var proof=new ArchiveProof(1,record.Id,Hash(operation),Hash(identity),Hash(new byte[]{1}));var path=ArchiveProofPath(record.Id);
        if(!File.Exists(path))Write(path,JsonSerializer.SerializeToUtf8Bytes(proof));else if(NativeJson.Read<ArchiveProof>(files.Read(path,4096))!=proof)throw Error();
        ResumeArchive(proof);
    }
    private void ResumeArchive(ArchiveProof proof)
    {
        if(proof.Format!=1||!CanonicalId(proof.Id))throw Error();foreach(var digest in new[]{proof.OperationDigest,proof.IdentityDigest,proof.CompletionDigest})Digest(digest);
        if(proof.CompletionDigest!=Hash(new byte[]{1}))throw Error();
        using var intent=Open(ArchiveProofPath(proof.Id),false);var expected=JsonSerializer.SerializeToUtf8Bytes(proof);if(Length(intent)!=expected.Length||Hash(intent,expected.Length)!=Hash(expected))throw Error();
        if(!Directory.Exists(AuditRoot)){requireSpace?.Invoke(4096);files.CreateDirectory(AuditRoot);}else using(files.PinDirectory(AuditRoot)){}
        var destination=Path.Combine(AuditRoot,proof.Id);if(!Directory.Exists(destination)){requireSpace?.Invoke(4096);files.CreateDirectory(destination);}else using(files.PinDirectory(destination)){}
        void MoveRecord(string name,string digest)
        {
            var active=Path.Combine(journal,name);var archived=Path.Combine(destination,name);
            if(File.Exists(active)&&File.Exists(archived))throw Error();var original=File.Exists(active)?active:archived;
            using var owned=Open(original,false);var bytes=Length(owned);if(bytes is <1 or >8*1024*1024||Hash(owned,bytes)!=digest)throw Error();
            if(original==active){Rename(owned,archived,false);if(!Native.FlushFileBuffers(owned))throw Error();fault?.Invoke("metadata_archive_member_moved");}
        }
        MoveRecord("operation-"+proof.Id+".json",proof.OperationDigest);MoveRecord("identity-"+proof.Id+".json",proof.IdentityDigest);MoveRecord("complete-"+proof.Id+".bin",proof.CompletionDigest);
        Rename(intent,Path.Combine(destination,"archive-"+proof.Id+".json"),false);if(!Native.FlushFileBuffers(intent))throw Error();fault?.Invoke("metadata_archive_complete");
    }
}

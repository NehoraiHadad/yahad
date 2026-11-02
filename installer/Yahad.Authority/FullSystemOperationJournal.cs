using System.Runtime.InteropServices;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record FullSystemOperationRecord(int Format,string InstallationId,string OperationId,string SourceReceiptDigest,string PolicyDigest,
    string GrantId,string GrantBindingDigest,FullSystemInstalledState Observed,string ManifestDigest,string ApplicationBundleDigest,string TargetVersion,string SourceCommit,int TargetSchema,
    string GrantKind="auto",string? ManualProposalDigest=null);

// Admission records are append only and contain source facts, never executable
// serialized capabilities. Reading one does not authorize forward progress.
internal static class FullSystemOperationJournal
{
    private const string DirectoryName=".authority/system-operations";
    internal static void Publish(InstalledStateFiles files,FullSystemOperationRecord record)
    {
        Validate(record,record.InstallationId,record.OperationId);
        foreach(var part in new[]{".authority",DirectoryName})
        {var directory=files.PathOf(part);if(!Directory.Exists(directory))files.CreateDirectory(directory);using var pin=files.PinDirectory(directory);}
        if(Directory.EnumerateFileSystemEntries(files.PathOf(DirectoryName)).Take(2001).Count()>2000)throw new IOException("FULL_SYSTEM_OPERATION_CATALOG_BOUND");
        var final=files.PathOf(DirectoryName+"/"+record.OperationId+".json");var bytes=JsonSerializer.SerializeToUtf8Bytes(record);
        if(File.Exists(final))
        {if(!files.Read(final,16384).AsSpan().SequenceEqual(bytes))throw new IOException("FULL_SYSTEM_OPERATION_REPLAY_BINDING");return;}
        var temporary=final+".partial-"+Guid.NewGuid().ToString("N");
        using(var output=files.Create(temporary)){output.Write(bytes);output.Flush(true);}
        // No REPLACE_EXISTING: a concurrently admitted different operation can
        // never overwrite the already reviewed protected identity.
        if(!MoveFileExW(@"\\?\"+temporary,@"\\?\"+final,8))throw new IOException("FULL_SYSTEM_OPERATION_PUBLICATION");
    }
    internal static FullSystemOperationRecord Read(InstalledStateFiles files,string installation,string operation)
    {
        InstalledActivationResolver.Id(operation);var record=NativeJson.Read<FullSystemOperationRecord>(files.Read(files.PathOf(DirectoryName+"/"+operation+".json"),16384));
        Validate(record,installation,operation);return record;
    }
    private static void Validate(FullSystemOperationRecord value,string installation,string operation)
    {
        foreach(var id in new[]{installation,operation,value.GrantId})InstalledActivationResolver.Id(id);
        foreach(var digest in new[]{value.SourceReceiptDigest,value.PolicyDigest,value.GrantBindingDigest,value.ManifestDigest,value.ApplicationBundleDigest,value.Observed.ActivationDigest})InstalledActivationResolver.Digest(digest);
        if(value.Format!=1||value.InstallationId!=installation||value.OperationId!=operation||value.Observed.BootstrapEpoch<1||value.TargetSchema<value.Observed.DatabaseSchema||
            value.TargetSchema>100000||value.Observed.DatabaseSchema<1||value.GrantKind is not("auto" or "manual")||
            (value.GrantKind=="manual")!=(value.ManualProposalDigest is not null))throw new IOException("FULL_SYSTEM_OPERATION_RECORD");
        if(value.ManualProposalDigest is {} manual)InstalledActivationResolver.Digest(manual);
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool MoveFileExW(string source,string target,uint flags);
}

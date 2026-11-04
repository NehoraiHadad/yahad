using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;

// The database files as they stand with the Runtime stopped, kept beside the
// dataset where the Runtime can read them but not change what a restore consumes.
// Media is append-only and stays where it is. Both directions hold the data lease,
// so nothing else can have the database open. The Runtime writes the data directory,
// so its files are reached without following a link it may have left there.
internal static class DatabaseBackup
{
    private sealed record Entry(string Name,long Bytes,string Sha256);
    private static readonly string[] Names=["app.sqlite-wal","app.sqlite-shm","app.sqlite"];
    private const string ManifestName="manifest.json";
    internal static long Size(string data)=>Names.Select(n=>new FileInfo(Path.Combine(data,n))).Where(f=>f.Exists).Sum(f=>f.Length);
    internal static void Create(string data,string backup)
    {
        UpgradeFiles.Remove(backup);var parent=Path.GetDirectoryName(backup)!;
        if(!Directory.Exists(parent))NativeInstallFiles.NewDirectory(parent);
        NativeInstallFiles.NewDirectory(backup);
        using var lease=WindowsFileBoundary.AcquireDataLease(data);
        var entries=new List<Entry>();
        foreach(var name in Names)
        {
            var source=Path.Combine(data,name);if(!File.Exists(source))continue;
            var(bytes,digest)=Copy(source,Path.Combine(backup,name));entries.Add(new(name,bytes,digest));
        }
        // The manifest is last: its presence means the backup is whole.
        NativeInstallFiles.PublishNew(Path.Combine(backup,ManifestName),JsonSerializer.SerializeToUtf8Bytes(entries));
    }
    // Verified before anything live is touched, and safe to run again after an
    // interruption. The main database file appears last and whole, through a
    // same-directory rename, so a partial restore can never be opened as a database.
    internal static void Restore(string data,string backup)
    {
        using var lease=WindowsFileBoundary.AcquireDataLease(data);
        var entries=NativeJson.Read<Entry[]>(NativeInstallFiles.ReadRecord(Path.Combine(backup,ManifestName),65536));
        if(entries.Any(e=>!Names.Contains(e.Name)))throw new IOException("UPGRADE_BACKUP_INVALID");
        foreach(var entry in entries)
        {
            using var file=new FileStream(Path.Combine(backup,entry.Name),FileMode.Open,FileAccess.Read,FileShare.Read);
            if(file.Length!=entry.Bytes||Convert.ToHexStringLower(SHA256.HashData(file))!=entry.Sha256)throw new IOException("UPGRADE_BACKUP_INVALID");
        }
        foreach(var partial in Directory.EnumerateFiles(data,"app.sqlite*.partial-*"))WindowsFileBoundary.DeleteNoFollow(partial,false);
        foreach(var name in Names.Reverse())
        {var live=Path.Combine(data,name);if(File.Exists(live))WindowsFileBoundary.DeleteNoFollow(live,false);}
        foreach(var name in Names)
        {
            var entry=entries.SingleOrDefault(e=>e.Name==name);if(entry is null)continue;
            var live=Path.Combine(data,name);var partial=live+".partial-"+Guid.NewGuid().ToString("N");
            var(bytes,digest)=Copy(Path.Combine(backup,name),partial);
            if(bytes!=entry.Bytes||digest!=entry.Sha256)throw new IOException("UPGRADE_BACKUP_INVALID");
            NativeInstallFiles.CommitNew(partial,live);
        }
    }
    private static (long Bytes,string Digest) Copy(string source,string target)
    {
        using var input=WindowsFileBoundary.OpenRead(source);using var output=NativeInstallFiles.CreateNewFile(target);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var chunk=new byte[1048576];long bytes=0;int n;
        while((n=input.Read(bytes,chunk))>0){hash.AppendData(chunk,0,n);output.Write(chunk,0,n);bytes+=n;}
        output.Flush(true);return(bytes,Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}

using System.Security.Cryptography;
using System.Text.Json;

namespace Yahad.Install;

/// <summary>Developer adapter: verified private Node, fixed read-only SQL, no PATH/tool lookup.</summary>
public sealed class NativeSqliteInspector : IDisposable
{
    private readonly string node;
    private readonly FileStream pinned;
    private const string Script = """
        const {DatabaseSync}=require('node:sqlite');
        const fs=require('node:fs');
        const db=new DatabaseSync(process.argv[1],{readOnly:true});
        try {
          db.exec('PRAGMA query_only=ON; PRAGMA trusted_schema=OFF');
          const checks=db.prepare('PRAGMA integrity_check').all();
          const foreign=db.prepare('PRAGMA foreign_key_check').all();
          const versions=db.prepare('SELECT version FROM schema_version').all();
          if(checks.length!==1||Object.values(checks[0])[0]!=='ok'||foreign.length||versions.length!==1||!Number.isSafeInteger(versions[0].version)||versions[0].version<1)throw Error('invalid database');
          fs.writeFileSync(process.argv[2],JSON.stringify({schema:versions[0].version}),{flag:'wx',mode:0o600});
        } finally {db.close();}
        """;
    public NativeSqliteInspector(string privateNode, string expectedSha256)
    {
        node=Path.GetFullPath(privateNode); SafeDirectory.NoReparseAncestors(node);
        pinned=new FileStream(node,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(Convert.ToHexStringLower(SHA256.HashData(pinned))!=expectedSha256)
        { pinned.Dispose();throw new IOException("RECOVERY_NODE_IDENTITY"); }
    }
    public async Task<int> InspectAsync(string database, string privateScratch, CancellationToken cancellation)
    {
        var output=Path.Combine(privateScratch,"sqlite-observation-"+Guid.NewGuid().ToString("N")+".json");
        using var job=JobProcess.Start(node,["-e",Script,database,output],privateScratch,LaunchEnvironment.Clean(privateScratch));
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(30));
            if(await job.WaitAsync(deadline.Token)!=0)throw new IOException("RECOVERY_SQLITE_REFUSED");
            await job.WaitForTreeEmptyAsync(deadline.Token);
            using var file=WindowsFileBoundary.OpenPrivateRead(output);
            using var json=JsonDocument.Parse(file.Small(4096));
            var schema=json.RootElement.GetProperty("schema").GetInt32();
            if(schema<1||schema>100000)throw new IOException("RECOVERY_SCHEMA_BOUND");
            return schema;
        }
        catch
        {
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await job.TerminateAndWaitForTreeEmptyAsync(cleanup.Token);
            throw;
        }
        finally { if(File.Exists(output))File.Delete(output); }
    }
    public void Dispose()=>pinned.Dispose();
}

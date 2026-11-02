using System.Security.Cryptography;
using System.Text.Json;

namespace Yahad.Install;

/// <summary>Developer adapter: verified private Node, fixed read-only SQL, no PATH/tool lookup.</summary>
public sealed class NativeSqliteInspector : IDisposable
{
    private readonly string node;
    private readonly FileStream pinned;private ProbeOwnershipException? pending;private readonly JobProcessFaultInjection? fault;
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
    public NativeSqliteInspector(string privateNode,string expectedSha256):this(privateNode,expectedSha256,null){}
    internal NativeSqliteInspector(string privateNode,string expectedSha256,JobProcessFaultInjection? fault)
    {this.fault=fault;
        node=Path.GetFullPath(privateNode); SafeDirectory.NoReparseAncestors(node);
        pinned=new FileStream(node,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(Convert.ToHexStringLower(SHA256.HashData(pinned))!=expectedSha256)
        { pinned.Dispose();throw new IOException("RECOVERY_NODE_IDENTITY"); }
    }
    public async Task<int> InspectAsync(string database,string privateScratch,CancellationToken cancellation)
        =>await InspectCoreAsync(database,privateScratch,cancellation,false);
    internal Task<int> InspectOwnedPrivateProbeAsync(string database,string privateScratch,CancellationToken cancellation)
        =>InspectCoreAsync(database,privateScratch,cancellation,true);
    internal async Task RequireOperatorAsync(string database,string privateScratch,string familyId,string parentUserId,CancellationToken cancellation,bool ownedPrivateProbe=false)
    {
        static bool Id(string value)=>Guid.TryParseExact(value,"D",out var id)&&id.ToString("D")==value;
        if(!Id(familyId)||!Id(parentUserId)||familyId==parentUserId)throw new IOException("UPDATE_OPERATOR_IDENTITY");
        if(pending is not null)throw new IOException("RECOVERY_PROBE_PENDING");
        var expected=JsonSerializer.Serialize(new[]{familyId,parentUserId});
        var script="const expected="+expected+";"+"""
            const {DatabaseSync}=require('node:sqlite');const fs=require('node:fs');
            const db=new DatabaseSync(process.argv[1],{readOnly:true});
            try{db.exec('PRAGMA query_only=ON; PRAGMA trusted_schema=OFF');
              const row=db.prepare("SELECT u.id FROM users u JOIN families f ON f.id=u.family_id WHERE f.id=? AND u.id=? AND u.role='parent'").all(...expected);
              if(row.length!==1)throw Error('operator mismatch');
              fs.writeFileSync(process.argv[2],JSON.stringify({schema:1}),{flag:'wx',mode:0o600});
            }finally{db.close();}
            """;
        var output=Path.Combine(privateScratch,"sqlite-observation-"+Guid.NewGuid().ToString("N")+".json");
        try{_=await SqliteProbeProcess.ReadAsync(node,script,database,privateScratch,output,TimeSpan.FromSeconds(30),cancellation,fault,ownedPrivateProbe?privateScratch:null);}
        catch(ProbeOwnershipException owned){pending=owned;owned.KeepAlive(this);owned.AfterCleanup(()=>pending=null);throw;}
    }
    private async Task<int> InspectCoreAsync(string database,string privateScratch,CancellationToken cancellation,bool ownedProbe)
    {
        if(pending is not null)throw new IOException("RECOVERY_PROBE_PENDING");
        var output=Path.Combine(privateScratch,"sqlite-observation-"+Guid.NewGuid().ToString("N")+".json");
        try{return await SqliteProbeProcess.ReadAsync(node,Script,database,privateScratch,output,TimeSpan.FromSeconds(30),cancellation,fault,ownedProbe?privateScratch:null);}
        catch(ProbeOwnershipException owned){pending=owned;owned.KeepAlive(this);owned.AfterCleanup(()=>pending=null);throw;}
        catch(IOException refused)when(refused.Message=="SQLITE_PROBE_REFUSED"){throw new IOException("RECOVERY_SQLITE_REFUSED",refused);}
    }
    public void Dispose(){if(pending is not null&&!pending.QuiescenceConfirmed)throw new IOException("RECOVERY_PROBE_QUIESCENCE_UNCONFIRMED");pinned.Dispose();}
}

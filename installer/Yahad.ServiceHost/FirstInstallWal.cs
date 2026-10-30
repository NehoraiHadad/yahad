using Yahad.Install;
namespace Yahad.Service;

// The same non-administrator service owns the exclusive data lease. SQLite,
// not file deletion or immutable=1, reconciles its genuine crash-left journal.
internal static class FirstInstallWal
{
    private const string Script="""
        const{DatabaseSync}=require('node:sqlite'),fs=require('node:fs');
        const db=new DatabaseSync(process.argv[1]);
        try{
          db.exec('PRAGMA trusted_schema=OFF; PRAGMA foreign_keys=ON');
          const check=db.prepare('PRAGMA integrity_check').all(),foreign=db.prepare('PRAGMA foreign_key_check').all(),version=db.prepare('SELECT version FROM schema_version').all();
          if(check.length!==1||Object.values(check[0])[0]!=='ok'||foreign.length||version.length!==1||!Number.isSafeInteger(version[0].version)||version[0].version<1)throw Error('schema refused');
          const checkpoint=db.prepare('PRAGMA wal_checkpoint(TRUNCATE)').get();
          if(checkpoint&&checkpoint.busy)throw Error('checkpoint busy');
          fs.writeFileSync(process.argv[2],JSON.stringify({schema:version[0].version}),{flag:'wx',mode:0o600});
        }finally{db.close();}
        """;
    internal static Task<int> ReconcileAsync(string node,string database,string temporary,CancellationToken cancellation)
    {
        var output=Path.Combine(temporary,"first-wal-"+Guid.NewGuid().ToString("N")+".json");
        return SqliteProbeProcess.ReadAsync(node,Script,database,temporary,output,TimeSpan.FromSeconds(30),cancellation);
    }
}

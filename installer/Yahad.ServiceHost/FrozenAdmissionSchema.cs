using System.Text.Json;
using Yahad.Install;

namespace Yahad.Service;

// Called ONLY with an actual exclusive data lease, an observed empty owned job,
// no SQLite sidecars, and retained no-write file pins. immutable=1 must never be
// used as a shortcut around those observations on a changing database.
internal static class FrozenAdmissionSchema
{
    private const string Script="""
        const{DatabaseSync}=require('node:sqlite'),fs=require('node:fs'),{pathToFileURL}=require('node:url');
        const uri=pathToFileURL(process.argv[1]).href+'?mode=ro&immutable=1';
        const db=new DatabaseSync(uri,{readOnly:true});
        try{
          db.exec('PRAGMA query_only=ON; PRAGMA trusted_schema=OFF');
          const check=db.prepare('PRAGMA integrity_check').all(),foreign=db.prepare('PRAGMA foreign_key_check').all(),version=db.prepare('SELECT version FROM schema_version').all();
          if(check.length!==1||Object.values(check[0])[0]!=='ok'||foreign.length||version.length!==1||!Number.isSafeInteger(version[0].version)||version[0].version<1)throw Error('schema refused');
          fs.writeFileSync(process.argv[2],JSON.stringify({schema:version[0].version}),{flag:'wx',mode:0o600});
        }finally{db.close();}
        """;
    internal static Task<int> ReadAsync(string node,string database,string privateTemporary,CancellationToken cancellation,JobProcessFaultInjection? fault=null)
    {
        var output=Path.Combine(privateTemporary,"admission-schema-"+Guid.NewGuid().ToString("N")+".json");
        return SqliteProbeProcess.ReadAsync(node,Script,database,privateTemporary,output,TimeSpan.FromSeconds(20),cancellation,fault);
    }
}

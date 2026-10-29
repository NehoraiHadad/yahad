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
    internal static async Task<int> ReadAsync(string node,string database,string privateTemporary,CancellationToken cancellation)
    {
        var output=Path.Combine(privateTemporary,"admission-schema-"+Guid.NewGuid().ToString("N")+".json");
        using var job=JobProcess.Start(node,["-e",Script,database,output],privateTemporary,LaunchEnvironment.Clean(privateTemporary));
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(20));
            if(await job.WaitAsync(deadline.Token)!=0)throw new IOException("ADMISSION_SQLITE_REFUSED");await job.WaitForTreeEmptyAsync(deadline.Token);
            using var read=WindowsFileBoundary.OpenPrivateRead(output);using var json=JsonDocument.Parse(read.Small(1024));
            var schema=json.RootElement.GetProperty("schema").GetInt32();if(schema<1||schema>100000)throw new IOException("ADMISSION_SCHEMA_BOUND");return schema;
        }
        catch{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await job.TerminateAndWaitForTreeEmptyAsync(cleanup.Token);throw;}
        finally{if(File.Exists(output))File.Delete(output);}
    }
}

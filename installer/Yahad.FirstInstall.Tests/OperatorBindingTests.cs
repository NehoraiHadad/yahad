using System.IO;
using System.Security.Cryptography;
using Yahad.Install;
internal static class OperatorBindingTests
{
    internal static async Task Run(string node)
    {
        var family=Guid.NewGuid().ToString("D");var parent=Guid.NewGuid().ToString("D");
        using var input=File.OpenRead(node);var digest=Convert.ToHexStringLower(SHA256.HashData(input));
        var root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());var data=Path.Combine(root,"app.sqlite");
        const string seed="const {DatabaseSync}=require('node:sqlite');const d=new DatabaseSync(process.argv[1]);d.exec('CREATE TABLE families(id TEXT PRIMARY KEY);CREATE TABLE users(id TEXT,family_id TEXT,role TEXT);');d.prepare('INSERT INTO families VALUES(?)').run(process.argv[2]);d.prepare('INSERT INTO users VALUES(?,?,?)').run(process.argv[3],process.argv[2],process.argv[4]);d.close();";
        async Task Seed(string role)
        {
            if(File.Exists(data))File.Delete(data);
            using var job=JobProcess.Start(node,["-e",seed,data,family,parent,role],root,LaunchEnvironment.Clean(root));using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if(await job.WaitAsync(deadline.Token)!=0)throw new IOException("OPERATOR_FIXTURE_PREPARATION");await job.WaitForTreeEmptyAsync(deadline.Token);
        }
        await Seed("parent");var before=SHA256.HashData(File.ReadAllBytes(data));
        using var inspector=new NativeSqliteInspector(node,digest);
        await inspector.RequireOperatorAsync(data,root,family,parent,default);
        if(!before.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(data))))throw new IOException("OPERATOR_CHECK_CHANGED_DATA");
        var refused=0;
        foreach(var pair in new[]{(Guid.NewGuid().ToString("D"),parent),(family,Guid.NewGuid().ToString("D")),("../wrong",parent),(family,family)})
        {try{await inspector.RequireOperatorAsync(data,root,pair.Item1,pair.Item2,default);}catch(IOException){refused++;}}
        await Seed("child");try{await inspector.RequireOperatorAsync(data,root,family,parent,default);}catch(IOException){refused++;}
        if(refused!=5)throw new IOException("OPERATOR_CHECK_UNEXPECTED_ADMISSION");
    }
}

using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yahad.Install;
using Yahad.Setup;

// The app archive's validator (docs/WINDOWS-APP-PACKAGE-PLAN.md 5.2): every refusal by
// name, before anything is written, and the per-member check while it is extracted.
internal static class AppPackageTests
{
    private static void Assert(bool value,string what){if(!value)throw new Exception("APP_PACKAGE_ASSERTION "+what);}
    private static void DenyCode(string code,Action action)
    {
        try{action();}catch(IOException error)when(error.Message==code){return;}
        catch(IOException error){throw new Exception("APP_PACKAGE_EXPECTED_"+code+" GOT "+error.Message);}
        throw new Exception("APP_PACKAGE_EXPECTED_"+code);
    }
    private static byte[] Text(string value)=>Encoding.UTF8.GetBytes(value);
    // A tree of exactly these members, serialised as the builder does.
    internal static PairFixture.Tree Tree(string version,IEnumerable<(string Path,byte[] Bytes)> members)
    {
        var sorted=members.OrderBy(m=>m.Path,StringComparer.Ordinal).ToArray();
        var record=JsonSerializer.SerializeToUtf8Bytes(new{format=TreeRecord.CurrentFormat,version,sourceCommit=PairFixture.AppCommit,files=sorted.Select(m=>new{path=m.Path,size=(long)m.Bytes.Length,sha256=TreeRecord.Sha256(m.Bytes)})});
        return new(TreeKind.App,version,PairFixture.AppCommit,sorted,record);
    }
    internal sealed record Entry(string Path,byte[] Bytes,int Attributes=0);
    // The archive's entries: the tree's members and its record, as the builder writes them.
    internal static List<Entry> Entries(PairFixture.Tree tree)=>[..tree.Members.Select(m=>new Entry(m.Path,m.Bytes)),new(InstalledLayout.AppTreeName,tree.Record)];
    internal static byte[] Zip(IEnumerable<Entry> entries)
    {
        using var data=new MemoryStream();
        using(var zip=new ZipArchive(data,ZipArchiveMode.Create,true))foreach(var entry in entries)
        {var created=zip.CreateEntry(entry.Path);if(entry.Attributes!=0)created.ExternalAttributes=entry.Attributes;using var stream=created.Open();stream.Write(entry.Bytes);}
        return data.ToArray();
    }
    // The signed manifest of one app archive: its bytes, its digest and its tree.
    internal static ReleaseManifest Release(byte[] zip,string treeSha256,string version="1.0.1",int schema=7,int[]? migrations=null,long? bytes=null,string? sha256=null)
        =>ReleaseManifest.Parse(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["format"]=2,["product"]="yahad-windows-release",["version"]=version,["sourceCommit"]=PairFixture.AppCommit,["keyId"]=new string('0',16),["createdUnix"]=1700000000,
            ["infrastructure"]=new JsonObject{["version"]=InfrastructureRelease.Version,["contractMin"]=InfrastructureRelease.ContractMin,["contractMax"]=InfrastructureRelease.ContractMax,
                ["sourceCommit"]=InfrastructureRelease.SourceCommit,["treeSha256"]=PairFixture.Infra().Sha256,
                ["setup"]=new JsonObject{["name"]=ReleaseManifest.SetupName(version),["sha256"]=new string('b',64),["bytes"]=1234}},
            ["app"]=new JsonObject{["archive"]=new JsonObject{["name"]=ReleaseManifest.AppArchiveName(version),["sha256"]=sha256??TreeRecord.Sha256(zip),["bytes"]=bytes??zip.Length},
                ["treeSha256"]=treeSha256,["requiresContract"]=InfrastructureRelease.ContractMin,["databaseSchema"]=schema,["databaseMigrationsFrom"]=new JsonArray([..(migrations??[5,6]).Select(m=>(JsonNode?)m)])},
            ["minimumFrom"]=new JsonObject{["app"]="0.0.0",["infrastructure"]="0.0.0"}
        }.ToJsonString()));
    internal static AppPackage Open(byte[] zip,ReleaseManifest release)=>new(new MemoryStream(zip),release);
    // A package of this tree as built, or of the tree with its entries changed.
    private static void Refuse(string code,PairFixture.Tree tree,Func<List<Entry>,List<Entry>>? change=null)
    {
        var zip=Zip(change is null?Entries(tree):change(Entries(tree)));
        DenyCode(code,()=>Open(zip,Release(zip,tree.Sha256,tree.Version)).Dispose());
    }
    private static List<Entry> With(List<Entry> entries,Entry extra)=>[..entries,extra];
    internal static async Task RunAsync(Func<string,Func<Task>,Task> check)
    {
        var app=PairFixture.App("1.0.1");
        await check("an app archive opens, records its tree and extracts it whole",()=>
        {
            var zip=Zip(Entries(app));var release=Release(zip,app.Sha256);
            using var package=Open(zip,release);
            Assert(package.App.Version=="1.0.1"&&package.App.SourceCommit==PairFixture.AppCommit&&package.App.TreeSha256==app.Sha256&&package.App.DatabaseSchema==7,"identity");
            Assert(package.App.Files.Length==app.Files.Length&&package.App.Files.All(f=>app.Files.Contains(f))&&package.Bytes==app.Files.Sum(f=>f.Size),"members");
            package.App.Require();
            var root=PrivateSession.Create(Path.GetTempPath());
            try
            {
                var tree=Path.Combine(root,"1.0.1");package.Extract(tree,_=>{},default,FullInstallerIntegrationTests.TestTarget);
                foreach(var member in package.App.Files)
                {var path=Path.Combine(tree,member.Path.Replace('/',Path.DirectorySeparatorChar));Assert(File.Exists(path)&&TreeRecord.Sha256(File.ReadAllBytes(path))==member.Sha256,member.Path);}
                Assert(Directory.EnumerateFiles(tree,"*",SearchOption.AllDirectories).Count()==package.App.Files.Length,"nothing else");
            }
            finally{Directory.Delete(root,true);}
            return Task.CompletedTask;
        });
        await check("the archive bytes are the signed ones before the zip is opened",()=>
        {
            var zip=Zip(Entries(app));
            DenyCode("RELEASE_APP_SIZE",()=>Open(zip,Release(zip,app.Sha256,bytes:zip.Length+1)).Dispose());
            DenyCode("RELEASE_APP_SIZE",()=>Open([..zip,0],Release(zip,app.Sha256)).Dispose());
            DenyCode("RELEASE_APP_HASH",()=>Open(zip,Release(zip,app.Sha256,sha256:new string('e',64))).Dispose());
            var flipped=(byte[])zip.Clone();flipped[^1]^=1;DenyCode("RELEASE_APP_HASH",()=>Open(flipped,Release(zip,app.Sha256)).Dispose());
            var garbage=Text("not a zip archive at all");DenyCode("APP_PACKAGE_ZIP",()=>Open(garbage,Release(garbage,app.Sha256)).Dispose());
            return Task.CompletedTask;
        });
        await check("the namespace is app-tree.json and app/ only",()=>
        {
            foreach(var name in new[]{"other/x.txt","App/x.txt","app/../x.txt","app/./x.txt","/app/x.txt","app\\x.txt","app/x:y","infra-tree.json","APP-TREE.JSON","app/dir/","app/aux.txt","app/x. "})
                Refuse("APP_PACKAGE_PATH",app,e=>With(e,new(name,Text("x"))));
            return Task.CompletedTask;
        });
        await check("source, dependencies, native code and executables are refused by name",()=>
        {
            foreach(var name in new[]{"app/src/main.ts","app/node_modules/x/index.js","app/native/x.txt","app/Native/x.txt","app/SRC/a.js","app/package-lock.json","app/Package-Lock.JSON",
                "app/a.node","app/dist/a.exe","app/a.DLL","app/tools/a.ps1","app/a.cmd","app/a.Bat"})
                Refuse("APP_PACKAGE_FORBIDDEN",app,e=>With(e,new(name,Text("x"))));
            return Task.CompletedTask;
        });
        await check("whatever its name, no member is a PE image",()=>
        {
            var members=app.Members.Select(m=>(m.Path,m.Bytes)).ToList();members.Add(("app/dist/assets/blob.bin",[(byte)'M',(byte)'Z',0x90,0]));
            Refuse("APP_PACKAGE_PE",Tree("1.0.1",members));
            // Only the header counts: MZ later in a member, or one byte, is not a PE image.
            var benign=app.Members.Select(m=>(m.Path,m.Bytes)).ToList();benign.Add(("app/dist/a.txt",Text("xMZ")));benign.Add(("app/dist/b.txt",[(byte)'M']));benign.Add(("app/dist/c.txt",[]));
            var tree=Tree("1.0.1",benign);var zip=Zip(Entries(tree));Open(zip,Release(zip,tree.Sha256)).Dispose();
            return Task.CompletedTask;
        });
        await check("names that collide on Windows and non-file entries are refused",()=>
        {
            Refuse("APP_PACKAGE_COLLISION",app,e=>With(e,e[0]));
            Refuse("APP_PACKAGE_COLLISION",app,e=>With(e,new(e[0].Path.ToUpperInvariant().Replace("APP/","app/"),Text("x"))));
            Refuse("APP_PACKAGE_COLLISION",app,e=>With(e,new("app/Package.json",Text("x"))));
            Refuse("APP_PACKAGE_MEMBER_TYPE",app,e=>[..e.Select(x=>x.Path=="app/dist/index.html"?x with{Attributes=unchecked((int)0xA1FF0000)}:x)]);
            Refuse("APP_PACKAGE_MEMBER_TYPE",app,e=>[..e.Select(x=>x.Path=="app/dist/index.html"?x with{Attributes=0x400}:x)]);
            Refuse("APP_PACKAGE_MEMBER_TYPE",app,e=>[..e.Select(x=>x.Path=="app/dist/index.html"?x with{Attributes=0x41ED0010}:x)]);
            return Task.CompletedTask;
        });
        await check("the member count is bounded",()=>
        {
            Refuse("APP_PACKAGE_BOUND",app,e=>[..e,..Enumerable.Range(0,TreeRecord.MaximumMembers).Select(i=>new Entry("app/many/"+i+".txt",[]))]);
            Refuse("APP_PACKAGE_BOUND",app,e=>[..e.Where(x=>x.Path==InstalledLayout.AppTreeName)]);
            return Task.CompletedTask;
        });
        await check("the required members are present",()=>
        {
            foreach(var required in AppPackage.Required)
                Refuse("APP_PACKAGE_MISSING",Tree("1.0.1",app.Members.Where(m=>m.Path!=required)));
            Refuse("APP_PACKAGE_MISSING",app,e=>[..e.Where(x=>x.Path!=InstalledLayout.AppTreeName)]);
            return Task.CompletedTask;
        });
        await check("the tree record is the signed one and names this release",()=>
        {
            var zip=Zip(Entries(app));
            DenyCode("APP_PACKAGE_TREE_MISMATCH",()=>Open(zip,Release(zip,new string('f',64))).Dispose());
            // The right bytes for another version or build are not this release's tree.
            var other=PairFixture.App("1.0.2");var otherZip=Zip(Entries(other));
            DenyCode("APP_PACKAGE_TREE_MISMATCH",()=>Open(otherZip,Release(otherZip,other.Sha256,version:"1.0.1")).Dispose());
            var notJson=Text("not a tree record");var broken=Zip([..app.Members.Select(m=>new Entry(m.Path,m.Bytes)),new(InstalledLayout.AppTreeName,notJson)]);
            DenyCode("APP_PACKAGE_TREE_MISMATCH",()=>Open(broken,Release(broken,TreeRecord.Sha256(notJson))).Dispose());
            return Task.CompletedTask;
        });
        await check("the archive holds exactly the members the tree lists",()=>
        {
            Refuse("APP_PACKAGE_MEMBERS",app,e=>With(e,new("app/dist/extra.js",Text("x"))));
            Refuse("APP_PACKAGE_MEMBERS",app,e=>[..e.Where(x=>x.Path!="app/legal/build-inputs.json")]);
            Refuse("APP_PACKAGE_MEMBERS",app,e=>[..e.Select(x=>x.Path=="app/dist/index.html"?x with{Bytes=[..x.Bytes,0]}:x)]);
            return Task.CompletedTask;
        });
        await check("a member whose bytes differ from the tree is refused while it is extracted",()=>
        {
            var zip=Zip([..Entries(app).Select(x=>x.Path=="app/dist/index.html"?x with{Bytes=x.Bytes.Select(b=>(byte)(b^1)).ToArray()}:x)]);
            using var package=Open(zip,Release(zip,app.Sha256));var root=PrivateSession.Create(Path.GetTempPath());
            try
            {
                DenyCode("APP_PACKAGE_MEMBER_HASH",()=>package.Extract(Path.Combine(root,"1.0.1"),_=>{},default,FullInstallerIntegrationTests.TestTarget));
                Assert(!Directory.EnumerateFiles(root,"*.partial-*",SearchOption.AllDirectories).Any(),"no partial left");
            }
            finally{Directory.Delete(root,true);}
            return Task.CompletedTask;
        });
    }
}

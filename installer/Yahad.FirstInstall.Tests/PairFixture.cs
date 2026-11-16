using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Yahad.Install;
using Yahad.Setup;

// Synthetic trees, format-2 install packages and format-7 installer records
// (docs/WINDOWS-APP-PACKAGE-PLAN.md 4.2, 5.3). The bytes are deterministic: the same
// version and salt always make the same tree record, and another salt makes another
// build of the same version.
internal static class PairFixture
{
    internal const string Owner="S-1-5-21-1-2-3-1001";
    internal static readonly string AppCommit=new('a',40);
    internal static readonly string[] InfraMembers=["host/Yahad.ServiceHost.exe","updater/Yahad.Updater.exe",
        "platform/Yahad.Platform.exe","runtime/node/node.exe","runtime/caddy/caddy.exe","legal/fixture.json",InfrastructureRecord.EngineMember];
    internal static readonly string[] AppMembers=AppMemberPolicy.Required;
    internal sealed record Tree(TreeKind Kind,string Version,string SourceCommit,(string Path,byte[] Bytes)[] Members,byte[] Record)
    {
        internal string Sha256=>TreeRecord.Sha256(Record);
        // What the installer records: every member relative to the tree root, the tree record included.
        internal InstallMember[] Files=>[..Members.Select(m=>new InstallMember(m.Path,m.Bytes.Length,TreeRecord.Sha256(m.Bytes))),new(TreeRecord.RecordName(Kind),Record.Length,Sha256)];
    }
    private static byte[] Content(string member,string salt)=>Encoding.UTF8.GetBytes(member=="app/server/main.mjs"?"synthetic-app-not-executed"+salt:"synthetic "+member+salt);
    // The record escapes as setup_package.py's json.dumps does: only what JSON requires.
    private static readonly JsonSerializerOptions Builder=new(){Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    internal static Tree Build(TreeKind kind,string version,string commit,IEnumerable<string> names,string salt="")
    {
        var members=names.Order(StringComparer.Ordinal).Select(n=>(n,Content(n,salt))).ToArray();
        var record=JsonSerializer.SerializeToUtf8Bytes(new{format=TreeRecord.CurrentFormat,version,sourceCommit=commit,files=members.Select(m=>new{path=m.Item1,size=(long)m.Item2.Length,sha256=TreeRecord.Sha256(m.Item2)})},Builder);
        return new(kind,version,commit,members,record);
    }
    internal static Tree Infra(string? version=null,string salt="")=>Build(TreeKind.Infrastructure,version??InfrastructureRelease.Version,InfrastructureRelease.SourceCommit,InfraMembers,salt);
    internal static Tree App(string version="1.0.0",string salt="")=>Build(TreeKind.App,version,AppCommit,AppMembers,salt);
    internal static InfrastructureRecord InfraRecord(Tree tree)
        =>new(tree.Version,InfrastructureRelease.ContractMin,InfrastructureRelease.ContractMax,tree.SourceCommit,tree.Files.Single(f=>f.Path==InfrastructureRecord.EngineMember).Sha256,tree.Sha256,tree.Files);
    internal static AppRecord AppRecordOf(Tree tree,int schema)=>new(tree.Version,tree.SourceCommit,InfrastructureRelease.ContractMin,tree.Sha256,schema,tree.Files);
    // infraSalt/appSalt make another build of the same version on that track only.
    internal static InstalledPair Pair(string infra="1.0.0",string app="1.0.0",int schema=6,string infraSalt="",string appSalt="")=>new(InfraRecord(Infra(infra,infraSalt)),AppRecordOf(App(app,appSalt),schema));
    internal static InstallerState State(string phase="committed",InstalledPair? active=null,InstalledPair? previous=null,string? releaseKey=null)
    {
        var installation=Guid.NewGuid().ToString("N");
        return new(InstallerState.CurrentFormat,installation,Guid.NewGuid().ToString("N"),Owner,47123,phase,FirstOwnerBinding.Create(installation,Owner),
            new UpdateSetupChoice(2,true,false,DateTimeOffset.UtcNow.ToUnixTimeSeconds()),InstallLocations.ServiceSid,active??Pair(),releaseKey,previous);
    }
    // A format-2 package of the compiled infrastructure and one app: the archive of every
    // member but the engine, the manifest, and the engine member's bytes (the Setup
    // image's prefix). extra/omit change the package and the manifest together; tamper
    // changes only the package's bytes of one member; change rewrites the manifest after
    // it is built.
    internal static (byte[] Zip,byte[] Manifest,byte[] Engine) Package(bool duplicate=false,string? extra=null,string? omit=null,Func<InstallPackageManifest,InstallPackageManifest>? change=null,
        Tree? infra=null,Tree? app=null,int schema=7,int[]? migrations=null,string? tamper=null,PackageMinimumFrom? minimum=null)
    {
        infra??=Infra();app??=App();var entries=new List<(string Path,byte[] Bytes)>();
        foreach(var tree in new[]{infra,app})
        {var prefix=InstallPackage.Prefix(tree.Kind);entries.AddRange(tree.Members.Select(m=>(prefix+m.Path,m.Bytes)));entries.Add((prefix+TreeRecord.RecordName(tree.Kind),tree.Record));}
        if(omit is not null&&entries.RemoveAll(e=>e.Path==omit)!=1)throw new IOException("FIXTURE_OMIT");
        if(extra is not null)entries.Add((extra,Encoding.UTF8.GetBytes("synthetic extra")));
        byte[] Written(string path,byte[] bytes)=>path==tamper?bytes.Select(b=>(byte)(b^1)).ToArray():bytes;
        var engine=entries.Where(e=>e.Path==InstallPackage.EnginePath).Select(e=>Written(e.Path,e.Bytes)).SingleOrDefault()??[];
        using var data=new MemoryStream();
        using(var zip=new ZipArchive(data,ZipArchiveMode.Create,true))foreach(var(path,bytes)in entries.Where(e=>e.Path!=InstallPackage.EnginePath))
        {
            var written=Written(path,bytes);
            using(var stream=zip.CreateEntry(path).Open())stream.Write(written);
            if(duplicate&&path==entries[0].Path){using var again=zip.CreateEntry(path).Open();again.Write(written);}
        }
        var archive=data.ToArray();
        var manifest=new InstallPackageManifest(InstallPackage.Format,new(infra.Version,InfrastructureRelease.ContractMin,InfrastructureRelease.ContractMax,infra.SourceCommit,infra.Sha256),
            new(app.Version,app.SourceCommit,InfrastructureRelease.ContractMin,app.Sha256,schema,migrations??[5,6]),minimum??new("0.0.0","0.0.0"),TreeRecord.Sha256(archive),
            entries.Select(e=>new InstallMember(e.Path,e.Bytes.Length,TreeRecord.Sha256(e.Bytes))).ToArray());
        return(archive,JsonSerializer.SerializeToUtf8Bytes(change?.Invoke(manifest)??manifest),engine);
    }
    internal static InstallPackage Open((byte[] Zip,byte[] Manifest,byte[] Engine) fixture)=>new(new MemoryStream(fixture.Zip),fixture.Manifest,()=>new MemoryStream(fixture.Engine));
    // The distributable layout: engine, archive, manifest and the 40-byte trailer.
    internal static byte[] Image((byte[] Zip,byte[] Manifest,byte[] Engine) fixture)
    {
        var trailer=new byte[SetupImage.TrailerLength];SetupImage.Magic.CopyTo(trailer);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(trailer.AsSpan(16),fixture.Engine.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(trailer.AsSpan(24),fixture.Zip.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(trailer.AsSpan(32),fixture.Manifest.Length);
        return[..fixture.Engine,..fixture.Zip,..fixture.Manifest,..trailer];
    }
}

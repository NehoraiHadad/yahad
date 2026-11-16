using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yahad.Install;
using Yahad.Setup;

// Everything about an upgrade that can be decided without a machine: the release
// signature, the manifest, the trust table, the upgrade record and the repair
// table. The machine half runs in installer/machine-acceptance.ps1.
internal static class UpgradeTests
{
    // Made once by Node's crypto (scripts/windows-release-key.mjs's algorithm); the
    // private key was discarded. Proves the two implementations agree byte for byte.
    private const string VectorKey="MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEBEv1AprOt1IqiC0BbPzhce175wrBgiHvkFiyLgHadqkx5VA5+txplrRS/0ZfLlljVGVVtPh/ajpQYqqetm0nxw==";
    private const string VectorManifest="""{"format":2,"product":"yahad-windows-release","version":"9.9.9","sourceCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","keyId":"a7e004b7c8eb8171","createdUnix":1700000000,"infrastructure":{"version":"1.0.0","contractMin":1,"contractMax":1,"sourceCommit":"cccccccccccccccccccccccccccccccccccccccc","treeSha256":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","setup":{"name":"Yahad.Setup-9.9.9.exe","sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","bytes":1234}},"app":{"archive":{"name":"yahad-windows-app-9.9.9.zip","sha256":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","bytes":5678},"treeSha256":"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff","requiresContract":1,"databaseSchema":7,"databaseMigrationsFrom":[5,6]},"minimumFrom":{"app":"0.0.0","infrastructure":"0.0.0"}}""";
    private const string VectorSignature="53/fJbBNKYACQtMw1Q9pO9V7Xz8WCas1F8L3VzwfeAV2t8chdg6136tO85+EghxC+A3I4+m3QStWW8ztcC/1vg==";
    // The same bytes signed under the retired v1 prefix: never a v2 signature.
    private const string VectorV1Signature="QA2FhOuo9mhONFF10f/lp22uNQnn+Kw7mcFeKDAo1z2jesU+7r6bbQ4PwSceBq7WQ4v6WSS8NRaocXHMaSS1tA==";
    private static void Assert(bool value,string what){if(!value)throw new Exception("UPGRADE_ASSERTION "+what);}
    private static void DenyCode(string code,Action action)
    {
        try{action();}catch(IOException error)when(error.Message==code){return;}
        throw new Exception("UPGRADE_EXPECTED_"+code);
    }
    private sealed class Release:IDisposable
    {
        private readonly ECDsa key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal byte[] Spki=>key.ExportSubjectPublicKeyInfo();
        internal byte[] Sign(byte[] manifest,string prefix=ReleaseSignature.Prefix)
            =>key.SignData((byte[])[..Encoding.ASCII.GetBytes(prefix),..manifest],HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        internal JsonObject Fields(string version="9.9.9",int schema=7,string minimum="0.0.0")=>Format2(TrustedReleaseKey.Id(Spki),version,schema,minimum);
        public void Dispose()=>key.Dispose();
    }
    // A format-2 manifest (5.1). Its infrastructure is the one this build compiles and
    // the fixture's trees describe, unless a case changes it.
    private static JsonObject Format2(string keyId,string version="9.9.9",int schema=7,string minimum="0.0.0",string? infra=null,string? infraTree=null,string? appTree=null,int contract=1)=>new()
    {
        ["format"]=2,["product"]="yahad-windows-release",["version"]=version,["sourceCommit"]=PairFixture.AppCommit,["keyId"]=keyId,["createdUnix"]=1700000000,
        ["infrastructure"]=new JsonObject{["version"]=infra??InfrastructureRelease.Version,["contractMin"]=InfrastructureRelease.ContractMin,["contractMax"]=InfrastructureRelease.ContractMax,
            ["sourceCommit"]=InfrastructureRelease.SourceCommit,["treeSha256"]=infraTree??PairFixture.Infra(infra).Sha256,
            ["setup"]=new JsonObject{["name"]="Yahad.Setup-"+version+".exe",["sha256"]=new string('b',64),["bytes"]=1234}},
        ["app"]=new JsonObject{["archive"]=new JsonObject{["name"]="yahad-windows-app-"+version+".zip",["sha256"]=new string('e',64),["bytes"]=5678},
            ["treeSha256"]=appTree??new string('f',64),["requiresContract"]=contract,["databaseSchema"]=schema,["databaseMigrationsFrom"]=new JsonArray(5,6)},
        ["minimumFrom"]=new JsonObject{["app"]=minimum,["infrastructure"]="0.0.0"}
    };
    private static byte[] Bytes(JsonObject value)=>Encoding.UTF8.GetBytes(value.ToJsonString());
    private static byte[] Text(string value)=>Encoding.UTF8.GetBytes(value);
    private static JsonObject With(JsonObject value,Action<JsonObject> change){var copy=value.DeepClone().AsObject();change(copy);return copy;}
    // A Setup-track move (infrastructure and app 1.0.0 to 1.0.1) or an app-track apply
    // (the same infrastructure tree, app 1.0.0 to 1.0.1).
    private static UpgradeState Upgrade(string phase="started",int fromSchema=6,int toSchema=7,bool restore=false,int attempts=0,string kind=UpgradeState.SetupKind)
        =>UpgradeState.Begin(kind,PairFixture.Pair(app:"1.0.0",schema:fromSchema),PairFixture.Pair(kind==UpgradeState.AppKind?"1.0.0":"1.0.1","1.0.1",schema:toSchema),1700000000)
            with{Phase=phase,RestoreData=restore,Attempts=attempts};
    internal static async Task RunAsync(Func<string,Func<Task>,Task> check)
    {
        await check("release signature agrees with the Node signing tool",()=>
        {
            var key=Convert.FromBase64String(VectorKey);var manifest=Text(VectorManifest);var signature=Convert.FromBase64String(VectorSignature);
            Assert(ReleaseSignature.Verify(key,manifest,signature),"vector");
            var release=ReleaseVerification.Verify(key,manifest,signature);
            Assert(release.Version=="9.9.9"&&release.Infrastructure.Setup.Bytes==1234&&release.App.Archive.Bytes==5678&&release.App.DatabaseMigrationsFrom.SequenceEqual([5,6]),"vector parsed");
            // The retired v1 domain never verifies, over the very same bytes and key.
            var v1=Convert.FromBase64String(VectorV1Signature);
            Assert(!ReleaseSignature.Verify(key,manifest,v1),"v1 prefix");
            DenyCode("RELEASE_SIGNATURE_INVALID",()=>ReleaseVerification.Verify(key,manifest,v1));
            return Task.CompletedTask;
        });
        await check("release signature refuses tampering, other keys and malformed input",()=>
        {
            using var release=new Release();using var other=new Release();var manifest=Bytes(release.Fields());var signature=release.Sign(manifest);
            Assert(ReleaseSignature.Verify(release.Spki,manifest,signature),"valid");
            var tampered=(byte[])manifest.Clone();tampered[^2]^=1;Assert(!ReleaseSignature.Verify(release.Spki,tampered,signature),"tampered manifest");
            var flipped=(byte[])signature.Clone();flipped[0]^=1;Assert(!ReleaseSignature.Verify(release.Spki,manifest,flipped),"tampered signature");
            Assert(!ReleaseSignature.Verify(other.Spki,manifest,signature),"wrong key");
            Assert(!ReleaseSignature.Verify(release.Spki,manifest,signature[..63]),"63 bytes");
            Assert(!ReleaseSignature.Verify(release.Spki,manifest,[..signature,0]),"65 bytes");
            Assert(!ReleaseSignature.Verify(release.Spki,manifest,release.Sign(manifest,"")),"no domain prefix");
            Assert(!ReleaseSignature.Verify([1,2,3],manifest,signature),"garbage key");
            Assert(!ReleaseSignature.Verify([..release.Spki,0],manifest,signature),"trailing key bytes");
            using var p384=ECDsa.Create(ECCurve.NamedCurves.nistP384);
            var foreign=p384.SignData((byte[])[..Encoding.ASCII.GetBytes(ReleaseSignature.Prefix),..manifest],HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            Assert(!ReleaseSignature.Verify(p384.ExportSubjectPublicKeyInfo(),manifest,foreign),"P-384");
            return Task.CompletedTask;
        });
        await check("release manifest is strict",()=>
        {
            using var release=new Release();var good=release.Fields();
            Assert(ReleaseManifest.Parse(Bytes(good)).Infrastructure.Setup.Bytes==1234,"good");
            // The largest legal manifest fits the 4096-byte bound with room to spare.
            var largest=With(good,v=>{v["app"]!["databaseMigrationsFrom"]=new JsonArray([..Enumerable.Range(1000-ReleaseManifest.MaximumMigrationSources,ReleaseManifest.MaximumMigrationSources).Select(s=>(JsonNode?)s)]);v["app"]!["databaseSchema"]=1000;});
            Assert(Bytes(largest).Length<=ReleaseManifest.MaximumBytes&&ReleaseManifest.Parse(Bytes(largest)).App.DatabaseMigrationsFrom.Length==ReleaseManifest.MaximumMigrationSources,"largest");
            var text=good.ToJsonString();JsonNode Infra(JsonObject v)=>v["infrastructure"]!;JsonNode App(JsonObject v)=>v["app"]!;
            var bad=new List<byte[]>
            {
                Bytes(With(good,v=>v["unknown"]=1)),Bytes(With(good,v=>Infra(v)["setup"]!["unknown"]=1)),Bytes(With(good,v=>App(v)["unknown"]=1)),Bytes(With(good,v=>v["minimumFrom"]!["unknown"]=1)),
                (byte[])[0xef,0xbb,0xbf,..Text(text)],Text(text+"x"),Text("{\"format\":2,"+text[1..]),Text(""),Text("null"),Text(text.Replace("\"format\"","\"Format\"")),
                new byte[ReleaseManifest.MaximumBytes+1],Bytes(With(good,v=>v["ballast"]=new JsonObject{["a"]=new JsonObject{["b"]=new JsonObject{["c"]=new JsonObject()}}})),
                Bytes(With(good,v=>v["format"]=1)),Bytes(With(good,v=>v["product"]="yahad-windows")),Bytes(With(good,v=>v["version"]="1.0")),Bytes(With(good,v=>v["version"]="01.0.0")),
                Bytes(With(good,v=>v["version"]="1.0.0-rc")),Bytes(With(good,v=>v["sourceCommit"]="abc")),Bytes(With(good,v=>v["keyId"]="ABC")),Bytes(With(good,v=>v["createdUnix"]=0)),
                Bytes(With(good,v=>Infra(v)["version"]="x")),Bytes(With(good,v=>Infra(v)["contractMin"]=2)),Bytes(With(good,v=>Infra(v)["treeSha256"]="ab")),Bytes(With(good,v=>Infra(v)["sourceCommit"]=null)),
                Bytes(With(good,v=>Infra(v)["setup"]!["name"]="other.exe")),Bytes(With(good,v=>Infra(v)["setup"]!["name"]="..\\Yahad.Setup-9.9.9.exe")),Bytes(With(good,v=>Infra(v)["setup"]!["sha256"]="ab")),
                Bytes(With(good,v=>Infra(v)["setup"]!["bytes"]=0)),Bytes(With(good,v=>Infra(v)["setup"]!["bytes"]=ReleaseManifest.MaximumSetupBytes+1)),
                Bytes(With(good,v=>App(v)["archive"]!["name"]="yahad-windows-app-9.9.8.zip")),Bytes(With(good,v=>App(v)["archive"]!["bytes"]=ReleaseManifest.MaximumAppArchiveBytes+1)),
                Bytes(With(good,v=>App(v)["databaseSchema"]=0)),Bytes(With(good,v=>App(v)["databaseMigrationsFrom"]=new JsonArray(7))),Bytes(With(good,v=>App(v)["databaseMigrationsFrom"]=new JsonArray(5,5))),
                Bytes(With(good,v=>App(v)["databaseMigrationsFrom"]=null)),Bytes(With(good,v=>App(v)["treeSha256"]="F".PadRight(64,'f'))),
                Bytes(With(good,v=>v["minimumFrom"]!["app"]="10.0.0")),Bytes(With(good,v=>v["minimumFrom"]!["infrastructure"]="999.0.0")),Bytes(With(good,v=>v["minimumFrom"]!["app"]="x")),
                Bytes(With(good,v=>v.Remove("keyId"))),Bytes(With(good,v=>v.Remove("minimumFrom"))),Bytes(With(good,v=>v.Remove("infrastructure"))),Bytes(With(good,v=>v["app"]=null)),
                Bytes(With(good,v=>Infra(v)["setup"]=null))
            };
            for(var i=0;i<bad.Count;i++)
            {var index=i;try{DenyCode("RELEASE_MANIFEST_INVALID",()=>ReleaseManifest.Parse(bad[index]));}catch(Exception error){throw new Exception("manifest case "+index,error);}}
            return Task.CompletedTask;
        });
        await check("release verification reads nothing before the signature",()=>
        {
            using var release=new Release();var garbage=Text("not json");
            DenyCode("RELEASE_SIGNATURE_INVALID",()=>ReleaseVerification.Verify(release.Spki,garbage,new byte[64]));
            DenyCode("RELEASE_SIGNATURE_INVALID",()=>ReleaseVerification.Verify(release.Spki,garbage,null));
            DenyCode("RELEASE_SIGNATURE_INVALID",()=>ReleaseVerification.Verify(release.Spki,new byte[ReleaseManifest.MaximumBytes+1],release.Sign(new byte[ReleaseManifest.MaximumBytes+1])));
            DenyCode("RELEASE_MANIFEST_INVALID",()=>ReleaseVerification.Verify(release.Spki,garbage,release.Sign(garbage)));
            using var other=new Release();var foreign=Bytes(With(release.Fields(),v=>v["keyId"]=TrustedReleaseKey.Id(other.Spki)));
            DenyCode("RELEASE_KEY_UNTRUSTED",()=>ReleaseVerification.Verify(release.Spki,foreign,release.Sign(foreign)));
            return Task.CompletedTask;
        });
        await check("the engine applies only the track a release selects for the installed pair",()=>
        {
            // Installed: the fixture infrastructure 1.0.0 with app 1.0.0 at schema 6.
            var installed=PairFixture.State(active:PairFixture.Pair("1.0.0","1.0.0",schema:6));var id=new string('0',16);var tree=installed.Active.Infrastructure.TreeSha256;
            ReleaseManifest Of(JsonObject value)=>ReleaseManifest.Parse(Bytes(value));
            ReleaseTrack? Select(JsonObject value)=>ReleaseSelection.Select(Of(value),installed.Active.Pointer());
            Assert(Select(Format2(id,"1.0.1",infra:"1.0.0",infraTree:tree))==ReleaseTrack.App,"same infrastructure, newer app");
            Assert(Select(Format2(id,"1.0.1",infra:"1.0.1"))==ReleaseTrack.Setup,"newer infrastructure");
            Assert(Select(Format2(id,"1.0.0",infra:"1.0.0",infraTree:tree)) is null,"same app");
            Assert(Select(Format2(id,"0.9.0",infra:"1.0.1")) is null,"newer infrastructure, older app");
            Assert(Select(Format2(id,"1.0.1",infra:"0.9.0")) is null,"older infrastructure");
            DenyCode("INFRA_IDENTITY_MISMATCH",()=>Select(Format2(id,"1.0.1",infra:"1.0.0",infraTree:new string('d',64))));
            DenyCode("APP_NEEDS_INFRASTRUCTURE",()=>Select(Format2(id,"1.0.1",infra:"1.0.0",infraTree:tree,contract:InfrastructureRelease.ContractMax+1)));
            DenyCode("RELEASE_UNSUPPORTED_FROM",()=>Select(Format2(id,"1.0.1",minimum:"1.0.1",infra:"1.0.0",infraTree:tree)));
            // The engine's own refusals: the other track, nothing newer, a schema it cannot open.
            ReleaseStage.RequireTrack(Of(Format2(id,"1.0.1",infra:"1.0.0",infraTree:tree)),installed,ReleaseTrack.App);
            ReleaseStage.RequireTrack(Of(Format2(id,"1.0.1",infra:"1.0.1")),installed,ReleaseTrack.Setup);
            DenyCode("RELEASE_TRACK_MISMATCH",()=>ReleaseStage.RequireTrack(Of(Format2(id,"1.0.1",infra:"1.0.1")),installed,ReleaseTrack.App));
            DenyCode("RELEASE_TRACK_MISMATCH",()=>ReleaseStage.RequireTrack(Of(Format2(id,"1.0.1",infra:"1.0.0",infraTree:tree)),installed,ReleaseTrack.Setup));
            DenyCode("RELEASE_NOT_NEWER",()=>ReleaseStage.RequireTrack(Of(Format2(id,"1.0.0",infra:"1.0.0",infraTree:tree)),installed,ReleaseTrack.App));
            // The recorded schema decides: 4 is neither the target nor a migration source.
            DenyCode("RELEASE_UNSUPPORTED_FROM",()=>ReleaseStage.RequireTrack(Of(Format2(id,"1.0.1",schema:9,infra:"1.0.0",infraTree:tree)),PairFixture.State(active:PairFixture.Pair("1.0.0","1.0.0",schema:4)),ReleaseTrack.App));
            Assert(Of(Format2(id,schema:9)).AcceptsSchema(9)&&!Of(Format2(id,schema:9)).AcceptsSchema(4)&&Of(Format2(id,schema:9)).AcceptsSchema(5),"schema rule");
            return Task.CompletedTask;
        });
        await check("a staged release is read by its exact names and must be that version",()=>
        {
            using var release=new Release();var root=PrivateSession.Create(Path.GetTempPath());
            try
            {
                var stage=Path.Combine(root,"9.9.9");Directory.CreateDirectory(stage);var manifest=Bytes(release.Fields());
                DenyCode("RELEASE_SIGNATURE_INVALID",()=>ReleaseStage.Read(stage,"9.9.9",release.Spki));
                // A glob would have found these; exact names do not.
                File.WriteAllBytes(Path.Combine(stage,"yahad-windows-release-9.9.9 (1).json"),manifest);File.WriteAllBytes(Path.Combine(stage,"yahad-windows-release-9.9.9 (1).json.sig"),release.Sign(manifest));
                DenyCode("RELEASE_SIGNATURE_INVALID",()=>ReleaseStage.Read(stage,"9.9.9",release.Spki));
                File.WriteAllBytes(Path.Combine(stage,ReleaseManifest.ManifestName("9.9.9")),manifest);
                DenyCode("RELEASE_SIGNATURE_INVALID",()=>ReleaseStage.Read(stage,"9.9.9",release.Spki));
                File.WriteAllBytes(Path.Combine(stage,ReleaseManifest.SignatureName("9.9.9")),[..release.Sign(manifest),0]);
                DenyCode("RELEASE_SIGNATURE_INVALID",()=>ReleaseStage.Read(stage,"9.9.9",release.Spki));
                File.WriteAllBytes(Path.Combine(stage,ReleaseManifest.SignatureName("9.9.9")),release.Sign(manifest));
                Assert(ReleaseStage.Read(stage,"9.9.9",release.Spki).Version=="9.9.9","exact names");
                // Another release's manifest under this version's names is not this stage.
                var other=Bytes(release.Fields("9.9.8"));File.WriteAllBytes(Path.Combine(stage,ReleaseManifest.ManifestName("9.9.9")),other);File.WriteAllBytes(Path.Combine(stage,ReleaseManifest.SignatureName("9.9.9")),release.Sign(other));
                DenyCode("RELEASE_MANIFEST_INVALID",()=>ReleaseStage.Read(stage,"9.9.9",release.Spki));
                DenyCode("RELEASE_SIGNATURE_INVALID",()=>ReleaseStage.Read(stage,"latest",release.Spki));
                DenyCode("RELEASE_SIGNATURE_INVALID",()=>ReleaseStage.Read("9.9.9","9.9.9",release.Spki));
            }
            finally{Directory.Delete(root,true);}
            return Task.CompletedTask;
        });
        await check("a staged Setup carries exactly the pair its signed manifest names",()=>
        {
            var package=PairFixture.Package(app:PairFixture.App("9.9.9"),schema:7,migrations:[5,6]);using var opened=PairFixture.Open(package);var manifest=opened.Manifest;
            var signed=Format2(new string('0',16),"9.9.9",appTree:manifest.App.TreeSha256);
            ReleaseManifest Of(Action<JsonObject>? change=null)=>ReleaseManifest.Parse(Bytes(change is null?signed:With(signed,change)));
            UpgradeHost.RequireAgrees(Of(),manifest);
            // Migration sources are a set: their order in the signed manifest is free.
            UpgradeHost.RequireAgrees(Of(v=>v["app"]!["databaseMigrationsFrom"]=new JsonArray(6,5)),manifest);
            foreach(var change in new Action<JsonObject>[]
            {
                v=>{v["version"]="9.9.8";v["infrastructure"]!["setup"]!["name"]=ReleaseManifest.SetupName("9.9.8");v["app"]!["archive"]!["name"]=ReleaseManifest.AppArchiveName("9.9.8");},v=>v["sourceCommit"]=new string('c',40),v=>v["infrastructure"]!["version"]="1.0.1",v=>v["infrastructure"]!["treeSha256"]=new string('d',64),
                v=>v["infrastructure"]!["sourceCommit"]=new string('c',40),v=>v["app"]!["treeSha256"]=new string('d',64),v=>v["app"]!["databaseSchema"]=8,
                v=>v["app"]!["databaseMigrationsFrom"]=new JsonArray(5),v=>v["app"]!["databaseMigrationsFrom"]=new JsonArray(4,5,6),v=>v["minimumFrom"]!["app"]="0.0.1",v=>v["minimumFrom"]!["infrastructure"]="0.0.1"
            })
            {var value=Of(change);DenyCode("RELEASE_SETUP_MISMATCH",()=>UpgradeHost.RequireAgrees(value,manifest));}
            return Task.CompletedTask;
        });
        await check("a Setup moves only from the pairs its package supports",()=>
        {
            var installed=PairFixture.Pair("1.0.0","1.0.0");
            using(var package=PairFixture.Open(PairFixture.Package(app:PairFixture.App("1.0.1"))))package.RequireFrom(installed);
            using(var package=PairFixture.Open(PairFixture.Package(app:PairFixture.App("1.0.1"),minimum:new("1.0.0","1.0.0"))))package.RequireFrom(installed);
            using(var package=PairFixture.Open(PairFixture.Package(app:PairFixture.App("1.0.1"),minimum:new("1.0.1","0.0.0"))))DenyCode("RELEASE_UNSUPPORTED_FROM",()=>package.RequireFrom(installed));
            // The package always carries the compiled infrastructure, so an older installed one is the case.
            using(var package=PairFixture.Open(PairFixture.Package(app:PairFixture.App("1.0.1"),minimum:new("0.0.0",InfrastructureRelease.Version))))DenyCode("RELEASE_UNSUPPORTED_FROM",()=>package.RequireFrom(PairFixture.Pair("0.0.0","1.0.0")));
            // Never newer than the package's own pair, never missing.
            foreach(var minimum in new PackageMinimumFrom?[]{new("1.0.2","0.0.0"),new("0.0.0","999.0.0"),new("x","0.0.0"),new("0.0.0","1.0"),null})
                DenyCode("PACKAGE_FORMAT",()=>PairFixture.Open(PairFixture.Package(app:PairFixture.App("1.0.1"),change:m=>m with{MinimumFrom=minimum!})).Dispose());
            return Task.CompletedTask;
        });
        await check("release versions compare as numbers",()=>
        {
            Assert(SemVer.Compare("10.0.0","9.9.9")>0,"major");Assert(SemVer.Compare("1.2.10","1.2.9")>0,"patch");Assert(SemVer.Compare("1.2.3","1.2.3")==0,"equal");
            Assert(SemVer.Compare("99999999999999.0.0","99999999999998.0.0")>0,"no overflow");Assert(SemVer.Compare("1.0.0","1.0.1")<0,"less");
            DenyCode("RELEASE_MANIFEST_INVALID",()=>SemVer.Compare("1.0","1.0.0"));
            return Task.CompletedTask;
        });
        await check("a build without a release key refuses every upgrade",()=>
        {
            using var release=new Release();using var other=new Release();
            DenyCode("RELEASE_KEY_UNTRUSTED",()=>TrustedReleaseKey.Require(null,null));
            DenyCode("RELEASE_KEY_UNTRUSTED",()=>TrustedReleaseKey.Require(release.Spki,null));
            DenyCode("RELEASE_KEY_UNTRUSTED",()=>TrustedReleaseKey.Require(null,release.Spki));
            DenyCode("RELEASE_KEY_UNTRUSTED",()=>TrustedReleaseKey.Require(release.Spki,other.Spki));
            DenyCode("RELEASE_KEY_UNTRUSTED",()=>TrustedReleaseKey.Require(release.Spki[..90],release.Spki[..90]));
            Assert(TrustedReleaseKey.Require(release.Spki,release.Spki).SequenceEqual(release.Spki),"same key admitted");
            Assert(release.Spki.Length==TrustedReleaseKey.SpkiBytes,"P-256 SPKI size");
            // The unit test build is compiled without -ReleaseKey.
            Assert(TrustedReleaseKey.Embedded is null,"test build carries no key");
            return Task.CompletedTask;
        });
        await check("upgrade record round-trips and refuses what it never writes",()=>
        {
            var state=Upgrade();var bytes=state.Encode();var read=UpgradeState.Parse(bytes);
            Assert(read.Id==state.Id&&read.Phase==state.Phase&&read.Kind=="setup"&&read.FromSchema==6&&read.ToSchema==7&&read.SchemaChanged&&read.To.App.Files.Length==state.To.App.Files.Length,"round trip");
            Assert(read.From.SameTrees(state.From)&&read.To.SameTrees(state.To)&&read.Track==UpgradeTrack.Infrastructure,"pairs");
            Assert(!Upgrade(fromSchema:7).SchemaChanged,"same schema");
            // FromSchema is the installer record's, never a live observation.
            Assert(UpgradeState.Begin(UpgradeState.AppKind,PairFixture.Pair(schema:4),PairFixture.Pair(app:"1.0.1",schema:7),0).FromSchema==4,"record schema");
            foreach(var kind in new[]{UpgradeState.SetupKind,UpgradeState.AppKind})
            {
                var begun=UpgradeState.Begin(kind,PairFixture.Pair(),PairFixture.Pair(kind==UpgradeState.AppKind?"1.0.0":"1.0.1","1.0.1",schema:7),0);Assert(begun.Phase==UpgradeState.Phases(kind)[0],"first phase "+kind);
                foreach(var phase in UpgradeState.Phases(kind))Assert(UpgradeState.Parse((begun with{Phase=phase}).Encode()).Phase==phase,kind+phase);
            }
            // Each kind moves exactly its own track, and knows only its own phases.
            DenyCode("INSTALL_STATE_INVALID",()=>(Upgrade(kind:UpgradeState.AppKind) with{Kind=UpgradeState.SetupKind}).Encode());
            DenyCode("INSTALL_STATE_INVALID",()=>(state with{Kind=UpgradeState.AppKind}).Encode());
            DenyCode("INSTALL_STATE_INVALID",()=>(Upgrade(kind:UpgradeState.AppKind) with{Phase="backed_up"}).Encode());
            DenyCode("INSTALL_STATE_INVALID",()=>(state with{Phase="switching"}).Encode());
            var json=JsonNode.Parse(bytes)!.AsObject();
            foreach(var bad in new[]{With(json,v=>v["Unknown"]=1),With(json,v=>v["Kind"]="updater"),With(json,v=>v["Kind"]="app"),With(json,v=>v["Phase"]="other"),With(json,v=>v.Remove("To")),With(json,v=>v["From"]="..\\x"),
                With(json,v=>v["To"]=v["From"]!.DeepClone()),With(json,v=>{var from=v["From"]!.DeepClone();v["From"]=v["To"]!.DeepClone();v["To"]=from;}),With(json,v=>v["FromSchema"]=5),
                With(json,v=>v["Id"]="x"),With(json,v=>{v["RestoreData"]=true;v["FromSchema"]=7;v["SchemaChanged"]=false;}),With(json,v=>v["SchemaChanged"]=false)})
            {var value=Bytes(bad);try{UpgradeState.Parse(value);}catch(IOException){continue;}throw new Exception("UPGRADE_STATE_ADMITTED "+bad.ToJsonString()[..60]);}
            DenyCode("LEGACY_INSTALL_UNSUPPORTED",()=>UpgradeState.Parse(Bytes(With(json,v=>v["Format"]=1))));
            return Task.CompletedTask;
        });
        await check("an upgrade moves forward on one or both tracks and never relabels a tree",()=>
        {
            var from=PairFixture.Pair("1.0.0","1.0.0");
            Assert(UpgradeRules.Decide(from,PairFixture.Pair("1.0.0","1.0.1"))==UpgradeTrack.App,"app only");
            Assert(UpgradeRules.Decide(from,PairFixture.Pair("1.0.1","1.0.0"))==UpgradeTrack.Infrastructure,"infrastructure only");
            Assert(UpgradeRules.Decide(from,PairFixture.Pair("1.0.1","1.0.1"))==UpgradeTrack.Infrastructure,"both");
            DenyCode("RELEASE_NOT_NEWER",()=>UpgradeRules.Decide(from,PairFixture.Pair("1.0.0","1.0.0")));
            DenyCode("INSTALL_DOWNGRADE_REFUSED",()=>UpgradeRules.Decide(from,PairFixture.Pair("1.0.0","0.9.0")));
            DenyCode("INSTALL_DOWNGRADE_REFUSED",()=>UpgradeRules.Decide(from,PairFixture.Pair("0.9.0","1.0.1")));
            DenyCode("INFRA_IDENTITY_MISMATCH",()=>UpgradeRules.Decide(from,PairFixture.Pair("1.0.0","1.0.1",infraSalt:"rebuilt")));
            DenyCode("APP_IDENTITY_MISMATCH",()=>UpgradeRules.Decide(from,PairFixture.Pair("1.0.1","1.0.0",appSalt:"rebuilt")));
            DenyCode("APP_IDENTITY_MISMATCH",()=>UpgradeRules.Decide(from,PairFixture.Pair("1.0.0","1.0.0",appSalt:"rebuilt")));
            Assert(UpgradeRules.IsUpgrade(from,PairFixture.Pair("1.0.0","1.0.1"))&&!UpgradeRules.IsUpgrade(from,from)&&!UpgradeRules.IsUpgrade(from,PairFixture.Pair("1.0.0","1.0.1",infraSalt:"rebuilt")),"applies");
            // A same-pair reinstall needs both tree digests equal, not only the versions.
            Assert(from.SameTrees(PairFixture.Pair("1.0.0","1.0.0"))&&!from.SameTrees(PairFixture.Pair("1.0.0","1.0.0",appSalt:"rebuilt"))&&!from.SameTrees(PairFixture.Pair("1.0.0","1.0.0",infraSalt:"rebuilt")),"same trees");
            return Task.CompletedTask;
        });
        await check("after a commit only the active pair and the one it replaced keep their trees",()=>
        {
            var active=PairFixture.Pair("1.0.1","1.0.2");var previous=PairFixture.Pair("1.0.0","1.0.2");
            Assert(UpgradeRules.Retained(TreeKind.Infrastructure,"1.0.1",active,previous)&&UpgradeRules.Retained(TreeKind.Infrastructure,"1.0.0",active,previous),"both infrastructures");
            Assert(!UpgradeRules.Retained(TreeKind.Infrastructure,"0.9.0",active,previous)&&!UpgradeRules.Retained(TreeKind.Infrastructure,"1.0.0",active,null),"older infrastructure");
            Assert(UpgradeRules.Retained(TreeKind.App,"1.0.2",active,previous)&&!UpgradeRules.Retained(TreeKind.App,"1.0.1",active,previous)&&!UpgradeRules.Retained(TreeKind.App,"1.0.0",active,previous),"one shared app");
            // An app version is not an infrastructure version: the tracks never cross.
            Assert(!UpgradeRules.Retained(TreeKind.App,"1.0.1",active,previous)&&!UpgradeRules.Retained(TreeKind.Infrastructure,"1.0.2",active,previous),"tracks are separate");
            return Task.CompletedTask;
        });
        await check("repair decisions follow the recorded phase",()=>
        {
            const string S=UpgradeState.SetupKind,A=UpgradeState.AppKind;
            foreach(var(kind,phase,attempts,action)in new[]
            {
                (S,"preparing",0,RepairAction.Discard),(S,"prepared",9,RepairAction.Discard),(S,"stopped",0,RepairAction.RollBack),(S,"backed_up",0,RepairAction.RollBack),(S,"switched",2,RepairAction.RollBack),
                (S,"started",0,RepairAction.RollBack),(S,"rolling_back",1,RepairAction.RollBack),(S,"started",3,RepairAction.GiveUp),(S,"rolling_back",3,RepairAction.GiveUp),(S,"stopped",4,RepairAction.GiveUp),
                (S,"healthy",0,RepairAction.FinishCommit),(S,"committed",9,RepairAction.FinishCommit),(S,"rolled_back",9,RepairAction.FinishRollback),
                // Section 7: a phase is recorded as its step begins.
                (A,"admitting",0,RepairAction.Discard),(A,"preparing",0,RepairAction.Discard),(A,"fencing",9,RepairAction.Discard),(A,"stopping",0,RepairAction.Discard),(A,"stopped",3,RepairAction.Discard),
                (A,"switching",0,RepairAction.RollBack),(A,"started",2,RepairAction.RollBack),(A,"healthy",0,RepairAction.RollBack),(A,"rolling_back",1,RepairAction.RollBack),
                (A,"switching",3,RepairAction.GiveUp),(A,"healthy",3,RepairAction.GiveUp),(A,"rolling_back",3,RepairAction.GiveUp),
                (A,"committed",9,RepairAction.FinishCommit),(A,"rolled_back",9,RepairAction.FinishRollback)
            })Assert(UpgradeRules.ForRepair(kind,phase,attempts)==action,kind+phase+attempts);
            DenyCode("INSTALL_STATE_INVALID",()=>UpgradeRules.ForRepair(S,"other",0));DenyCode("INSTALL_STATE_INVALID",()=>UpgradeRules.ForRepair(A,"backed_up",0));
            DenyCode("INSTALL_STATE_INVALID",()=>UpgradeRules.ForRepair(S,"switching",0));DenyCode("INSTALL_STATE_INVALID",()=>UpgradeRules.ForRepair("updater","preparing",0));
            foreach(var kind in new[]{S,A})
            {
                Assert(new HashSet<string>(UpgradeState.Phases(kind)).Count==UpgradeState.Phases(kind).Length,"phases unique");
                foreach(var phase in UpgradeState.Phases(kind))_=UpgradeRules.ForRepair(kind,phase,0);
            }
            return Task.CompletedTask;
        });
        await check("data is restored exactly when the new release may have touched it",()=>
        {
            foreach(var phase in new[]{"preparing","prepared","stopped"})Assert(!UpgradeRules.RestoresData(Upgrade(phase)),phase);
            foreach(var phase in new[]{"backed_up","switched","started"})Assert(UpgradeRules.RestoresData(Upgrade(phase)),phase);
            foreach(var phase in new[]{"backed_up","switched","started"})Assert(!UpgradeRules.RestoresData(Upgrade(phase,fromSchema:7)),"same schema "+phase);
            Assert(UpgradeRules.RestoresData(Upgrade("rolling_back",restore:true)),"a resumed rollback keeps its decision");
            Assert(!UpgradeRules.RestoresData(Upgrade("rolling_back")),"a rollback from before the backup never restores");
            // The app track's backup is whole once its switch began.
            const string A=UpgradeState.AppKind;
            foreach(var phase in new[]{"admitting","preparing","fencing","stopping","stopped"})Assert(!UpgradeRules.RestoresData(Upgrade(phase,kind:A)),A+phase);
            foreach(var phase in new[]{"switching","started","healthy"})Assert(UpgradeRules.RestoresData(Upgrade(phase,kind:A))&&!UpgradeRules.RestoresData(Upgrade(phase,fromSchema:7,kind:A)),A+phase);
            Assert(UpgradeRules.RestoresData(Upgrade("rolling_back",restore:true,kind:A))&&!UpgradeRules.RestoresData(Upgrade("rolling_back",kind:A)),"app rollback keeps its decision");
            return Task.CompletedTask;
        });
        await check("health needs this release, its schema and consecutive answers",()=>
        {
            Assert(UpgradeRules.HealthPasses(new(true,"1.0.1",7),"1.0.1",7),"good");Assert(!UpgradeRules.HealthPasses(new(true,"1.0.1",null),"1.0.1",7),"no schema reported");
            Assert(!UpgradeRules.HealthPasses(null,"1.0.1",7),"no answer");Assert(!UpgradeRules.HealthPasses(new(false,null,null),"1.0.1",7),"503");
            Assert(!UpgradeRules.HealthPasses(new(true,"1.0.0",7),"1.0.1",7),"old version");Assert(!UpgradeRules.HealthPasses(new(true,"1.0.1",6),"1.0.1",7),"old schema");
            var count=0;foreach(var passed in new[]{true,true,false,true})count=UpgradeRules.NextHealthyPolls(count,passed);Assert(count==1,"a failure resets");
            Assert(UpgradeRules.RequiredHealthyPolls==3&&UpgradeRules.HealthWindowSeconds==300,"window");
            return Task.CompletedTask;
        });
        await check("upgrade exit codes separate refused, rolled back and left for repair",()=>
        {
            foreach(var(code,exit)in new[]
            {
                ("UPGRADED",0),("REPAIRED",0),("NOTHING_TO_REPAIR",0),("RELEASE_SIGNATURE_INVALID",2),("RELEASE_KEY_UNTRUSTED",2),("RELEASE_NOT_NEWER",2),("RELEASE_SETUP_MISMATCH",2),("RELEASE_MANIFEST_INVALID",2),("RELEASE_UNSUPPORTED_FROM",2),
                ("UPGRADE_LOCKED",2),("UPGRADE_INCOMPLETE",2),("NOT_INSTALLED",2),("INSTALL_INCOMPLETE",2),("LEGACY_INSTALL_UNSUPPORTED",2),("INFRA_IDENTITY_MISMATCH",2),("APP_IDENTITY_MISMATCH",2),
                ("INSTALL_DOWNGRADE_REFUSED",2),("ACTIVE_POINTER_INVALID",2),("TREE_RECORD_INVALID",2),(RepairHost.HandOff,1),("REPAIR_RELAUNCHED",1),("REPAIR_RUNNING_FROM_TARGET",1),("ROLLBACK_FAILED",3),("UPGRADE_COMMIT_INCOMPLETE",3),("REPAIR_UNHEALTHY",3),("ROLLED_BACK_UNHEALTHY",4),("UPGRADE_PREPARE_FAILED",4),("ADMIN_REQUIRED",1),("UNEXPECTED",1),
                ("RELEASE_TRACK_MISMATCH",2),("APP_NEEDS_INFRASTRUCTURE",2),("RELEASE_APP_SIZE",2),("RELEASE_APP_HASH",2),("APP_PACKAGE_PE",2),("APP_PACKAGE_MEMBER_HASH",2),("DISK_SPACE",1)
            })Assert(UpgradeRules.Exit(code)==exit,code);
            // Every refusal reaches the result as itself, not as a generic failure.
            foreach(var code in new[]{"RELEASE_SIGNATURE_INVALID","RELEASE_KEY_UNTRUSTED","RELEASE_NOT_NEWER","RELEASE_SETUP_MISMATCH","RELEASE_MANIFEST_INVALID","RELEASE_UNSUPPORTED_FROM","UPGRADE_LOCKED","UPGRADE_INCOMPLETE","UPGRADE_BACKUP_INVALID","NOT_INSTALLED","INSTALL_INCOMPLETE",
                "LEGACY_INSTALL_UNSUPPORTED","INFRA_IDENTITY_MISMATCH","APP_IDENTITY_MISMATCH","ACTIVE_POINTER_INVALID","TREE_RECORD_INVALID","RELEASE_TRACK_MISMATCH","APP_NEEDS_INFRASTRUCTURE","RELEASE_APP_SIZE","RELEASE_APP_HASH",
                "APP_PACKAGE_ZIP","APP_PACKAGE_PATH","APP_PACKAGE_FORBIDDEN","APP_PACKAGE_COLLISION","APP_PACKAGE_MEMBER_TYPE","APP_PACKAGE_BOUND","APP_PACKAGE_MISSING","APP_PACKAGE_TREE_MISMATCH","APP_PACKAGE_MEMBERS","APP_PACKAGE_MEMBER","APP_PACKAGE_PE","APP_PACKAGE_SIZE","APP_PACKAGE_MEMBER_HASH"})
                Assert(SetupOperation.Code(new IOException(code))==code,code);
            // A discard reports an archive the validator refused as itself, anything else as the failed preparation.
            Assert(UpgradeRules.Discarded("APP_PACKAGE_PE")=="APP_PACKAGE_PE"&&UpgradeRules.Discarded("DISK_SPACE")=="UPGRADE_PREPARE_FAILED"&&UpgradeRules.Discarded("SERVICE_START_FAILED")=="UPGRADE_PREPARE_FAILED","discarded");
            return Task.CompletedTask;
        });
        await check("a private copy runs outside every tree it may delete, in a fresh protected directory under the system temp",()=>
        {
            var parent=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Temp");Assert(PrivateCopy.Parent==parent,"parent");
            var copy=Path.Combine(parent,"yahad-setup-"+Guid.NewGuid().ToString("N"));Assert(PrivateCopy.IsPrivateDirectory(copy,parent),"fresh");
            foreach(var other in new[]{Path.Combine(parent,"yahad-setup-"+Guid.NewGuid().ToString("D")),Path.Combine(parent,"yahad-setup-"+new string('A',32)),Path.Combine(parent,"other-"+Guid.NewGuid().ToString("N")),
                Path.Combine(Path.GetTempPath(),"yahad-setup-"+Guid.NewGuid().ToString("N")),Path.Combine(copy,"yahad-setup-"+Guid.NewGuid().ToString("N"))})Assert(!PrivateCopy.IsPrivateDirectory(other,parent),other);
            // Removal deletes the binary root and the state root; the copy is in neither.
            Assert(PrivateCopy.RemovalDeletes(InstalledLayout.EngineImage("1.2.3"))&&PrivateCopy.RemovalDeletes(Path.Combine(InstallLocations.State,"x.exe")),"installed engine");
            Assert(!PrivateCopy.RemovalDeletes(Path.Combine(copy,InstalledLayout.EngineName))&&!PrivateCopy.RemovalDeletes(Path.Combine(Path.GetTempPath(),"Setup.exe")),"copy");
            // Only the waiting engine's own image is moved out instead of deleted.
            var origin=InstalledLayout.EngineImage("1.2.3");
            Assert(PrivateCopy.IsOrigin(origin,origin)&&PrivateCopy.IsOrigin(origin.ToUpperInvariant(),origin),"origin");
            Assert(!PrivateCopy.IsOrigin(InstalledLayout.EngineImage("1.2.4"),origin)&&!PrivateCopy.IsOrigin(origin,null),"other engine");
            // The copy is given this engine's command line after its image name, unchanged.
            Assert(PrivateCopy.Arguments(@"""C:\Program Files\Y\Yahad.Setup.exe"" --quiet-uninstall --data keep --result ""C:\a b\r.json""")==@" --quiet-uninstall --data keep --result ""C:\a b\r.json""","quoted");
            Assert(PrivateCopy.Arguments(@"C:\Y\Yahad.Setup.exe"+"\t"+@"--repair --result C:\r.json")=="\t"+@"--repair --result C:\r.json"&&PrivateCopy.Arguments(@"C:\Y\Yahad.Setup.exe")=="","unquoted");
            return Task.CompletedTask;
        });
        await check("a reinstall restores only the migration copy bound to its own attempt",()=>
        {
            var state=PairFixture.State("preparing",PairFixture.Pair("1.0.0","1.0.1",schema:6));var binding=InstallBackup.Encode(state);
            Assert(InstallBackup.Matches(binding,state),"own attempt");Assert(InstallBackup.Matches(binding,state with{Phase="removing_keep_data"}),"the same attempt mid-removal");
            Assert(!InstallBackup.Matches(binding,state with{InstallationId=Guid.NewGuid().ToString("N")}),"another installation");
            Assert(!InstallBackup.Matches(binding,state with{DataId=Guid.NewGuid().ToString("N")}),"another dataset");
            Assert(!InstallBackup.Matches(binding,state with{Active=state.Active with{App=state.Active.App with{DatabaseSchema=7}}}),"committed at the new schema");
            Assert(!InstallBackup.Matches(binding,state with{Active=PairFixture.Pair("1.0.0","1.0.1",schema:6,appSalt:"other")}),"another app build");
            Assert(!InstallBackup.Matches(binding,state with{Active=PairFixture.Pair("1.0.0","1.0.2",schema:6)}),"another target");
            foreach(var bad in new[]{"{}","[]","not json","{\"InstallationId\":null}"})Assert(!InstallBackup.Matches(System.Text.Encoding.UTF8.GetBytes(bad),state),bad);
            var root=PrivateSession.Create(Path.GetTempPath());
            try
            {
                var backup=Path.Combine(root,"install-backup");Directory.CreateDirectory(backup);
                File.WriteAllBytes(Path.Combine(backup,"attempt.json"),binding);Assert(!InstallBackup.BoundTo(backup,state),"no whole copy without its manifest");
                File.WriteAllBytes(Path.Combine(backup,"manifest.json"),"[]"u8.ToArray());Assert(InstallBackup.BoundTo(backup,state),"whole and bound");
                File.Delete(Path.Combine(backup,"attempt.json"));Assert(!InstallBackup.BoundTo(backup,state),"an unbound copy is never restored");
                Assert(!InstallBackup.BoundTo(Path.Combine(root,"absent"),state),"absent");
            }
            finally{Directory.Delete(root,true);}
            return Task.CompletedTask;
        });
        await check("removal inventory holds active.json, the key and every tree of both pairs once, and nothing else",()=>
        {
            var state=PairFixture.State("removing_keep_data",PairFixture.Pair("1.0.0","1.0.1"));var active=state.Active;
            var plain=RemovalIntent.Inventory(state);Assert(plain.Length==1+active.Infrastructure.Files.Length+active.App.Files.Length,"one pair");
            // An app-only apply retains the shared infrastructure tree, listed once.
            var previous=PairFixture.Pair("1.0.0","1.0.0");
            var full=state with{ReleaseKeySha256=new string('f',64),Previous=previous};full.Require();
            var inventory=RemovalIntent.Inventory(full);
            Assert(inventory.Length==2+active.Infrastructure.Files.Length+active.App.Files.Length+previous.App.Files.Length,"shared infrastructure, two apps and the key");
            Assert(inventory.Select(f=>f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()==inventory.Length,"unique");
            Assert(inventory.Any(f=>f.Path=="release-key.spki"&&f.Size==TrustedReleaseKey.SpkiBytes&&f.Sha256==full.ReleaseKeySha256),"key member");
            Assert(inventory.Any(f=>f.Path=="infra/1.0.0/setup/Yahad.Setup.exe"&&f.Sha256==active.Infrastructure.EngineSha256),"engine");
            Assert(inventory.Any(f=>f.Path=="apps/1.0.0/app/server/main.mjs")&&inventory.Any(f=>f.Path=="apps/1.0.1/app/server/main.mjs"),"both apps");
            // An infrastructure move retains two infrastructure trees.
            var moved=PairFixture.State("removing_keep_data",PairFixture.Pair("1.0.1","1.0.1"),PairFixture.Pair("1.0.0","1.0.1"));moved.Require();
            var movedInventory=RemovalIntent.Inventory(moved);
            Assert(movedInventory.Length==1+2*active.Infrastructure.Files.Length+active.App.Files.Length&&movedInventory.Any(f=>f.Path=="infra/1.0.0/setup/Yahad.Setup.exe")&&movedInventory.Any(f=>f.Path=="infra/1.0.1/setup/Yahad.Setup.exe"),"two infrastructures, one app");
            var intent=new RemovalIntent(InstallerState.CurrentFormat,full,UninstallMode.KeepData,"prepared");intent.Require();Assert(intent.Binaries.SequenceEqual(inventory),"the receipt's inventory is its record's");
            // A retry reads the recorded state back as separate objects.
            intent.RequireSameInstallation(NativeJson.Read<InstallerState>(JsonSerializer.SerializeToUtf8Bytes(full with{Phase="committed"})));
            DenyCode("UNINSTALL_INTENT_INVALID",()=>intent.RequireSameInstallation(full with{Previous=PairFixture.Pair("1.0.0","1.0.0",appSalt:"rebuilt")}));
            DenyCode("UNINSTALL_INTENT_INVALID",()=>intent.RequireSameInstallation(full with{Previous=null}));
            // Without a recorded key or previous pair, neither is in the inventory.
            var bare=new RemovalIntent(InstallerState.CurrentFormat,state,UninstallMode.KeepData,"prepared");bare.Require();
            Assert(bare.Binaries.SequenceEqual(plain)&&!bare.Binaries.Any(f=>f.Path=="release-key.spki"||f.Path.StartsWith("apps/1.0.0/",StringComparison.Ordinal)),"neither key nor previous app");
            // The previous pair is older than the active one on both tracks; never the reverse.
            DenyCode("INSTALL_STATE_INVALID",()=>(full with{Previous=PairFixture.Pair("1.0.0","1.0.2")}).Require());
            DenyCode("INSTALL_STATE_INVALID",()=>(full with{Previous=PairFixture.Pair("2.0.0","1.0.0")}).Require());
            return Task.CompletedTask;
        });
        await check("Setup mutex counts an abandoned owner as acquired",()=>
        {
            Assert(SetupInstallLock.Acquired(0),"signaled");Assert(SetupInstallLock.Acquired(0x80),"abandoned");
            Assert(!SetupInstallLock.Acquired(0x102),"timeout");Assert(!SetupInstallLock.Acquired(0xffffffff),"failed");
            return Task.CompletedTask;
        });
        await check("unattended modes reach the entry point without a WPF type",()=>
        {
            var assembly=typeof(SetupEntry).Assembly;Assert(assembly.EntryPoint?.DeclaringType==typeof(SetupEntry),"entry point");
            var window=typeof(SetupEntry).GetMethod("Window",BindingFlags.Static|BindingFlags.NonPublic)!;
            Assert(window.MethodImplementationFlags.HasFlag(MethodImplAttributes.NoInlining),"window stays out of line");
            foreach(var args in new[]{new[]{"--quiet-upgrade",@"C:\release","--result",@"C:\r.json"},["--repair","--result",@"C:\r.json"]})Assert(QuietSetup.Parse(args) is not null,args[0]);
            Assert(QuietSetup.Parse(["--quiet-upgrade",@"C:\release","--result",@"C:\r.json"])!.UpgradeDirectory==@"C:\release","upgrade directory");
            Assert(QuietSetup.Parse(["--repair","--result",@"C:\r.json"])!.Repair,"repair");
            Assert(QuietSetup.Parse(["--repair"]) is null&&QuietSetup.Parse(["--quiet-upgrade",@"C:\release"]) is null&&QuietSetup.Parse(["--apply-app",@"C:\stage\1.0.1"]) is null,"a result file is required");
            var apply=QuietSetup.Parse(["--apply-app",@"C:\stage\1.0.1","--result",@"C:\r.json"])!;
            Assert(apply.AppStage==@"C:\stage\1.0.1"&&apply.UpgradeDirectory is null&&!apply.Repair&&!apply.Uninstall,"app stage");
            try{QuietSetup.Parse(["--apply-app",@"stage\1.0.1","--result",@"C:\r.json"]);throw new Exception("relative stage accepted");}catch(IOException){}
            return Task.CompletedTask;
        });
    }
}

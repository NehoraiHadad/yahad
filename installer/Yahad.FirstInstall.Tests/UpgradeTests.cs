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
    private const string VectorKey="MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEZHSdqiSk121+ZjQy3v9Gdbwg1Aly6F4Yrq16FKzffkeJJdJlc6Tu2TFqdkPo+GWVcdnrkK0LtHMzdOOPIcLNIw==";
    private const string VectorManifest="eyJmb3JtYXQiOjEsInByb2R1Y3QiOiJ5YWhhZC13aW5kb3dzIiwidmVyc2lvbiI6IjkuOS45Iiwic291cmNlQ29tbWl0IjoiYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYSIsInNldHVwIjp7Im5hbWUiOiJZYWhhZC5TZXR1cC05LjkuOS5leGUiLCJzaGEyNTYiOiJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiIiwiYnl0ZXMiOjEyMzR9LCJkYXRhYmFzZVNjaGVtYSI6NywibWluaW11bUZyb21WZXJzaW9uIjoiMC4wLjAiLCJrZXlJZCI6ImFkNjdhNTFmYzFlNjkxZTAiLCJjcmVhdGVkVW5peCI6MTcwMDAwMDAwMH0=";
    private const string VectorSignature="dZkCj3sKWi0br785aKOse0fyyXUI3Sahw7YlkpAwySqX58Uu/rgQOp5rLSc8v8bRecVXsppBJ3N6YBGegO/WHw==";
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
        internal JsonObject Fields(string version="9.9.9",int schema=7,string minimum="0.0.0")=>new()
        {
            ["format"]=1,["product"]="yahad-windows",["version"]=version,["sourceCommit"]=new string('a',40),
            ["setup"]=new JsonObject{["name"]="Yahad.Setup-"+version+".exe",["sha256"]=new string('b',64),["bytes"]=1234},
            ["databaseSchema"]=schema,["minimumFromVersion"]=minimum,["keyId"]=TrustedReleaseKey.Id(Spki),["createdUnix"]=1700000000
        };
        public void Dispose()=>key.Dispose();
    }
    private static byte[] Bytes(JsonObject value)=>Encoding.UTF8.GetBytes(value.ToJsonString());
    private static byte[] Text(string value)=>Encoding.UTF8.GetBytes(value);
    private static JsonObject With(JsonObject value,Action<JsonObject> change){var copy=value.DeepClone().AsObject();change(copy);return copy;}
    private static UpgradeState Upgrade(string phase="started",int? fromSchema=6,int toSchema=7,bool restore=false,int attempts=0)
    {
        var files=FullInstallerIntegrationTests.State().Files;
        return UpgradeState.Begin("1.0.0","1.0.1",fromSchema,toSchema,new(new string('a',64),new string('b',64),new string('c',40),new string('d',64),files),1700000000) with{Phase=phase,RestoreData=restore,Attempts=attempts};
    }
    internal static async Task RunAsync(Func<string,Func<Task>,Task> check)
    {
        await check("release signature agrees with the Node signing tool",()=>
        {
            var key=Convert.FromBase64String(VectorKey);var manifest=Convert.FromBase64String(VectorManifest);var signature=Convert.FromBase64String(VectorSignature);
            Assert(ReleaseSignature.Verify(key,manifest,signature),"vector");
            Assert(ReleaseVerification.Verify(key,manifest,signature,"1.0.0",6).Version=="9.9.9","vector admitted");
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
            Assert(ReleaseManifest.Parse(Bytes(good)).Setup.Bytes==1234,"good");
            var text=good.ToJsonString();var bad=new List<byte[]>
            {
                Bytes(With(good,v=>v["unknown"]=1)),Bytes(With(good,v=>v["setup"]!["unknown"]=1)),
                (byte[])[0xef,0xbb,0xbf,..Text(text)],Text(text+"x"),Text("{\"format\":1,"+text[1..]),Text(""),Text("null"),
                new byte[ReleaseManifest.MaximumBytes+1],Bytes(With(good,v=>v["ballast"]=new JsonObject{["a"]=new JsonObject{["b"]=new JsonObject{["c"]=new JsonObject()}}})),
                Bytes(With(good,v=>v["format"]=2)),Bytes(With(good,v=>v["product"]="yahad")),Bytes(With(good,v=>v["version"]="1.0")),Bytes(With(good,v=>v["version"]="01.0.0")),
                Bytes(With(good,v=>v["version"]="1.0.0-rc")),Bytes(With(good,v=>v["minimumFromVersion"]="x")),Bytes(With(good,v=>v["sourceCommit"]="abc")),Bytes(With(good,v=>v["keyId"]="ABC")),
                Bytes(With(good,v=>v["setup"]!["name"]="other.exe")),Bytes(With(good,v=>v["setup"]!["name"]="..\\Yahad.Setup-9.9.9.exe")),Bytes(With(good,v=>v["setup"]!["sha256"]="ab")),
                Bytes(With(good,v=>v["setup"]!["bytes"]=0)),Bytes(With(good,v=>v["setup"]!["bytes"]=ReleaseManifest.MaximumSetupBytes+1)),Bytes(With(good,v=>v["databaseSchema"]=0)),Bytes(With(good,v=>v["createdUnix"]=0)),
                Bytes(With(good,v=>v.Remove("keyId"))),Bytes(With(good,v=>v.Remove("setup"))),Bytes(With(good,v=>v["sourceCommit"]=null)),Bytes(With(good,v=>v["setup"]=null))
            };
            for(var i=0;i<bad.Count;i++)
            {var index=i;try{DenyCode("RELEASE_MANIFEST_INVALID",()=>ReleaseManifest.Parse(bad[index]));}catch(Exception error){throw new Exception("manifest case "+index,error);}}
            return Task.CompletedTask;
        });
        await check("release verification reads nothing before the signature",()=>
        {
            using var release=new Release();var garbage=Text("not json");
            DenyCode("RELEASE_SIGNATURE_INVALID",()=>ReleaseVerification.Verify(release.Spki,garbage,new byte[64],"1.0.0",6));
            DenyCode("RELEASE_SIGNATURE_INVALID",()=>ReleaseVerification.Verify(release.Spki,garbage,null,"1.0.0",6));
            DenyCode("RELEASE_SIGNATURE_INVALID",()=>ReleaseVerification.Verify(release.Spki,new byte[ReleaseManifest.MaximumBytes+1],release.Sign(new byte[ReleaseManifest.MaximumBytes+1]),"1.0.0",6));
            DenyCode("RELEASE_MANIFEST_INVALID",()=>ReleaseVerification.Verify(release.Spki,garbage,release.Sign(garbage),"1.0.0",6));
            return Task.CompletedTask;
        });
        await check("release verification refuses a downgrade, a replay and an unsupported source",()=>
        {
            using var release=new Release();
            byte[] Signed(JsonObject value,out byte[] signature){var bytes=Bytes(value);signature=release.Sign(bytes);return bytes;}
            void Refuse(string code,JsonObject value,string installed,int? schema=6){var bytes=Signed(value,out var signature);DenyCode(code,()=>ReleaseVerification.Verify(release.Spki,bytes,signature,installed,schema));}
            var good=Signed(release.Fields(),out var valid);Assert(ReleaseVerification.Verify(release.Spki,good,valid,"1.0.0",null).Version=="9.9.9","unknown schema admitted");
            Refuse("RELEASE_NOT_NEWER",release.Fields("9.9.9"),"9.9.9");Refuse("RELEASE_NOT_NEWER",release.Fields("9.9.9"),"10.0.0");
            Refuse("RELEASE_UNSUPPORTED_FROM",release.Fields(minimum:"2.0.0"),"1.0.0");Refuse("RELEASE_UNSUPPORTED_FROM",release.Fields(schema:5),"1.0.0");
            using var other=new Release();Refuse("RELEASE_KEY_UNTRUSTED",With(release.Fields(),v=>v["keyId"]=TrustedReleaseKey.Id(other.Spki)),"1.0.0");
            return Task.CompletedTask;
        });
        await check("release versions compare as numbers",()=>
        {
            Assert(ReleaseManifest.Compare("10.0.0","9.9.9")>0,"major");Assert(ReleaseManifest.Compare("1.2.10","1.2.9")>0,"patch");Assert(ReleaseManifest.Compare("1.2.3","1.2.3")==0,"equal");
            Assert(ReleaseManifest.Compare("99999999999999.0.0","99999999999998.0.0")>0,"no overflow");Assert(ReleaseManifest.Compare("1.0.0","1.0.1")<0,"less");
            DenyCode("RELEASE_MANIFEST_INVALID",()=>ReleaseManifest.Compare("1.0","1.0.0"));
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
            Assert(read.Id==state.Id&&read.Phase==state.Phase&&read.FromSchema==6&&read.ToSchema==7&&read.SchemaChanged&&read.Target.Files.Length==state.Target.Files.Length,"round trip");
            Assert(UpgradeState.Parse(Upgrade(fromSchema:null).Encode()).SchemaChanged,"unknown source is a change");
            Assert(!Upgrade(fromSchema:7).SchemaChanged,"same schema");
            foreach(var phase in UpgradeState.Phases)Assert(UpgradeState.Parse((state with{Phase=phase}).Encode()).Phase==phase,phase);
            var json=JsonNode.Parse(bytes)!.AsObject();
            foreach(var bad in new[]{With(json,v=>v["Unknown"]=1),With(json,v=>v["Format"]=2),With(json,v=>v["Phase"]="other"),With(json,v=>v.Remove("Target")),With(json,v=>v["From"]="..\\x"),With(json,v=>{v["From"]="2.0.0";}),With(json,v=>v["Id"]="x"),With(json,v=>{v["RestoreData"]=true;v["FromSchema"]=7;v["SchemaChanged"]=false;}),With(json,v=>v["SchemaChanged"]=false)})
            {var value=Bytes(bad);try{UpgradeState.Parse(value);}catch(IOException){continue;}throw new Exception("UPGRADE_STATE_ADMITTED "+bad.ToJsonString()[..60]);}
            return Task.CompletedTask;
        });
        await check("repair decisions follow the recorded phase",()=>
        {
            foreach(var(phase,attempts,action)in new[]
            {
                ("preparing",0,RepairAction.Discard),("prepared",9,RepairAction.Discard),("stopped",0,RepairAction.RollBack),("backed_up",0,RepairAction.RollBack),("switched",2,RepairAction.RollBack),
                ("started",0,RepairAction.RollBack),("rolling_back",1,RepairAction.RollBack),("started",3,RepairAction.GiveUp),("rolling_back",3,RepairAction.GiveUp),("stopped",4,RepairAction.GiveUp),
                ("healthy",0,RepairAction.FinishCommit),("committed",9,RepairAction.FinishCommit),("rolled_back",9,RepairAction.FinishRollback)
            })Assert(UpgradeRules.ForRepair(phase,attempts)==action,phase+attempts);
            DenyCode("INSTALL_STATE_INVALID",()=>UpgradeRules.ForRepair("other",0));
            Assert(new HashSet<string>(UpgradeState.Phases).SetEquals(UpgradeState.Phases),"phases unique");
            return Task.CompletedTask;
        });
        await check("data is restored exactly when the new release may have touched it",()=>
        {
            foreach(var phase in new[]{"preparing","prepared","stopped"})Assert(!UpgradeRules.RestoresData(Upgrade(phase)),phase);
            foreach(var phase in new[]{"backed_up","switched","started"})Assert(UpgradeRules.RestoresData(Upgrade(phase)),phase);
            foreach(var phase in new[]{"backed_up","switched","started"})Assert(!UpgradeRules.RestoresData(Upgrade(phase,fromSchema:7)),"same schema "+phase);
            Assert(UpgradeRules.RestoresData(Upgrade("rolling_back",restore:true)),"a resumed rollback keeps its decision");
            Assert(!UpgradeRules.RestoresData(Upgrade("rolling_back")),"a rollback from before the backup never restores");
            return Task.CompletedTask;
        });
        await check("health needs this release, its schema and consecutive answers",()=>
        {
            Assert(UpgradeRules.HealthPasses(new(true,"1.0.1",7),"1.0.1",7),"good");Assert(UpgradeRules.HealthPasses(new(true,"1.0.1",9),"1.0.1",null),"any schema when unknown");
            Assert(!UpgradeRules.HealthPasses(null,"1.0.1",7),"no answer");Assert(!UpgradeRules.HealthPasses(new(false,null,null),"1.0.1",null),"503");
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
                ("UPGRADE_LOCKED",2),("UPGRADE_INCOMPLETE",2),("NOT_INSTALLED",2),("INSTALL_INCOMPLETE",2),("REPAIR_RUNNING_FROM_TARGET",2),("ROLLBACK_FAILED",3),("UPGRADE_COMMIT_INCOMPLETE",3),("REPAIR_UNHEALTHY",3),("ROLLED_BACK_UNHEALTHY",4),("UPGRADE_PREPARE_FAILED",4),("ADMIN_REQUIRED",1),("UNEXPECTED",1)
            })Assert(UpgradeRules.Exit(code)==exit,code);
            // Every refusal reaches the result as itself, not as a generic failure.
            foreach(var code in new[]{"RELEASE_SIGNATURE_INVALID","RELEASE_KEY_UNTRUSTED","RELEASE_NOT_NEWER","RELEASE_SETUP_MISMATCH","RELEASE_MANIFEST_INVALID","RELEASE_UNSUPPORTED_FROM","UPGRADE_LOCKED","UPGRADE_INCOMPLETE","UPGRADE_BACKUP_INVALID","NOT_INSTALLED","INSTALL_INCOMPLETE","REPAIR_RUNNING_FROM_TARGET"})
                Assert(SetupOperation.Code(new IOException(code))==code,code);
            return Task.CompletedTask;
        });
        await check("removal inventory holds both retained releases and the key, and nothing else",()=>
        {
            var state=FullInstallerIntegrationTests.State("removing_keep_data","1.0.1");
            var plain=RemovalIntent.Inventory(state,1234);Assert(plain.Length==state.Files.Length+1,"one release");
            var previous=new RetainedRelease("1.0.0",new string('e',64),4321,state.Files);
            var full=state with{ReleaseKeySha256=new string('f',64),Previous=previous};full.Require();
            var inventory=RemovalIntent.Inventory(full,1234);
            Assert(inventory.Length==2*state.Files.Length+3,"two releases and the key");
            Assert(inventory.Any(f=>f.Path=="release-key.spki"&&f.Size==TrustedReleaseKey.SpkiBytes&&f.Sha256==full.ReleaseKeySha256),"key member");
            Assert(inventory.Any(f=>f.Path=="versions/1.0.1/Yahad.Setup.exe"&&f.Size==1234),"current Setup");
            Assert(inventory.Any(f=>f.Path=="versions/1.0.0/Yahad.Setup.exe"&&f.Size==4321&&f.Sha256==previous.SetupSha256),"retained Setup");
            Assert(inventory.Any(f=>f.Path=="versions/1.0.0/host/Yahad.ServiceHost.exe")&&inventory.Any(f=>f.Path=="versions/1.0.1/host/Yahad.ServiceHost.exe"),"both hosts");
            var intent=new RemovalIntent(InstallerState.CurrentFormat,full,UninstallMode.KeepData,inventory,"prepared");intent.Require();
            // A retry reads the recorded state back as separate objects.
            intent.RequireSameInstallation(NativeJson.Read<InstallerState>(JsonSerializer.SerializeToUtf8Bytes(full with{Phase="committed"})));
            DenyCode("UNINSTALL_INTENT_INVALID",()=>intent.RequireSameInstallation(full with{Previous=previous with{SetupBytes=1}}));
            DenyCode("UNINSTALL_INTENT_INVALID",()=>intent.RequireSameInstallation(full with{Previous=null}));
            foreach(var path in new[]{"versions/2.0.0/host/x","other","release-key.spki2"})
                DenyCode("UNINSTALL_INTENT_INVALID",()=>(intent with{Binaries=[..inventory,new(path,1,new string('d',64))]}).Require());
            foreach(var path in new[]{"versions/1.0.1/host/../x","versions/1.0.1/other/x"})DenyCode("PACKAGE_PATH",()=>(intent with{Binaries=[..inventory,new(path,1,new string('d',64))]}).Require());
            // Without a recorded key or previous release, neither is admissible.
            var bare=new RemovalIntent(InstallerState.CurrentFormat,state,UninstallMode.KeepData,plain,"prepared");bare.Require();
            DenyCode("UNINSTALL_INTENT_INVALID",()=>(bare with{Binaries=[..plain,new("release-key.spki",91,new string('f',64))]}).Require());
            DenyCode("UNINSTALL_INTENT_INVALID",()=>(bare with{Binaries=[..plain,new("versions/1.0.0/Yahad.Setup.exe",1,new string('f',64))]}).Require());
            // The previous release is older than the current one; never the reverse.
            DenyCode("INSTALL_STATE_INVALID",()=>(full with{Previous=previous with{Version="1.0.1"}}).Require());
            DenyCode("INSTALL_STATE_INVALID",()=>(full with{Previous=previous with{Version="2.0.0"}}).Require());
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
            Assert(QuietSetup.Parse(["--repair"]) is null&&QuietSetup.Parse(["--quiet-upgrade",@"C:\release"]) is null,"a result file is required");
            return Task.CompletedTask;
        });
    }
}

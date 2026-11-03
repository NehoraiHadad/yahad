using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Yahad.Install;
using Yahad.Platform;
using Yahad.Service;
using Yahad.Setup;
using Yahad.Updater;

// Everything about the Updater that can be decided without a machine or GitHub: the
// size caps and framing, what is verified before what, and every install decision.
// The machine half, from the parent's click to both services on the new version, runs
// in installer/machine-acceptance.ps1. Only the download from GitHub itself is here
// and nowhere else, because its source is fixed and carries no CI-signed release.
internal static class UpdaterTests
{
    private const string Repo="https://github.com/NehoraiHadad/yahad/releases/download/";
    private static void Assert(bool value,string what){if(!value)throw new Exception("UPDATER_ASSERTION "+what);}
    private static async Task DenyAsync(string code,Func<Task> action)
    {
        try{await action();}catch(IOException error)when(error.Message==code){return;}
        throw new Exception("UPDATER_EXPECTED_"+code);
    }
    private static void Deny(string code,Action action)=>DenyAsync(code,()=>{action();return Task.CompletedTask;}).GetAwaiter().GetResult();
    private static string Id()=>Guid.NewGuid().ToString("D");
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class Unsized(byte[] body):HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>stream.WriteAsync(body).AsTask();
        protected override bool TryComputeLength(out long length){length=-1;return false;}
    }
    // The network the source sees. Anything not routed is a 404.
    private sealed class Feed:HttpMessageHandler
    {
        internal readonly List<string> Requests=[];
        internal readonly Dictionary<string,Func<HttpResponseMessage>> Routes=[];
        internal void Bytes(string url,byte[] body)=>Routes[url]=()=>new(HttpStatusCode.OK){Content=new ByteArrayContent(body)};
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            var url=request.RequestUri!.ToString();lock(Requests)Requests.Add(url);
            return Task.FromResult(Routes.TryGetValue(url,out var route)?route():new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
    private sealed class Bed:IDisposable
    {
        private readonly ECDsa signing=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string sid=WindowsIdentity.GetCurrent().User!.Value;
        internal readonly Feed Network=new();internal readonly GitHubReleaseSource Source;
        internal readonly UpdaterPaths Paths;
        internal readonly List<(StagedRelease Release,string Result)> Launched=[];
        internal DateTimeOffset Now=new(DateTime.Today.AddHours(12));
        internal Exception? LaunchFailure;
        internal byte[] Spki=>signing.ExportSubjectPublicKeyInfo();
        internal Bed()
        {
            var root=Path.Combine(Path.GetTempPath(),"yahad-updater-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            Paths=new(root,$"O:{sid}D:P(A;OICI;FA;;;{sid})",$"O:{sid}D:P(A;;FA;;;{sid})");Source=new(Network);
        }
        internal UpdaterEngine Engine(string current="1.0.0",bool key=true)
        {
            var engine=new UpdaterEngine(Paths,current,()=>key?Spki:null,key?Spki:null,Source,(release,result)=>{if(LaunchFailure is not null)throw LaunchFailure;Launched.Add((release,result));},()=>Now);
            engine.Start();return engine;
        }
        internal byte[] Sign(byte[] manifest)=>signing.SignData((byte[])[..Encoding.ASCII.GetBytes(ReleaseSignature.Prefix),..manifest],HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        internal JsonObject Fields(string version,byte[] setup,string minimum="0.0.0")=>new()
        {
            ["format"]=1,["product"]="yahad-windows",["version"]=version,["sourceCommit"]=new string('a',40),
            ["setup"]=new JsonObject{["name"]="Yahad.Setup-"+version+".exe",["sha256"]=Hash(setup),["bytes"]=setup.Length},
            ["databaseSchema"]=7,["minimumFromVersion"]=minimum,["keyId"]=TrustedReleaseKey.Id(Spki),["createdUnix"]=1700000000
        };
        internal static byte[] Encode(JsonObject value)=>Encoding.UTF8.GetBytes(value.ToJsonString());
        internal static string ManifestUrl(string version)=>Repo+"v"+version+"/yahad-windows-"+version+".json";
        internal static string SetupUrl(string version)=>Repo+"v"+version+"/Yahad.Setup-"+version+".exe";
        // Publishes what GitHub's list would say and what its assets would answer.
        internal void List(string tag,bool draft=false,bool prerelease=false,long? manifestSize=null,long? signatureSize=null,long? setupSize=null,byte[]? manifest=null,byte[]? signature=null,byte[]? setup=null,string? digest=null)
        {
            var version=tag.TrimStart('v');var assets=new JsonArray();
            void Asset(string name,long size,string url,string? sum=null)=>assets.Add(new JsonObject{["name"]=name,["size"]=size,["browser_download_url"]=url,["digest"]=sum,["uploader"]=new JsonObject{["login"]="x"}});
            if(manifest is not null&&signature is not null&&setup is not null)
            {
                var name="yahad-windows-"+version+".json";
                Asset(name,manifestSize??manifest.Length,ManifestUrl(version),null);Asset(name+".sig",signatureSize??signature.Length,ManifestUrl(version)+".sig");
                Asset("Yahad.Setup-"+version+".exe",setupSize??setup.Length,SetupUrl(version),digest);
                Network.Bytes(ManifestUrl(version),manifest);Network.Bytes(ManifestUrl(version)+".sig",signature);Network.Bytes(SetupUrl(version),setup);
            }
            Network.Bytes(GitHubReleaseSource.Latest,Encode(new JsonObject{["tag_name"]=tag,["draft"]=draft,["prerelease"]=prerelease,["assets"]=assets,["body"]="notes"}));
        }
        internal void Good(string version="9.9.9",byte[]? setup=null)
        {
            setup??=[1,2,3,4];var manifest=Encode(Fields(version,setup));List("v"+version,manifest:manifest,signature:Sign(manifest),setup:setup);
        }
        internal static async Task<UpdaterStatus> CheckAsync(UpdaterEngine engine)
        {
            var reply=engine.Handle(new(1,"check",Id()));if(!reply.Ok)throw new Exception("CHECK_REFUSED "+reply.Code);
            await engine.Work;return Status(engine);
        }
        internal static UpdaterStatus Status(UpdaterEngine engine)=>engine.Handle(new(1,"status")).Status!;
        internal bool Asked(string url){lock(Network.Requests)return Network.Requests.Contains(url);}
        internal string[] Staged(string version="9.9.9")=>Directory.Exists(Paths.Stage(version))?Directory.GetFiles(Paths.Stage(version)).Select(name=>Path.GetFileName(name)!).ToArray():[];
        public void Dispose(){Source.Dispose();signing.Dispose();Directory.Delete(Paths.Root,true);}
    }
    private sealed class FakeScm:IScmApi
    {
        internal readonly List<ServiceReport> Reports=[];internal Func<uint,uint>? Handler;internal string? Name;
        public void Dispatch(string name,Action<string[]> main){Name=name;main([]);}
        public void Register(string name,Func<uint,uint> handler)=>Handler=handler;
        public void Publish(ServiceReport report)=>Reports.Add(report);
    }
    private static UpdaterStatus SampleStatus()=>new(true,"notify","available","1.0.0","9.9.9",new("9.9.9",new string('f',64)),5,6,null,new(Id(),"check","succeeded",1,2,null,null),new(Id(),"auto","applied"));

    internal static async Task RunAsync(Func<string,Func<Task>,Task> check)
    {
        await check("updater: the GitHub list is capped and strict, and tolerates GitHub's other fields",()=>
        {
            var good=GitHubReleaseSource.ParseRelease(Encoding.UTF8.GetBytes("""{"id":1,"author":{"login":"x","nested":{"a":[1]}},"tag_name":"v1.2.3","draft":false,"prerelease":false,"assets":[{"id":2,"name":"a","size":3,"browser_download_url":"https://github.com/a","digest":null,"uploader":{"login":"y"}}]}"""));
            Assert(good.TagName=="v1.2.3"&&good.Assets is [{Name:"a",Size:3,Digest:null}],"list parsed");
            Deny("GITHUB_RESPONSE_BOUND",()=>GitHubReleaseSource.ParseRelease(new byte[GitHubReleaseSource.MaximumListBytes+1]));
            foreach(var bad in new[]{"","[]","{}","not json","""{"tag_name":null,"draft":false,"prerelease":false,"assets":[]}""","""{"tag_name":"v1","draft":false,"prerelease":false}""","""{"tag_name":"v1","draft":"no","prerelease":false,"assets":[]}""",
                """{"tag_name":"v1","draft":false,"prerelease":false,"assets":[],"x":[[[[[[[[[[1]]]]]]]]]]}"""})
                Deny("GITHUB_LIST_INVALID",()=>GitHubReleaseSource.ParseRelease(Encoding.UTF8.GetBytes(bad)));
            var many=new JsonArray();for(var i=0;i<1001;i++)many.Add(new JsonObject{["name"]="a",["size"]=1,["browser_download_url"]="u"});
            Deny("GITHUB_LIST_INVALID",()=>GitHubReleaseSource.ParseRelease(Bed.Encode(new JsonObject{["tag_name"]="v1",["draft"]=false,["prerelease"]=false,["assets"]=many})));
            return Task.CompletedTask;
        });
        await check("updater: responses over their cap stop whether or not they declare a length",async()=>
        {
            var feed=new Feed();using var source=new GitHubReleaseSource(feed);var url=Repo+"x";
            feed.Bytes(url,new byte[65]);await DenyAsync("GITHUB_RESPONSE_BOUND",()=>source.GetAssetAsync(url,64,default));
            feed.Routes[url]=()=>new(HttpStatusCode.OK){Content=new Unsized(new byte[65])};await DenyAsync("GITHUB_RESPONSE_BOUND",()=>source.GetAssetAsync(url,64,default));
            feed.Routes[url]=()=>new(HttpStatusCode.OK){Content=new Unsized(new byte[64])};Assert((await source.GetAssetAsync(url,64,default)).Length==64,"at the cap");
            feed.Routes[url]=()=>{var compressed=new ByteArrayContent([1]);compressed.Headers.ContentEncoding.Add("gzip");return new(HttpStatusCode.OK){Content=compressed};};
            await DenyAsync("GITHUB_CONTENT_ENCODING",()=>source.GetAssetAsync(url,64,default));
            feed.Routes.Remove(url);
            try{await source.GetAssetAsync(url,64,default);throw new Exception("HTTP_ERROR_ACCEPTED");}catch(HttpRequestException){}
        });
        await check("updater: only https to GitHub's own hosts, followed a bounded number of times",async()=>
        {
            foreach(var good in new[]{GitHubReleaseSource.Latest,"https://github.com/a","https://objects.githubusercontent.com/a?x=1","https://release-assets.githubusercontent.com/a"})GitHubReleaseSource.SafeUrl(new(good));
            foreach(var bad in new[]{"http://github.com/a","https://evil.example/a","https://github.com.evil.example/a","https://github.com:444/a","https://user@github.com/a","https://github.com/a#b","https://127.0.0.1/a","ftp://github.com/a"})
                Deny("GITHUB_URL",()=>GitHubReleaseSource.SafeUrl(new(bad)));
            var feed=new Feed();using var source=new GitHubReleaseSource(feed);var start=Repo+"start";
            HttpResponseMessage To(string target){var moved=new HttpResponseMessage(HttpStatusCode.Found);moved.Headers.Location=new(target);return moved;}
            feed.Routes[start]=()=>To("https://evil.example/a");
            await DenyAsync("GITHUB_URL",()=>source.GetAssetAsync(start,8,default));Assert(feed.Requests is [var only]&&only==start,"the foreign host was contacted");
            feed.Routes[start]=()=>To("https://release-assets.githubusercontent.com/a");feed.Bytes("https://release-assets.githubusercontent.com/a",[7]);
            Assert((await source.GetAssetAsync(start,8,default)) is [7],"redirect to GitHub's asset host");
            feed.Routes[start]=()=>To(start);feed.Requests.Clear();
            await DenyAsync("GITHUB_REDIRECT_LIMIT",()=>source.GetAssetAsync(start,8,default));Assert(feed.Requests.Count==GitHubReleaseSource.MaximumRedirects+1,"redirect count");
        });
        await check("updater: frames are bounded before anything is allocated or parsed",async()=>
        {
            static byte[] Frame(int length,byte[]? body=null)=>[..BitConverter.GetBytes(length),..body??[]];
            foreach(var length in new[]{0,-1,int.MinValue,UpdaterProtocol.MaximumFrame+1,int.MaxValue})
                await DenyAsync("UPDATER_FRAME",()=>UpdaterProtocol.ReadAsync<UpdaterRequest>(new MemoryStream(Frame(length)),default));
            foreach(var body in new[]{"not json","""{"v":1,"op":"status","extra":1}""","""{"v":1,"op":"status","op":"check"}""","null"})
            {var bytes=Encoding.UTF8.GetBytes(body);await DenyAsync("UPDATER_FRAME",()=>UpdaterProtocol.ReadAsync<UpdaterRequest>(new MemoryStream(Frame(bytes.Length,bytes)),default));}
            try{await UpdaterProtocol.ReadAsync<UpdaterRequest>(new MemoryStream(Frame(10,[1,2])),default);throw new Exception("SHORT_FRAME_ACCEPTED");}catch(EndOfStreamException){}
            await DenyAsync("UPDATER_FRAME",()=>UpdaterProtocol.WriteAsync(new MemoryStream(),new UpdaterRequest(1,new string('x',UpdaterProtocol.MaximumFrame)),default));
            var round=new MemoryStream();await UpdaterProtocol.WriteAsync(round,new UpdaterRequest(1,"status"),default);round.Position=0;
            Assert((await UpdaterProtocol.ReadAsync<UpdaterRequest>(round,default)).Op=="status","round trip");
            Assert(UpdaterProtocol.IsId(Id())&&!UpdaterProtocol.IsId(null)&&!UpdaterProtocol.IsId(Id().ToUpperInvariant())&&!UpdaterProtocol.IsId("x"),"id shape");
            Assert(UpdaterProtocol.IsFingerprint(new string('a',64))&&!UpdaterProtocol.IsFingerprint(new string('A',64))&&!UpdaterProtocol.IsFingerprint(new string('a',64)+"\n"),"fingerprint shape");
        });
        await check("updater: without an installed key nothing is ever fetched or run",async()=>
        {
            using var bed=new Bed();bed.Good();var engine=bed.Engine(key:false);
            var status=Bed.Status(engine);Assert(!status.Enabled&&status.Phase=="idle"&&status.InstallTarget is null,"reports unavailable");
            foreach(var request in new UpdaterRequest[]{new(1,"check",Id()),new(1,"install",Id(),"9.9.9",new string('a',64)),new(1,"set-mode",Id(),Mode:"auto")})
                Assert(engine.Handle(request) is {Ok:false,Code:"NOT_AVAILABLE"},request.Op+" accepted without a key");
            bed.Now=bed.Now.AddDays(3);engine.Tick();await engine.Work;
            Assert(bed.Network.Requests.Count==0&&bed.Launched.Count==0&&!Directory.Exists(bed.Paths.Staging),"acted without a key");
            // A staged folder is not offered either, and an installed key the image does
            // not carry counts as no key at all.
            using var other=new Bed();var foreign=new UpdaterEngine(bed.Paths,"1.0.0",()=>bed.Spki,other.Spki,bed.Source,(_,_)=>throw new Exception("LAUNCHED"),()=>bed.Now);foreign.Start();
            Assert(!Bed.Status(foreign).Enabled,"a mismatched key enabled updates");
        });
        await check("updater: a release that is not newer, not final or not for Windows fetches nothing",async()=>
        {
            foreach(var (tag,draft,prerelease) in new[]{("v1.0.0",false,false),("v0.5.3",false,false),("v9.9.9",true,false),("v9.9.9",false,true),("nightly",false,false),("v9.9.9",false,false)})
            {
                using var bed=new Bed();bed.List(tag,draft,prerelease);var engine=bed.Engine();var status=await Bed.CheckAsync(engine);
                Assert(status.Error is null&&status.Phase=="idle"&&status.LatestVersion is null&&status.LastRequest is {State:"succeeded"}&&status.CheckedAt is not null,tag+" status");
                Assert(bed.Network.Requests is [GitHubReleaseSource.Latest],tag+" fetched more than the list");
            }
        });
        await check("updater: the manifest's signature is checked over raw bytes before it is parsed, and no Setup is fetched before that",async()=>
        {
            var setup=new byte[]{1,2,3};
            async Task Refused(Action<Bed> arrange,string error)
            {
                using var bed=new Bed();arrange(bed);var engine=bed.Engine();var status=await Bed.CheckAsync(engine);
                Assert(status.Error==error&&status.Phase==(error=="incompatible"?"incompatible":"failed")&&status.InstallTarget is null&&status.LastRequest is {State:"failed"},"check ended "+status.Error+"/"+status.Phase);
                Assert(!bed.Asked(Bed.SetupUrl("9.9.9"))&&!Directory.Exists(bed.Paths.Staging),"the Setup was fetched before verification");
            }
            // Signed by someone else, and not even JSON: refused as a signature, unparsed.
            await Refused(bed=>{using var other=new Bed();var manifest=Bed.Encode(bed.Fields("9.9.9",setup));bed.List("v9.9.9",manifest:manifest,signature:other.Sign(manifest),setup:setup);},"signature");
            await Refused(bed=>{using var other=new Bed();var manifest=Encoding.UTF8.GetBytes("{{{ not json");bed.List("v9.9.9",manifest:manifest,signature:other.Sign(manifest),setup:setup);},"signature");
            await Refused(bed=>{var manifest=Bed.Encode(bed.Fields("9.9.9",setup));var signature=bed.Sign(manifest);manifest[10]^=1;bed.List("v9.9.9",manifest:manifest,signature:signature,setup:setup);},"signature");
            // Rightly signed, but not an acceptable release for this tag or this machine.
            await Refused(bed=>{var manifest=Encoding.UTF8.GetBytes("{{{ not json");bed.List("v9.9.9",manifest:manifest,signature:bed.Sign(manifest),setup:setup);},"signature");
            await Refused(bed=>{var manifest=Bed.Encode(bed.Fields("9.9.8",setup));bed.Network.Bytes(Bed.ManifestUrl("9.9.9"),manifest);bed.List("v9.9.9",manifest:manifest,signature:bed.Sign(manifest),setup:setup);},"signature");
            await Refused(bed=>{var manifest=Bed.Encode(bed.Fields("9.9.9",setup,minimum:"5.0.0"));bed.List("v9.9.9",manifest:manifest,signature:bed.Sign(manifest),setup:setup);},"incompatible");
            await Refused(bed=>{var manifest=Bed.Encode(bed.Fields("9.9.9",setup));bed.List("v9.9.9",manifest:manifest,signature:bed.Sign(manifest),setup:setup,setupSize:setup.Length+1);},"archive");
            await Refused(bed=>{var manifest=Bed.Encode(bed.Fields("9.9.9",setup));bed.List("v9.9.9",manifest:manifest,signature:bed.Sign(manifest),setup:setup,digest:"sha256:"+new string('0',64));},"archive");
            // Declared sizes that cannot be a release are not even fetched.
            using var big=new Bed();var manifest=Bed.Encode(big.Fields("9.9.9",setup));big.List("v9.9.9",manifestSize:ReleaseManifest.MaximumBytes+1,manifest:manifest,signature:big.Sign(manifest),setup:setup);
            Assert((await Bed.CheckAsync(big.Engine())).Error=="signature"&&big.Network.Requests is [GitHubReleaseSource.Latest],"an oversized manifest was fetched");
            using var longer=new Bed();manifest=Bed.Encode(longer.Fields("9.9.9",setup));longer.List("v9.9.9",signatureSize:65,manifest:manifest,signature:longer.Sign(manifest),setup:setup);
            Assert((await Bed.CheckAsync(longer.Engine())).Error=="signature"&&longer.Network.Requests is [GitHubReleaseSource.Latest],"an oversized signature was fetched");
        });
        await check("updater: a download that differs from the signed size or hash never appears under the Setup's name",async()=>
        {
            var signed=new byte[]{1,2,3,4};
            foreach(var (served,hidden) in new (byte[],bool)[]{(new byte[]{1,2,3},false),(new byte[]{1,2,3},true),(new byte[]{1,2,3,4,5},false),(new byte[]{1,2,3,4,5},true),(new byte[]{9,9,9,9},false)})
            {
                using var bed=new Bed();var manifest=Bed.Encode(bed.Fields("9.9.9",signed));bed.List("v9.9.9",manifest:manifest,signature:bed.Sign(manifest),setup:signed);
                bed.Network.Routes[Bed.SetupUrl("9.9.9")]=()=>new(HttpStatusCode.OK){Content=hidden?new Unsized(served):new ByteArrayContent(served)};
                var status=await Bed.CheckAsync(bed.Engine());
                Assert(status.Error=="archive"&&status.InstallTarget is null&&bed.Staged().Length==0,"served "+served.Length+" bytes, ended "+status.Error);
            }
        });
        await check("updater: a checked release is offered by its manifest's fingerprint and installs only as that target",async()=>
        {
            using var bed=new Bed();var setup=new byte[]{1,2,3,4};var manifest=Bed.Encode(bed.Fields("9.9.9",setup));var fingerprint=Hash(manifest);
            bed.List("v9.9.9",manifest:manifest,signature:bed.Sign(manifest),setup:setup,digest:"sha256:"+Hash(setup));var engine=bed.Engine();
            var status=await Bed.CheckAsync(engine);
            Assert(status is {Phase:"available",LatestVersion:"9.9.9",Error:null}&&status.InstallTarget==new UpdaterTarget("9.9.9",fingerprint),"offered "+status.Phase);
            Assert(bed.Staged().OrderBy(n=>n,StringComparer.Ordinal).SequenceEqual(["Yahad.Setup-9.9.9.exe","yahad-windows-9.9.9.json","yahad-windows-9.9.9.json.sig"]),"stage contents");
            // Refused without running anything.
            foreach(var (version,print) in new[]{("9.9.9",new string('0',64)),("9.9.8",fingerprint)})Assert(engine.Handle(new(1,"install",Id(),version,print)) is {Ok:false,Code:"STALE_TARGET"},"another target accepted");
            foreach(var bad in new UpdaterRequest[]{new(2,"status"),new(1,"nope",Id()),new(1,"check"),new(1,"check","x"),new(1,"install",Id(),"9.9.9"),new(1,"install",Id(),"latest",fingerprint),new(1,"install",null,"9.9.9",fingerprint),new(1,"set-mode",Id(),Mode:"always"),new(1,"defer",Id())})
                Assert(engine.Handle(bad) is {Ok:false,Code:"BAD_REQUEST"},"bad request "+bad.Op);
            Assert(bed.Launched.Count==0,"launched for a refused request");
            var id=Id();File.WriteAllText(bed.Paths.LastUpgrade,"{}");
            Assert(engine.Handle(new(1,"install",id,"9.9.9",fingerprint)).Ok,"install refused");
            Assert(bed.Launched is [var launched]&&launched.Release.Setup==Path.Combine(bed.Paths.Stage("9.9.9"),"Yahad.Setup-9.9.9.exe")&&launched.Result==bed.Paths.LastUpgrade,"launch");
            Assert(!File.Exists(bed.Paths.LastUpgrade),"an old result would be read as this install's");
            status=Bed.Status(engine);Assert(status is {Phase:"activating",LastRequest:{Action:"install",State:"running"}}&&status.LastRequest.Id==id,"activating");
            foreach(var again in new UpdaterRequest[]{new(1,"install",Id(),"9.9.9",fingerprint),new(1,"check",Id())})Assert(engine.Handle(again) is {Ok:false,Code:"BUSY"},"a second "+again.Op+" while activating");
            Assert(bed.Launched.Count==1,"launched twice");
        });
        await check("updater: a staged release is believed only from its bytes each time",async()=>
        {
            using var bed=new Bed();bed.Good();var engine=bed.Engine();var target=(await Bed.CheckAsync(engine)).InstallTarget!;
            // Another engine, as after a restart, finds it with no record of having staged it.
            Assert(Bed.Status(bed.Engine()).InstallTarget==target,"not rediscovered");
            var image=Path.Combine(bed.Paths.Stage("9.9.9"),"Yahad.Setup-9.9.9.exe");var bytes=File.ReadAllBytes(image);
            File.WriteAllBytes(image,[9,9,9,9]);
            Assert(engine.Handle(new(1,"install",Id(),target.Version,target.Fingerprint)) is {Ok:false,Code:"STALE_TARGET"},"a tampered Setup was accepted");
            Assert(Bed.Status(bed.Engine()).InstallTarget is null&&bed.Launched.Count==0,"a tampered Setup was offered");
            File.WriteAllBytes(image,bytes);File.WriteAllBytes(Path.Combine(bed.Paths.Stage("9.9.9"),"yahad-windows-9.9.9.json.sig"),new byte[64]);
            Assert(Bed.Status(bed.Engine()).InstallTarget is null,"an unsigned stage was offered");
            // Copied here under an older installed version's name: signed for another version.
            using var moved=new Bed();moved.Good();await Bed.CheckAsync(moved.Engine());Directory.Move(moved.Paths.Stage("9.9.9"),moved.Paths.Stage("9.9.10"));
            Assert(Bed.Status(moved.Engine()).InstallTarget is null,"a stage under another version's name was offered");
        });
        await check("updater: the launched Setup's result is read by whichever Updater is running",async()=>
        {
            foreach(var (code,phase,error,succeeded,refused) in new[]{("UPGRADED","healthy",(string?)null,true,false),("ROLLED_BACK_UNHEALTHY","rolled_back","health",false,true),("RELEASE_UNSUPPORTED_FROM","incompatible","incompatible",false,true),
                ("RELEASE_SIGNATURE_INVALID","failed","signature",false,true),("ROLLBACK_FAILED","available","rollback",false,false),("DISK_SPACE","available","storage",false,false),("SOMETHING_NEW","available","internal",false,false)})
            {
                using var bed=new Bed();bed.Good();var engine=bed.Engine();var target=(await Bed.CheckAsync(engine)).InstallTarget!;
                Assert(engine.Handle(new(1,"install",Id(),target.Version,target.Fingerprint)).Ok,"install refused");
                Assert(Bed.Status(engine).Phase=="activating","no result yet");
                File.WriteAllText(bed.Paths.LastUpgrade,"""{"Code":""");Assert(Bed.Status(engine).Phase=="activating",code+" half-written result");
                File.WriteAllText(bed.Paths.LastUpgrade,JsonSerializer.Serialize(new{Code=code,Ok=succeeded}));
                // Success means a new Updater reads it; any other ending, the old one again.
                var next=bed.Engine(succeeded?"9.9.9":"1.0.0");var status=Bed.Status(next);
                Assert(status.Phase==phase&&status.Error==error&&status.LastRequest is {Action:"install"} last&&last.State==(succeeded?"succeeded":"failed")&&last.Error==error,code+" reads "+status.Phase+"/"+status.Error);
                Assert((status.InstallTarget is null)==(succeeded||refused),code+" offer");
                Assert(Directory.Exists(bed.Paths.Stage("9.9.9"))!=(succeeded||refused),code+" stage");
                Assert(bed.Launched.Count==1,code+" launched again");
                if(refused)
                {
                    // A refused release is never fetched again, not even by a later check.
                    bed.Network.Requests.Clear();status=await Bed.CheckAsync(next);
                    Assert(status.InstallTarget is null&&bed.Network.Requests is [GitHubReleaseSource.Latest],code+" fetched again");
                }
            }
        });
        await check("updater: a Setup that never reports ends the install after the timeout; one that cannot start fails at once",async()=>
        {
            using var bed=new Bed();bed.Good();var engine=bed.Engine();var target=(await Bed.CheckAsync(engine)).InstallTarget!;
            Assert(engine.Handle(new(1,"install",Id(),target.Version,target.Fingerprint)).Ok,"install refused");
            bed.Now=bed.Now.Add(UpdaterRules.ResultTimeout-TimeSpan.FromMinutes(1));Assert(Bed.Status(engine).Phase=="activating","gave up early");
            bed.Now=bed.Now.AddMinutes(2);var status=Bed.Status(engine);
            Assert(status is {Error:"internal",LastRequest:{State:"failed",Error:"internal"}}&&status.Phase=="available","did not give up: "+status.Phase);
            bed.LaunchFailure=new Win32Exception(5);
            Assert(engine.Handle(new(1,"install",Id(),target.Version,target.Fingerprint)) is {Ok:false,Code:"INTERNAL"},"a failed launch was accepted");
            status=Bed.Status(engine);Assert(status is {Error:"internal",LastRequest:{State:"failed"}}&&status.Phase=="available","failed launch left "+status.Phase);
        });
        await check("updater: state survives a restart, a corrupt record falls back to notify, and an open check closes",async()=>
        {
            using var bed=new Bed();bed.Good();var engine=bed.Engine();var id=Id();
            Assert(Bed.Status(engine).Mode=="notify","default mode");
            Assert(engine.Handle(new(1,"set-mode",id,Mode:"auto")).Ok,"mode refused");
            var status=Bed.Status(bed.Engine());Assert(status.Mode=="auto"&&status.LastModeRequest==new UpdaterLastMode(id,"auto","applied"),"mode lost");
            // What Setup seeds is what the Updater reads.
            File.WriteAllBytes(bed.Paths.State,UpdaterRecord.Seed(true).Encode());Assert(Bed.Status(bed.Engine()).Mode=="auto","seeded mode");
            File.WriteAllBytes(bed.Paths.State,UpdaterRecord.Seed(false).Encode());Assert(Bed.Status(bed.Engine()).Mode=="notify","seeded notify");
            File.WriteAllText(bed.Paths.State,"{corrupt");Assert(Bed.Status(bed.Engine()).Mode=="notify","corrupt record");
            File.WriteAllBytes(bed.Paths.State,(UpdaterRecord.Seed(true) with{LastRequest=new(id,"check","running",1,null,null,null)}).Encode());
            status=Bed.Status(bed.Engine());Assert(status.LastRequest is {State:"failed",Error:"internal"}&&status.Mode=="auto","an open check stayed open");
            File.WriteAllText(Path.Combine(bed.Paths.Root,"state.json.partial-1"),"x");bed.Engine();Assert(!File.Exists(Path.Combine(bed.Paths.Root,"state.json.partial-1")),"a partial stayed");
        });
        await check("updater: the schedule checks daily after a delay and installs unattended only in auto mode at night",async()=>
        {
            using var bed=new Bed();bed.Good();var engine=bed.Engine();
            bed.Now=bed.Now.AddMinutes(5);engine.Tick();Assert(bed.Network.Requests.Count==0,"checked before the first delay");
            bed.Now=bed.Now.AddMinutes(6);engine.Tick();await engine.Work;Assert(bed.Asked(GitHubReleaseSource.Latest)&&Bed.Status(engine).Phase=="available","the first check did not run");
            var night=new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(3.5));bed.Network.Requests.Clear();
            bed.Now=night;engine.Tick();Assert(bed.Launched.Count==0,"notify mode installed by itself");
            Assert(engine.Handle(new(1,"set-mode",Id(),Mode:"auto")).Ok,"mode refused");
            bed.Now=night.AddHours(8);engine.Tick();Assert(bed.Launched.Count==0,"installed outside the quiet hours");
            bed.Now=night;engine.Tick();Assert(bed.Launched.Count==1&&bed.Network.Requests.Count==0,"auto mode did not install at night");
            engine.Tick();Assert(bed.Launched.Count==1,"installed twice");
            var at=new DateTimeOffset(2026,1,1,3,0,0,TimeSpan.Zero);UpdaterLastRequest Last(string action,DateTimeOffset when)=>new(Id(),action,"failed",when.ToUnixTimeMilliseconds(),null,null,null);
            Assert(UpdaterRules.AutoInstallDue(at,null)&&UpdaterRules.AutoInstallDue(at,Last("check",at))&&!UpdaterRules.AutoInstallDue(at.AddHours(2),null)&&!UpdaterRules.AutoInstallDue(at.AddHours(-1),null),"quiet hours");
            Assert(!UpdaterRules.AutoInstallDue(at,Last("install",at.AddHours(-23)))&&UpdaterRules.AutoInstallDue(at,Last("install",at.AddHours(-24))),"one attempt a day");
            Assert(!UpdaterRules.CheckDue(at,at,null,default)&&UpdaterRules.CheckDue(at.AddMinutes(10),at,null,default),"first check");
            var checkedAt=at.AddMinutes(10).ToUnixTimeMilliseconds();
            Assert(!UpdaterRules.CheckDue(at.AddHours(24),at,checkedAt,default)&&UpdaterRules.CheckDue(at.AddHours(25),at,checkedAt,default)&&!UpdaterRules.CheckDue(at.AddHours(25),at,checkedAt,TimeSpan.FromHours(1)),"daily with jitter");
        });
        await check("updater: failures reach the parent as the words src/updates.ts accepts",()=>
        {
            foreach(var (error,expected) in new (Exception,string)[]{(new HttpRequestException(),"network"),(new TaskCanceledException(),"network"),(new IOException("GITHUB_URL"),"network"),(new IOException("GITHUB_LIST_INVALID"),"network"),(new IOException("RELEASE_SIGNATURE_INVALID"),"signature"),(new IOException("RELEASE_KEY_UNTRUSTED"),"signature"),
                (new IOException("RELEASE_UNSUPPORTED_FROM"),"incompatible"),(new IOException("RELEASE_SETUP_HASH"),"archive"),(new IOException("RELEASE_SETUP_SIZE"),"archive"),(new IOException("DISK_SPACE"),"storage"),(new IOException("other"),"internal"),(new InvalidOperationException(),"internal")})
                Assert(UpdaterRules.CheckError(error)==expected,error.Message+" maps to "+UpdaterRules.CheckError(error));
            return Task.CompletedTask;
        });
        await check("updater: the pipe answers only the allowed account, and a bad client costs only its connection",async()=>
        {
            var me=WindowsIdentity.GetCurrent().User!;var descriptor=$"D:P(A;;GA;;;{me.Value})";var handled=0;
            UpdaterReply Handle(UpdaterRequest request){Interlocked.Increment(ref handled);return new(1,true,Status:SampleStatus());}
            var name="yahad-updater-test-"+Guid.NewGuid().ToString("N");using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var end=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);var server=new UpdaterPipe(name,me,descriptor,Handle).RunAsync(end.Token);
            uint seen=0;var reply=await UpdaterClient.AskAsync(name,process=>seen=process,new(1,"status"),stop.Token);
            Assert(reply.Ok&&reply.Status==SampleStatus() with{LastRequest=reply.Status!.LastRequest,LastModeRequest=reply.Status.LastModeRequest,InstallTarget=reply.Status.InstallTarget}&&seen==(uint)Environment.ProcessId,"round trip");
            // The client refuses a server it cannot verify before sending a byte.
            var before=handled;
            await DenyAsync("UPDATER_SERVER_IDENTITY",()=>UpdaterClient.AskAsync(name,_=>throw new IOException("UPDATER_SERVER_IDENTITY"),new(1,"check",Id()),stop.Token));
            // Oversized and garbage frames: the server drops them and stays up.
            foreach(var frame in new byte[][]{BitConverter.GetBytes(int.MaxValue),[..BitConverter.GetBytes(3),1,2,3]})
            {await using var raw=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);await raw.ConnectAsync(3000,stop.Token);await raw.WriteAsync(frame,stop.Token);await raw.FlushAsync(stop.Token);try{Assert(await raw.ReadAsync(new byte[1],stop.Token)==0,"a bad frame was answered");}catch(IOException){}}
            Assert(handled==before,"a refused or malformed request reached the engine");
            Assert((await UpdaterClient.AskAsync(name,_=>{},new(1,"status"),stop.Token)).Ok,"the server did not survive");
            await end.CancelAsync();try{await server;}catch(OperationCanceledException){}
            // Admitted by the DACL but not the account the service expects: no reply.
            var strict="yahad-updater-test-"+Guid.NewGuid().ToString("N");handled=0;using var over=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var guarded=new UpdaterPipe(strict,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),descriptor,Handle).RunAsync(over.Token);
            try{await UpdaterClient.AskAsync(strict,_=>{},new(1,"status"),stop.Token);throw new Exception("PEER_NOT_REFUSED");}catch(IOException){}
            Assert(handled==0,"an unexpected account reached the engine");
            await over.CancelAsync();try{await guarded;}catch(OperationCanceledException){}
        });
        await check("platform: replies carry exactly what the app's status schema reads, and a refusal for anything else",async()=>
        {
            UpdaterOperator Owner()=>new("family","user");
            var input=Encoding.UTF8.GetBytes("""{"kind":"status"}""");
            var status=await UpdaterClient.HandleAsync(input,Owner,(request,_)=>{Assert(request==new UpdaterRequest(1,"status"),"status request");return Task.FromResult(new UpdaterReply(1,true,Status:SampleStatus()));},default);
            Assert(status["schema"]!.GetValue<int>()==1&&status["ok"]!.GetValue<bool>(),"status reply");var result=status["result"]!.AsObject();
            Assert(result.Select(p=>p.Key).OrderBy(k=>k,StringComparer.Ordinal).SequenceEqual(new[]{"checkedAt","currentVersion","enabled","error","installTarget","lastModeRequest","lastRequest","latestVersion","mode","modeControlAllowed","operator","phase","updatedAt"}),"status fields "+string.Join(",",result.Select(p=>p.Key)));
            Assert(result["operator"]!["familyId"]!.GetValue<string>()=="family"&&result["operator"]!["userId"]!.GetValue<string>()=="user"&&result["modeControlAllowed"]!.GetValue<bool>(),"operator");
            Assert(result["lastModeRequest"]!.AsObject().ContainsKey("error")&&result["lastRequest"]!.AsObject().ContainsKey("finishedAt")&&result["installTarget"]!["fingerprint"]!.GetValue<string>().Length==64,"nested fields");
            static bool Refused(JsonObject value)=>value["ok"]!.GetValue<bool>()==false&&value["code"]!.GetValue<string>()=="UPDATES_UNAVAILABLE";
            var never=(Func<UpdaterRequest,CancellationToken,Task<UpdaterReply>>)((_,_)=>throw new Exception("ASKED"));
            string Submit(object command)=>JsonSerializer.Serialize(new{kind="submit",command});
            var id=Id();
            foreach(var bad in new[]{"","[]","{}","""{"kind":"other"}""",Submit(new{id,action="check",familyId="family",userId="other"}),Submit(new{id,action="check",familyId="other",userId="user"}),Submit(new{id,action="restart",familyId="family",userId="user"}),Submit(new{action="check",familyId="family",userId="user"})})
            {
                var asked=false;var value=await UpdaterClient.HandleAsync(Encoding.UTF8.GetBytes(bad),Owner,(_,_)=>{asked=true;return Task.FromResult(new UpdaterReply(1,true));},default);
                Assert(Refused(value)&&(bad.Contains("restart")||!asked),"accepted "+bad);
            }
            Assert(Refused(await UpdaterClient.HandleAsync(input,()=>throw new IOException("NO_CAPABILITY"),never,default)),"status without an owner");
            foreach(var failure in new Exception[]{new IOException("x"),new TimeoutException(),new Win32Exception(5),new UnauthorizedAccessException(),new OperationCanceledException()})
                Assert(Refused(await UpdaterClient.HandleAsync(input,Owner,(_,_)=>throw failure,default)),failure.GetType().Name);
            Assert(Refused(await UpdaterClient.HandleAsync(input,Owner,(_,_)=>Task.FromResult(new UpdaterReply(1,false,"NOT_AVAILABLE")),default)),"refused status");
            var fingerprint=new string('c',64);
            var commands=new (string Text,UpdaterRequest Expected)[]{(Submit(new{id,action="check",familyId="family",userId="user"}),new(1,"check",id)),(Submit(new{id,action="set_mode",mode="auto",familyId="family",userId="user"}),new(1,"set-mode",id,Mode:"auto")),
                (Submit(new{id,action="install",target=new{version="9.9.9",fingerprint},familyId="family",userId="user"}),new(1,"install",id,"9.9.9",fingerprint))};
            foreach(var (text,expected) in commands)
                foreach(var (answer,state) in new (UpdaterReply,string?)[]{(new(1,true),"queued"),(new(1,false,"BUSY"),"pending"),(new(1,false,"STALE_TARGET"),"target_changed"),(new(1,false,"NOT_AVAILABLE"),null),(new(1,false,"INTERNAL"),null)})
                {
                    UpdaterRequest? sent=null;var value=await UpdaterClient.HandleAsync(Encoding.UTF8.GetBytes(text),Owner,(request,_)=>{sent=request;return Task.FromResult(answer);},default);
                    Assert(sent==expected,"sent "+sent);Assert(state is null?Refused(value):value["result"]!["state"]!.GetValue<string>()==state,answer.Code+" answered "+value.ToJsonString());
                }
        });
        await check("platform: the pipe server must be the process the SCM runs as this release's Updater",()=>
        {
            var image=InstalledLayout.UpdaterCommand("1.2.3");const uint running=4;
            UpdaterClient.RequireServer(77,new(running,77,"LocalSystem",image),"1.2.3");UpdaterClient.RequireServer(77,new(running,77,"localsystem",image),"1.2.3");
            foreach(var (server,observed) in new (uint,UpdaterObservation)[]{(78,new(running,77,"LocalSystem",image)),(77,new(1,77,"LocalSystem",image)),(0,new(running,0,"LocalSystem",image)),(77,new(running,77,"NT SERVICE\\Yahad",image)),(77,new(running,77,"LocalSystem",InstalledLayout.UpdaterCommand("1.2.4"))),(77,new(running,77,"LocalSystem",image+" --extra")),(77,new(running,77,"LocalSystem",""))})
                Deny("UPDATER_SERVER_IDENTITY",()=>UpdaterClient.RequireServer(server,observed,"1.2.3"));
            return Task.CompletedTask;
        });
        await check("updater: the Setup is started with its own directory and result, quoted, and nothing else",()=>
        {
            Assert(UpdateLauncher.CommandLine(@"C:\P D\Setup.exe",@"C:\P D\9.9.9",@"C:\P D\r.json")==@"""C:\P D\Setup.exe"" --quiet-upgrade ""C:\P D\9.9.9"" --result ""C:\P D\r.json""","command line");
            foreach(var bad in new[]{"C:\\a\"b","C:\\a\\"})Deny("UPDATER_LAUNCH_PATH",()=>UpdateLauncher.CommandLine(bad,"C:\\d","C:\\r"));
            Assert(QuietSetupAccepts(UpdateLauncher.CommandLine("s","C:\\d","C:\\r.json")),"Setup refuses the Updater's command line");
            return Task.CompletedTask;
        });
        await check("repair task: SYSTEM runs this version's retained Setup at boot and daily, and the Task Scheduler accepts the definition",()=>
        {
            var now=new DateTimeOffset(DateTime.Today.AddHours(20));var xml=RepairTask.Definition("1.2.3",now);XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";var task=XDocument.Parse(xml).Root!;
            Assert(task.Descendants(ns+"UserId").Single().Value=="S-1-5-18"&&task.Descendants(ns+"RunLevel").Single().Value=="HighestAvailable","principal");
            Assert(task.Descendants(ns+"BootTrigger").Count()==1&&task.Descendants(ns+"CalendarTrigger").Count()==1,"triggers");
            Assert(task.Descendants(ns+"Command").Single().Value==InstallLocations.InstallerImage("1.2.3"),"image");
            Assert(task.Descendants(ns+"Arguments").Single().Value==RepairTask.Arguments&&RepairTask.Arguments.StartsWith("--repair --result \"",StringComparison.Ordinal)&&RepairTask.Arguments.Contains(InstalledLayout.UpdaterRoot,StringComparison.Ordinal),"arguments");
            // A boundary already past would fire inside the Setup that registers it.
            Assert(DateTime.Parse(task.Descendants(ns+"StartBoundary").Single().Value,System.Globalization.CultureInfo.InvariantCulture)>now.LocalDateTime,"first run is in the past");
            dynamic scheduler=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true)!)!;scheduler.Connect();
            scheduler.GetFolder("\\").RegisterTask("\\Yahad-validate-only",xml,1,null,null,5,null);
            try{scheduler.GetFolder("\\").GetTask("Yahad-validate-only");throw new Exception("VALIDATION_REGISTERED_A_TASK");}catch(Exception error)when(error.HResult==unchecked((int)0x80070002)){}
            return Task.CompletedTask;
        });
        await check("updater service: reports start, running and stop to the SCM, and a failure so its restart actions run",async()=>
        {
            var scm=new FakeScm();
            var code=new UpdaterService(scm,async(ready,stop)=>{ready();Assert(scm.Handler!(ServiceDefinition.Interrogate)==0&&scm.Handler(99)==120,"controls");Assert(scm.Handler(ServiceDefinition.Stop)==0&&scm.Handler(ServiceDefinition.Shutdown)==0,"stop");await Task.Delay(-1,stop);}).Run();
            Assert(code==0&&scm.Name==ServiceDefinition.UpdaterName,"dispatch");
            Assert(scm.Reports.Select(r=>r.State).SequenceEqual([ServiceState.StartPending,ServiceState.Running,ServiceState.StopPending,ServiceState.Stopped]),"states "+string.Join(",",scm.Reports.Select(r=>r.State)));
            Assert(scm.Reports[1].AcceptedControls==5&&scm.Reports[3]==new ServiceReport(ServiceState.Stopped,0,0,0,0,0),"running and clean stop");
            var failed=new FakeScm();new UpdaterService(failed,(_,_)=>throw new IOException("UPDATER_IMAGE_LOCATION")).Run();
            Assert(failed.Reports[^1]==new ServiceReport(ServiceState.Stopped,0,0,0,1066,1),"a failure must reach the SCM");
            await Task.CompletedTask;
        });
        await check("updater service: its SCM entry differs from the Runtime's only where it must",()=>
        {
            var (runtime,updater)=(ServiceSpec.Runtime,ServiceSpec.Updater);
            Assert(updater.Name=="Yahad.Acceptance.Updater"&&updater.Account=="LocalSystem"&&updater.Delayed&&!updater.Restricted&&updater.FailureDelays.SequenceEqual(new uint[]{60000,300000,600000}),"updater");
            Assert(runtime.Name==ServiceDefinition.Name&&runtime.Account==ServiceDefinition.Account&&!runtime.Delayed&&runtime.Restricted&&runtime.FailureDelays.SequenceEqual(new uint[]{5000,30000,120000}),"runtime");
            Assert(updater.Command("1.2.3")==InstalledLayout.UpdaterCommand("1.2.3")&&updater.Command("1.2.3").Contains(@"\versions\1.2.3\updater\Yahad.Updater.exe"" --service",StringComparison.Ordinal),"image "+updater.Command("1.2.3"));
            try{updater.Command("..\\x");throw new Exception("VERSION_ACCEPTED");}catch(Exception error)when(error is IOException or ArgumentException){}
            return Task.CompletedTask;
        });
    }
    // The Updater and Setup agree on the shape of --quiet-upgrade.
    private static bool QuietSetupAccepts(string line)
    {
        var tokens=System.Text.RegularExpressions.Regex.Matches(line,"\"([^\"]*)\"|(\\S+)").Select(m=>m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Value).Skip(1).ToArray();
        return QuietSetup.Parse(tokens)?.UpgradeDirectory=="C:\\d";
    }
}

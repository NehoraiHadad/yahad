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
        internal readonly Feed Network=new();internal readonly GitHubReleaseSource Source;
        internal readonly UpdaterPaths Paths;
        internal readonly List<(UpdateLaunch Launch,string Result)> Launched=[];
        internal DateTimeOffset Now=new(DateTime.Today.AddHours(12));
        internal Exception? LaunchFailure;
        // The live active.json every decision reads: change it and the next decision sees it.
        internal ActivePointer Pointer=Installed("1.0.0");
        internal byte[] SetupBytes=[1,2,3,4],AppBytes=[5,6,7,8,9];
        internal byte[] Spki=>signing.ExportSubjectPublicKeyInfo();
        internal Bed()
        {
            var root=Path.Combine(Path.GetTempPath(),"yahad-updater-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            Paths=new(root);Source=new(Network);
        }
        internal static readonly string InfraTree=new('c',64);
        internal static ActivePointer Installed(string app,string infra="1.0.0",string? tree=null)=>new(1,new(infra,1,1,tree??InfraTree),new(app,1,new string('a',64)));
        // A restarted Updater; given an app version, the pair it finds installed.
        internal UpdaterEngine Engine(string? current=null,bool key=true)
        {
            if(current is not null)Pointer=Installed(current,Pointer.Infrastructure.Version,Pointer.Infrastructure.TreeSha256);
            var engine=new UpdaterEngine(Paths,()=>Pointer,()=>key?Spki:null,key?Spki:null,Source,(launch,result)=>{if(LaunchFailure is not null)throw LaunchFailure;Launched.Add((launch,result));},()=>Now);
            engine.Start();return engine;
        }
        internal byte[] Sign(byte[] manifest,string prefix=ReleaseSignature.Prefix)=>signing.SignData((byte[])[..Encoding.ASCII.GetBytes(prefix),..manifest],HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        // A format-2 release naming both payloads. The Setup track is a newer
        // infrastructure; the app track the installed one, tree and range included.
        internal JsonObject Fields(string version,ReleaseTrack track,string minimum="0.0.0",int requires=1,string? tree=null)=>new()
        {
            ["format"]=2,["product"]="yahad-windows-release",["version"]=version,["sourceCommit"]=new string('a',40),["keyId"]=TrustedReleaseKey.Id(Spki),["createdUnix"]=1700000000,
            ["infrastructure"]=new JsonObject{["version"]=track==ReleaseTrack.Setup?"2.0.0":Pointer.Infrastructure.Version,["contractMin"]=1,["contractMax"]=1,["sourceCommit"]=new string('b',40),["treeSha256"]=tree??Pointer.Infrastructure.TreeSha256,
                ["setup"]=new JsonObject{["name"]=ReleaseManifest.SetupName(version),["sha256"]=Hash(SetupBytes),["bytes"]=SetupBytes.Length}},
            ["app"]=new JsonObject{["archive"]=new JsonObject{["name"]=ReleaseManifest.AppArchiveName(version),["sha256"]=Hash(AppBytes),["bytes"]=AppBytes.Length},
                ["treeSha256"]=new string('d',64),["requiresContract"]=requires,["databaseSchema"]=7,["databaseMigrationsFrom"]=new JsonArray(6)},
            ["minimumFrom"]=new JsonObject{["app"]=minimum,["infrastructure"]="0.0.0"}
        };
        internal static byte[] Encode(JsonObject value)=>Encoding.UTF8.GetBytes(value.ToJsonString());
        internal static string ManifestUrl(string version)=>Repo+"v"+version+"/"+ReleaseManifest.ManifestName(version);
        internal static string SetupUrl(string version)=>Repo+"v"+version+"/"+ReleaseManifest.SetupName(version);
        internal static string AppUrl(string version)=>Repo+"v"+version+"/"+ReleaseManifest.AppArchiveName(version);
        internal static string PayloadUrl(string version,ReleaseTrack track)=>track==ReleaseTrack.Setup?SetupUrl(version):AppUrl(version);
        internal static string PayloadName(string version,ReleaseTrack track)=>track==ReleaseTrack.Setup?ReleaseManifest.SetupName(version):ReleaseManifest.AppArchiveName(version);
        // Publishes what GitHub's list would say and what its assets would answer: the
        // release's manifest, signature and both payloads, as a real release carries them.
        internal void List(string tag,bool draft=false,bool prerelease=false,long? manifestSize=null,long? signatureSize=null,long? setupSize=null,long? appSize=null,byte[]? manifest=null,byte[]? signature=null,string? setupDigest=null,string? appDigest=null)
        {
            var version=tag.TrimStart('v');var assets=new JsonArray();
            void Asset(string name,long size,string url,string? sum=null)=>assets.Add(new JsonObject{["name"]=name,["size"]=size,["browser_download_url"]=url,["digest"]=sum,["uploader"]=new JsonObject{["login"]="x"}});
            if(manifest is not null&&signature is not null)
            {
                Asset(ReleaseManifest.ManifestName(version),manifestSize??manifest.Length,ManifestUrl(version));Asset(ReleaseManifest.SignatureName(version),signatureSize??signature.Length,ManifestUrl(version)+".sig");
                Asset(ReleaseManifest.SetupName(version),setupSize??SetupBytes.Length,SetupUrl(version),setupDigest);Asset(ReleaseManifest.AppArchiveName(version),appSize??AppBytes.Length,AppUrl(version),appDigest);
                Network.Bytes(ManifestUrl(version),manifest);Network.Bytes(ManifestUrl(version)+".sig",signature);Network.Bytes(SetupUrl(version),SetupBytes);Network.Bytes(AppUrl(version),AppBytes);
            }
            Network.Bytes(GitHubReleaseSource.Latest,Encode(new JsonObject{["tag_name"]=tag,["draft"]=draft,["prerelease"]=prerelease,["assets"]=assets,["body"]="notes"}));
        }
        internal byte[] Good(string version="9.9.9",ReleaseTrack track=ReleaseTrack.App)
        {
            var manifest=Encode(Fields(version,track));List("v"+version,manifest:manifest,signature:Sign(manifest));return manifest;
        }
        internal static async Task<UpdaterStatus> CheckAsync(UpdaterEngine engine)
        {
            var reply=engine.Handle(new(1,"check",Id()));if(!reply.Ok)throw new Exception("CHECK_REFUSED "+reply.Code);
            await engine.Work;return Status(engine);
        }
        internal static UpdaterStatus Status(UpdaterEngine engine)=>engine.Handle(new(1,"status")).Status!;
        internal bool Asked(string url){lock(Network.Requests)return Network.Requests.Contains(url);}
        internal bool AskedPayload(string version="9.9.9")=>Asked(SetupUrl(version))||Asked(AppUrl(version));
        internal string[] Staged(string version="9.9.9")=>Directory.Exists(Paths.Stage(version))?Directory.GetFileSystemEntries(Paths.Stage(version)).Select(name=>Path.GetFileName(name)!).Order(StringComparer.Ordinal).ToArray():[];
        internal static string[] Expected(string version,ReleaseTrack track)=>new[]{ReleaseManifest.ManifestName(version),ReleaseManifest.SignatureName(version),PayloadName(version,track)}.Order(StringComparer.Ordinal).ToArray();
        public void Dispose(){Source.Dispose();signing.Dispose();Directory.Delete(Paths.Root,true);}
    }
    private sealed class FakeScm:IScmApi
    {
        internal readonly List<ServiceReport> Reports=[];internal Func<uint,uint>? Handler;internal string? Name;
        public void Dispatch(string name,Action<string[]> main){Name=name;main([]);}
        public void Register(string name,Func<uint,uint> handler)=>Handler=handler;
        public void Publish(ServiceReport report)=>Reports.Add(report);
    }
    private static UpdaterStatus SampleStatus()=>new(true,"notify","available","1.0.0","9.9.9",new("9.9.9",new string('f',64)),5,6,null,new(Id(),"install","succeeded",1,2,null,new("9.9.9",new string('f',64))),new(Id(),"auto","applied"));

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
                await DenyAsync("UPDATER_FRAME",()=>UpdaterProtocol.ReadAsync(new MemoryStream(Frame(length)),UpdaterJsonContext.Default.UpdaterRequest,default));
            foreach(var body in new[]{"not json","""{"v":1,"op":"status","extra":1}""","""{"v":1,"op":"status","op":"check"}""","null"})
            {var bytes=Encoding.UTF8.GetBytes(body);await DenyAsync("UPDATER_FRAME",()=>UpdaterProtocol.ReadAsync(new MemoryStream(Frame(bytes.Length,bytes)),UpdaterJsonContext.Default.UpdaterRequest,default));}
            try{await UpdaterProtocol.ReadAsync(new MemoryStream(Frame(10,[1,2])),UpdaterJsonContext.Default.UpdaterRequest,default);throw new Exception("SHORT_FRAME_ACCEPTED");}catch(EndOfStreamException){}
            await DenyAsync("UPDATER_FRAME",()=>UpdaterProtocol.WriteAsync(new MemoryStream(),new UpdaterRequest(1,new string('x',UpdaterProtocol.MaximumFrame)),UpdaterJsonContext.Default.UpdaterRequest,default));
            var round=new MemoryStream();await UpdaterProtocol.WriteAsync(round,new UpdaterRequest(1,"status"),UpdaterJsonContext.Default.UpdaterRequest,default);round.Position=0;
            Assert((await UpdaterProtocol.ReadAsync(round,UpdaterJsonContext.Default.UpdaterRequest,default)).Op=="status","round trip");
            // The deepest reply there is: what every status says after the first install.
            var fullest=new UpdaterReply(1,true,Status:SampleStatus());var deep=new MemoryStream();await UpdaterProtocol.WriteAsync(deep,fullest,UpdaterJsonContext.Default.UpdaterReply,default);deep.Position=0;
            var deepReply=await UpdaterProtocol.ReadAsync(deep,UpdaterJsonContext.Default.UpdaterReply,default);
            Assert(deepReply.Status?.LastRequest?.Target==fullest.Status!.LastRequest!.Target&&deepReply.Status!.InstallTarget==fullest.Status.InstallTarget,"the deepest reply");
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
            using var other=new Bed();var foreign=new UpdaterEngine(bed.Paths,()=>bed.Pointer,()=>bed.Spki,other.Spki,bed.Source,(_,_)=>throw new Exception("LAUNCHED"),()=>bed.Now);foreign.Start();
            Assert(!Bed.Status(foreign).Enabled,"a mismatched key enabled updates");
        });
        await check("updater: a release that is not newer, not final or without the format-2 manifest fetches nothing",async()=>
        {
            foreach(var (tag,draft,prerelease) in new[]{("v1.0.0",false,false),("v0.5.3",false,false),("v9.9.9",true,false),("v9.9.9",false,true),("nightly",false,false),("v9.9.9",false,false)})
            {
                using var bed=new Bed();bed.List(tag,draft,prerelease);var engine=bed.Engine();var status=await Bed.CheckAsync(engine);
                Assert(status.Error is null&&status.Phase=="idle"&&status.LatestVersion is null&&status.LastRequest is {State:"succeeded"}&&status.CheckedAt is not null,tag+" status");
                Assert(bed.Network.Requests is [GitHubReleaseSource.Latest],tag+" fetched more than the list");
            }
            // Today's format-1 names, or only half of the format-2 pair, are not a release for this machine.
            foreach(var names in new[]{new[]{"yahad-windows-9.9.9.json","yahad-windows-9.9.9.json.sig","Yahad.Setup-9.9.9.exe"},[ReleaseManifest.ManifestName("9.9.9"),ReleaseManifest.AppArchiveName("9.9.9")],[ReleaseManifest.SignatureName("9.9.9"),ReleaseManifest.SetupName("9.9.9")]})
            {
                using var bed=new Bed();var assets=new JsonArray();foreach(var name in names)assets.Add(new JsonObject{["name"]=name,["size"]=64,["browser_download_url"]=Repo+"v9.9.9/"+name});
                bed.Network.Bytes(GitHubReleaseSource.Latest,Bed.Encode(new JsonObject{["tag_name"]="v9.9.9",["draft"]=false,["prerelease"]=false,["assets"]=assets}));
                var status=await Bed.CheckAsync(bed.Engine());
                Assert(status is {Error:null,Phase:"idle",LatestVersion:null}&&bed.Network.Requests is [GitHubReleaseSource.Latest],names[0]+" was read as a release");
            }
        });
        await check("updater: the manifest's signature is checked over raw bytes before it is parsed, and no payload is fetched before that",async()=>
        {
            // Refused for good: a later check reads only the list. Anything else is asked again.
            async Task Refused(Action<Bed> arrange,string error,bool refused=true)
            {
                using var bed=new Bed();arrange(bed);var engine=bed.Engine();var status=await Bed.CheckAsync(engine);
                Assert(status.Error==error&&status.Phase==(error=="incompatible"?"incompatible":"failed")&&status.InstallTarget is null&&status.LastRequest is {State:"failed"},"check ended "+status.Error+"/"+status.Phase);
                Assert(!bed.AskedPayload()&&!Directory.Exists(bed.Paths.Staging),"a payload was fetched before verification");
                bed.Network.Requests.Clear();status=await Bed.CheckAsync(engine);
                Assert((bed.Network.Requests is [GitHubReleaseSource.Latest])==refused&&status.InstallTarget is null,error+(refused?" was asked again":" was refused for good"));
            }
            byte[] Signed(Bed bed,JsonObject fields,out byte[] signature){var manifest=Bed.Encode(fields);signature=bed.Sign(manifest);return manifest;}
            // Signed by someone else, and not even JSON: refused as a signature, unparsed.
            await Refused(bed=>{using var other=new Bed();var manifest=Bed.Encode(bed.Fields("9.9.9",ReleaseTrack.App));bed.List("v9.9.9",manifest:manifest,signature:other.Sign(manifest));},"signature");
            await Refused(bed=>{using var other=new Bed();var manifest=Encoding.UTF8.GetBytes("{{{ not json");bed.List("v9.9.9",manifest:manifest,signature:other.Sign(manifest));},"signature");
            await Refused(bed=>{var manifest=Signed(bed,bed.Fields("9.9.9",ReleaseTrack.App),out var signature);manifest[10]^=1;bed.List("v9.9.9",manifest:manifest,signature:signature);},"signature");
            // The v1 message over the same bytes is not a v2 signature.
            await Refused(bed=>{var manifest=Bed.Encode(bed.Fields("9.9.9",ReleaseTrack.App));bed.List("v9.9.9",manifest:manifest,signature:bed.Sign(manifest,"yahad-release-v1\n"));},"signature");
            // Rightly signed, but not an acceptable release for this tag or this machine.
            await Refused(bed=>{var manifest=Encoding.UTF8.GetBytes("{{{ not json");bed.List("v9.9.9",manifest:manifest,signature:bed.Sign(manifest));},"signature");
            await Refused(bed=>{var fields=bed.Fields("9.9.9",ReleaseTrack.App);fields["format"]=1;var manifest=Signed(bed,fields,out var signature);bed.List("v9.9.9",manifest:manifest,signature:signature);},"signature");
            await Refused(bed=>{var manifest=Signed(bed,bed.Fields("9.9.8",ReleaseTrack.App),out var signature);bed.List("v9.9.9",manifest:manifest,signature:signature);},"signature");
            await Refused(bed=>{var manifest=Signed(bed,bed.Fields("9.9.9",ReleaseTrack.App,minimum:"5.0.0"),out var signature);bed.List("v9.9.9",manifest:manifest,signature:signature);},"incompatible");
            await Refused(bed=>{var manifest=Signed(bed,bed.Fields("9.9.9",ReleaseTrack.Setup,minimum:"5.0.0"),out var signature);bed.List("v9.9.9",manifest:manifest,signature:signature);},"incompatible");
            // An app its own infrastructure cannot run is a defective release, on either track.
            foreach(var track in new[]{ReleaseTrack.App,ReleaseTrack.Setup})
                await Refused(bed=>{var manifest=Signed(bed,bed.Fields("9.9.9",track,requires:2),out var signature);bed.List("v9.9.9",manifest:manifest,signature:signature);},"incompatible");
            // Same infrastructure version, other bytes: internal, and asked again.
            await Refused(bed=>{var manifest=Signed(bed,bed.Fields("9.9.9",ReleaseTrack.App,tree:new string('e',64)),out var signature);bed.List("v9.9.9",manifest:manifest,signature:signature);},"internal",refused:false);
            // Declared sizes that cannot be a release are not even fetched.
            using var big=new Bed();var manifest=big.Good();big.List("v9.9.9",manifestSize:ReleaseManifest.MaximumBytes+1,manifest:manifest,signature:big.Sign(manifest));
            Assert((await Bed.CheckAsync(big.Engine())).Error=="signature"&&big.Network.Requests is [GitHubReleaseSource.Latest],"an oversized manifest was fetched");
            using var longer=new Bed();manifest=longer.Good();longer.List("v9.9.9",signatureSize:65,manifest:manifest,signature:longer.Sign(manifest));
            Assert((await Bed.CheckAsync(longer.Engine())).Error=="signature"&&longer.Network.Requests is [GitHubReleaseSource.Latest],"an oversized signature was fetched");
        });
        await check("updater: each track fetches only its own payload, against GitHub's size and digest, and retries a mismatch",async()=>
        {
            foreach(var track in new[]{ReleaseTrack.App,ReleaseTrack.Setup})
            {
                var setup=track==ReleaseTrack.Setup;
                foreach(var (size,digest) in new[]{(1L,(string?)null),(0L,"sha256:"+new string('0',64))})
                {
                    using var bed=new Bed();var manifest=Bed.Encode(bed.Fields("9.9.9",track));var payload=setup?bed.SetupBytes:bed.AppBytes;
                    bed.List("v9.9.9",manifest:manifest,signature:bed.Sign(manifest),setupSize:setup?payload.Length+size:null,appSize:setup?null:payload.Length+size,setupDigest:setup?digest:null,appDigest:setup?null:digest);
                    var engine=bed.Engine();var status=await Bed.CheckAsync(engine);
                    Assert(status is {Error:"archive",Phase:"failed",InstallTarget:null}&&!bed.AskedPayload()&&!Directory.Exists(bed.Paths.Staging),track+" listed "+size+"/"+digest+" ended "+status.Error);
                    bed.Network.Requests.Clear();await Bed.CheckAsync(engine);Assert(bed.Asked(Bed.ManifestUrl("9.9.9")),track+" an archive mismatch refused the version");
                }
                // The other track's payload, listed wrong, is never looked at.
                using var other=new Bed();var good=Bed.Encode(other.Fields("9.9.9",track));
                other.List("v9.9.9",manifest:good,signature:other.Sign(good),setupSize:setup?null:1,appSize:setup?1:null,setupDigest:setup?"sha256:"+Hash(other.SetupBytes):"sha256:"+new string('0',64),appDigest:setup?"sha256:"+new string('0',64):"sha256:"+Hash(other.AppBytes));
                var offered=await Bed.CheckAsync(other.Engine());
                Assert(offered.Phase=="available"&&other.Asked(Bed.PayloadUrl("9.9.9",track))&&!other.Asked(Bed.PayloadUrl("9.9.9",setup?ReleaseTrack.App:ReleaseTrack.Setup)),track+" fetched the other payload");
            }
        });
        await check("updater: a download that differs from the signed size or hash never appears under the payload's name",async()=>
        {
            foreach(var track in new[]{ReleaseTrack.App,ReleaseTrack.Setup})
                foreach(var (served,hidden) in new (byte[],bool)[]{(new byte[]{1,2,3},false),(new byte[]{1,2,3},true),(new byte[]{1,2,3,4,5,6},false),(new byte[]{1,2,3,4,5,6},true),(new byte[]{9,9,9,9,9},false)})
                {
                    using var bed=new Bed();bed.SetupBytes=[1,2,3,4,5];var manifest=Bed.Encode(bed.Fields("9.9.9",track));bed.List("v9.9.9",manifest:manifest,signature:bed.Sign(manifest));
                    bed.Network.Routes[Bed.PayloadUrl("9.9.9",track)]=()=>new(HttpStatusCode.OK){Content=hidden?new Unsized(served):new ByteArrayContent(served)};
                    var status=await Bed.CheckAsync(bed.Engine());
                    Assert(status.Error=="archive"&&status.InstallTarget is null&&bed.Staged().Length==0,track+" served "+served.Length+" bytes, ended "+status.Error);
                }
        });
        await check("updater: a checked release is offered by its track's fingerprint, staged under exact names, and installs only as that target",async()=>
        {
            foreach(var track in new[]{ReleaseTrack.App,ReleaseTrack.Setup})
            {
                using var bed=new Bed();var manifest=Bed.Encode(bed.Fields("9.9.9",track));var fingerprint=Hash([..Encoding.ASCII.GetBytes(UpdaterRules.TrackName(track)+"\n"),..manifest]);
                bed.List("v9.9.9",manifest:manifest,signature:bed.Sign(manifest),setupDigest:"sha256:"+Hash(bed.SetupBytes),appDigest:"sha256:"+Hash(bed.AppBytes));var engine=bed.Engine();
                var status=await Bed.CheckAsync(engine);
                Assert(status is {Phase:"available",LatestVersion:"9.9.9",Error:null,CurrentVersion:"1.0.0"}&&status.InstallTarget==new UpdaterTarget("9.9.9",fingerprint),track+" offered "+status.Phase);
                Assert(bed.Staged().SequenceEqual(Bed.Expected("9.9.9",track)),track+" stage contents "+string.Join(",",bed.Staged()));
                // Refused without running anything: another version, the bare manifest hash, the other track's fingerprint.
                var otherTrack=UpdaterRules.Fingerprint(track==ReleaseTrack.App?ReleaseTrack.Setup:ReleaseTrack.App,manifest);
                foreach(var (version,print) in new[]{("9.9.9",new string('0',64)),("9.9.8",fingerprint),("9.9.9",Hash(manifest)),("9.9.9",otherTrack)})
                    Assert(engine.Handle(new(1,"install",Id(),version,print)) is {Ok:false,Code:"STALE_TARGET"},track+" another target accepted");
                foreach(var bad in new UpdaterRequest[]{new(2,"status"),new(1,"nope",Id()),new(1,"check"),new(1,"check","x"),new(1,"install",Id(),"9.9.9"),new(1,"install",Id(),"latest",fingerprint),new(1,"install",null,"9.9.9",fingerprint),new(1,"set-mode",Id(),Mode:"always"),new(1,"defer",Id())})
                    Assert(engine.Handle(bad) is {Ok:false,Code:"BAD_REQUEST"},"bad request "+bad.Op);
                Assert(bed.Launched.Count==0,"launched for a refused request");
                var id=Id();File.WriteAllText(bed.Paths.LastUpgrade,"{}");
                Assert(engine.Handle(new(1,"install",id,"9.9.9",fingerprint)).Ok,track+" install refused");
                // The Setup track runs the staged Setup; the app track the active infrastructure's installed engine.
                var image=track==ReleaseTrack.Setup?Path.Combine(bed.Paths.Stage("9.9.9"),ReleaseManifest.SetupName("9.9.9")):InstalledLayout.EngineImage("1.0.0");
                var launched=bed.Launched.Single();
                Assert(launched.Launch==new UpdateLaunch(track,image,bed.Paths.Stage("9.9.9"))&&launched.Result==bed.Paths.LastUpgrade,track+" launch "+launched.Launch);
                Assert(launched.Launch.Verb==(track==ReleaseTrack.Setup?"--quiet-upgrade":"--apply-app"),"verb");
                Assert(!File.Exists(bed.Paths.LastUpgrade),"an old result would be read as this install's");
                status=Bed.Status(engine);Assert(status is {Phase:"activating",LastRequest:{Action:"install",State:"running"}}&&status.LastRequest.Id==id,"activating");
                foreach(var again in new UpdaterRequest[]{new(1,"install",Id(),"9.9.9",fingerprint),new(1,"check",Id())})Assert(engine.Handle(again) is {Ok:false,Code:"BUSY"},"a second "+again.Op+" while activating");
                // The stage the engine reads stays while it runs.
                engine.Tick();Assert(bed.Launched.Count==1&&bed.Staged().SequenceEqual(Bed.Expected("9.9.9",track)),"launched twice, or the stage was touched");
            }
        });
        await check("updater: a staged release is believed only from its exact files' bytes each time",async()=>
        {
            foreach(var track in new[]{ReleaseTrack.App,ReleaseTrack.Setup})
            {
                using var bed=new Bed();bed.Good(track:track);var engine=bed.Engine();var target=(await Bed.CheckAsync(engine)).InstallTarget!;
                // Another engine, as after a restart, finds it with no record of having staged it.
                Assert(Bed.Status(bed.Engine()).InstallTarget==target,"not rediscovered");
                var stage=bed.Paths.Stage("9.9.9");
                File.WriteAllBytes(Path.Combine(stage,Bed.PayloadName("9.9.9",track)),[9,9,9,9]);
                Assert(engine.Handle(new(1,"install",Id(),target.Version,target.Fingerprint)) is {Ok:false,Code:"STALE_TARGET"},"a tampered payload was accepted");
                Assert(!Directory.Exists(stage)&&bed.Launched.Count==0,"a tampered stage stayed");
                // Staged again by a check, then spoiled one way at a time: never offered, and removed.
                async Task Spoiled(Action spoil,string what)
                {
                    Assert((await Bed.CheckAsync(engine)).InstallTarget==target,what+": not restaged");
                    spoil();Assert(Bed.Status(bed.Engine()).InstallTarget is null&&!Directory.Exists(stage),what+" was offered or kept");
                }
                await Spoiled(()=>File.WriteAllBytes(Path.Combine(stage,ReleaseManifest.SignatureName("9.9.9")),new byte[64]),"an unsigned stage");
                await Spoiled(()=>File.WriteAllBytes(Path.Combine(stage,"extra.txt"),[1]),"an extra file");
                await Spoiled(()=>Directory.CreateDirectory(Path.Combine(stage,"extra")),"an extra folder");
                await Spoiled(()=>File.WriteAllBytes(Path.Combine(stage,Bed.PayloadName("9.9.9",track==ReleaseTrack.App?ReleaseTrack.Setup:ReleaseTrack.App)),[1]),"the other track's payload beside it");
                await Spoiled(()=>File.Move(Path.Combine(stage,ReleaseManifest.ManifestName("9.9.9")),Path.Combine(stage,"yahad-windows-9.9.9.json")),"a manifest under another name");
                // Copied here under an older installed version's name: signed for another version.
                using var moved=new Bed();moved.Good(track:track);await Bed.CheckAsync(moved.Engine());Directory.Move(moved.Paths.Stage("9.9.9"),moved.Paths.Stage("9.9.10"));
                Assert(Bed.Status(moved.Engine()).InstallTarget is null&&!Directory.Exists(moved.Paths.Stage("9.9.10")),"a stage under another version's name was offered or kept");
            }
        });
        await check("updater: a change of track replaces the stage, and the old target is stale",async()=>
        {
            // A Setup release published over a staged app release of the same version.
            using var bed=new Bed();bed.Good();var engine=bed.Engine();var app=(await Bed.CheckAsync(engine)).InstallTarget!;
            Assert(bed.Staged().SequenceEqual(Bed.Expected("9.9.9",ReleaseTrack.App)),"app stage");
            var manifest=bed.Good(track:ReleaseTrack.Setup);var setup=(await Bed.CheckAsync(engine)).InstallTarget!;
            Assert(setup==new UpdaterTarget("9.9.9",UpdaterRules.Fingerprint(ReleaseTrack.Setup,manifest))&&setup!=app&&bed.Staged().SequenceEqual(Bed.Expected("9.9.9",ReleaseTrack.Setup)),"the stage was not replaced: "+string.Join(",",bed.Staged()));
            Assert(engine.Handle(new(1,"install",Id(),app.Version,app.Fingerprint)) is {Ok:false,Code:"STALE_TARGET"}&&bed.Launched.Count==0,"the app target installed over a Setup stage");
            Assert(engine.Handle(new(1,"install",Id(),setup.Version,setup.Fingerprint)).Ok&&bed.Launched is [{Launch.Track:ReleaseTrack.Setup}],"the Setup target");
            // The installed pair changes under a staged app release so that it now selects
            // the Setup track: the stage is no release any more, and goes with the target.
            using var moved=new Bed();var published=moved.Good();var running=moved.Engine();var target=(await Bed.CheckAsync(running)).InstallTarget!;
            moved.Pointer=Bed.Installed("1.0.0","0.9.0");
            Assert(running.Handle(new(1,"install",Id(),target.Version,target.Fingerprint)) is {Ok:false,Code:"STALE_TARGET"}&&moved.Launched.Count==0,"a stale track installed");
            Assert(!Directory.Exists(moved.Paths.Stage("9.9.9")),"the other track's stage stayed");
            Assert((await Bed.CheckAsync(running)).InstallTarget!.Fingerprint==UpdaterRules.Fingerprint(ReleaseTrack.Setup,published)&&moved.Staged().SequenceEqual(Bed.Expected("9.9.9",ReleaseTrack.Setup)),"restaged for the Setup track");
        });
        await check("updater: the installed pair is read live, after a commit, after a rollback and between checks",async()=>
        {
            // App track, committed: the same Updater keeps running and now runs 9.9.9.
            using var bed=new Bed();bed.Good();var engine=bed.Engine();var target=(await Bed.CheckAsync(engine)).InstallTarget!;
            Assert(engine.Handle(new(1,"install",Id(),target.Version,target.Fingerprint)).Ok,"install refused");
            bed.Pointer=Bed.Installed("9.9.9");Assert(Bed.Status(engine) is {Phase:"activating",CurrentVersion:"9.9.9"},"current is not live");
            File.WriteAllText(bed.Paths.LastUpgrade,JsonSerializer.Serialize(new{Code="UPGRADED",Ok=true}));
            var status=Bed.Status(engine);
            Assert(status is {Phase:"healthy",CurrentVersion:"9.9.9",InstallTarget:null,LatestVersion:null}&&!Directory.Exists(bed.Paths.Stage("9.9.9")),"after the commit: "+status.Phase+"/"+status.CurrentVersion);
            bed.Network.Requests.Clear();status=await Bed.CheckAsync(engine);
            Assert(status is {Phase:"idle",InstallTarget:null}&&bed.Network.Requests is [GitHubReleaseSource.Latest],"the installed release was offered again");
            bed.Good("9.9.10");status=await Bed.CheckAsync(engine);
            Assert(status.InstallTarget is {Version:"9.9.10"}&&bed.Staged("9.9.10").SequenceEqual(Bed.Expected("9.9.10",ReleaseTrack.App)),"the next release after a commit");
            // Rolled back: the pointer moved to the new pair and back during the apply.
            using var back=new Bed();back.Good();var running=back.Engine();target=(await Bed.CheckAsync(running)).InstallTarget!;
            Assert(running.Handle(new(1,"install",Id(),target.Version,target.Fingerprint)).Ok,"install refused");
            back.Pointer=Bed.Installed("9.9.9");Assert(Bed.Status(running).CurrentVersion=="9.9.9","during the apply");
            // An Updater restarted while the engine applies leaves the stage it reads alone.
            Assert(Bed.Status(back.Engine()).Phase=="activating"&&back.Staged().SequenceEqual(Bed.Expected("9.9.9",ReleaseTrack.App)),"a restart touched the stage being applied");
            back.Pointer=Bed.Installed("1.0.0");File.WriteAllText(back.Paths.LastUpgrade,JsonSerializer.Serialize(new{Code="ROLLED_BACK_UNHEALTHY",Ok=false}));
            status=Bed.Status(running);
            Assert(status is {Phase:"rolled_back",Error:"health",CurrentVersion:"1.0.0",InstallTarget:null}&&!Directory.Exists(back.Paths.Stage("9.9.9")),"after the rollback: "+status.Phase);
            // Changed by someone else between checks (a manual Setup): the stage is no update any more.
            using var manual=new Bed();manual.Good();var idle=manual.Engine();target=(await Bed.CheckAsync(idle)).InstallTarget!;
            manual.Pointer=Bed.Installed("9.9.9");Assert(Bed.Status(idle).CurrentVersion=="9.9.9","between checks");
            Assert(idle.Handle(new(1,"install",Id(),target.Version,target.Fingerprint)) is {Ok:false,Code:"STALE_TARGET"}&&manual.Launched.Count==0&&!Directory.Exists(manual.Paths.Stage("9.9.9")),"the installed version was installed again");
            // An active.json that cannot be read is no answer at all.
            using var broken=new Bed();var bad=false;
            var unread=new UpdaterEngine(broken.Paths,()=>bad?throw new IOException("ACTIVE_POINTER_INVALID"):broken.Pointer,()=>broken.Spki,broken.Spki,broken.Source,(_,_)=>throw new Exception("LAUNCHED"),()=>broken.Now);unread.Start();bad=true;
            Assert(unread.Handle(new(1,"status")) is {Ok:false,Code:"INTERNAL"},"status without a pointer");
            broken.Good();Assert(unread.Handle(new(1,"check",Id())).Ok,"check refused");await unread.Work;bad=false;
            Assert(Bed.Status(unread) is {Error:"internal",InstallTarget:null},"a check without a pointer");
        });
        await check("updater: the launched engine's result is read by whichever Updater is running",async()=>
        {
            foreach(var (code,phase,error,succeeded,refused) in new[]{("UPGRADED","healthy",(string?)null,true,false),("ROLLED_BACK_UNHEALTHY","rolled_back","health",false,true),("RELEASE_UNSUPPORTED_FROM","incompatible","incompatible",false,true),
                ("APP_NEEDS_INFRASTRUCTURE","incompatible","incompatible",false,true),("RELEASE_SIGNATURE_INVALID","failed","signature",false,true),("ROLLBACK_FAILED","available","rollback",false,false),("DISK_SPACE","available","storage",false,false),
                ("RELEASE_APP_HASH","available","archive",false,false),("APP_PACKAGE_FORBIDDEN","available","archive",false,false),("INFRA_IDENTITY_MISMATCH","available","internal",false,false),("SOMETHING_NEW","available","internal",false,false)})
            {
                using var bed=new Bed();bed.Good();var engine=bed.Engine();var target=(await Bed.CheckAsync(engine)).InstallTarget!;
                Assert(engine.Handle(new(1,"install",Id(),target.Version,target.Fingerprint)).Ok,"install refused");
                Assert(Bed.Status(engine).Phase=="activating","no result yet");
                File.WriteAllText(bed.Paths.LastUpgrade,"""{"Code":""");Assert(Bed.Status(engine).Phase=="activating",code+" half-written result");
                File.WriteAllText(bed.Paths.LastUpgrade,JsonSerializer.Serialize(new{Code=code,Ok=succeeded}));
                // Success means the pair names the new app; any other ending, the old one again.
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
        await check("updater: an engine that never reports ends the install after the timeout; one that cannot start fails at once",async()=>
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
        await check("updater: failures reach the parent as the words src/updates.ts accepts, and refuse a version only for a signature or a defective release",()=>
        {
            var words=new HashSet<string>{"configuration","network","signature","archive","build","backup","health","rollback","storage","maintenance","incompatible","internal","target_changed"};
            // The precise code is the deliberate code, or else the exception's type: never its message.
            foreach(var (error,expected,refuse,code) in new (Exception,string,bool,string)[]{(new HttpRequestException(),"network",false,"HttpRequestException"),(new TaskCanceledException(),"network",false,"TaskCanceledException"),(new IOException("GITHUB_URL"),"network",false,"GITHUB_URL"),(new IOException("GITHUB_LIST_INVALID"),"network",false,"GITHUB_LIST_INVALID"),
                (new HttpRequestException("GITHUB_HTTP",null,HttpStatusCode.Forbidden),"network",false,"GITHUB_HTTP:403"),(new HttpRequestException(HttpRequestError.NameResolutionError,"No such host is known. (api.github.com:443)"),"network",false,"HttpRequestException:NameResolutionError"),
                (new IOException("RELEASE_SIGNATURE_INVALID"),"signature",true,"RELEASE_SIGNATURE_INVALID"),(new IOException("RELEASE_KEY_UNTRUSTED"),"signature",true,"RELEASE_KEY_UNTRUSTED"),(new IOException("RELEASE_MANIFEST_INVALID"),"signature",true,"RELEASE_MANIFEST_INVALID"),
                (new IOException("RELEASE_UNSUPPORTED_FROM"),"incompatible",true,"RELEASE_UNSUPPORTED_FROM"),(new IOException("APP_NEEDS_INFRASTRUCTURE"),"incompatible",true,"APP_NEEDS_INFRASTRUCTURE"),
                (new IOException("RELEASE_SETUP_HASH"),"archive",false,"RELEASE_SETUP_HASH"),(new IOException("RELEASE_SETUP_SIZE"),"archive",false,"RELEASE_SETUP_SIZE"),(new IOException("RELEASE_APP_HASH"),"archive",false,"RELEASE_APP_HASH"),(new IOException("RELEASE_APP_SIZE"),"archive",false,"RELEASE_APP_SIZE"),
                (new IOException("DISK_SPACE"),"storage",false,"DISK_SPACE"),(new IOException("INFRA_IDENTITY_MISMATCH"),"internal",false,"INFRA_IDENTITY_MISMATCH"),(new IOException("ACTIVE_POINTER_INVALID"),"internal",false,"ACTIVE_POINTER_INVALID"),
                (new IOException("other"),"internal",false,"IOException"),(new IOException(@"C:\ProgramData\Yahad\secret"),"internal",false,"IOException"),(new IOException("NOT_A_code"),"internal",false,"IOException"),
                (new FileNotFoundException(@"C:\ProgramData\Yahad\secret"),"internal",false,"FileNotFoundException:2"),(new UnauthorizedAccessException(@"C:\secret"),"internal",false,"UnauthorizedAccessException:5"),
                (new Win32Exception(32),"internal",false,"Win32Exception:32"),(new InvalidOperationException(),"internal",false,"InvalidOperationException")})
            {
                var failure=UpdaterRules.CheckError(error);
                Assert(failure==new CheckFailure(expected,refuse,code)&&words.Contains(expected),error.GetType().Name+" maps to "+failure);
                Assert(System.Text.RegularExpressions.Regex.IsMatch(failure.Code,@"\A[A-Za-z][A-Za-z0-9_]{0,63}(:[A-Za-z0-9]{1,32})?\z"),"the code is not what src/updates.ts accepts: "+failure.Code);
            }
            foreach(var (code,phase,error,refuse) in new (string?,string,string?,bool)[]{("UPGRADED","healthy",null,false),("ROLLED_BACK_UNHEALTHY","rolled_back","health",true),("ROLLBACK_FAILED","failed","rollback",false),("DISK_SPACE","failed","storage",false),
                ("RELEASE_UNSUPPORTED_FROM","incompatible","incompatible",true),("APP_NEEDS_INFRASTRUCTURE","incompatible","incompatible",true),("RELEASE_APP_SIZE","failed","archive",false),("RELEASE_APP_HASH","failed","archive",false),
                ("APP_PACKAGE_FORBIDDEN","failed","archive",false),("APP_PACKAGE_TREE_MISMATCH","failed","archive",false),("RELEASE_SIGNATURE_INVALID","failed","signature",true),("RELEASE_KEY_UNTRUSTED","failed","signature",true),
                ("RELEASE_MANIFEST_INVALID","failed","signature",true),("RELEASE_NOT_NEWER","failed","signature",true),("RELEASE_SETUP_MISMATCH","failed","signature",true),
                ("INFRA_IDENTITY_MISMATCH","failed","internal",false),("APP_IDENTITY_MISMATCH","failed","internal",false),("RELEASE_TRACK_MISMATCH","failed","internal",false),("UPGRADE_PREPARE_FAILED","failed","internal",false),
                ("UPGRADE_COMMIT_INCOMPLETE","failed","internal",false),("UPGRADE_LOCKED","failed","internal",false),("UPGRADE_INCOMPLETE","failed","internal",false),("APP_PACKAGE","failed","internal",false),(null,"failed","internal",false),
                ("not a code","failed","internal",false),(@"C:\secret","failed","internal",false)})
            {
                // The Setup's own code is kept; one that is no deliberate code is never passed on.
                var ending=UpdaterRules.Ending(code);var precise=code=="UPGRADED"?null:UpdaterRules.IsCode(code)?code:UpdaterRules.ResultInvalid;
                Assert(ending==new UpgradeEnding(code=="UPGRADED",phase,error,refuse,precise)&&(error is null||words.Contains(error)),code+" ends "+ending);
            }
            return Task.CompletedTask;
        });
        await check("updater: a failure keeps its precise code and whether the version is refused for good, in the record, the status and the event",async()=>
        {
            // A check refused for good: the signature, recorded with the candidate version.
            using var bed=new Bed();var reported=new List<UpdateFailure>();
            UpdaterEngine Engine()=>Started(new UpdaterEngine(bed.Paths,()=>bed.Pointer,()=>bed.Spki,bed.Spki,bed.Source,(launch,result)=>{if(bed.LaunchFailure is not null)throw bed.LaunchFailure;bed.Launched.Add((launch,result));},()=>bed.Now,reported.Add));
            static UpdaterEngine Started(UpdaterEngine engine){engine.Start();return engine;}
            using(var other=new Bed()){var manifest=Bed.Encode(bed.Fields("9.9.9",ReleaseTrack.App));bed.List("v9.9.9",manifest:manifest,signature:other.Sign(manifest));}
            var engine=Engine();var status=await Bed.CheckAsync(engine);
            Assert(status is {Error:"signature",ErrorCode:"RELEASE_SIGNATURE_INVALID",ErrorPermanent:true,LastRequest:{Error:"signature",Code:"RELEASE_SIGNATURE_INVALID"}},"signature status "+status);
            Assert(reported is [{Operation:"check",Error:"signature",Code:"RELEASE_SIGNATURE_INVALID",Permanent:true,From:"1.0.0",Version:"9.9.9",Cause:IOException}],"signature event "+string.Join(",",reported));
            // It survives a restart, and a success clears all three.
            Assert(Bed.Status(Engine()) is {ErrorCode:"RELEASE_SIGNATURE_INVALID",ErrorPermanent:true},"lost on restart");
            bed.Good("9.9.10");status=await Bed.CheckAsync(engine);
            Assert(status is {Error:null,ErrorCode:null,ErrorPermanent:null,LastRequest:{Code:null}}&&reported.Count==1,"a success kept the failure");
            // A network failure carries the HTTP status, and asking again can help.
            using var limited=new Bed();limited.Network.Routes[GitHubReleaseSource.Latest]=()=>new(HttpStatusCode.Forbidden);
            status=await Bed.CheckAsync(limited.Engine());
            Assert(status is {Error:"network",ErrorCode:"GITHUB_HTTP:403",ErrorPermanent:false},"network status "+status);
            // An install's ending: the Setup's code, refused only with its target.
            var install=(await Bed.CheckAsync(engine)).InstallTarget!;reported.Clear();
            Assert(engine.Handle(new(1,"install",Id(),install.Version,install.Fingerprint)).Ok,"install refused");
            Assert(Bed.Status(engine) is {ErrorCode:null,ErrorPermanent:null},"activating kept the old failure");
            File.WriteAllText(bed.Paths.LastUpgrade,JsonSerializer.Serialize(new{Code="ROLLED_BACK_UNHEALTHY",Ok=false}));
            status=Bed.Status(engine);
            Assert(status is {Phase:"rolled_back",Error:"health",ErrorCode:"ROLLED_BACK_UNHEALTHY",ErrorPermanent:true,LastRequest:{Code:"ROLLED_BACK_UNHEALTHY"}},"rollback status "+status);
            Assert(reported is [{Operation:"install",Error:"health",Code:"ROLLED_BACK_UNHEALTHY",Permanent:true,Version:"9.9.10",Cause:null}],"rollback event "+string.Join(",",reported));
            // No result before the timeout, and an engine that cannot start.
            bed.Good("9.9.11");install=(await Bed.CheckAsync(engine)).InstallTarget!;
            Assert(engine.Handle(new(1,"install",Id(),install.Version,install.Fingerprint)).Ok,"install refused");
            bed.Now=bed.Now.Add(UpdaterRules.ResultTimeout).AddMinutes(1);
            Assert(Bed.Status(engine) is {Error:"internal",ErrorCode:UpdaterRules.ResultMissing,ErrorPermanent:false},"missing result");
            bed.LaunchFailure=new Win32Exception(5);reported.Clear();
            Assert(engine.Handle(new(1,"install",Id(),install.Version,install.Fingerprint)) is {Ok:false,Code:"INTERNAL"},"a failed launch was accepted");
            Assert(Bed.Status(engine) is {Error:"internal",ErrorCode:"Win32Exception:5",LastRequest:{Code:"Win32Exception:5"}}&&reported is [{Operation:"install",Code:"Win32Exception:5",Cause:Win32Exception}],"launch failure");
            // A check the last run did not finish says so.
            File.WriteAllBytes(bed.Paths.State,(UpdaterRecord.Seed(true) with{LastRequest=new(Id(),"check","running",1,null,null,null)}).Encode());reported.Clear();
            Assert(Bed.Status(Engine()).LastRequest is {Error:"internal",Code:UpdaterRules.CheckInterrupted}&&reported is [{Code:UpdaterRules.CheckInterrupted}],"interrupted check");
        });
        await check("updater: the precise fields are written only when set, so a record without a failure reads as before",()=>
        {
            var plain=Encoding.UTF8.GetString((UpdaterRecord.Seed(true) with{LastRequest=new(Id(),"check","succeeded",1,2,null,null)}).Encode());
            Assert(!plain.Contains("errorCode")&&!plain.Contains("errorPermanent")&&!plain.Contains("\"code\""),"a record without a failure carries the new fields: "+plain);
            // What an older Updater wrote is read as it was.
            var older=UpdaterRecord.Parse(Encoding.UTF8.GetBytes("""{"format":1,"mode":"auto","phase":"failed","lastRequest":{"id":"11111111-1111-4111-8111-111111111111","action":"check","state":"failed","startedAt":1,"finishedAt":2,"error":"network","target":null},"lastModeRequest":null,"checkedAt":2,"lastUpdateAt":null,"latest":null,"error":"network","refusedVersion":null}"""));
            Assert(older is {Error:"network",ErrorCode:null,ErrorPermanent:null,LastRequest.Code:null},"an older record");
            var failed=UpdaterRecord.Parse((older with{ErrorCode="GITHUB_HTTP:403",ErrorPermanent=false,LastRequest=older.LastRequest! with{Code="GITHUB_HTTP:403"}}).Encode());
            Assert(failed is {ErrorCode:"GITHUB_HTTP:403",ErrorPermanent:false,LastRequest.Code:"GITHUB_HTTP:403"},"round trip");
            // The event: fixed words, the code and versions; never the cause's message.
            var cause=new IOException(@"C:\ProgramData\Yahad\secret");
            try{throw cause;}catch(IOException){}
            using var json=JsonDocument.Parse(ServiceStartupDiagnostic.EncodeUpdate("check","internal","IOException",false,"1.0.0","9.9.9",cause));var root=json.RootElement;
            Assert(root.EnumerateObject().Select(p=>p.Name).SequenceEqual(["Format","Operation","Error","Code","Permanent","From","Version","SourceCommit","Sites"]),"event fields "+root);
            Assert(root.GetProperty("Code").GetString()=="IOException"&&root.GetProperty("Sites").GetArrayLength()>=1&&!root.ToString().Contains("secret"),"event "+root);
            using var ending=JsonDocument.Parse(ServiceStartupDiagnostic.EncodeUpdate("install","health","ROLLED_BACK_UNHEALTHY",true,null,"9.9.9",null));
            Assert(ending.RootElement.GetProperty("Sites").GetArrayLength()==0&&ending.RootElement.GetProperty("From").ValueKind==JsonValueKind.Null,"install event "+ending.RootElement);
            return Task.CompletedTask;
        });
        await check("updater: the fingerprint binds the track, and the disk budget is the track's",()=>
        {
            var manifest=Encoding.UTF8.GetBytes("{}");
            Assert(UpdaterRules.Fingerprint(ReleaseTrack.Setup,manifest)==Hash(Encoding.ASCII.GetBytes("setup\n{}"))&&UpdaterRules.Fingerprint(ReleaseTrack.App,manifest)==Hash(Encoding.ASCII.GetBytes("app\n{}")),"fingerprint");
            using var bed=new Bed();bed.SetupBytes=new byte[1000];bed.AppBytes=new byte[700];var release=ReleaseManifest.Parse(Bed.Encode(bed.Fields("9.9.9",ReleaseTrack.App)));
            Assert(UpdaterRules.DiskBudget(release,ReleaseTrack.Setup)==1000+UpdaterRules.DiskReserve&&UpdaterRules.DiskBudget(release,ReleaseTrack.App)==2100+UpdaterRules.DiskReserve,"budget");
            Assert(UpdaterRules.Payload(release,ReleaseTrack.Setup)==release.Infrastructure.Setup&&UpdaterRules.Payload(release,ReleaseTrack.App)==release.App.Archive,"payload");
            return Task.CompletedTask;
        });
        await check("updater: the pipe answers only the allowed account, and a bad client costs only its connection",async()=>
        {
            var me=WindowsIdentity.GetCurrent().User!;var descriptor=$"D:P(A;;GA;;;{me.Value})";var handled=0;
            UpdaterReply Handle(UpdaterRequest request){Interlocked.Increment(ref handled);return new(1,true,Status:SampleStatus());}
            var name="yahad-updater-test-"+Guid.NewGuid().ToString("N");using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var reports=0;void Report(Exception error)=>Interlocked.Increment(ref reports);
            using var end=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);var server=new UpdaterPipe(name,me,descriptor,Handle,Report).RunAsync(end.Token);
            uint seen=0;var reply=await UpdaterClient.AskAsync(name,process=>seen=process,new(1,"status"),stop.Token);
            Assert(reply.Ok&&reply.Status==SampleStatus() with{LastRequest=reply.Status!.LastRequest,LastModeRequest=reply.Status.LastModeRequest,InstallTarget=reply.Status.InstallTarget}&&seen==(uint)Environment.ProcessId,"round trip");
            // The client refuses a server it cannot verify before sending a byte.
            var before=handled;
            await DenyAsync("UPDATER_SERVER_IDENTITY",()=>UpdaterClient.AskAsync(name,_=>throw new IOException("UPDATER_SERVER_IDENTITY"),new(1,"check",Id()),stop.Token));
            // Oversized and garbage frames: the server drops them and stays up.
            foreach(var frame in new byte[][]{BitConverter.GetBytes(int.MaxValue),[..BitConverter.GetBytes(3),1,2,3]})
            {await using var raw=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);await raw.ConnectAsync(3000,stop.Token);await raw.WriteAsync(frame,stop.Token);await raw.FlushAsync(stop.Token);try{Assert(await raw.ReadAsync(new byte[1],stop.Token)==0,"a bad frame was answered");}catch(IOException){}}
            Assert(handled==before&&reports==0,"a refused or malformed request reached the engine, or counted as a failure of the service");
            Assert((await UpdaterClient.AskAsync(name,_=>{},new(1,"status"),stop.Token)).Ok,"the server did not survive");
            await end.CancelAsync();try{await server;}catch(OperationCanceledException){}
            // Admitted by the DACL but not the account the service expects: no reply.
            var strict="yahad-updater-test-"+Guid.NewGuid().ToString("N");handled=0;using var over=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var guarded=new UpdaterPipe(strict,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),descriptor,Handle,Report).RunAsync(over.Token);
            try{await UpdaterClient.AskAsync(strict,_=>{},new(1,"status"),stop.Token);throw new Exception("PEER_NOT_REFUSED");}catch(UpdaterRefusal refusal)when(refusal.Message.StartsWith("EXCHANGE",StringComparison.Ordinal)){}
            Assert(handled==0&&reports==0,"an unexpected account reached the engine, or counted as a failure of the service");
            await over.CancelAsync();try{await guarded;}catch(OperationCanceledException){}
            // No server: a clean reason after the connect bound, and nothing listening is trusted.
            await DenyAsync("PIPE_CONNECT:1460",()=>UpdaterClient.AskAsync("yahad-updater-test-"+Guid.NewGuid().ToString("N"),_=>throw new Exception("VERIFIED_NOTHING"),new(1,"status"),stop.Token));
        });
        await check("updater: the service's own failure answers INTERNAL, is reported, and leaves it serving",async()=>
        {
            var me=WindowsIdentity.GetCurrent().User!;var name="yahad-updater-test-"+Guid.NewGuid().ToString("N");using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            // Each is what the client could not tell from a refusal: IO kinds, one unforeseen, and a reply that cannot be framed.
            var failures=new Queue<Func<UpdaterReply>>([()=>throw new IOException("X"),()=>throw new FileNotFoundException(),()=>throw new NotSupportedException(),()=>new(1,false,new string('x',UpdaterProtocol.MaximumFrame))]);
            var count=failures.Count;var reported=new List<Exception>();
            using var end=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var server=new UpdaterPipe(name,me,$"D:P(A;;GA;;;{me.Value})",_=>failures.TryDequeue(out var next)?next():new(1,true),reported.Add).RunAsync(end.Token);
            for(var i=0;i<count;i++)Assert(await UpdaterClient.AskAsync(name,_=>{},new(1,"status"),stop.Token) is {Ok:false,Code:"INTERNAL"},"failure "+i);
            Assert(reported.Count==count,"reported");Assert((await UpdaterClient.AskAsync(name,_=>{},new(1,"status"),stop.Token)).Ok,"the server did not survive");
            await end.CancelAsync();try{await server;}catch(OperationCanceledException){}
        });
        await check("updater: a fresh process identifies a client at Identification level, which can open no file",async()=>
        {
            var name="yahad-updater-test-"+Guid.NewGuid().ToString("N");using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var probe=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!,["--updater-pipe-probe",name,WindowsIdentity.GetCurrent().User!.Value]){RedirectStandardOutput=true,UseShellExecute=false})!;
            try
            {
                Assert(await probe.StandardOutput.ReadLineAsync(stop.Token)=="READY","the probe did not start");
                Assert(await UpdaterClient.AskAsync(name,_=>{},new(1,"status"),stop.Token) is {Ok:true,Code:"PROBE"},"probe reply");
            }
            finally{probe.Kill();await probe.WaitForExitAsync(CancellationToken.None);}
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
            // A failure adds exactly its precise code and whether the version is refused for good.
            var failing=SampleStatus() with{Phase="failed",Error="signature",ErrorCode="RELEASE_SIGNATURE_INVALID",ErrorPermanent=true};
            failing=failing with{LastRequest=failing.LastRequest! with{State="failed",Error="signature",Code="RELEASE_SIGNATURE_INVALID"}};
            var failed=(await UpdaterClient.HandleAsync(input,Owner,(_,_)=>Task.FromResult(new UpdaterReply(1,true,Status:failing)),default))["result"]!.AsObject();
            Assert(failed.Select(p=>p.Key).Except(result.Select(p=>p.Key)).Order(StringComparer.Ordinal).SequenceEqual(new[]{"errorCode","errorPermanent"}),"failure fields "+failed.ToJsonString());
            Assert(failed["errorCode"]!.GetValue<string>()=="RELEASE_SIGNATURE_INVALID"&&failed["errorPermanent"]!.GetValue<bool>()&&failed["lastRequest"]!["code"]!.GetValue<string>()=="RELEASE_SIGNATURE_INVALID","failure values "+failed.ToJsonString());
            Assert(!result["lastRequest"]!.AsObject().ContainsKey("code"),"a request without a failure carries a code");
            static bool Refused(JsonObject value)=>value["ok"]!.GetValue<bool>()==false&&value["code"]!.GetValue<string>()=="UPDATES_UNAVAILABLE";
            static string ReasonOf(JsonObject value)=>value["reason"]!.GetValue<string>();
            var never=(Func<UpdaterRequest,CancellationToken,Task<UpdaterReply>>)((_,_)=>throw new Exception("ASKED"));
            string Submit(object command)=>JsonSerializer.Serialize(new{kind="submit",command});
            var id=Id();
            foreach(var bad in new[]{"","[]","{}","""{"kind":"other"}""",Submit(new{id,action="check",familyId="family",userId="other"}),Submit(new{id,action="check",familyId="other",userId="user"}),Submit(new{id,action="restart",familyId="family",userId="user"}),Submit(new{action="check",familyId="family",userId="user"})})
            {
                var asked=false;var value=await UpdaterClient.HandleAsync(Encoding.UTF8.GetBytes(bad),Owner,(_,_)=>{asked=true;return Task.FromResult(new UpdaterReply(1,true));},default);
                Assert(Refused(value)&&(bad.Contains("restart")||!asked),"accepted "+bad);
            }
            var unowned=await UpdaterClient.HandleAsync(input,()=>throw new Win32Exception(5),never,default);Assert(Refused(unowned)&&ReasonOf(unowned)=="OWNER:5","status without an owner: "+unowned.ToJsonString());
            foreach(var (failure,reason) in new (Exception,string)[]{(new IOException(@"C:\secret path"),"UPDATER"),(new TimeoutException(),"UPDATER:1460"),(new Win32Exception(5),"UPDATER:5"),(new UnauthorizedAccessException(),"UPDATER:5"),(new OperationCanceledException(),"UPDATER:1223"),(new EndOfStreamException(),"UPDATER:38"),(new UpdaterRefusal("SCM_SERVICE:5"),"SCM_SERVICE:5")})
            {var value=await UpdaterClient.HandleAsync(input,Owner,(_,_)=>throw failure,default);Assert(Refused(value)&&ReasonOf(value)==reason,failure.GetType().Name+" gave "+value.ToJsonString());}
            var unavailable=await UpdaterClient.HandleAsync(input,Owner,(_,_)=>Task.FromResult(new UpdaterReply(1,false,"NOT_AVAILABLE")),default);Assert(Refused(unavailable)&&ReasonOf(unavailable)=="UPDATER_NOT_AVAILABLE","refused status");
            Assert(ReasonOf(await UpdaterClient.HandleAsync(Encoding.UTF8.GetBytes(Submit(new{id,action="check",familyId="other",userId="user"})),Owner,never,default))=="OPERATOR","operator reason");
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
            foreach(var (server,observed,reason) in new (uint,UpdaterObservation,string)[]{(78,new(running,77,"LocalSystem",image),"SERVER_PROCESS"),(77,new(1,77,"LocalSystem",image),"SERVER_STATE"),(0,new(running,0,"LocalSystem",image),"SERVER_STATE"),(77,new(running,77,"NT SERVICE\\Yahad",image),"SERVER_ACCOUNT"),(77,new(running,77,"LocalSystem",InstalledLayout.UpdaterCommand("1.2.4")),"SERVER_IMAGE"),(77,new(running,77,"LocalSystem",image+" --extra"),"SERVER_IMAGE"),(77,new(running,77,"LocalSystem",""),"SERVER_IMAGE")})
                Deny(reason,()=>UpdaterClient.RequireServer(server,observed,"1.2.3"));
            // The SCM read itself, against a service every machine runs.
            var scheduler=UpdaterClient.Observe("Schedule");
            Assert(scheduler.State==running&&scheduler.ProcessId!=0&&string.Equals(scheduler.Account,"LocalSystem",StringComparison.OrdinalIgnoreCase)&&scheduler.Command.Contains("svchost",StringComparison.OrdinalIgnoreCase),"observed "+scheduler);
            Deny("SCM_SERVICE:1060",()=>UpdaterClient.Observe("Yahad.No.Such.Service"));
            return Task.CompletedTask;
        });
        await check("updater: the engine is started with its stage and result, quoted, and nothing else",()=>
        {
            var setup=new UpdateLaunch(ReleaseTrack.Setup,@"C:\P D\Setup.exe",@"C:\P D\9.9.9");
            Assert(UpdateLauncher.CommandLine(setup,@"C:\P D\r.json")==@"""C:\P D\Setup.exe"" --quiet-upgrade ""C:\P D\9.9.9"" --result ""C:\P D\r.json""","Setup track command line");
            // The app track runs the active infrastructure's installed engine, never anything staged.
            var app=UpdateLaunch.For(new("9.9.9",ReleaseTrack.App,new string('a',64),@"C:\P D\9.9.9",@"C:\P D\9.9.9\"+ReleaseManifest.AppArchiveName("9.9.9")),Bed.Installed("1.0.0","1.2.3"));
            Assert(app==new UpdateLaunch(ReleaseTrack.App,InstalledLayout.EngineImage("1.2.3"),@"C:\P D\9.9.9")&&app.Image.EndsWith(@"\infra\1.2.3\setup\Yahad.Setup.exe",StringComparison.Ordinal),"app track image "+app.Image);
            Assert(UpdateLauncher.CommandLine(app,@"C:\P D\r.json")=="\""+InstalledLayout.EngineImage("1.2.3")+@""" --apply-app ""C:\P D\9.9.9"" --result ""C:\P D\r.json""","app track command line");
            var staged=UpdateLaunch.For(new("9.9.9",ReleaseTrack.Setup,new string('a',64),@"C:\d",@"C:\d\"+ReleaseManifest.SetupName("9.9.9")),Bed.Installed("1.0.0","1.2.3"));
            Assert(staged==new UpdateLaunch(ReleaseTrack.Setup,@"C:\d\Yahad.Setup-9.9.9.exe",@"C:\d"),"Setup track image");
            foreach(var bad in new[]{"C:\\a\"b","C:\\a\\"})
            {
                Deny("UPDATER_LAUNCH_PATH",()=>UpdateLauncher.CommandLine(setup with{Image=bad},"C:\\r"));Deny("UPDATER_LAUNCH_PATH",()=>UpdateLauncher.CommandLine(setup with{Directory=bad},"C:\\r"));
                Deny("UPDATER_LAUNCH_PATH",()=>UpdateLauncher.CommandLine(setup,bad));
            }
            var upgrade=SetupParses(UpdateLauncher.CommandLine(setup with{Image="s",Directory="C:\\d"},"C:\\r.json"));
            Assert(upgrade?.UpgradeDirectory=="C:\\d"&&upgrade.AppStage is null&&upgrade.ResultPath=="C:\\r.json","Setup refuses the Updater's Setup-track command line");
            var apply=SetupParses(UpdateLauncher.CommandLine(app,@"C:\P D\r.json"));
            Assert(apply?.AppStage==@"C:\P D\9.9.9"&&apply.UpgradeDirectory is null&&!apply.Repair&&!apply.Uninstall&&apply.ResultPath==@"C:\P D\r.json","Setup refuses the Updater's app-track command line");
            return Task.CompletedTask;
        });
        await check("repair task: SYSTEM runs this infrastructure's payload-free engine at boot and daily, and the Task Scheduler accepts the definition",()=>
        {
            var now=new DateTimeOffset(DateTime.Today.AddHours(20));var xml=RepairTask.Definition("1.2.3",now);XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";var task=XDocument.Parse(xml).Root!;
            Assert(task.Descendants(ns+"UserId").Single().Value=="S-1-5-18"&&task.Descendants(ns+"RunLevel").Single().Value=="HighestAvailable","principal");
            Assert(task.Descendants(ns+"BootTrigger").Count()==1&&task.Descendants(ns+"CalendarTrigger").Count()==1,"triggers");
            Assert(task.Descendants(ns+"Command").Single().Value==InstalledLayout.EngineImage("1.2.3"),"image");
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
            Assert(updater.Command("1.2.3")==InstalledLayout.UpdaterCommand("1.2.3")&&updater.Command("1.2.3").Contains(@"\infra\1.2.3\updater\Yahad.Updater.exe"" --service",StringComparison.Ordinal),"image "+updater.Command("1.2.3"));
            try{updater.Command("..\\x");throw new Exception("VERSION_ACCEPTED");}catch(Exception error)when(error is IOException or ArgumentException){}
            return Task.CompletedTask;
        });
    }
    // The Updater and Setup agree on the shape of --quiet-upgrade and --apply-app: the
    // Updater's command line, split at its quotes, as Setup parses it.
    private static QuietSetup.Request? SetupParses(string line)
    {
        var tokens=System.Text.RegularExpressions.Regex.Matches(line,"\"([^\"]*)\"|(\\S+)").Select(m=>m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Value).Skip(1).ToArray();
        return QuietSetup.Parse(tokens);
    }
}

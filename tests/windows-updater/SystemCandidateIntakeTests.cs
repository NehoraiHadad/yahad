using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yahad.Authority;
using Yahad.Install;

internal static class SystemCandidateIntakeTests
{
    private static readonly string Commit=new('a',40);
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        foreach(var annotated in new[]{false,true})await check("system source resolves actual "+(annotated?"annotated":"lightweight")+" tag rather than target_commitish",async()=>
        {
            var artifact=Build();var visited=new List<string>();var source=await Source(artifact,annotated:annotated,visited:visited);
            Assert(source.SourceCommit==Commit&&source.AssetName=="yahad-system-native-0.6.1.zip"&&visited.Count==(annotated?3:2));
        });
        foreach(var mutation in new[]{"tag_cycle","cross_repo","blob","missing_digest","wrong_repo","draft","oversized"})await check("system fixed source rejects "+mutation,async()=>
        {
            var artifact=Build();var visited=new List<string>();await Refused(()=>Source(artifact,annotated:true,visited:visited,change:responses=>
            {
                var latest=JsonNode.Parse(responses[SystemSourceRelease.Api+"/releases/latest"])!.AsObject();var reference=JsonNode.Parse(responses[SystemSourceRelease.Api+"/git/ref/tags/v0.6.1"])!.AsObject();var tagUrl=SystemSourceRelease.Api+"/git/tags/"+new string('b',40);var tag=JsonNode.Parse(responses[tagUrl])!.AsObject();
                if(mutation=="tag_cycle")tag["object"]=JsonNode.Parse(Json(new{type="tag",sha=new string('b',40),url=tagUrl}));
                if(mutation=="cross_repo")reference["object"]!["url"]="https://api.github.com/repos/other/repo/git/tags/"+new string('b',40);
                if(mutation=="blob")reference["object"]!["type"]="blob";
                if(mutation=="missing_digest")latest["assets"]![0]!.AsObject().Remove("digest");
                if(mutation=="wrong_repo")latest["url"]="https://api.github.com/repos/other/repo/releases/1";
                if(mutation=="draft")latest["draft"]=true;
                responses[SystemSourceRelease.Api+"/releases/latest"]=mutation=="oversized"?new byte[1024*1024+1]:Json(latest);responses[SystemSourceRelease.Api+"/git/ref/tags/v0.6.1"]=Json(reference);responses[tagUrl]=Json(tag);
            }));Assert(visited.Count<=3);
        });
        foreach(var mutation in new[]{"format","profile","target","source","version","os","schema","protocol","prefix","entry","security"})await check("system manifest rejects "+mutation+" before slot creation",async()=>
        {
            var artifact=Build(changeManifest:root=>
            {
                if(mutation=="format")root["format"]=1;if(mutation=="profile")root["profile"]="full";if(mutation=="target")root["target"]="linux-x64";if(mutation=="source")root["sourceCommit"]=new string('c',40);if(mutation=="version")root["version"]="0.6.0";if(mutation=="os")root["minimumWindowsBuild"]=999999;
                if(mutation=="schema")root["database"]!["migrationsFrom"]=new JsonArray(1,2);
                if(mutation=="protocol")root["protocols"]!["handoff"]=JsonNode.Parse(Json(new{minimumReader=2,maximumReader=2,writer=2}));
                if(mutation=="prefix")root["components"]![0]!["prefix"]="bootstrap/unapproved";if(mutation=="entry")root["components"]![0]!["entryPoint"]="other.exe";
                if(mutation=="security")root["components"]![0]!["files"]![0]!["path"]="enrollment.json";
            });var source=await Source(artifact);await Refused(()=>Task.FromResult(SystemCandidateManifest.Validate(artifact.Manifest,artifact.Application,source,Environment())));
        });
        await check("full system stages app plus five component folders without live binaries/data/legacy-pointer mutation",async()=>
        {
            using var f=new Fixture();var artifact=Build();var source=await Source(artifact);f.Cache(artifact,source);var legacy=Json(new{fixture="legacy pointer"});File.WriteAllBytes(f.App.Files.PathOf("intake/available.json"),legacy);
            Directory.CreateDirectory(f.Binary.PathOf("host"));var oldHost=f.Binary.PathOf("host/Yahad.ServiceHost.exe");File.WriteAllBytes(oldHost,[1,2,3]);Directory.CreateDirectory(f.App.Files.PathOf("datasets/fixture/data"));var data=f.App.Files.PathOf("datasets/fixture/data/app.sqlite");File.WriteAllBytes(data,[7,8,9]);
            var staged=await f.Intake.StageVerifiedArchiveAsync(source,f.CachePath(source),Environment(),default);var facts=f.Intake.RequireVerified(staged.SourceReceiptDigest);
            Assert(facts.Components.Count==5&&facts.TargetSchema==7&&facts.MigrationSources.Contains(6)&&facts.SourceCommit==Commit);
            Assert(File.Exists(Path.Combine(staged.SystemSlotRoot,"bootstrap","owner","Yahad.Owner.exe"))&&File.Exists(Path.Combine(staged.ApplicationPayloadRoot,"app","src","main.ts")));
            Assert(File.ReadAllBytes(oldHost).SequenceEqual(new byte[]{1,2,3})&&File.ReadAllBytes(data).SequenceEqual(new byte[]{7,8,9})&&legacy.SequenceEqual(File.ReadAllBytes(f.App.Files.PathOf("intake/available.json"))));
            Assert(f.App.Before.SequenceEqual(File.ReadAllBytes(f.App.Files.PathOf("activation.json")))&&f.PolicyChecks>=5);
        });
        await check("full profile reuses existing HTTPS client and rotates shared verified cache for three staged candidates",async()=>
        {
            var artifacts=new[]{Build("0.6.1"),Build("0.6.2"),Build("0.6.3")};var selected=0;var calls=0;
            using var f=new Fixture(new SourceHandler(request=>
            {
                calls++;Assert(request.Headers.Authorization is null);var responses=Responses(artifacts[selected],false);responses[SystemSourceRelease.Api+"/releases/assets/2"]=artifacts[selected].Zip;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new ByteArrayContent(responses[request.RequestUri!.AbsoluteUri])};
            }));
            for(selected=0;selected<artifacts.Length;selected++){var staged=await f.Intake.CheckAndStageAsync(Environment(),default)??throw new Exception("SYSTEM_TEST_CANDIDATE_MISSING");Assert(f.Intake.RequireVerified(staged.SourceReceiptDigest).Version==artifacts[selected].Version);}
            Assert(calls==9&&Directory.EnumerateFiles(f.App.Files.PathOf("intake"),"archive-*.zip").Count()==2&&Directory.EnumerateFiles(f.App.Files.PathOf("intake"),"system-source-*.json").Count()==3);
            Assert(f.App.Before.SequenceEqual(File.ReadAllBytes(f.App.Files.PathOf("activation.json"))));
        });
        await check("system descriptor defensively copies manifests and exposes immutable inventory",async()=>
        {
            var artifact=Build();var source=await Source(artifact);var descriptor=SystemCandidateManifest.Validate(artifact.Manifest,artifact.Application,source,Environment());var digest=descriptor.SystemManifestDigest;var copy=descriptor.ManifestBytes;copy[0]^=1;
            Assert(SystemCandidateManifest.Hash(descriptor.ManifestBytes)==digest&&descriptor.Components.Count==5&&descriptor.DatabaseMigrationsFrom.SequenceEqual(new[]{1,2,3,4,5,6}));
        });
        foreach(var mutation in new[]{"component_commit","clr_missing","extra_member","private_path","bad_app_schema"})await check("full system intake refuses "+mutation+" before publication",async()=>
        {
            using var f=new Fixture();var artifact=Build(changeFiles:files=>
            {
                if(mutation=="component_commit"){var key="bootstrap/authority/build-identity.json";var identity=JsonNode.Parse(files[key])!.AsObject();identity["sourceCommit"]=new string('b',40);files[key]=Json(identity);}
                if(mutation=="clr_missing")files.Remove("bootstrap/authority/coreclr.dll");
                if(mutation=="private_path")files["bootstrap/owner/Secrets/master-key"]=[1];
                if(mutation=="bad_app_schema"){var key="payload/app/src/database-schema.ts";files[key]=Encoding.UTF8.GetBytes("export const DATABASE_SCHEMA = 8;\nexport const DATABASE_MIGRATIONS_FROM = [6] as const;\n");}
            },extraMember:mutation=="extra_member"?"bootstrap/authority/undeclared.txt":null);var source=await Source(artifact);f.Cache(artifact,source);
            await Refused(()=>f.Intake.StageVerifiedArchiveAsync(source,f.CachePath(source),Environment(),default));Assert(!File.Exists(f.App.Files.PathOf("intake/system-available.json"))&&f.App.Before.SequenceEqual(File.ReadAllBytes(f.App.Files.PathOf("activation.json"))));
        });
        await check("full intake requires current explicit policy and never stages through a revoked callback",async()=>
        {
            using var f=new Fixture();var artifact=Build();var source=await Source(artifact);f.Cache(artifact,source);f.Enabled=false;
            await Refused(()=>f.Intake.StageVerifiedArchiveAsync(source,f.CachePath(source),Environment(),default));Assert(!Directory.Exists(f.Binary.PathOf("system-slots"))&&!File.Exists(f.App.Files.PathOf("intake/system-available.json")));
        });
        foreach(var mutation in new[]{"source_receipt","slot","application"})await check("protected resolver rechecks "+mutation+" bytes before policy can mint capability",async()=>
        {
            using var f=new Fixture();var artifact=Build();var source=await Source(artifact);f.Cache(artifact,source);var staged=await f.Intake.StageVerifiedArchiveAsync(source,f.CachePath(source),Environment(),default);
            var path=mutation switch{"source_receipt"=>f.App.Files.PathOf("intake/system-source-"+staged.SourceReceiptDigest+".json"),"slot"=>Path.Combine(staged.SystemSlotRoot,"bootstrap","authority","Yahad.Authority.exe"),_=>Path.Combine(staged.ApplicationPayloadRoot,"app","src","main.ts")};File.AppendAllText(path,"tampered fixture");
            await Refused(()=>Task.FromResult(f.Intake.RequireVerified(staged.SourceReceiptDigest)));
        });
        await check("system slots refuse interrupted staging and low-space allocation without deleting residue",async()=>
        {
            using var f=new Fixture();var artifact=Build();var source=await Source(artifact);var descriptor=SystemCandidateManifest.Validate(artifact.Manifest,artifact.Application,source,Environment());
            var noSpace=new InstalledSystemSlots(f.Binary,()=>0);await Refused(()=>Task.FromResult(noSpace.Stage(descriptor.SystemManifestDigest,artifact.Manifest,descriptor.Components,path=>new MemoryStream(artifact.Files[path],false),default)));
            var residue=f.Binary.PathOf("system-slots/.staging-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(residue);
            await Refused(()=>Task.FromResult(f.Slots.Stage(descriptor.SystemManifestDigest,artifact.Manifest,descriptor.Components,path=>new MemoryStream(artifact.Files[path],false),default)));Assert(Directory.Exists(residue));
        });
    }
    private static SystemCandidateEnvironment Environment()=>new("0.6.0",6,99999,new Dictionary<string,int>{{"system",1},{"handoff",1},{"authorityRuntime",1},{"ownerAuthority",1},{"activation",1},{"journal",1},{"recovery",1},{"policy",2}});
    private static async Task Refused(Func<Task> test)
    {try{await test();}catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException){return;}throw new Exception("SYSTEM_INTAKE_UNEXPECTED_ADMISSION");}
    private static void Assert(bool value){if(!value)throw new Exception("SYSTEM_INTAKE_ASSERTION");}
    private static byte[] Json(object value)=>JsonSerializer.SerializeToUtf8Bytes(value);
    private static async Task<SystemSourceRelease> Source(Artifact artifact,bool annotated=false,List<string>? visited=null,Action<Dictionary<string,byte[]>>? change=null)
    {
        var responses=Responses(artifact,annotated);change?.Invoke(responses);
        return await SystemSourceRelease.CheckAsync((uri,maximum,token)=>{token.ThrowIfCancellationRequested();visited?.Add(uri.AbsoluteUri);return Task.FromResult(responses[uri.AbsoluteUri]);},default);
    }
    private static Dictionary<string,byte[]> Responses(Artifact artifact,bool annotated)
    {
        var api=SystemSourceRelease.Api;var tag="v"+artifact.Version;var sha=annotated?new string('b',40):Commit;var kind=annotated?"tag":"commit";
        return new Dictionary<string,byte[]>(StringComparer.Ordinal)
        {
            [api+"/releases/latest"]=Json(new{id=1,url=api+"/releases/1",tag_name=tag,target_commitish="untrusted-branch-value",draft=false,prerelease=false,assets=new[]{new{id=2,name="yahad-system-native-"+artifact.Version+".zip",url=api+"/releases/assets/2",state="uploaded",size=artifact.Zip.Length,digest="sha256:"+SystemCandidateManifest.Hash(artifact.Zip)}}}),
            [api+"/git/ref/tags/"+tag]=Json(new{@ref="refs/tags/"+tag,url=api+"/git/refs/tags/"+tag,@object=new{type=kind,sha,url=api+"/git/"+(annotated?"tags":"commits")+"/"+sha}}),
            [api+"/git/tags/"+new string('b',40)]=Json(new{sha=new string('b',40),url=api+"/git/tags/"+new string('b',40),@object=new{type="commit",sha=Commit,url=api+"/git/commits/"+Commit}})
        };
    }
    private sealed record Artifact(string Version,byte[] Zip,byte[] Manifest,byte[] Application,Dictionary<string,byte[]> Files);
    private static Artifact Build(string version="0.6.1",Action<JsonObject>? changeManifest=null,Action<Dictionary<string,byte[]>>? changeFiles=null,string? extraMember=null)
    {
        var app=NativeCandidateIntakeTests.Build(version,7,files=>files["app/src/database-schema.ts"]=Encoding.UTF8.GetBytes("export const DATABASE_SCHEMA = 7;\nexport const DATABASE_MIGRATIONS_FROM = [1, 2, 3, 4, 5, 6] as const;\n"));
        var files=new Dictionary<string,byte[]>(StringComparer.Ordinal);byte[] image;
        using(var input=new MemoryStream(app.Zip))using(var zip=new ZipArchive(input,ZipArchiveMode.Read)){foreach(var entry in zip.Entries){using var source=entry.Open();using var output=new MemoryStream();source.CopyTo(output);files["payload/"+entry.FullName]=output.ToArray();}image=files["payload/runtime/node/node.exe"];}
        foreach(var kind in Enum.GetValues<SystemComponentKind>())
        {
            var prefix=FixedSystemComponents.SlotPrefix(kind);var entry=FixedSystemComponents.EntryPoint(kind);var stem=Path.GetFileNameWithoutExtension(entry);var artifact=kind switch{SystemComponentKind.RuntimeHost=>"Yahad.ServiceHost",SystemComponentKind.Owner=>"Yahad.Owner",SystemComponentKind.Probe=>"Node",_=>"Yahad.Authority"};files[prefix+"/"+entry]=image.ToArray();
            if(kind!=SystemComponentKind.Probe)
            {
                foreach(var name in new[]{stem+".dll","coreclr.dll","hostfxr.dll","hostpolicy.dll","System.Private.CoreLib.dll"})files[prefix+"/"+name]=image.ToArray();
                var frameworks=new List<object>{new{name="Microsoft.NETCore.App",version="10.0.14"}};if(kind==SystemComponentKind.Owner)frameworks.Add(new{name="Microsoft.WindowsDesktop.App",version="10.0.14"});
                files[prefix+"/"+stem+".runtimeconfig.json"]=Json(new{runtimeOptions=new{includedFrameworks=frameworks}});
                var runtimeAssets=new Dictionary<string,object>{{stem+".dll",new{}}};var nativeAssets=new Dictionary<string,object>{{"coreclr.dll",new{}},{"hostfxr.dll",new{}},{"hostpolicy.dll",new{}}};
                var libraries=new Dictionary<string,object>{{"runtimepack",new{runtime=runtimeAssets,native=nativeAssets}}};
                files[prefix+"/"+stem+".deps.json"]=Json(new{runtimeTarget=new{name="net10.0/win-x64"},targets=new Dictionary<string,object>{{"net10.0/win-x64",libraries}}});
            }
            files[prefix+"/build-identity.json"]=Json(new{format=1,component=kind.ToString(),artifact,version,sourceCommit=Commit,target="win-x64",entry,entrySha256=SystemCandidateManifest.Hash(image),runtime=kind==SystemComponentKind.Probe?"node-private":"dotnet-private",runtimeVersion="10.0.14",upstreamVersion="24.22.2",sourceUrl="https://nodejs.org/dist/v24.22.2/node-v24.22.2-win-x64.zip"});
        }
        changeFiles?.Invoke(files);var components=Enum.GetValues<SystemComponentKind>().Select(kind=>
        {
            var prefix=FixedSystemComponents.SlotPrefix(kind);var entry=FixedSystemComponents.EntryPoint(kind);
            return new{kind=kind.ToString(),prefix,entryPoint=entry,entrySha256=SystemCandidateManifest.Hash(files[prefix+"/"+entry]),files=files.Where(p=>p.Key.StartsWith(prefix+"/",StringComparison.Ordinal)).Select(p=>new{path=p.Key[(prefix.Length+1)..],size=p.Value.Length,sha256=SystemCandidateManifest.Hash(p.Value)}).ToArray()};
        }).ToArray();
        var protocols=new[]{"system","handoff","authorityRuntime","ownerAuthority","activation","journal","recovery","policy"}.ToDictionary(name=>name,name=>new{minimumReader=name=="policy"?2:1,maximumReader=name=="policy"?2:1,writer=name=="policy"?2:1});
        var root=JsonNode.Parse(Json(new{format=2,kind="windows-system-candidate",profile="full-system-v1",target="win-x64",version,sourceCommit=Commit,minimumWindowsBuild=19045,applicationManifestSha256=SystemCandidateManifest.Hash(files["payload/bundle.json"]),database=new{targetSchema=7,migrationsFrom=new[]{1,2,3,4,5,6}},protocols,components}))!.AsObject();changeManifest?.Invoke(root);var manifest=Json(root);files["system-manifest.json"]=manifest;
        using var outputZip=new MemoryStream();using(var zip=new ZipArchive(outputZip,ZipArchiveMode.Create,true)){foreach(var pair in files){var entry=zip.CreateEntry(pair.Key,CompressionLevel.Fastest);entry.ExternalAttributes=0x81a4<<16;using var stream=entry.Open();stream.Write(pair.Value);}if(extraMember is not null){using var stream=zip.CreateEntry(extraMember).Open();stream.WriteByte(1);}}
        return new(version,outputZip.ToArray(),manifest,files["payload/bundle.json"],files);
    }
    private sealed class Fixture:IDisposable
    {
        internal NativeCandidateIntakeTests.Fixture App=new();internal InstalledStateFiles Binary;internal InstalledSystemSlots Slots;internal NativeCandidateIntake Native;internal SystemCandidateIntake Intake;internal bool Enabled=true;internal int PolicyChecks;
        internal Fixture(HttpMessageHandler? transport=null)
        {
            Binary=InstalledStateFiles.Private(SafeDirectory.CreatePrivateSession(Path.GetTempPath()));Slots=new(Binary,()=>long.MaxValue);Native=new(App.Files,App.Anchor,()=>DateTimeOffset.UtcNow,transport??new NoNetwork(),availableBytes:()=>long.MaxValue);
            Intake=new(App.Files,App.Anchor,Slots,Native,()=>{PolicyChecks++;if(!Enabled)throw new UnauthorizedAccessException("FULL_POLICY_REQUIRED");},Environment);
        }
        internal string CachePath(SystemSourceRelease source)=>App.Files.PathOf("intake/archive-"+source.ArchiveSha256+".zip");
        internal void Cache(Artifact artifact,SystemSourceRelease source)=>File.WriteAllBytes(CachePath(source),artifact.Zip);
        public void Dispose()=>Native.Dispose();
    }
    private sealed class NoNetwork:HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>throw new Exception("SYSTEM_TEST_UNEXPECTED_NETWORK");}
    private sealed class SourceHandler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(response(request));}
}

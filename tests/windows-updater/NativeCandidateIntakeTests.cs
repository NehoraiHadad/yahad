using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;

// Synthetic private-root intake only. Every HTTP response and candidate PE is a
// fixture. No network, process launch, installed service, or real data is involved.
// The fixed-source archive transport/cache and the application payload stager are
// driven directly: the full-system adapter (SystemCandidateIntakeTests) owns the
// source check, system manifest and pointer publication around them.
internal static class NativeCandidateIntakeTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("native intake requires the current full policy before archive network",async()=>
        {
            var f=new Fixture();var artifact=Build("0.6.1");var calls=0;var source=await Source(artifact);
            using var intake=f.Intake(artifact,new Handler(_=>{calls++;return Task.FromResult(Ok(artifact.Zip));}));
            await Refused(()=>intake.AcquireSystemArchiveAsync(source,()=>throw new UnauthorizedAccessException("FULL_POLICY_REQUIRED"),default));
            Assert(calls==0&&!Directory.EnumerateFiles(f.Files.PathOf("intake"),"*.zip*").Any());
        });
        await check("native intake reserves application free space before downloading",async()=>
        {
            foreach(var free in new[]{-1L,NativeCandidateIntake.MinimumFreeBytes})
            {
                var f=new Fixture();var artifact=Build("0.6.1");var calls=0;
                using var intake=f.Intake(artifact,new Handler(request=>{calls++;return Task.FromResult(Ok(artifact.Zip));}),availableBytes:()=>free);
                await RefusedWithCode(()=>f.Acquire(intake,artifact),"NATIVE_INTAKE_DISK_SPACE");
                Assert(calls==0&&!Directory.EnumerateFiles(f.Files.PathOf("intake"),"*.zip*").Any()&&!Directory.EnumerateFileSystemEntries(f.Files.PathOf("payloads"),".intake-*").Any());
                Assert(f.Before.SequenceEqual(File.ReadAllBytes(f.Files.PathOf("activation.json"))));
            }
        });
        await check("native intake rechecks actual extraction space without allocating another cache on retries",async()=>
        {
            var f=new Fixture();var artifact=Build("0.6.1");var free=long.MaxValue;var calls=0;
            Handler Transport()=>new(request=>{calls++;return Task.FromResult(Ok(artifact.Zip));});
            using(var intake=f.Intake(artifact,Transport(),availableBytes:()=>free)){await f.Acquire(intake,artifact);free=NativeCandidateIntake.MinimumFreeBytes;await RefusedWithCode(()=>Task.FromResult(f.Stage(intake,artifact)),"NATIVE_INTAKE_DISK_SPACE");}
            for(var i=0;i<3;i++){using var retry=f.Intake(artifact,Transport(),availableBytes:()=>free);await RefusedWithCode(async()=>{await f.Acquire(retry,artifact);f.Stage(retry,artifact);},"NATIVE_INTAKE_DISK_SPACE");}
            Assert(calls==1&&Directory.EnumerateFiles(f.Files.PathOf("intake"),"archive-*.zip").Count()==1&&!Directory.EnumerateFileSystemEntries(f.Files.PathOf("payloads"),".intake-*").Any()&&!Directory.Exists(f.Files.PathOf("payloads/"+Hash(artifact.Manifest))));
        });
        await check("native intake stops mid-download space loss then retries owned partial after space recovers",async()=>
        {
            var f=new Fixture();var artifact=Build("0.6.1");var free=long.MaxValue;var calls=0;
            var transport=new Handler(request=>{calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(new ReadNotificationStream(artifact.Zip,()=>free=NativeCandidateIntake.MinimumFreeBytes))});});
            using(var intake=f.Intake(artifact,transport,availableBytes:()=>free))await RefusedWithCode(()=>f.Acquire(intake,artifact),"NATIVE_INTAKE_DISK_SPACE");
            var residue=Directory.GetFiles(f.Files.PathOf("intake"),"download-*.zip.partial");Assert(residue.Length==1);var length=new FileInfo(residue[0]).Length;
            for(var i=0;i<3;i++)
            {
                using var retry=f.Intake(artifact,new Handler(_=>{calls++;return Task.FromResult(Ok(artifact.Zip));}),availableBytes:()=>free);
                await RefusedWithCode(()=>f.Acquire(retry,artifact),"NATIVE_INTAKE_DISK_SPACE");
            }
            Assert(calls==1&&Directory.GetFiles(f.Files.PathOf("intake"),"download-*.zip.partial").Length==1&&new FileInfo(residue[0]).Length==length&&!Directory.EnumerateFiles(f.Files.PathOf("intake"),"archive-*.zip").Any());
            free=long.MaxValue;using var recovered=f.Intake(artifact,availableBytes:()=>free);f.Verify(artifact,await f.Publish(recovered,artifact));Assert(!File.Exists(residue[0]));
        });
        foreach(var kind in new[]{"download","staging","cache_limit"})
            await check("native intake bounds existing "+kind+" residue before more network or allocation",async()=>
            {
                var f=new Fixture();var artifact=Build("0.6.1");var calls=0;
                if(kind=="download")File.WriteAllBytes(f.Files.PathOf("intake/download-"+Guid.NewGuid().ToString("N")+".zip.partial"),[1]);
                if(kind=="staging")Directory.CreateDirectory(f.Files.PathOf("payloads/.intake-"+Guid.NewGuid().ToString("N")));
                if(kind=="cache_limit")for(var i=0;i<NativeCandidateIntake.MaximumRetainedCandidates+1;i++)File.WriteAllBytes(f.Files.PathOf("intake/archive-"+i.ToString("x64")+".zip"),[1]);
                var before=Directory.EnumerateFileSystemEntries(f.Files.Root,"*",SearchOption.AllDirectories).Count();
                for(var i=0;i<3;i++)
                {
                    using var intake=f.Intake(artifact,new Handler(_=>{calls++;return Task.FromResult(Ok(artifact.Zip));}));
                    await RefusedWithCode(()=>f.Acquire(intake,artifact),"NATIVE_INTAKE_MAINTENANCE_REQUIRED");
                }
                // The exclusive operation creates its one persistent process.lock.
                Assert(calls==0&&Directory.EnumerateFileSystemEntries(f.Files.Root,"*",SearchOption.AllDirectories).Count()==before+1);
            });
        await check("native intake rotates only oldest verified archive for third candidate and preserves all payload/data/audit",async()=>
        {
            var f=new Fixture();var first=Build("0.6.1");var second=Build("0.6.2");var third=Build("0.6.3");
            foreach(var artifact in new[]{first,second}){using var intake=f.Intake(artifact);await f.Publish(intake,artifact);}
            var oldest=f.Files.PathOf("intake/archive-"+Hash(first.Zip)+".zip");var retained=f.Files.PathOf("intake/archive-"+Hash(second.Zip)+".zip");
            File.SetCreationTimeUtc(oldest,new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc));File.SetCreationTimeUtc(retained,new DateTime(2021,1,1,0,0,0,DateTimeKind.Utc));
            Directory.CreateDirectory(f.Files.PathOf("datasets/fixture/data"));Directory.CreateDirectory(f.Files.PathOf("backups/fixture"));
            File.WriteAllBytes(f.Files.PathOf("datasets/fixture/data/synthetic.sqlite"),[1,2,3]);File.WriteAllBytes(f.Files.PathOf("backups/fixture/synthetic.backup"),[4,5,6]);File.WriteAllBytes(f.Files.PathOf("intake/unknown-preserved.txt"),[7,8,9]);
            var preserved=Directory.EnumerateFiles(f.Files.Root,"*",SearchOption.AllDirectories).Where(path=>path!=oldest&&!Path.GetFileName(path).StartsWith("archive-",StringComparison.Ordinal)).ToDictionary(path=>path,File.ReadAllBytes,StringComparer.Ordinal);
            var replacementDenied=false;
            using(var intake=f.Intake(third,fault:phase=>
            {
                if(phase=="cache_rotation_verified")try{File.WriteAllBytes(oldest,[42]);}catch(IOException){replacementDenied=true;}
            }))f.Verify(third,await f.Publish(intake,third));
            Assert(replacementDenied&&!File.Exists(oldest)&&File.Exists(retained)&&File.Exists(f.Files.PathOf("intake/archive-"+Hash(third.Zip)+".zip")));
            Assert(Directory.EnumerateFiles(f.Files.PathOf("intake"),"archive-*.zip").Count()==NativeCandidateIntake.MaximumRetainedCandidates&&Directory.EnumerateFiles(f.Files.PathOf("intake"),"system-source-*.json").Count()==3&&Directory.EnumerateFiles(f.Files.PathOf("intake"),"receipt-*.json").Count()==3);
            foreach(var pair in preserved)Assert(File.Exists(pair.Key)&&pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key)));
            using var repeat=f.Intake(third);f.Verify(third,await f.Publish(repeat,third));
        });
        foreach(var mutation in new[]{"tampered","unpublished","changed_after_inventory","open_reader"})
            await check("native intake cannot rotate cache with "+mutation+" evidence",async()=>
            {
                var f=new Fixture();var first=Build("0.6.1");var second=Build("0.6.2");var third=Build("0.6.3");
                foreach(var artifact in new[]{first,second})
                {
                    if(mutation=="unpublished")File.WriteAllBytes(f.Files.PathOf("intake/archive-"+Hash(artifact.Zip)+".zip"),artifact.Zip);
                    else{using var intake=f.Intake(artifact);await f.Publish(intake,artifact);}
                }
                var oldest=f.Files.PathOf("intake/archive-"+Hash(first.Zip)+".zip");var recent=f.Files.PathOf("intake/archive-"+Hash(second.Zip)+".zip");
                File.SetCreationTimeUtc(oldest,new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc));File.SetCreationTimeUtc(recent,new DateTime(2021,1,1,0,0,0,DateTimeKind.Utc));
                if(mutation=="tampered")File.AppendAllText(oldest,"tampered synthetic cache");var calls=0;
                using var reader=mutation=="open_reader"?new FileStream(oldest,FileMode.Open,FileAccess.Read,FileShare.Read):null;
                using var attempted=f.Intake(third,new Handler(request=>{calls++;return Task.FromResult(Ok(third.Zip));}),phase=>{if(mutation=="changed_after_inventory"&&phase=="cache_rotation_opening"){var changed=File.ReadAllBytes(oldest);changed[0]^=1;File.WriteAllBytes(oldest,changed);}});
                await RefusedWithCode(()=>f.Acquire(attempted,third),"NATIVE_INTAKE_MAINTENANCE_REQUIRED");
                Assert(calls==0&&File.Exists(oldest)&&File.Exists(recent)&&!File.Exists(f.Files.PathOf("intake/archive-"+Hash(third.Zip)+".zip")));
                Assert(f.Before.SequenceEqual(File.ReadAllBytes(f.Files.PathOf("activation.json"))));
            });
        await check("native intake rejects unknown archive names without retiring any file",async()=>
        {
            var f=new Fixture();var artifact=Build("0.6.1");var unknown=f.Files.PathOf("intake/archive-unknown.zip");File.WriteAllBytes(unknown,[5,6,7]);var calls=0;
            using var intake=f.Intake(artifact,new Handler(_=>{calls++;return Task.FromResult(Ok(artifact.Zip));}));
            await RefusedWithCode(()=>f.Acquire(intake,artifact),"NATIVE_INTAKE_MAINTENANCE_REQUIRED");Assert(calls==0&&File.ReadAllBytes(unknown).SequenceEqual(new byte[]{5,6,7}));
        });
        await check("native intake stages two successive compatible fixture candidates",async()=>
        {
            var f=new Fixture();var a=Build("0.6.1");using(var intake=f.Intake(a))f.Verify(a,await f.Publish(intake,a));
            var b=Build("0.6.2");using(var intake=f.Intake(b))f.Verify(b,await f.Publish(intake,b));
            Assert(f.Before.SequenceEqual(File.ReadAllBytes(f.Files.PathOf("activation.json"))));
            Assert(f.Roles.Contains(InstalledFileRole.Payload)&&f.Roles.Contains(InstalledFileRole.Recovery));
        });
        await check("native intake repeat check revalidates immutable existing candidate",async()=>
        {
            var f=new Fixture();var artifact=Build("0.6.1");string root;
            using(var intake=f.Intake(artifact))root=await f.Publish(intake,artifact);
            using(var intake=f.Intake(artifact)){await f.Acquire(intake,artifact);Assert(f.Stage(intake,artifact)==root&&intake.VerifyStagedApplicationCandidate(artifact.Manifest,artifact.Version,6,default)==root);}
            File.AppendAllText(f.Files.PathOf("payloads/"+Hash(artifact.Manifest)+"/app/server/main.mjs"),"changed");
            using(var intake=f.Intake(artifact)){await Refused(()=>Task.FromResult(f.Stage(intake,artifact)));await Refused(()=>Task.FromResult(intake.VerifyStagedApplicationCandidate(artifact.Manifest,artifact.Version,6,default)));}
        });
        foreach(var destination in new[]{"http://github.com/unsafe","https://github.com.evil.invalid/payload","https://127.0.0.1/payload","https://user@github.com/payload","https://github.com:444/payload","https://release-assets.githubusercontent.com/file#fragment"})
            await check("native intake rejects download redirect "+destination,async()=>
            {
                var f=new Fixture();var artifact=Build("0.6.1");var calls=0;
                using var intake=f.Intake(artifact,new Handler(request=>{calls++;var response=new HttpResponseMessage(HttpStatusCode.Found);response.Headers.Location=new(destination);return Task.FromResult(response);}));
                await Refused(()=>f.Acquire(intake,artifact));Assert(calls==1&&!Directory.EnumerateFiles(f.Files.PathOf("intake"),"archive-*.zip").Any());
            });
        await check("native intake allows bounded public CDN redirect without credentials",async()=>
        {
            var f=new Fixture();var artifact=Build("0.6.1");var calls=0;
            using var intake=f.Intake(artifact,new Handler(request=>
            {
                calls++;Assert(request.Headers.Authorization is null);
                if(request.RequestUri!.Host=="api.github.com"){var response=new HttpResponseMessage(HttpStatusCode.Found);response.Headers.Location=new("https://release-assets.githubusercontent.com/fixture?sig=synthetic");return Task.FromResult(response);}
                return Task.FromResult(Ok(artifact.Zip));
            }));
            Assert(File.Exists(await f.Acquire(intake,artifact))&&calls==2);
        });
        await check("native intake refuses redirect loops and metadata redirects",async()=>
        {
            foreach(var metadataRedirect in new[]{true,false})
            {
                var f=new Fixture();var artifact=Build("0.6.1");var calls=0;
                using var intake=f.Intake(artifact,new Handler(request=>{calls++;var response=new HttpResponseMessage(HttpStatusCode.Found);response.Headers.Location=new("https://github.com/loop");return Task.FromResult(response);}));
                await Refused(()=>metadataRedirect?intake.CheckSystemSourceAsync(()=>{},default):f.Acquire(intake,artifact));
                Assert((metadataRedirect?calls==1:calls<=6)&&!Directory.EnumerateFiles(f.Files.PathOf("intake"),"archive-*.zip").Any());
            }
        });
        foreach(var path in new[]{"../escape","app/../escape","app/dist/a:b","app/dist/CON.txt","app/dist/trailing.","app/dist/back\\slash","app/dist/.env.local","app/dist/Data/app.sqlite","app/dist/data/file.txt","runtime/authority/tool.exe","app/dist/a/file.txt/child",
            "app/src/main.ts","app/node_modules/tsx/package.json","app/package-lock.json","app/server/extra.mjs","app/extra.txt"})
            await check("native intake refuses unsafe declared path "+path,async()=>
            {
                var f=new Fixture();var artifact=Build("0.6.1",changeFiles:items=>{items[path]=[1];if(path=="app/dist/a/file.txt/child")items["app/dist/a/file.txt"]=[2];});
                using var intake=f.Intake(artifact);await f.Acquire(intake,artifact);await Refused(()=>Task.FromResult(f.Stage(intake,artifact)));f.AssertNotStaged(artifact);
            });
        // Archive member types (symlinks, attributes) are owned by the full-system
        // archive reader before the application stager is entered.
        foreach(var mutation in new[]{"schema","arch","profile","launch","checksum","extra","collision","directory_case","private_clr","package","source","server_bundle","server_record"})
            await check("native intake refuses candidate mismatch "+mutation,async()=>
            {
                var f=new Fixture();var artifact=Build("0.6.1",schema:mutation=="schema"?7:6,changeFiles:items=>
                {
                    if(mutation=="arch"){var pe=items["runtime/node/node.exe"];BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x84),0xaa64);}
                    if(mutation=="collision"){items["app/dist/A.txt"]=[1];items["app/dist/a.txt"]=[1];}
                    if(mutation=="directory_case"){items["app/dist/Folder/a.txt"]=[1];items["app/dist/folder/b.txt"]=[1];}
                    if(mutation=="private_clr")items["app/native/Yahad.Platform.runtimeconfig.json"]=Json(new{runtimeOptions=new{framework=new{name="Microsoft.NETCore.App",version="10.0.0"}}});
                    if(mutation=="package")items["app/package.json"]=Json(new{version="0.6.9",engines=new{node=">=24.0.0 <25"},dependencies=new{tsx="4.20.6"}});
                    if(mutation=="server_record")items["app/server/native-supervisor.mjs"]=Encoding.UTF8.GetBytes("// not the recorded supervisor bundle");
                    if(mutation=="source")items["legal/build-inputs.json"]=Json(new{databaseSchema=6,appSourceCommit=new string('a',40),helperSourceCommit=new string('b',40),packagerSourceCommit=new string('a',40)});
                },changeManifest:manifest=>{if(mutation=="profile")manifest["profile"]="core-fixture";if(mutation=="launch")manifest["launch"]!["node"]="runtime/caddy/caddy.exe";},alterZip:mutation,
                changeBundle:record=>{if(mutation=="server_bundle")record["outputs"]![0]!["sha256"]=new string('0',64);});
                using var intake=f.Intake(artifact);await f.Acquire(intake,artifact);await Refused(()=>Task.FromResult(f.Stage(intake,artifact)));f.AssertNotStaged(artifact);
            });
        foreach(var phase in new[]{"download_bytes_flushed","payload_verified","payload_published"})
            await check("native intake crash boundary "+phase+" cannot expose incomplete bytes",async()=>
            {
                var f=new Fixture();var artifact=Build("0.6.1");
                using(var intake=f.Intake(artifact,fault:point=>{if(point==phase)throw new IOException("SYNTHETIC_CRASH");}))await Refused(()=>f.Publish(intake,artifact));
                Assert(File.Exists(f.Files.PathOf("intake/archive-"+Hash(artifact.Zip)+".zip"))==(phase!="download_bytes_flushed"));
                Assert(Directory.Exists(f.Files.PathOf("payloads/"+Hash(artifact.Manifest)))==(phase=="payload_published"));
                using var retry=f.Intake(artifact);f.Verify(artifact,await f.Publish(retry,artifact));
            });
        foreach(var sameRelease in new[]{true,false})await check("native interrupted body then successful "+(sameRelease?"same":"next")+" release preserves protected data",async()=>
        {
            var f=new Fixture();var artifact=Build("0.6.1");
            using(var interrupted=f.Intake(artifact,new Handler(request=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(new InterruptedBody(artifact.Zip))}))))await Refused(()=>f.Acquire(interrupted,artifact));
            var partial=Directory.GetFiles(f.Files.PathOf("intake"),"download-*.zip.partial").Single();var prefix=File.ReadAllBytes(partial);Assert(prefix.Length>0&&prefix.Length<artifact.Zip.Length);
            var next=sameRelease?artifact:Build("0.6.2");using var retry=f.Intake(next);f.Verify(next,await f.Publish(retry,next));
            Assert(sameRelease?!File.Exists(partial):File.ReadAllBytes(partial).SequenceEqual(prefix));
            Assert(f.Before.SequenceEqual(File.ReadAllBytes(f.Files.PathOf("activation.json"))));
        });
        await check("native intake cancellation and offline failure preserve protected head and cache",async()=>
        {
            foreach(var cancel in new[]{true,false})
            {
                var f=new Fixture();var artifact=Build("0.6.1");
                using var source=new CancellationTokenSource();using var intake=f.Intake(artifact,new Handler(request=>{if(cancel){source.Cancel();return Task.FromCanceled<HttpResponseMessage>(source.Token);}throw new HttpRequestException("SYNTHETIC_OFFLINE");}));
                await Refused(()=>f.Acquire(intake,artifact,source.Token));
                Assert(!Directory.EnumerateFiles(f.Files.PathOf("intake"),"archive-*.zip").Any()&&f.Before.SequenceEqual(File.ReadAllBytes(f.Files.PathOf("activation.json"))));
            }
        });
        await check("native intake rejects interrupted and oversized bodies without publication",async()=>
        {
            foreach(var overflow in new[]{true,false})
            {
                var f=new Fixture();var artifact=Build("0.6.1");var bytes=overflow?artifact.Zip.Concat(new byte[]{0}).ToArray():artifact.Zip[..^1];
                using var intake=f.Intake(artifact,new Handler(request=>Task.FromResult(Ok(bytes))));
                await Refused(()=>f.Acquire(intake,artifact));Assert(!Directory.EnumerateFiles(f.Files.PathOf("intake"),"archive-*.zip").Any());
            }
        });
        await check("native intake rechecks full policy after downloading and before archive publication",async()=>
        {
            var f=new Fixture();var artifact=Build("0.6.1");var revoked=false;var source=await Source(artifact);
            using var intake=f.Intake(artifact,fault:point=>{if(point=="download_bytes_flushed")revoked=true;});
            await Refused(()=>intake.AcquireSystemArchiveAsync(source,()=>{if(revoked)throw new UnauthorizedAccessException("FULL_POLICY_REQUIRED");},default));
            Assert(revoked&&!Directory.EnumerateFiles(f.Files.PathOf("intake"),"archive-*.zip").Any());
        });
        await check("native intake serializes separate instances before duplicate network work",async()=>
        {
            var f=new Fixture();var artifact=Build("0.6.1");var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
            using var first=f.Intake(artifact,new Handler(async request=>{calls++;entered.SetResult();await resume.Task;return Ok(artifact.Zip);}));
            var running=f.Acquire(first,artifact);await entered.Task;using(var second=f.Intake(artifact))await Refused(()=>f.Acquire(second,artifact));resume.SetResult();Assert(File.Exists(await running)&&calls==1);
        });
    }
    private static async Task Refused(Func<Task> action)
    {try{await action();}catch(Exception error)when(error is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception){return;}throw new Exception("NATIVE_INTAKE_UNEXPECTED_ADMISSION");}
    private static async Task RefusedWithCode(Func<Task> action,string code)
    {try{await action();}catch(IOException error){if(error.Message!=code)throw new Exception("NATIVE_INTAKE_WRONG_REFUSAL "+error.Message);return;}throw new Exception("NATIVE_INTAKE_UNEXPECTED_ADMISSION");}
    private static void Assert(bool value){if(!value)throw new Exception("NATIVE_INTAKE_ASSERTION");}
    private static byte[] Json(object value)=>JsonSerializer.SerializeToUtf8Bytes(value);
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static HttpResponseMessage Ok(byte[] bytes)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
    // Fixed-source facts for the artifact through the production metadata parser; no network.
    private static Task<SystemSourceRelease> Source(Artifact artifact)
    {
        var api=SystemSourceRelease.Api;var tag="v"+artifact.Version;var commit=new string('a',40);
        return SystemSourceRelease.CheckAsync((uri,_,_)=>Task.FromResult(uri.AbsoluteUri==api+"/releases/latest"
            ?Json(new{id=1,url=api+"/releases/1",tag_name=tag,draft=false,prerelease=false,assets=new[]{new{id=2,name="yahad-system-native-"+artifact.Version+".zip",state="uploaded",size=artifact.Zip.Length,digest="sha256:"+Hash(artifact.Zip),url=api+"/releases/assets/2"}}})
            :Json(new{@ref="refs/tags/"+tag,url=api+"/git/refs/tags/"+tag,@object=new{type="commit",sha=commit,url=api+"/git/commits/"+commit}})),default);
    }
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> response):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>response(request);}
    private sealed class InterruptedBody(byte[] bytes):MemoryStream(bytes,false)
    {
        private bool interrupted;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
        {if(interrupted)throw new HttpRequestException("SYNTHETIC_INTERRUPTED_BODY");interrupted=true;return await base.ReadAsync(buffer[..Math.Min(buffer.Length,(int)Math.Max(1,Length/2))],cancellationToken);}
    }
    private sealed class ReadNotificationStream(byte[] bytes,Action read):MemoryStream(bytes,false)
    {public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default){var count=await base.ReadAsync(buffer,cancellationToken);if(count>0)read();return count;}}
    internal sealed record Artifact(string Version,byte[] Zip,byte[] Manifest);
    internal static Artifact Build(string version,int schema=6,Action<Dictionary<string,byte[]>>? changeFiles=null,Action<JsonObject>? changeManifest=null,string? alterZip=null,Action<JsonObject>? changeBundle=null)
    {
        var server=new[]{"app/server/main.mjs","app/server/native-supervisor.mjs"};
        var names=new[]{"app/package.json",server[0],server[1],"app/dist/index.html","runtime/node/node.exe","app/native/Yahad.Platform.exe","app/native/Yahad.Platform.runtimeconfig.json","app/native/Yahad.Platform.deps.json","app/native/coreclr.dll","app/native/hostfxr.dll","app/native/hostpolicy.dll","app/native/System.Private.CoreLib.dll","legal/runtime-lock.json","legal/SBOM.json","legal/server-bundle.json","legal/ThirdPartyNotices-server.txt","legal/Node-LICENSE.txt","legal/DotNet-LICENSE.txt","legal/DotNet-ThirdPartyNotices.txt","runtime/ffmpeg/ffmpeg.exe","runtime/caddy/caddy.exe","legal/FFmpeg-LICENSE.txt","legal/ffmpeg-build.json","legal/Caddy-LICENSE.txt","legal/Caddy-NOTICES.txt","legal/build-inputs.json"};
        var files=names.ToDictionary(n=>n,n=>new[]{".exe",".dll",".node"}.Contains(Path.GetExtension(n))?Image():Encoding.UTF8.GetBytes("fixture"),StringComparer.Ordinal);
        files["app/package.json"]=Json(new{version,engines=new{node=">=24.0.0 <25"},dependencies=new{tsx="4.20.6"}});
        foreach(var name in server)files[name]=Encoding.UTF8.GetBytes("// bundled "+name);
        files["app/native/Yahad.Platform.runtimeconfig.json"]=Json(new{runtimeOptions=new{includedFrameworks=new[]{new{name="Microsoft.NETCore.App",version="10.0.14"}}}});
        files["app/native/Yahad.Platform.deps.json"]=Json(new{runtimeTarget=new{name="net10.0/win-x64"},targets=new Dictionary<string,object>{{"net10.0/win-x64",new Dictionary<string,object>()}}});
        files["legal/build-inputs.json"]=Json(new{databaseSchema=schema,databaseMigrationsFrom=Enumerable.Range(1,schema-1).ToArray(),appSourceCommit=new string('a',40),helperSourceCommit=new string('a',40),packagerSourceCommit=new string('a',40)});files["legal/SBOM.json"]=Json(new{fixture=true});
        var record=JsonNode.Parse(Json(new{format="yahad-server-bundle",schema=1,outputs=server.Select(name=>new{path=name,size=files[name].Length,sha256=Hash(files[name])}).ToArray()}))!.AsObject();
        changeFiles?.Invoke(files);changeBundle?.Invoke(record);files["legal/server-bundle.json"]=JsonSerializer.SerializeToUtf8Bytes(record);
        var componentPaths=new Dictionary<string,string>{{"node","runtime/node/node.exe"},{"platform","app/native/Yahad.Platform.exe"},{"ffmpeg","runtime/ffmpeg/ffmpeg.exe"},{"caddy","runtime/caddy/caddy.exe"}};
        files["legal/runtime-lock.json"]=Json(new{schema=1,target="win-x64",components=componentPaths.Select(pair=>new{id=pair.Key,path=pair.Value,sha256=Hash(files[pair.Value]),version="24.22.2",origin="project-built",sourceCommit=new string('a',40),sourceUrl=pair.Key switch{"node"=>"https://nodejs.org/dist/v24.22.2/node-v24.22.2-win-x64.zip","caddy"=>"https://github.com/caddyserver/caddy/releases/download/v2.11.7/caddy.zip","ffmpeg"=>"https://ffmpeg.org/releases/ffmpeg.tar.xz",_=>"https://github.com/NehoraiHadad/yahad"}}).ToArray()});
        var manifest=JsonNode.Parse(Json(new{schema=1,kind="windows-runtime-candidate",target="win-x64",profile="full",version,authority="application-updater",familyInstallationReady=false,machineAcceptance="not-performed",launch=new{node="runtime/node/node.exe",entry="app/server/main.mjs",helper="app/native/Yahad.Platform.exe",ffmpeg="runtime/ffmpeg/ffmpeg.exe",caddy="runtime/caddy/caddy.exe"},files=files.Select(pair=>new{path=pair.Key,size=pair.Value.Length,sha256=Hash(pair.Value)}).ToArray(),peModules=files.Where(pair=>new[]{".exe",".dll",".node"}.Contains(Path.GetExtension(pair.Key))).ToDictionary(pair=>pair.Key,_=>new{machine="x64",managed=false,imports=Array.Empty<string>()})}))!.AsObject();changeManifest?.Invoke(manifest);var bytes=JsonSerializer.SerializeToUtf8Bytes(manifest);
        using var output=new MemoryStream();using(var archive=new ZipArchive(output,ZipArchiveMode.Create,true))
        {
            void Add(string name,byte[] contents){var entry=archive.CreateEntry(name,CompressionLevel.Fastest);entry.ExternalAttributes=(0x8000|0x1a4)<<16;if(alterZip=="symlink"&&name=="app/server/main.mjs")entry.ExternalAttributes=0xa1ff<<16;using var stream=entry.Open();stream.Write(contents);}
            Add("bundle.json",bytes);foreach(var pair in files)Add(pair.Key,alterZip=="checksum"&&pair.Key=="app/server/main.mjs"?Encoding.UTF8.GetBytes("changed"):pair.Value);if(alterZip=="extra")Add("app/extra",[1]);
        }
        return new(version,output.ToArray(),bytes);
    }
    private static byte[] Image()
    {
        var bytes=new byte[2048];bytes[0]=(byte)'M';bytes[1]=(byte)'Z';
        void U16(int at,ushort value)=>BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at),value);void U32(int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
        U32(0x3c,0x80);bytes[0x80]=(byte)'P';bytes[0x81]=(byte)'E';U16(0x84,0x8664);U16(0x86,1);U16(0x94,240);U16(0x98,0x20b);U32(0x98+60,512);U32(0x98+108,16);var section=0x98+240;U32(section+8,1536);U32(section+12,0x1000);U32(section+16,1536);U32(section+20,512);return bytes;
    }
    internal sealed class Fixture
    {
        internal InstalledStateFiles Files;internal InstalledAuthorityAnchor Anchor;internal byte[] Before;internal List<InstalledFileRole> Roles=[];
        internal Fixture()
        {
            var root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());Files=InstalledStateFiles.Private(root,projection:(_,role)=>Roles.Add(role));
            Directory.CreateDirectory(Files.PathOf("intake"));Directory.CreateDirectory(Files.PathOf("payloads"));var installation=Guid.NewGuid().ToString("N");var digest=new string('a',64);
            Anchor=new(1,installation,ServiceDefinition.Name,InstalledAuthorityReader.AuthorityService,digest,digest);
            var manifest=Json(new{version="0.6.0"});var release=Hash(manifest);Directory.CreateDirectory(Files.PathOf("payloads/"+release+"/legal"));File.WriteAllBytes(Files.PathOf("payloads/"+release+"/bundle.json"),manifest);File.WriteAllBytes(Files.PathOf("payloads/"+release+"/legal/build-inputs.json"),Json(new{databaseSchema=6}));
            var web=Json(new InstalledWeb(1,installation,"https://yahad.invalid",32000));File.WriteAllBytes(Files.PathOf("web.json"),web);
            Before=Json(new InstalledActivation(1,installation,1,release,Guid.NewGuid().ToString("N"),6,6,digest,digest,digest,Hash(web),"committed",null));File.WriteAllBytes(Files.PathOf("activation.json"),Before);File.WriteAllBytes(Files.PathOf("epoch.json"),Json(new InstalledEpoch(1,installation,1,Hash(Before))));File.WriteAllBytes(Files.PathOf("bootstrap.json"),Json(new{schema=1,service=ServiceDefinition.Name,installationId=installation,activationReceiptDigest=Hash(Before)}));
        }
        internal NativeCandidateIntake Intake(Artifact artifact,HttpMessageHandler? handler=null,Action<string>? fault=null,Func<long>? availableBytes=null)=>new(Files,Anchor,()=>DateTimeOffset.UtcNow,handler??new Handler(_=>Task.FromResult(Ok(artifact.Zip))),fault,availableBytes??(()=>long.MaxValue));
        internal async Task<string> Acquire(NativeCandidateIntake intake,Artifact artifact,CancellationToken token=default)=>await intake.AcquireSystemArchiveAsync(await Source(artifact),()=>{},token);
        internal string Stage(NativeCandidateIntake intake,Artifact artifact,int schema=6)
        {
            using var archive=ZipFile.OpenRead(Files.PathOf("intake/archive-"+Hash(artifact.Zip)+".zip"));
            var entries=archive.Entries.ToDictionary(entry=>entry.FullName,entry=>entry,StringComparer.Ordinal);
            return intake.StageApplicationCandidate(artifact.Manifest,artifact.Version,schema,entries,default);
        }
        // The archive, payload and the two receipts the full-system adapter records:
        // the retained-cache inventory needs that evidence before it may rotate.
        internal async Task<string> Publish(NativeCandidateIntake intake,Artifact artifact)
        {
            var source=await Source(artifact);await intake.AcquireSystemArchiveAsync(source,()=>{},default);var root=Stage(intake,artifact);
            long bytes=0;foreach(var file in NativeJson.Read<JsonElement>(artifact.Manifest).GetProperty("files").EnumerateArray())bytes+=file.GetProperty("size").GetInt64();
            var app=Json(new IntakeReceipt(1,Anchor.InstallationId,Hash(artifact.Manifest),artifact.Version,"stable",bytes,6,Anchor.IntakePolicyDigest,Anchor.IntakePinDigest));
            var receipt=Json(new SystemSourceReceipt(2,"full-system-v1",Anchor.InstallationId,NativeCandidateIntake.Repository,source.ReleaseId,source.Tag,source.AssetId,source.AssetName,source.Bytes,source.ArchiveSha256,source.SourceCommit,source.SourceProofDigest,Hash(artifact.Manifest),Hash(artifact.Manifest),Hash(app),6,artifact.Version));
            foreach(var (path,contents) in new[]{(Files.PathOf("intake/receipt-"+Hash(app)+".json"),app),(Files.PathOf("intake/system-source-"+Hash(receipt)+".json"),receipt)})
                if(!File.Exists(path))intake.WriteIntakeRecord(path,contents);
            return root;
        }
        internal void Verify(Artifact artifact,string root)
        {
            Assert(root==Files.PathOf("payloads/"+Hash(artifact.Manifest))&&File.ReadAllBytes(Path.Combine(root,"bundle.json")).SequenceEqual(artifact.Manifest));
            using var intake=new NativeCandidateIntake(Files,Anchor,()=>DateTimeOffset.UtcNow,new Handler(_=>throw new HttpRequestException("NO_NETWORK")));
            Assert(intake.VerifyStagedApplicationCandidate(artifact.Manifest,artifact.Version,6,default)==root&&Before.SequenceEqual(File.ReadAllBytes(Files.PathOf("activation.json"))));
        }
        internal void AssertNotStaged(Artifact artifact)
        {
            Assert(!Directory.Exists(Files.PathOf("payloads/"+Hash(artifact.Manifest))));
            Assert(Before.SequenceEqual(File.ReadAllBytes(Files.PathOf("activation.json"))));
        }
    }
}

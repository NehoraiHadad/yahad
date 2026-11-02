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
internal static class NativeCandidateIntakeTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        foreach(var mutation in new[]{"missing","repository","channel","pattern","trust","installation","policy","pins","identity"})
            await check("native intake requires protected enrollment before network: "+mutation,async()=>
            {
                var f=new Fixture(enroll:false);var artifact=Build("0.6.1");var calls=0;
                if(mutation!="missing")
                {
                    var enrollment=new NativeUpdateEnrollment(1,f.Anchor.InstallationId,NativeCandidateIntake.Repository,"stable","yahad-app-native-{version}.zip","https-github-unsigned","synthetic-family","synthetic-parent",f.Anchor.IntakePolicyDigest,f.Anchor.IntakePinDigest);
                    enrollment=mutation switch{"repository"=>enrollment with{Repository="other/repo"},"channel"=>enrollment with{Channel="preview"},"pattern"=>enrollment with{AssetPattern="yachad-app.zip"},"trust"=>enrollment with{Trust="signature"},"installation"=>enrollment with{InstallationId=Guid.NewGuid().ToString("N")},"policy"=>enrollment with{PolicyDigest=new string('0',64)},"pins"=>enrollment with{PinDigest=new string('0',64)},_=>enrollment with{UserId=""}};
                    File.WriteAllBytes(f.Files.PathOf("intake/enrollment.json"),Json(enrollment));
                }
                using var intake=f.Intake(artifact,new Handler(_=>{calls++;return Task.FromResult(Ok(Metadata(artifact)));}));
                await Refused(()=>intake.CheckAndPublishAsync(default));Assert(calls==0&&!File.Exists(f.Files.PathOf("intake/available.json")));
            });
        foreach(var version in new[]{"0.5.9","0.6.0"})
            await check("native intake never downloads an older or already installed version: "+version,async()=>
            {
                var f=new Fixture();var artifact=Build(version);var calls=0;
                using var intake=f.Intake(artifact,new Handler(_=>{calls++;return Task.FromResult(Ok(Metadata(artifact)));}));
                if(version=="0.5.9")await Refused(()=>intake.CheckAndPublishAsync(default));else Assert(await intake.CheckAndPublishAsync(default) is null);
                Assert(calls==1&&!File.Exists(f.Files.PathOf("intake/available.json"))&&!Directory.EnumerateFiles(f.Files.PathOf("intake"),"archive-*.zip").Any());
            });
        await check("native intake reserves application free space before downloading",async()=>
        {
            foreach(var free in new[]{-1L,NativeCandidateIntake.MinimumFreeBytes})
            {
                var f=new Fixture();var artifact=Build("0.6.1");var calls=0;
                using var intake=f.Intake(artifact,new Handler(request=>{calls++;return Task.FromResult(Ok(Metadata(artifact)));}),availableBytes:()=>free);
                await RefusedWithCode(()=>intake.CheckAndPublishAsync(default),"NATIVE_INTAKE_DISK_SPACE");
                Assert(calls==1&&!Directory.EnumerateFiles(f.Files.PathOf("intake"),"*.zip*").Any()&&!Directory.EnumerateFileSystemEntries(f.Files.PathOf("payloads"),".intake-*").Any());
                Assert(f.Before.SequenceEqual(File.ReadAllBytes(f.Files.PathOf("activation.json"))));
            }
        });
        await check("native intake rechecks actual extraction space without allocating another cache on retries",async()=>
        {
            var f=new Fixture();var artifact=Build("0.6.1");var free=long.MaxValue;var calls=0;
            Handler Transport()=>new(request=>{calls++;return Task.FromResult(Ok(request.RequestUri!.AbsoluteUri==NativeCandidateIntake.Latest?Metadata(artifact):artifact.Zip));});
            using(var intake=f.Intake(artifact,Transport(),phase=>{if(phase=="download_verified")free=NativeCandidateIntake.MinimumFreeBytes;},()=>free))await RefusedWithCode(()=>intake.CheckAndPublishAsync(default),"NATIVE_INTAKE_DISK_SPACE");
            for(var i=0;i<3;i++){using var retry=f.Intake(artifact,Transport(),availableBytes:()=>free);await RefusedWithCode(()=>retry.CheckAndPublishAsync(default),"NATIVE_INTAKE_DISK_SPACE");}
            Assert(calls==5&&Directory.EnumerateFiles(f.Files.PathOf("intake"),"archive-*.zip").Count()==1&&!Directory.EnumerateFileSystemEntries(f.Files.PathOf("payloads"),".intake-*").Any()&&!File.Exists(f.Files.PathOf("intake/available.json")));
        });
        await check("native intake stops mid-download space loss then retries owned partial after space recovers",async()=>
        {
            var f=new Fixture();var artifact=Build("0.6.1");var free=long.MaxValue;var calls=0;
            var transport=new Handler(request=>
            {
                calls++;if(request.RequestUri!.AbsoluteUri==NativeCandidateIntake.Latest)return Task.FromResult(Ok(Metadata(artifact)));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(new ReadNotificationStream(artifact.Zip,()=>free=NativeCandidateIntake.MinimumFreeBytes))});
            });
            using(var intake=f.Intake(artifact,transport,availableBytes:()=>free))await RefusedWithCode(()=>intake.CheckAndPublishAsync(default),"NATIVE_INTAKE_DISK_SPACE");
            var residue=Directory.GetFiles(f.Files.PathOf("intake"),"download-*.zip.partial");Assert(residue.Length==1);var length=new FileInfo(residue[0]).Length;
            for(var i=0;i<3;i++)
            {
                using var retry=f.Intake(artifact,new Handler(_=>{calls++;return Task.FromResult(Ok(Metadata(artifact)));}),availableBytes:()=>free);
                await RefusedWithCode(()=>retry.CheckAndPublishAsync(default),"NATIVE_INTAKE_DISK_SPACE");
            }
            Assert(calls==5&&Directory.GetFiles(f.Files.PathOf("intake"),"download-*.zip.partial").Length==1&&new FileInfo(residue[0]).Length==length&&!File.Exists(f.Files.PathOf("intake/available.json")));
            free=long.MaxValue;using var recovered=f.Intake(artifact,availableBytes:()=>free);f.Verify(artifact,(await recovered.CheckAndPublishAsync(default))!);Assert(!File.Exists(residue[0]));
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
                    using var intake=f.Intake(artifact,new Handler(_=>{calls++;return Task.FromResult(Ok(Metadata(artifact)));}));
                    await RefusedWithCode(()=>intake.CheckAndPublishAsync(default),"NATIVE_INTAKE_MAINTENANCE_REQUIRED");
                }
                // The exclusive operation creates its one persistent process.lock.
                Assert(calls==0&&Directory.EnumerateFileSystemEntries(f.Files.Root,"*",SearchOption.AllDirectories).Count()==before+1&&!File.Exists(f.Files.PathOf("intake/available.json")));
            });
        await check("native intake rotates only oldest verified archive for third candidate and preserves all payload/data/audit",async()=>
        {
            var f=new Fixture();var first=Build("0.6.1");var second=Build("0.6.2");var third=Build("0.6.3");
            foreach(var artifact in new[]{first,second}){using var intake=f.Intake(artifact);await intake.CheckAndPublishAsync(default);}
            var oldest=f.Files.PathOf("intake/archive-"+Hash(first.Zip)+".zip");var retained=f.Files.PathOf("intake/archive-"+Hash(second.Zip)+".zip");
            File.SetCreationTimeUtc(oldest,new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc));File.SetCreationTimeUtc(retained,new DateTime(2021,1,1,0,0,0,DateTimeKind.Utc));
            Directory.CreateDirectory(f.Files.PathOf("datasets/fixture/data"));Directory.CreateDirectory(f.Files.PathOf("backups/fixture"));
            File.WriteAllBytes(f.Files.PathOf("datasets/fixture/data/synthetic.sqlite"),[1,2,3]);File.WriteAllBytes(f.Files.PathOf("backups/fixture/synthetic.backup"),[4,5,6]);File.WriteAllBytes(f.Files.PathOf("intake/unknown-preserved.txt"),[7,8,9]);
            var preserved=Directory.EnumerateFiles(f.Files.Root,"*",SearchOption.AllDirectories).Where(path=>path!=oldest&&!Path.GetFileName(path).StartsWith("archive-",StringComparison.Ordinal)&&Path.GetFileName(path)!="available.json").ToDictionary(path=>path,File.ReadAllBytes,StringComparer.Ordinal);
            var replacementDenied=false;
            using(var intake=f.Intake(third,fault:phase=>
            {
                if(phase=="cache_rotation_verified")try{File.WriteAllBytes(oldest,[42]);}catch(IOException){replacementDenied=true;}
            }))f.Verify(third,(await intake.CheckAndPublishAsync(default))!);
            Assert(replacementDenied&&!File.Exists(oldest)&&File.Exists(retained)&&File.Exists(f.Files.PathOf("intake/archive-"+Hash(third.Zip)+".zip")));
            Assert(Directory.EnumerateFiles(f.Files.PathOf("intake"),"archive-*.zip").Count()==NativeCandidateIntake.MaximumRetainedCandidates&&Directory.EnumerateFiles(f.Files.PathOf("intake"),"source-*.json").Count()==3&&Directory.EnumerateFiles(f.Files.PathOf("intake"),"receipt-*.json").Count()==3);
            foreach(var pair in preserved)Assert(File.Exists(pair.Key)&&pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key)));
            using var repeat=f.Intake(third);f.Verify(third,(await repeat.CheckAndPublishAsync(default))!);
        });
        foreach(var mutation in new[]{"tampered","unpublished","changed_after_inventory","open_reader"})
            await check("native intake cannot rotate cache with "+mutation+" evidence",async()=>
            {
                var f=new Fixture();var first=Build("0.6.1");var second=Build("0.6.2");var third=Build("0.6.3");
                foreach(var artifact in new[]{first,second})
                {
                    if(mutation=="unpublished")File.WriteAllBytes(f.Files.PathOf("intake/archive-"+Hash(artifact.Zip)+".zip"),artifact.Zip);
                    else{using var intake=f.Intake(artifact);await intake.CheckAndPublishAsync(default);}
                }
                var oldest=f.Files.PathOf("intake/archive-"+Hash(first.Zip)+".zip");var recent=f.Files.PathOf("intake/archive-"+Hash(second.Zip)+".zip");
                File.SetCreationTimeUtc(oldest,new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc));File.SetCreationTimeUtc(recent,new DateTime(2021,1,1,0,0,0,DateTimeKind.Utc));
                if(mutation=="tampered")File.AppendAllText(oldest,"tampered synthetic cache");
                var pointerPath=f.Files.PathOf("intake/available.json");var pointer=File.Exists(pointerPath)?File.ReadAllBytes(pointerPath):null;var calls=0;
                using var reader=mutation=="open_reader"?new FileStream(oldest,FileMode.Open,FileAccess.Read,FileShare.Read):null;
                using var attempted=f.Intake(third,new Handler(request=>{calls++;return Task.FromResult(Ok(request.RequestUri!.AbsoluteUri==NativeCandidateIntake.Latest?Metadata(third):third.Zip));}),phase=>{if(mutation=="changed_after_inventory"&&phase=="cache_rotation_opening"){var changed=File.ReadAllBytes(oldest);changed[0]^=1;File.WriteAllBytes(oldest,changed);}});
                await RefusedWithCode(()=>attempted.CheckAndPublishAsync(default),"NATIVE_INTAKE_MAINTENANCE_REQUIRED");
                Assert(calls==(mutation=="tampered"?0:1)&&File.Exists(oldest)&&File.Exists(recent)&&!File.Exists(f.Files.PathOf("intake/archive-"+Hash(third.Zip)+".zip")));
                Assert(pointer is null?!File.Exists(pointerPath):pointer.SequenceEqual(File.ReadAllBytes(pointerPath)));
                Assert(f.Before.SequenceEqual(File.ReadAllBytes(f.Files.PathOf("activation.json"))));
            });
        await check("native intake rejects unknown archive names without retiring any file",async()=>
        {
            var f=new Fixture();var artifact=Build("0.6.1");var unknown=f.Files.PathOf("intake/archive-unknown.zip");File.WriteAllBytes(unknown,[5,6,7]);var calls=0;
            using var intake=f.Intake(artifact,new Handler(_=>{calls++;return Task.FromResult(Ok(Metadata(artifact)));}));
            await RefusedWithCode(()=>intake.CheckAndPublishAsync(default),"NATIVE_INTAKE_MAINTENANCE_REQUIRED");Assert(calls==0&&File.ReadAllBytes(unknown).SequenceEqual(new byte[]{5,6,7}));
        });
        await check("native intake publishes two successive compatible fixture candidates through existing selection",async()=>
        {
            var f=new Fixture();var a=Build("0.6.1");using(var intake=f.Intake(a)){var selected=await intake.CheckAndPublishAsync(default);Assert(selected?.Catalog.Facts.TargetVersion=="0.6.1");f.Verify(a,selected!);}
            var b=Build("0.6.2");using(var intake=f.Intake(b)){var selected=await intake.CheckAndPublishAsync(default);Assert(selected?.Catalog.Facts.TargetVersion=="0.6.2");f.Verify(b,selected!);}
            Assert(f.Before.SequenceEqual(File.ReadAllBytes(f.Files.PathOf("activation.json"))));
            Assert(f.Roles.Contains(InstalledFileRole.Payload)&&f.Roles.Contains(InstalledFileRole.Recovery));
        });
        await check("native intake repeat check revalidates immutable existing candidate",async()=>
        {
            var f=new Fixture();var artifact=Build("0.6.1");string target;
            using(var intake=f.Intake(artifact))target=(await intake.CheckAndPublishAsync(default))!.Target;
            using(var intake=f.Intake(artifact))Assert((await intake.CheckAndPublishAsync(default))!.Target==target);
            File.AppendAllText(f.Files.PathOf("payloads/"+Hash(artifact.Manifest)+"/app/src/main.ts"),"changed");
            using(var intake=f.Intake(artifact))await Refused(()=>intake.CheckAndPublishAsync(default));
            Assert(NativeJson.Read<IntakePointer>(File.ReadAllBytes(f.Files.PathOf("intake/available.json"))).ReceiptDigest==target);
        });
        foreach(var mutation in new[]{"repository","digest_missing","digest_mismatch","size","duplicate","draft","prerelease","version","asset_url","state","linux_only","duplicate_json"})
            await check("native intake rejects source metadata "+mutation,async()=>
            {
                var f=new Fixture();var artifact=Build("0.6.1");var calls=0;
                var raw=Metadata(artifact);var root=JsonNode.Parse(raw)!.AsObject();var asset=root["assets"]![0]!.AsObject();
                switch(mutation)
                {
                    case "repository":root["url"]="https://api.github.com/repos/other/repo/releases/1";break;
                    case "digest_missing":asset.Remove("digest");break;
                    case "digest_mismatch":asset["digest"]="sha256:"+new string('0',64);break;
                    case "size":asset["size"]=NativeCandidateIntake.MaximumArchiveBytes+1;break;
                    case "duplicate":root["assets"]!.AsArray().Add(asset.DeepClone());break;
                    case "draft":root["draft"]=true;break;
                    case "prerelease":root["prerelease"]=true;break;
                    case "version":root["tag_name"]="v0.06.1";break;
                    case "asset_url":asset["url"]="https://api.github.com/repos/other/repo/releases/assets/2";break;
                    case "state":asset["state"]="starter";break;
                    case "linux_only":asset["name"]="yachad-app.zip";break;
                }
                raw=JsonSerializer.SerializeToUtf8Bytes(root);if(mutation=="duplicate_json")raw=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(raw).Replace("\"draft\":false","\"draft\":false,\"draft\":false",StringComparison.Ordinal));
                using var intake=f.Intake(artifact,new Handler(request=>{calls++;return Task.FromResult(Ok(request.RequestUri!.AbsoluteUri==NativeCandidateIntake.Latest?raw:artifact.Zip));}));
                await Refused(()=>intake.CheckAndPublishAsync(default));Assert(!File.Exists(f.Files.PathOf("intake/available.json")));Assert(calls==(mutation=="digest_mismatch"?2:1));
            });
        foreach(var destination in new[]{"http://github.com/unsafe","https://github.com.evil.invalid/payload","https://127.0.0.1/payload","https://user@github.com/payload","https://github.com:444/payload","https://release-assets.githubusercontent.com/file#fragment"})
            await check("native intake rejects download redirect "+destination,async()=>
            {
                var f=new Fixture();var artifact=Build("0.6.1");var calls=0;
                using var intake=f.Intake(artifact,new Handler(request=>{calls++;if(request.RequestUri!.AbsoluteUri==NativeCandidateIntake.Latest)return Task.FromResult(Ok(Metadata(artifact)));var response=new HttpResponseMessage(HttpStatusCode.Found);response.Headers.Location=new(destination);return Task.FromResult(response);}));
                await Refused(()=>intake.CheckAndPublishAsync(default));Assert(calls==2&&!File.Exists(f.Files.PathOf("intake/available.json")));
            });
        await check("native intake allows bounded public CDN redirect without credentials",async()=>
        {
            var f=new Fixture();var artifact=Build("0.6.1");var calls=0;
            using var intake=f.Intake(artifact,new Handler(request=>
            {
                calls++;Assert(request.Headers.Authorization is null);
                if(request.RequestUri!.AbsoluteUri==NativeCandidateIntake.Latest)return Task.FromResult(Ok(Metadata(artifact)));
                if(request.RequestUri.Host=="api.github.com"){var response=new HttpResponseMessage(HttpStatusCode.Found);response.Headers.Location=new("https://release-assets.githubusercontent.com/fixture?sig=synthetic");return Task.FromResult(response);}
                return Task.FromResult(Ok(artifact.Zip));
            }));
            Assert((await intake.CheckAndPublishAsync(default)) is not null&&calls==3);
        });
        await check("native intake refuses redirect loops and metadata redirects",async()=>
        {
            foreach(var metadataRedirect in new[]{true,false})
            {
                var f=new Fixture();var artifact=Build("0.6.1");var calls=0;
                using var intake=f.Intake(artifact,new Handler(request=>{calls++;if(!metadataRedirect&&request.RequestUri!.AbsoluteUri==NativeCandidateIntake.Latest)return Task.FromResult(Ok(Metadata(artifact)));var response=new HttpResponseMessage(HttpStatusCode.Found);response.Headers.Location=new("https://github.com/loop");return Task.FromResult(response);}));
                await Refused(()=>intake.CheckAndPublishAsync(default));Assert(calls<=7&&!File.Exists(f.Files.PathOf("intake/available.json")));
            }
        });
        foreach(var path in new[]{"../escape","app/../escape","app/a:b","app/CON.txt","app/trailing.","app/back\\slash","app/.env.local","app/Data/app.sqlite","runtime/authority/tool.exe","app/a/file.txt/child"})
            await check("native intake refuses unsafe declared path "+path,async()=>
            {
                var f=new Fixture();var artifact=Build("0.6.1",changeFiles:items=>{items[path]=[1];if(path=="app/a/file.txt/child")items["app/a/file.txt"]=[2];});
                using var intake=f.Intake(artifact);await Refused(()=>intake.CheckAndPublishAsync(default));Assert(!File.Exists(f.Files.PathOf("intake/available.json")));
            });
        foreach(var mutation in new[]{"schema","arch","profile","launch","checksum","extra","symlink","collision","directory_case","private_clr","package","source"})
            await check("native intake refuses candidate mismatch "+mutation,async()=>
            {
                var f=new Fixture();var artifact=Build("0.6.1",schema:mutation=="schema"?7:6,changeFiles:items=>
                {
                    if(mutation=="arch"){var pe=items["runtime/node/node.exe"];BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(0x84),0xaa64);}
                    if(mutation=="collision"){items["app/A.txt"]=[1];items["app/a.txt"]=[1];}
                    if(mutation=="directory_case"){items["app/Folder/a.txt"]=[1];items["app/folder/b.txt"]=[1];}
                    if(mutation=="private_clr")items["app/native/Yahad.Platform.runtimeconfig.json"]=Json(new{runtimeOptions=new{framework=new{name="Microsoft.NETCore.App",version="10.0.0"}}});
                    if(mutation=="package")items["app/package.json"]=Json(new{version="0.6.9",engines=new{node=">=24.0.0 <25"},dependencies=new{tsx="4.20.6"}});
                    if(mutation=="source")items["legal/build-inputs.json"]=Json(new{databaseSchema=6,appSourceCommit=new string('a',40),helperSourceCommit=new string('b',40),packagerSourceCommit=new string('a',40)});
                },changeManifest:manifest=>{if(mutation=="profile")manifest["profile"]="core-fixture";if(mutation=="launch")manifest["launch"]!["node"]="runtime/caddy/caddy.exe";},alterZip:mutation);
                using var intake=f.Intake(artifact);await Refused(()=>intake.CheckAndPublishAsync(default));Assert(!File.Exists(f.Files.PathOf("intake/available.json")));
            });
        foreach(var phase in new[]{"download_verified","payload_verified","payload_published","receipt_published","pointer_prepared","pointer_published"})
            await check("native intake crash boundary "+phase+" cannot expose incomplete bytes",async()=>
            {
                var f=new Fixture();var artifact=Build("0.6.1");using(var intake=f.Intake(artifact,fault:point=>{if(point==phase)throw new IOException("SYNTHETIC_CRASH");}))await Refused(()=>intake.CheckAndPublishAsync(default));
                Assert(File.Exists(f.Files.PathOf("intake/available.json"))==(phase=="pointer_published"));
                if(phase=="pointer_published")f.Verify(artifact,f.Selection.Select());
                else{using var retry=f.Intake(artifact);f.Verify(artifact,(await retry.CheckAndPublishAsync(default))!);}
            });
        foreach(var sameRelease in new[]{true,false})await check("native interrupted body then successful "+(sameRelease?"same":"next")+" release preserves protected data",async()=>
        {
            var f=new Fixture();var artifact=Build("0.6.1");
            using(var interrupted=f.Intake(artifact,new Handler(request=>Task.FromResult(request.RequestUri!.AbsoluteUri==NativeCandidateIntake.Latest?Ok(Metadata(artifact)):new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(new InterruptedBody(artifact.Zip))}))))await Refused(()=>interrupted.CheckAndPublishAsync(default));
            var partial=Directory.GetFiles(f.Files.PathOf("intake"),"download-*.zip.partial").Single();var prefix=File.ReadAllBytes(partial);Assert(prefix.Length>0&&prefix.Length<artifact.Zip.Length);
            var next=sameRelease?artifact:Build("0.6.2");using var retry=f.Intake(next);f.Verify(next,(await retry.CheckAndPublishAsync(default))!);
            Assert(sameRelease?!File.Exists(partial):File.ReadAllBytes(partial).SequenceEqual(prefix));
            Assert(f.Before.SequenceEqual(File.ReadAllBytes(f.Files.PathOf("activation.json"))));
        });
        await check("native intake cancellation and offline failure preserve protected head and prior pointer",async()=>
        {
            foreach(var cancel in new[]{true,false})
            {
                var f=new Fixture();var artifact=Build("0.6.1");var prior=Json(new{fixture="prior pointer"});File.WriteAllBytes(f.Files.PathOf("intake/available.json"),prior);
                using var source=new CancellationTokenSource();using var intake=f.Intake(artifact,new Handler(request=>{if(cancel){source.Cancel();return Task.FromCanceled<HttpResponseMessage>(source.Token);}throw new HttpRequestException("SYNTHETIC_OFFLINE");}));
                await Refused(()=>intake.CheckAndPublishAsync(source.Token));Assert(prior.SequenceEqual(File.ReadAllBytes(f.Files.PathOf("intake/available.json")))&&f.Before.SequenceEqual(File.ReadAllBytes(f.Files.PathOf("activation.json"))));
            }
        });
        await check("native intake rejects interrupted and oversized bodies without publication",async()=>
        {
            foreach(var overflow in new[]{true,false})
            {
                var f=new Fixture();var artifact=Build("0.6.1");var bytes=overflow?artifact.Zip.Concat(new byte[]{0}).ToArray():artifact.Zip[..^1];
                using var intake=f.Intake(artifact,new Handler(request=>Task.FromResult(Ok(request.RequestUri!.AbsoluteUri==NativeCandidateIntake.Latest?Metadata(artifact):bytes))));
                await Refused(()=>intake.CheckAndPublishAsync(default));Assert(!File.Exists(f.Files.PathOf("intake/available.json")));
            }
        });
        await check("native intake head race cannot publish a downloaded candidate",async()=>
        {
            var f=new Fixture();var artifact=Build("0.6.1");using var intake=f.Intake(artifact,fault:phase=>{if(phase=="payload_published")File.AppendAllText(f.Files.PathOf("activation.json")," ");});
            await Refused(()=>intake.CheckAndPublishAsync(default));Assert(!File.Exists(f.Files.PathOf("intake/available.json")));
        });
        await check("native intake rechecks enrollment after downloading and before pointer publication",async()=>
        {
            foreach(var phase in new[]{"payload_published","pointer_prepared"})
            {
                var f=new Fixture();var artifact=Build("0.6.1");using var intake=f.Intake(artifact,fault:point=>
                {if(point==phase){var path=f.Files.PathOf("intake/enrollment.json");var enrollment=NativeJson.Read<NativeUpdateEnrollment>(File.ReadAllBytes(path));File.WriteAllBytes(path,Json(enrollment with{UserId="changed-parent"}));}});
                await Refused(()=>intake.CheckAndPublishAsync(default));Assert(!File.Exists(f.Files.PathOf("intake/available.json")));
            }
        });
        await check("native intake serializes separate instances before duplicate network work",async()=>
        {
            var f=new Fixture();var artifact=Build("0.6.1");var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
            using var first=f.Intake(artifact,new Handler(async request=>{calls++;if(request.RequestUri!.AbsoluteUri==NativeCandidateIntake.Latest){entered.SetResult();await resume.Task;return Ok(Metadata(artifact));}return Ok(artifact.Zip);}));
            var running=first.CheckAndPublishAsync(default);await entered.Task;using(var second=f.Intake(artifact))await Refused(()=>second.CheckAndPublishAsync(default));resume.SetResult();Assert((await running) is not null&&calls==2);
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
    private static byte[] Metadata(Artifact artifact)=>Json(new{id=1,url="https://api.github.com/repos/"+NativeCandidateIntake.Repository+"/releases/1",tag_name="v"+artifact.Version,draft=false,prerelease=false,assets=new[]{new{id=2,name="yahad-app-native-"+artifact.Version+".zip",state="uploaded",size=artifact.Zip.Length,digest="sha256:"+Hash(artifact.Zip),url="https://api.github.com/repos/"+NativeCandidateIntake.Repository+"/releases/assets/2"}}});
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
    internal static Artifact Build(string version,int schema=6,Action<Dictionary<string,byte[]>>? changeFiles=null,Action<JsonObject>? changeManifest=null,string? alterZip=null)
    {
        var names=new[]{"app/package.json","app/package-lock.json","app/src/main.ts","app/dist/index.html","app/node_modules/tsx/package.json","app/node_modules/@esbuild/win32-x64/esbuild.exe","runtime/node/node.exe","app/native/Yahad.Platform.exe","app/native/Yahad.Platform.runtimeconfig.json","app/native/Yahad.Platform.deps.json","app/native/coreclr.dll","app/native/hostfxr.dll","app/native/hostpolicy.dll","app/native/System.Private.CoreLib.dll","legal/runtime-lock.json","legal/SBOM.json","legal/Node-LICENSE.txt","legal/DotNet-LICENSE.txt","legal/DotNet-ThirdPartyNotices.txt","runtime/ffmpeg/ffmpeg.exe","runtime/caddy/caddy.exe","legal/FFmpeg-LICENSE.txt","legal/ffmpeg-build.json","legal/Caddy-LICENSE.txt","legal/Caddy-NOTICES.txt","legal/build-inputs.json"};
        var files=names.ToDictionary(n=>n,n=>new[]{".exe",".dll",".node"}.Contains(Path.GetExtension(n))?Image():Encoding.UTF8.GetBytes("fixture"),StringComparer.Ordinal);
        var package=new{version,engines=new{node=">=24.0.0 <25"},dependencies=new{tsx="4.20.6"}};
        files["app/package.json"]=Json(package);files["app/package-lock.json"]=Json(new{version,packages=new Dictionary<string,object>{{"",package}}});
        files["app/native/Yahad.Platform.runtimeconfig.json"]=Json(new{runtimeOptions=new{includedFrameworks=new[]{new{name="Microsoft.NETCore.App",version="10.0.14"}}}});
        files["app/native/Yahad.Platform.deps.json"]=Json(new{runtimeTarget=new{name="net10.0/win-x64"},targets=new Dictionary<string,object>{{"net10.0/win-x64",new Dictionary<string,object>()}}});
        files["legal/build-inputs.json"]=Json(new{databaseSchema=schema,appSourceCommit=new string('a',40),helperSourceCommit=new string('a',40),packagerSourceCommit=new string('a',40)});files["legal/SBOM.json"]=Json(new{fixture=true});
        changeFiles?.Invoke(files);
        var componentPaths=new Dictionary<string,string>{{"node","runtime/node/node.exe"},{"platform","app/native/Yahad.Platform.exe"},{"ffmpeg","runtime/ffmpeg/ffmpeg.exe"},{"caddy","runtime/caddy/caddy.exe"}};
        files["legal/runtime-lock.json"]=Json(new{schema=1,target="win-x64",components=componentPaths.Select(pair=>new{id=pair.Key,path=pair.Value,sha256=Hash(files[pair.Value]),version="24.22.2",origin="project-built",sourceCommit=new string('a',40),sourceUrl=pair.Key switch{"node"=>"https://nodejs.org/dist/v24.22.2/node-v24.22.2-win-x64.zip","caddy"=>"https://github.com/caddyserver/caddy/releases/download/v2.11.7/caddy.zip","ffmpeg"=>"https://ffmpeg.org/releases/ffmpeg.tar.xz",_=>"https://github.com/NehoraiHadad/yahad"}}).ToArray()});
        var manifest=JsonNode.Parse(Json(new{schema=1,kind="windows-runtime-candidate",target="win-x64",profile="full",version,authority="application-updater",familyInstallationReady=false,machineAcceptance="not-performed",launch=new{node="runtime/node/node.exe",entry="app/src/main.ts",helper="app/native/Yahad.Platform.exe",ffmpeg="runtime/ffmpeg/ffmpeg.exe",caddy="runtime/caddy/caddy.exe"},files=files.Select(pair=>new{path=pair.Key,size=pair.Value.Length,sha256=Hash(pair.Value)}).ToArray(),peModules=files.Where(pair=>new[]{".exe",".dll",".node"}.Contains(Path.GetExtension(pair.Key))).ToDictionary(pair=>pair.Key,_=>new{machine="x64",managed=false,imports=Array.Empty<string>()})}))!.AsObject();changeManifest?.Invoke(manifest);var bytes=JsonSerializer.SerializeToUtf8Bytes(manifest);
        using var output=new MemoryStream();using(var archive=new ZipArchive(output,ZipArchiveMode.Create,true))
        {
            void Add(string name,byte[] contents){var entry=archive.CreateEntry(name,CompressionLevel.Fastest);entry.ExternalAttributes=(0x8000|0x1a4)<<16;if(alterZip=="symlink"&&name=="app/src/main.ts")entry.ExternalAttributes=0xa1ff<<16;using var stream=entry.Open();stream.Write(contents);}
            Add("bundle.json",bytes);foreach(var pair in files)Add(pair.Key,alterZip=="checksum"&&pair.Key=="app/src/main.ts"?Encoding.UTF8.GetBytes("changed"):pair.Value);if(alterZip=="extra")Add("app/extra",[1]);
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
        internal InstalledStateFiles Files;internal InstalledAuthorityAnchor Anchor;internal InstalledIntakeSelection Selection;internal byte[] Before;internal List<InstalledFileRole> Roles=[];
        internal Fixture(bool enroll=true)
        {
            var root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());Files=InstalledStateFiles.Private(root,projection:(_,role)=>Roles.Add(role));
            Directory.CreateDirectory(Files.PathOf("intake"));Directory.CreateDirectory(Files.PathOf("payloads"));var installation=Guid.NewGuid().ToString("N");var digest=new string('a',64);
            Anchor=new(1,installation,ServiceDefinition.Name,InstalledAuthorityReader.AuthorityService,"S-1-5-21-100-200-300-1001",digest,digest);Selection=new(Files,Anchor,()=>DateTimeOffset.UtcNow);
            if(enroll)File.WriteAllBytes(Files.PathOf("intake/enrollment.json"),Json(new NativeUpdateEnrollment(1,installation,NativeCandidateIntake.Repository,"stable","yahad-app-native-{version}.zip","https-github-unsigned","synthetic-family","synthetic-parent",digest,digest)));
            var manifest=Json(new{version="0.6.0"});var release=Hash(manifest);Directory.CreateDirectory(Files.PathOf("payloads/"+release+"/legal"));File.WriteAllBytes(Files.PathOf("payloads/"+release+"/bundle.json"),manifest);File.WriteAllBytes(Files.PathOf("payloads/"+release+"/legal/build-inputs.json"),Json(new{databaseSchema=6}));
            var web=Json(new InstalledWeb(1,installation,"https://yahad.invalid",32000));File.WriteAllBytes(Files.PathOf("web.json"),web);
            Before=Json(new InstalledActivation(1,installation,1,release,Guid.NewGuid().ToString("N"),6,6,digest,digest,digest,digest,Hash(web),"committed",null));File.WriteAllBytes(Files.PathOf("activation.json"),Before);File.WriteAllBytes(Files.PathOf("epoch.json"),Json(new InstalledEpoch(1,installation,1,Hash(Before))));File.WriteAllBytes(Files.PathOf("bootstrap.json"),Json(new{schema=1,service=ServiceDefinition.Name,installationId=installation,activationReceiptDigest=Hash(Before)}));
        }
        internal NativeCandidateIntake Intake(Artifact artifact,HttpMessageHandler? handler=null,Action<string>? fault=null,Func<long>? availableBytes=null)=>new(Files,Anchor,()=>DateTimeOffset.UtcNow,handler??new Handler(request=>Task.FromResult(Ok(request.RequestUri!.AbsoluteUri==NativeCandidateIntake.Latest?Metadata(artifact):artifact.Zip))),fault,availableBytes??(()=>long.MaxValue));
        internal void Verify(Artifact artifact,IntakeSelection selected)
        {
            var observed=Selection.Select(selected.Target);Assert(observed.Target==selected.Target&&observed.Catalog.Operation.ReleaseDigest==Hash(artifact.Manifest));
            Assert(File.ReadAllBytes(Files.PathOf("payloads/"+Hash(artifact.Manifest)+"/bundle.json")).SequenceEqual(artifact.Manifest));
            var receipt=NativeJson.Read<IntakeReceipt>(File.ReadAllBytes(Files.PathOf("intake/receipt-"+selected.Target+".json")));Assert(receipt.BundleDigest==Hash(artifact.Manifest)&&receipt.Channel=="stable"&&receipt.Schema==6);
            Assert(File.Exists(Files.PathOf("intake/source-"+selected.Target+".json"))&&Before.SequenceEqual(File.ReadAllBytes(Files.PathOf("activation.json"))));
        }
    }
}

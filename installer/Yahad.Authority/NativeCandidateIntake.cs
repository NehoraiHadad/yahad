using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yahad.Install;
using Yahad.Service;
using Microsoft.Win32.SafeHandles;

namespace Yahad.Authority;

// The existing authority's intake publisher, not an activation authority. Composition
// must first require the protected source enrollment. This class never changes mode,
// delegation, activation, bootstrap, application data, or installation permissions.
// HTTPS/GitHub is the enrolled publisher trust; hashes bind bytes, not a second signer.
internal sealed class NativeCandidateIntake : IDisposable
{
    internal const string Repository="NehoraiHadad/yahad";
    internal const string Latest="https://api.github.com/repos/"+Repository+"/releases/latest";
    internal const long MaximumArchiveBytes=2L*1024*1024*1024;
    internal const long MinimumFreeBytes=512L*1024*1024;
    internal const int MaximumRetainedCandidates=2;
    private const long MaximumPayloadBytes=2L*1024*1024*1024,MaximumFileBytes=512L*1024*1024;
    private const int MaximumFiles=50000,MaximumManifestBytes=8*1024*1024;
    private readonly InstalledStateFiles files;
    private readonly InstalledAuthorityAnchor anchor;
    private readonly HttpClient client;
    private readonly Action<string>? fault;
    private readonly Func<long> availableBytes;
    private long allocationUnit;
    private static readonly Regex Stable=new(@"^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$",RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Hosts=new(StringComparer.Ordinal){"api.github.com","github.com","objects.githubusercontent.com","release-assets.githubusercontent.com"};
    private static readonly Dictionary<string,string> Launch=new(){["node"]="runtime/node/node.exe",["entry"]="app/server/main.mjs",["helper"]="app/native/Yahad.Platform.exe",["ffmpeg"]="runtime/ffmpeg/ffmpeg.exe",["caddy"]="runtime/caddy/caddy.exe"};
    // One self-contained bundle per fixed process entry; no source tree, loader or node_modules.
    private static readonly string[] Server=["app/server/main.mjs","app/server/native-migrate-v1.mjs","app/server/native-supervisor.mjs"];
    private static readonly string[] AppRoots=["app/server/","app/native/","app/dist/","app/fixtures/","app/deploy/"];
    private static readonly string[] Required=["app/package.json","app/server/main.mjs","app/server/native-migrate-v1.mjs","app/server/native-supervisor.mjs","app/dist/index.html","runtime/node/node.exe","app/native/Yahad.Platform.exe","app/native/Yahad.Platform.runtimeconfig.json","app/native/Yahad.Platform.deps.json","app/native/coreclr.dll","app/native/hostfxr.dll","app/native/hostpolicy.dll","app/native/System.Private.CoreLib.dll","legal/runtime-lock.json","legal/SBOM.json","legal/server-bundle.json","legal/ThirdPartyNotices-server.txt","legal/Node-LICENSE.txt","legal/DotNet-LICENSE.txt","legal/DotNet-ThirdPartyNotices.txt","runtime/ffmpeg/ffmpeg.exe","runtime/caddy/caddy.exe","legal/FFmpeg-LICENSE.txt","legal/ffmpeg-build.json","legal/Caddy-LICENSE.txt","legal/Caddy-NOTICES.txt","legal/build-inputs.json"];

    internal NativeCandidateIntake(InstalledStateFiles files,InstalledAuthorityAnchor anchor,Func<DateTimeOffset> clock,HttpMessageHandler? transport=null,Action<string>? fault=null,Func<long>? availableBytes=null)
    {
        this.files=files;this.anchor=anchor;this.fault=fault;
        this.availableBytes=availableBytes??(()=>new DriveInfo(Path.GetPathRoot(files.Root)!).AvailableFreeSpace);
        // The injected transport is trusted test/composition code, never IPC input.
        client=new(transport??new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None}){Timeout=Timeout.InfiniteTimeSpan};
    }
    public void Dispose()=>client.Dispose();
    internal string StateRoot=>files.Root;
    internal async Task<SystemSourceRelease> CheckSystemSourceAsync(Action requireFullPolicy,CancellationToken cancellation)
    {
        requireFullPolicy();using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var result=await SystemSourceRelease.CheckAsync(async(uri,maximum,token)=>
        {
            requireFullPolicy();var url=uri.AbsoluteUri;var api=SystemSourceRelease.Api;
            if(url!=api+"/releases/latest"&&!Regex.IsMatch(url,"^"+Regex.Escape(api)+@"/git/ref/tags/v?(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$",RegexOptions.CultureInvariant)&&!Regex.IsMatch(url,"^"+Regex.Escape(api)+"/git/tags/[a-f0-9]{40}$",RegexOptions.CultureInvariant))throw Error("SYSTEM_SOURCE_URL");
            if(maximum is <1 or >1024*1024)throw Error("SYSTEM_SOURCE_BOUND");
            using var response=await Get(uri,"application/vnd.github+json",false,token);using var input=await response.Content.ReadAsStreamAsync(token);using var output=new MemoryStream();await CopyBounded(input,output,maximum,null,null,token);return output.ToArray();
        },deadline.Token);requireFullPolicy();return result;
    }
    internal async Task<string> AcquireSystemArchiveAsync(SystemSourceRelease source,Action requireFullPolicy,CancellationToken cancellation)
    {
        requireFullPolicy();files.ValidateRoot();files.EnsureDirectory(files.PathOf("intake"));files.EnsureDirectory(files.PathOf("payloads"));
        using var operation=files.Lease(files.PathOf("intake"));using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromMinutes(10));var token=deadline.Token;
        allocationUnit=VolumeAllocationUnit();var recovery=new NativeIntakeRecovery(files,fault:fault,requireSpace:bytes=>RequireSpace(Allocated(bytes)+allocationUnit));recovery.Reconcile(token);var retained=InspectRetained(token,recovery);var archive=files.PathOf("intake/archive-"+source.ArchiveSha256+".zip");
        if(File.Exists(archive)){using var input=new PinnedStream(files.Open(archive));CopyVerified(input,Stream.Null,source.Bytes,source.ArchiveSha256,token);return archive;}
        var release=new Release(source.ReleaseId,source.Tag,source.Version,source.AssetId,source.AssetName,source.Bytes,source.ArchiveSha256,source.AssetUrl);
        requireFullPolicy();RotateArchiveCache(retained,source.ArchiveSha256,token);RequireSpace(checked(Allocated(source.Bytes-recovery.ReusableDownloadBytes(Hash(JsonSerializer.SerializeToUtf8Bytes(release)),source.ArchiveSha256,source.Bytes))+4*allocationUnit));
        await Download(release,recovery,token,requireFullPolicy);return archive;
    }

    // Invoked only by the explicitly authorized full-system adapter. This stages
    // app bytes; it cannot create approvals, change enrollment or select a head.
    // The legacy entry point still derives its schema from the installed head.
    internal string StageApplicationCandidate(byte[] manifest,string version,int targetSchema,Dictionary<string,ZipArchiveEntry> entries,CancellationToken token)
    {
        files.ValidateRoot();files.EnsureDirectory(files.PathOf("payloads"));allocationUnit=VolumeAllocationUnit();
        var recovery=new NativeIntakeRecovery(files,fault:fault,requireSpace:bytes=>RequireSpace(Allocated(bytes)+allocationUnit));recovery.Reconcile(token);
        if(Directory.EnumerateFileSystemEntries(files.PathOf("payloads"),".intake-*").Any())throw Error("MAINTENANCE_REQUIRED");
        var plan=Plan(manifest,version,targetSchema,entries);return StageApplicationPlan(manifest,plan,Hash(manifest),entries,token);
    }
    internal string VerifyStagedApplicationCandidate(byte[] manifest,string version,int targetSchema,CancellationToken token)
    {var plan=Plan(manifest,version,targetSchema,null);var root=files.PathOf("payloads/"+Hash(manifest));VerifyPublished(root,manifest,plan,token);return root;}
    private string StageApplicationPlan(byte[] manifest,CandidatePlan plan,string digest,Dictionary<string,ZipArchiveEntry> entries,CancellationToken token)
    {
        var destination=files.PathOf("payloads/"+digest);
        if(Directory.Exists(destination)){VerifyPublished(destination,manifest,plan,token);return destination;}
        var extractBytes=Allocated(manifest.Length);foreach(var item in plan.Files)extractBytes=checked(extractBytes+Allocated(item.Size));
        var directoryNames=new HashSet<string>(StringComparer.Ordinal);foreach(var item in plan.Files)for(var at=item.Path.IndexOf('/');at>=0;at=item.Path.IndexOf('/',at+1))directoryNames.Add(item.Path[..at]);
        RequireSpace(checked(extractBytes+(directoryNames.Count+4L)*allocationUnit));
        var recovery=new NativeIntakeRecovery(files,fault:fault,requireSpace:bytes=>RequireSpace(Allocated(bytes)+allocationUnit));
        var staging=recovery.Stage("payload",digest,plan.Files.Select(f=>new NativeIntakeMember(f.Path,f.Size,f.Sha256)).Append(new("bundle.json",manifest.Length,digest)));
        foreach(var item in plan.Files)
        {
            token.ThrowIfCancellationRequested();var path=Child(staging,item.Path);CreateParents(staging,Path.GetDirectoryName(path)!);
            using var input=entries[item.Path].Open();RequireSpace(Allocated(item.Size)+allocationUnit);using var output=files.Create(path);CopyVerified(input,output,item.Size,item.Sha256,token,bytes=>RequireSpace(Allocated(bytes)+allocationUnit));output.Flush(true);
        }
        WriteOnce(Path.Combine(staging,"bundle.json"),manifest);VerifyMetadata(staging,plan,token);fault?.Invoke("payload_verified");token.ThrowIfCancellationRequested();
        recovery.PublishedDirectory(staging,destination);fault?.Invoke("payload_published");return destination;
    }
    internal static void PublishProtectedPointer(string from,string to,InstalledStateFiles state)
    {
        var pointer=state.PathOf("intake/system-available.json");if(to!=pointer||!from.StartsWith(pointer+".partial-",StringComparison.Ordinal)||!Guid.TryParseExact(from[(pointer.Length+9)..],"N",out _))throw Error("SYSTEM_POINTER_PATH");
        using var parent=state.PinDirectory(state.PathOf("intake"));Move(from,to,true);
    }
    internal void WriteIntakeRecord(string path,byte[] bytes)
    {if(bytes.Length is <1 or >32768||!path.StartsWith(files.PathOf("intake")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw Error("SYSTEM_RECORD_PATH");WriteOnce(path,bytes);}

    private async Task<byte[]> FetchMetadata(CancellationToken token)
    {
        using var response=await Get(new(Latest),"application/vnd.github+json",false,token);
        using var stream=await response.Content.ReadAsStreamAsync(token);using var output=new MemoryStream();
        await CopyBounded(stream,output,1024*1024,null,null,token);return output.ToArray();
    }
    private async Task Download(Release release,NativeIntakeRecovery recovery,CancellationToken token,Action requireEnrollment)
    {
        using var response=await Get(new(release.Url),"application/octet-stream",true,token);
        if(response.Content.Headers.ContentLength is {} length&&length!=release.Size)throw Error("DOWNLOAD_SIZE");
        using var input=await response.Content.ReadAsStreamAsync(token);using var owned=recovery.Download(Hash(JsonSerializer.SerializeToUtf8Bytes(release)),release.Sha256,release.Size);
        await CopyBounded(input,owned.Stream,release.Size,release.Size,release.Sha256,token,bytes=>RequireSpace(Allocated(bytes)+allocationUnit));owned.Stream.Flush(true);
        fault?.Invoke("download_bytes_flushed");requireEnrollment();token.ThrowIfCancellationRequested();owned.Publish();
    }
    private async Task<HttpResponseMessage> Get(Uri start,string accept,bool redirect,CancellationToken token)
    {
        var next=start;
        for(var count=0;count<=5;count++)
        {
            SafeUrl(next);using var request=new HttpRequestMessage(HttpMethod.Get,next);
            request.Headers.Add("User-Agent","Yahad-native-authority");request.Headers.Add("Accept",accept);request.Headers.Add("X-GitHub-Api-Version","2022-11-28");
            var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
            if(response.RequestMessage?.RequestUri is {} actual&&actual!=next){response.Dispose();throw Error("AUTOMATIC_REDIRECT");}
            if(response.StatusCode==HttpStatusCode.OK)
            {if(response.Content.Headers.ContentEncoding.Count!=0){response.Dispose();throw Error("CONTENT_ENCODING");}return response;}
            if(redirect&&((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)&&response.Headers.Location is {} location)
            {var following=new Uri(next,location);response.Dispose();SafeUrl(following);next=following;continue;}
            response.Dispose();throw Error("HTTP");
        }
        throw Error("REDIRECT_LIMIT");
    }
    private static void SafeUrl(Uri uri)
    {if(!uri.IsAbsoluteUri||uri.Scheme!="https"||!Hosts.Contains(uri.IdnHost)||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.Fragment.Length!=0)throw Error("SOURCE_URL");}
    private static Release ParseRelease(byte[] bytes)
    {
        var root=NativeJson.Read<JsonElement>(bytes);var id=Long(root,"id");var tag=String(root,"tag_name");var version=tag.StartsWith('v')?tag[1..]:tag;RequireVersion(version);
        if(id<1||root.GetProperty("draft").ValueKind!=JsonValueKind.False||root.GetProperty("prerelease").ValueKind!=JsonValueKind.False||String(root,"url")!="https://api.github.com/repos/"+Repository+"/releases/"+id)throw Error("RELEASE");
        var assets=root.GetProperty("assets");if(assets.ValueKind!=JsonValueKind.Array||assets.GetArrayLength()>1000)throw Error("ASSETS");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);Release? selected=null;var expected="yahad-app-native-"+version+".zip";
        foreach(var asset in assets.EnumerateArray())
        {
            var name=String(asset,"name");if(!names.Add(name))throw Error("DUPLICATE_ASSET");if(name!=expected)continue;
            var assetId=Long(asset,"id");var size=Long(asset,"size");var sha=String(asset,"digest");var url=String(asset,"url");
            if(assetId<1||size is <1 or >MaximumArchiveBytes||String(asset,"state")!="uploaded"||!Regex.IsMatch(sha,"^sha256:[a-f0-9]{64}$",RegexOptions.CultureInvariant)||url!="https://api.github.com/repos/"+Repository+"/releases/assets/"+assetId)throw Error("ASSET_IDENTITY");
            selected=new(id,tag,version,assetId,name,size,sha[7..],url);
        }
        return selected??throw Error("NATIVE_ASSET_MISSING");
    }

    private static Dictionary<string,ZipArchiveEntry> ArchiveEntries(ZipArchive archive)
    {
        if(archive.Entries.Count is <2 or >MaximumFiles+1)throw Error("ARCHIVE_COUNT");
        var exact=new Dictionary<string,ZipArchiveEntry>(StringComparer.Ordinal);var folded=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in archive.Entries)
        {
            var type=(entry.ExternalAttributes>>16)&0xf000;
            if(!folded.Add(entry.FullName)||!exact.TryAdd(entry.FullName,entry)||entry.Name.Length==0||type is not(0 or 0x8000)||(entry.ExternalAttributes&0x410)!=0)throw Error("ARCHIVE_MEMBER");
            if(entry.FullName!="bundle.json")ValidatePath(entry.FullName);
        }
        if(!exact.TryGetValue("bundle.json",out var metadata)||metadata.Length is <1 or >MaximumManifestBytes)throw Error("MANIFEST_BOUND");return exact;
    }
    private static CandidatePlan Plan(byte[] manifest,string version,int schema,Dictionary<string,ZipArchiveEntry>? entries)
    {
        var value=NativeJson.Read<JsonElement>(manifest);
        if(Int(value,"schema")!=1||String(value,"kind")!="windows-runtime-candidate"||String(value,"target")!="win-x64"||String(value,"profile")!="full"||String(value,"authority")!="application-updater"||String(value,"version")!=version||value.GetProperty("familyInstallationReady").ValueKind!=JsonValueKind.False)throw Error("MANIFEST_CONTRACT");
        var launch=value.GetProperty("launch");foreach(var pair in Launch)if(String(launch,pair.Key)!=pair.Value)throw Error("LAUNCH");
        var records=value.GetProperty("files");if(records.ValueKind!=JsonValueKind.Array||records.GetArrayLength() is <1 or >MaximumFiles)throw Error("FILE_COUNT");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var exact=new HashSet<string>(StringComparer.Ordinal);var result=new List<CandidateFile>();long total=0;
        foreach(var record in records.EnumerateArray())
        {
            var name=String(record,"path");ValidatePath(name);var size=Long(record,"size");var sha=String(record,"sha256");
            if(!names.Add(name)||!exact.Add(name)||size is <0 or >MaximumFileBytes||(total+=size)>MaximumPayloadBytes||!Regex.IsMatch(sha,"^[a-f0-9]{64}$",RegexOptions.CultureInvariant)||entries is not null&&(!entries.TryGetValue(name,out var entry)||entry.Length!=size))throw Error("FILE_RECORD");
            result.Add(new(name,size,sha));
        }
        var prefixes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var name in names)for(var at=name.IndexOf('/');at>=0;at=name.IndexOf('/',at+1))
        {
            var prefix=name[..at];if(names.Contains(prefix)||prefixes.TryGetValue(prefix,out var prior)&&prior!=prefix)throw Error("PATH_COLLISION");prefixes[prefix]=prefix;
        }
        if(total<1||entries is not null&&entries.Count!=result.Count+1||Required.Any(n=>!exact.Contains(n)))throw Error("PAYLOAD_CLOSURE");
        return new(result,total,schema,version,value);
    }
    internal static void ValidatePath(string name)
    {
        if(name.Length>240||!(name.StartsWith("app/",StringComparison.Ordinal)||name.StartsWith("runtime/",StringComparison.Ordinal)||name.StartsWith("legal/",StringComparison.Ordinal))||name.Contains('\\')||Regex.IsMatch(name,@"\.(key|pem|sqlite(?:-wal|-shm)?|backup)$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))throw Error("PATH");
        if(name.StartsWith("runtime/",StringComparison.Ordinal)&&!name.StartsWith("runtime/node/",StringComparison.Ordinal)&&!name.StartsWith("runtime/ffmpeg/",StringComparison.Ordinal)&&!name.StartsWith("runtime/caddy/",StringComparison.Ordinal))throw Error("RUNTIME_PATH");
        if(name.StartsWith("app/",StringComparison.Ordinal)&&name!="app/package.json"&&!AppRoots.Any(root=>name.StartsWith(root,StringComparison.Ordinal))||name.StartsWith("app/server/",StringComparison.Ordinal)&&!Server.Contains(name,StringComparer.Ordinal))throw Error("APP_PATH");
        var parts=name.Split('/');if(parts.Length>64)throw Error("DEPTH");
        foreach(var part in parts)
            if(part is "" or "." or ".."||part.Equals(".git",StringComparison.OrdinalIgnoreCase)||part.StartsWith(".env",StringComparison.OrdinalIgnoreCase)||part.EndsWith('.')||part.EndsWith(' ')||part.IndexOfAny(Path.GetInvalidFileNameChars())>=0||Regex.IsMatch(part,@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\..*)?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)||new[]{"data","backups","secrets","updatestate"}.Contains(part,StringComparer.OrdinalIgnoreCase))throw Error("PATH");
    }
    private void VerifyPublished(string root,byte[] manifest,CandidatePlan plan,CancellationToken token)
    {
        using var pin=files.PinDirectory(root);if(!files.Read(Path.Combine(root,"bundle.json"),MaximumManifestBytes).SequenceEqual(manifest))throw Error("EXISTING_PAYLOAD");
        var expected=new HashSet<string>(plan.Files.Select(f=>f.Path),StringComparer.Ordinal){"bundle.json"};var observed=new HashSet<string>(StringComparer.Ordinal);
        var directories=new HashSet<string>(StringComparer.Ordinal);var count=0;
        foreach(var name in expected)for(var at=name.IndexOf('/');at>=0;at=name.IndexOf('/',at+1))directories.Add(name[..at]);
        void Walk(string directory,int depth)
        {
            if(depth>64)throw Error("DEPTH");using var held=files.PinDirectory(directory);
            foreach(var path in Directory.EnumerateFileSystemEntries(directory))
            {token.ThrowIfCancellationRequested();if(++count>150000)throw Error("EXISTING_PAYLOAD");if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw Error("REPARSE");var relative=Path.GetRelativePath(root,path).Replace('\\','/');if(Directory.Exists(path)){if(!directories.Contains(relative))throw Error("EXISTING_PAYLOAD");Walk(path,depth+1);}else if(!observed.Add(relative)||observed.Count>MaximumFiles+1)throw Error("EXISTING_PAYLOAD");}
        }
        Walk(root,0);if(!observed.SetEquals(expected))throw Error("EXISTING_PAYLOAD");
        foreach(var item in plan.Files)
        {using var input=new PinnedStream(files.Open(Child(root,item.Path)));CopyVerified(input,Stream.Null,item.Size,item.Sha256,token);}
        VerifyMetadata(root,plan,token);
    }
    private void VerifyMetadata(string root,CandidatePlan plan,CancellationToken token)
    {
        JsonElement Json(string name,int bound=65536)=>NativeJson.Read<JsonElement>(files.Read(Child(root,name),bound));
        var build=Json("legal/build-inputs.json");if(Int(build,"databaseSchema")!=plan.Schema)throw Error("SCHEMA");
        var source=String(build,"appSourceCommit");if(!Regex.IsMatch(source,"^[a-f0-9]{40}$",RegexOptions.CultureInvariant)||String(build,"helperSourceCommit")!=source||String(build,"packagerSourceCommit")!=source)throw Error("BUILD_IDENTITY");
        var package=Json("app/package.json");
        if(String(package,"version")!=plan.Version||String(package.GetProperty("engines"),"node")!=">=24.0.0 <25")throw Error("PACKAGE_IDENTITY");
        // The bundler's record pins each shipped server entry.
        var server=Json("legal/server-bundle.json",1024*1024);var outputs=server.GetProperty("outputs");var bundled=new HashSet<string>(StringComparer.Ordinal);
        if(String(server,"format")!="yahad-server-bundle"||Int(server,"schema")!=1||outputs.GetArrayLength()!=Server.Length)throw Error("SERVER_BUNDLE");
        foreach(var output in outputs.EnumerateArray())
        {var path=String(output,"path");var file=plan.Files.SingleOrDefault(f=>f.Path==path);if(!Server.Contains(path,StringComparer.Ordinal)||!bundled.Add(path)||file is null||file.Sha256!=String(output,"sha256")||file.Size!=Long(output,"size"))throw Error("SERVER_BUNDLE");}
        var runtime=Json("app/native/Yahad.Platform.runtimeconfig.json").GetProperty("runtimeOptions");
        if(runtime.TryGetProperty("framework",out _)||runtime.TryGetProperty("frameworks",out _)||!runtime.GetProperty("includedFrameworks").EnumerateArray().Any(f=>String(f,"name")=="Microsoft.NETCore.App"&&String(f,"version").StartsWith("10.",StringComparison.Ordinal)))throw Error("PRIVATE_CLR");
        var dependencyMap=Json("app/native/Yahad.Platform.deps.json",16*1024*1024);var target=String(dependencyMap.GetProperty("runtimeTarget"),"name");
        if(!target.EndsWith("/win-x64",StringComparison.Ordinal))throw Error("PRIVATE_CLR");
        foreach(var library in dependencyMap.GetProperty("targets").GetProperty(target).EnumerateObject())foreach(var category in new[]{"runtime","native"})if(library.Value.TryGetProperty(category,out var assets))foreach(var asset in assets.EnumerateObject())
        {var leaf=asset.Name.Split('/')[^1];if(leaf.Length==0||leaf.Contains('\\')||!plan.Files.Any(f=>f.Path=="app/native/"+leaf))throw Error("PRIVATE_CLR_DEPENDENCY");}
        var runtimeLock=Json("legal/runtime-lock.json");var components=runtimeLock.GetProperty("components");
        if(Int(runtimeLock,"schema")!=1||String(runtimeLock,"target")!="win-x64"||components.GetArrayLength()!=4)throw Error("RUNTIME_LOCK");
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var component in components.EnumerateArray())
        {
            var id=String(component,"id");var key=id=="platform"?"helper":id;
            if(!ids.Add(id)||!Launch.TryGetValue(key,out var path)||id is not("platform" or "node" or "ffmpeg" or "caddy")||String(component,"path")!=path||plan.Files.Single(f=>f.Path==path).Sha256!=String(component,"sha256"))throw Error("RUNTIME_LOCK");
            if(!Uri.TryCreate(String(component,"sourceUrl"),UriKind.Absolute,out var url)||url.Scheme!="https"||url.UserInfo!=""||url.Query!=""||url.Fragment!="")throw Error("RUNTIME_PROVENANCE");
            if(id=="platform"&&(String(component,"origin")!="project-built"||String(component,"sourceCommit")!=source)||id=="node"&&(url.Host!="nodejs.org"||!Regex.IsMatch(String(component,"version"),@"^24\.\d+\.\d+$",RegexOptions.CultureInvariant))||id=="caddy"&&(url.Host!="github.com"||!url.AbsolutePath.StartsWith("/caddyserver/caddy/releases/download/",StringComparison.Ordinal)))throw Error("RUNTIME_PROVENANCE");
        }
        _=Json("legal/SBOM.json",16*1024*1024);
        var declared=plan.Manifest.GetProperty("peModules");var modules=plan.Files.Where(f=>new[]{".exe",".dll",".node"}.Contains(Path.GetExtension(f.Path),StringComparer.OrdinalIgnoreCase)).ToArray();
        if(declared.ValueKind!=JsonValueKind.Object||declared.EnumerateObject().Count()!=modules.Length)throw Error("PE_INVENTORY");
        foreach(var module in modules)
        {
            token.ThrowIfCancellationRequested();using var stream=new PinnedStream(files.Open(Child(root,module.Path)));using var pe=new PEReader(stream);
            var headers=pe.PEHeaders;var managed=headers.CorHeader is {} clr&&(clr.Flags&CorFlags.ILOnly)!=0&&(clr.Flags&CorFlags.Requires32Bit)==0;
            if(headers.PEHeader is null||headers.CoffHeader.Machine!=Machine.Amd64&&!(headers.CoffHeader.Machine==Machine.I386&&managed&&headers.PEHeader.Magic==PEMagic.PE32)||module.Path.StartsWith("runtime/",StringComparison.Ordinal)&&managed)throw Error("PE_ARCHITECTURE");
            var description=declared.GetProperty(module.Path);if(String(description,"machine")!=(managed?"anycpu-il":"x64")||description.GetProperty("managed").GetBoolean()!=managed)throw Error("PE_INVENTORY");
        }
        // Import/provenance audits remain the existing offline packager's job. No
        // executable is loaded to inspect it; start-time byte/closure checks remain.
    }

    private static async Task CopyBounded(Stream input,Stream output,long maximum,long? exact,string? digest,CancellationToken token,Action<long>? beforeWrite=null)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[65536];long total=0;
        while(true){var count=await input.ReadAsync(buffer,token);if(count==0)break;if((total+=count)>maximum)throw Error("DOWNLOAD_BOUND");hash.AppendData(buffer.AsSpan(0,count));beforeWrite?.Invoke(count);await output.WriteAsync(buffer.AsMemory(0,count),token);}
        if(total<1||exact is {} size&&total!=size)throw Error("DOWNLOAD_SIZE");if(digest is not null&&Convert.ToHexStringLower(hash.GetHashAndReset())!=digest)throw Error("DOWNLOAD_DIGEST");
    }
    private static void CopyVerified(Stream input,Stream output,long size,string digest,CancellationToken token,Action<long>? beforeWrite=null)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[65536];long total=0;
        while(true){token.ThrowIfCancellationRequested();var count=input.Read(buffer);if(count==0)break;if((total+=count)>size)throw Error("EXPANSION");hash.AppendData(buffer.AsSpan(0,count));beforeWrite?.Invoke(count);output.Write(buffer,0,count);}
        if(total!=size||Convert.ToHexStringLower(hash.GetHashAndReset())!=digest)throw Error("FILE_DIGEST");
    }
    internal static byte[] ReadEntry(ZipArchiveEntry entry,int maximum,CancellationToken token)
    {if(entry.Length is <1||entry.Length>maximum)throw Error("MANIFEST_BOUND");using var input=entry.Open();using var output=new MemoryStream();var buffer=new byte[65536];int count;while((count=input.Read(buffer))!=0){token.ThrowIfCancellationRequested();if(output.Length+count>maximum)throw Error("MANIFEST_BOUND");output.Write(buffer,0,count);}if(output.Length!=entry.Length)throw Error("MANIFEST_BOUND");return output.ToArray();}
    internal static void CheckCentralDirectory(Stream stream)
    {
        // ZIP32 suffices for the current deterministic <=2GiB / <=50001-member
        // format. Bound central-directory materialization before ZipArchive allocates.
        var length=stream.Length;if(length<22||length>MaximumArchiveBytes)throw Error("ZIP_DIRECTORY");var tail=new byte[(int)Math.Min(length,65557)];stream.Position=length-tail.Length;stream.ReadExactly(tail);
        for(var i=tail.Length-22;i>=0;i--)
        {
            if(BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i))!=0x06054b50||i+22+BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i+20))!=tail.Length)continue;
            var count=BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i+10));var size=BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i+12));var offset=BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i+16));
            if(BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i+4))!=0||BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i+6))!=0||count!=BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i+8))||count is <2 or >MaximumFiles+1||size>32*1024*1024||(long)offset+size!=length-tail.Length+i)throw Error("ZIP_DIRECTORY");
            stream.Position=offset;var header=new byte[46];
            for(var member=0;member<count;member++)
            {
                if(stream.Position+46>(long)offset+size)throw Error("ZIP_DIRECTORY");stream.ReadExactly(header);
                var flags=BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));var method=BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
                var nameLength=BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));var extra=BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));var comment=BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
                if(BinaryPrimitives.ReadUInt32LittleEndian(header)!=0x02014b50||(flags&~0x80e)!=0||method is not(0 or 8)||nameLength is <1 or >1024||BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34))!=0||BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20))==uint.MaxValue||BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24))==uint.MaxValue||BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(42))>=offset)throw Error("ZIP_DIRECTORY");
                stream.Position+=nameLength+extra+comment;if(stream.Position>(long)offset+size)throw Error("ZIP_DIRECTORY");
            }
            if(stream.Position!=(long)offset+size)throw Error("ZIP_DIRECTORY");
            stream.Position=0;return;
        }
        throw Error("ZIP_DIRECTORY");
    }
    private string Child(string root,string relative)=>files.PathOf(Path.GetRelativePath(files.Root,Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar))));
    private void CreateParents(string root,string directory)
    {if(directory==root)return;CreateParents(root,Path.GetDirectoryName(directory)!);if(Directory.Exists(directory)){using var pin=files.PinDirectory(directory);}else files.CreateDirectory(directory);}
    private void WriteOnce(string path,byte[] bytes){RequireSpace(Allocated(bytes.Length)+allocationUnit);using var file=files.Create(path);file.Write(bytes);file.Flush(true);}
    private void WriteImmutable(string path,byte[] bytes)
    {if(File.Exists(path)){if(!files.Read(path,32768).SequenceEqual(bytes))throw Error("IMMUTABLE_COLLISION");return;}WriteOnce(path,bytes);}
    private JsonElement ReadJson(string name,int maximum)=>NativeJson.Read<JsonElement>(files.Read(files.PathOf(name),maximum));
    private static string String(JsonElement value,string name)=>value.GetProperty(name).GetString()??throw Error("JSON_STRING");
    private static long Long(JsonElement value,string name)=>value.GetProperty(name).GetInt64();
    private static int Int(JsonElement value,string name)=>value.GetProperty(name).GetInt32();
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void RequireVersion(string version){if(!Stable.IsMatch(version))throw Error("VERSION");}
    private static IOException Error(string code)=>new("NATIVE_INTAKE_"+code);
    private long Allocated(long bytes)=>checked(((Math.Max(1,bytes)+allocationUnit-1)/allocationUnit)*allocationUnit);
    private void RequireSpace(long additional)
    {var free=availableBytes();if(additional<0||free<0||free<checked(MinimumFreeBytes+additional))throw Error("DISK_SPACE");}
    private long VolumeAllocationUnit()
    {
        if(!GetDiskFreeSpaceW(Path.GetPathRoot(files.Root)!,out var sectors,out var bytes,out _,out _))throw Error("VOLUME_GEOMETRY");
        var unit=(long)sectors*bytes;if(unit is <512 or >2*1024*1024)throw Error("VOLUME_GEOMETRY");return unit;
    }
    private Retained InspectRetained(CancellationToken token,NativeIntakeRecovery recovery)
    {
        var count=0;var sources=0;long bytes=0;var archives=new List<CachedArchive>();var published=new Dictionary<string,long>(StringComparer.Ordinal);
        foreach(var path in Directory.EnumerateFileSystemEntries(files.PathOf("intake")))
        {
            token.ThrowIfCancellationRequested();if(++count>512)throw Error("MAINTENANCE_REQUIRED");var name=Path.GetFileName(path);
            if(name.StartsWith("download-",StringComparison.Ordinal)&&!recovery.KnowsDownload(path))throw Error("MAINTENANCE_REQUIRED");
            if(name.StartsWith("archive-",StringComparison.Ordinal))
            {
                if(!Regex.IsMatch(name,"^archive-[a-f0-9]{64}\\.zip$",RegexOptions.CultureInvariant)||archives.Count>=MaximumRetainedCandidates)throw Error("MAINTENANCE_REQUIRED");
                try
                {
                    using var input=new PinnedStream(files.Open(path));if(input.Length is <1 or >MaximumArchiveBytes||(bytes+=input.Length)>MaximumRetainedCandidates*MaximumArchiveBytes)throw Error("MAINTENANCE_REQUIRED");
                    var digest=name[8..^4];CopyVerified(input,Stream.Null,input.Length,digest,token);
                    // The pinned read denies replacement while its creation time is read.
                    archives.Add(new(name,digest,input.Length,File.GetCreationTimeUtc(path)));
                }
                catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {throw new IOException("NATIVE_INTAKE_MAINTENANCE_REQUIRED",error);}
            }
            if(name.StartsWith("source-",StringComparison.Ordinal))
            {
                if(!Regex.IsMatch(name,"^source-[a-f0-9]{64}\\.json$",RegexOptions.CultureInvariant)||++sources>512)throw Error("MAINTENANCE_REQUIRED");
                try
                {
                    var source=NativeJson.Read<SourceEvidence>(files.Read(path,32768));var receiptDigest=name[7..^5];
                    var receiptBytes=files.Read(files.PathOf("intake/receipt-"+receiptDigest+".json"),32768);var receipt=NativeJson.Read<IntakeReceipt>(receiptBytes);
                    var tag=source.Tag??throw Error("MAINTENANCE_REQUIRED");var version=tag.StartsWith('v')?tag[1..]:tag;RequireVersion(version);
                    var archiveSha=source.ArchiveSha256??throw Error("MAINTENANCE_REQUIRED");
                    if(source.Format!=1||source.Repository!=Repository||source.ReleaseId<1||source.AssetId<1||source.Name!="yahad-app-native-"+version+".zip"||source.Bytes is <1 or >MaximumArchiveBytes||!Regex.IsMatch(archiveSha,"^[a-f0-9]{64}$",RegexOptions.CultureInvariant)||!Regex.IsMatch(source.BundleDigest??"","^[a-f0-9]{64}$",RegexOptions.CultureInvariant)||Hash(receiptBytes)!=receiptDigest||receipt.Format!=1||receipt.InstallationId!=anchor.InstallationId||receipt.BundleDigest!=source.BundleDigest||receipt.Version!=version||receipt.Channel!="stable"||receipt.Schema is <1 or >100000||receipt.PayloadBytes is <1 or >MaximumPayloadBytes||!Regex.IsMatch(receipt.PolicyDigest??"","^[a-f0-9]{64}$",RegexOptions.CultureInvariant)||!Regex.IsMatch(receipt.PinDigest??"","^[a-f0-9]{64}$",RegexOptions.CultureInvariant)||published.TryGetValue(archiveSha,out var prior)&&prior!=source.Bytes)throw Error("MAINTENANCE_REQUIRED");
                    published[archiveSha]=source.Bytes;
                }
                catch(Exception error)when(error is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
                {throw new IOException("NATIVE_INTAKE_MAINTENANCE_REQUIRED",error);}
            }
            if(name.StartsWith("system-source-",StringComparison.Ordinal))
            {
                if(!Regex.IsMatch(name,"^system-source-[a-f0-9]{64}\\.json$",RegexOptions.CultureInvariant)||++sources>512)throw Error("MAINTENANCE_REQUIRED");
                try
                {
                    var receiptBytes=files.Read(path,32768);if(Hash(receiptBytes)!=name[14..^5])throw Error("MAINTENANCE_REQUIRED");var receipt=NativeJson.Read<SystemSourceReceipt>(receiptBytes);
                    if(receipt.InstallationId!=anchor.InstallationId)throw Error("MAINTENANCE_REQUIRED");var source=SystemSourceRelease.FromProtectedReceipt(receipt);
                    FixedSystemComponents.Digest(receipt.ApplicationReceiptDigest);var appBytes=files.Read(files.PathOf("intake/receipt-"+receipt.ApplicationReceiptDigest+".json"),32768);var app=NativeJson.Read<IntakeReceipt>(appBytes);
                    if(Hash(appBytes)!=receipt.ApplicationReceiptDigest||app.InstallationId!=anchor.InstallationId||app.BundleDigest!=receipt.ApplicationManifestDigest||app.Version!=receipt.Version||app.Schema!=receipt.DatabaseTargetSchema||published.TryGetValue(source.ArchiveSha256,out var prior)&&prior!=source.Bytes)throw Error("MAINTENANCE_REQUIRED");
                    published[source.ArchiveSha256]=source.Bytes;
                }
                catch(Exception error)when(error is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
                {throw new IOException("NATIVE_INTAKE_MAINTENANCE_REQUIRED",error);}
            }
        }
        // Even a correctly named interrupted directory is evidence, never a new
        // extraction destination. Stop before another cache or staging allocation.
        if(Directory.EnumerateFileSystemEntries(files.PathOf("payloads"),".intake-*").Any())throw Error("MAINTENANCE_REQUIRED");
        return new(archives,published);
    }
    private void RotateArchiveCache(Retained retained,string incomingDigest,CancellationToken token)
    {
        if(retained.Archives.Count<MaximumRetainedCandidates)return;
        var oldest=retained.Archives.OrderBy(a=>a.CreatedAt).ThenBy(a=>a.Name,StringComparer.Ordinal).First();
        if(oldest.Digest==incomingDigest||!retained.Published.TryGetValue(oldest.Digest,out var publishedBytes)||publishedBytes!=oldest.Bytes)throw Error("MAINTENANCE_REQUIRED");
        var path=files.PathOf("intake/"+oldest.Name);
        fault?.Invoke("cache_rotation_opening");
        // Hash and delete through the SAME exclusive no-follow native handle. A
        // path-based File.Delete after releasing a verification handle would race.
        using(var parent=files.PinDirectory(files.PathOf("intake")))
        using(var handle=CacheNative.CreateFileW(@"\\?\"+path,0x80030080,0,IntPtr.Zero,3,0x02200000,IntPtr.Zero))
        {
            if(handle.IsInvalid||!CacheNative.GetFileInformationByHandle(handle,out var info)||(info.Attributes&0x410)!=0||info.Links!=1||(((long)info.SizeHigh<<32)|info.SizeLow)!=oldest.Bytes)throw Error("MAINTENANCE_REQUIRED");
            var error=CacheNative.GetSecurityInfo(handle,1,5,out _,out _,out _,out _,out var descriptor);if(error!=0)throw Error("MAINTENANCE_REQUIRED");
            try
            {
                var length=CacheNative.GetSecurityDescriptorLength(descriptor);if(length is 0 or >65536)throw Error("MAINTENANCE_REQUIRED");var bytes=new byte[(int)length];Marshal.Copy(descriptor,bytes,0,bytes.Length);var acl=new RawSecurityDescriptor(bytes,0);
                if(files.Roles is {} roles)roles.Validate(acl,InstalledFileRole.Recovery,false);
                else WindowsFileBoundary.ValidateAcl(acl,WindowsIdentity.GetCurrent().User??throw Error("MAINTENANCE_REQUIRED"),false);
            }
            finally{CacheNative.LocalFree(descriptor);}
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[65536];long offset=0;
            while(offset<oldest.Bytes){token.ThrowIfCancellationRequested();var read=RandomAccess.Read(handle,buffer.AsSpan(0,(int)Math.Min(buffer.Length,oldest.Bytes-offset)),offset);if(read==0)throw Error("MAINTENANCE_REQUIRED");hash.AppendData(buffer.AsSpan(0,read));offset+=read;}
            if(Convert.ToHexStringLower(hash.GetHashAndReset())!=oldest.Digest)throw Error("MAINTENANCE_REQUIRED");
            fault?.Invoke("cache_rotation_verified");token.ThrowIfCancellationRequested();byte delete=1;
            if(!CacheNative.SetFileInformationByHandle(handle,4,ref delete,1))throw Error("MAINTENANCE_REQUIRED");
        }
        if(File.Exists(path))throw Error("MAINTENANCE_REQUIRED");fault?.Invoke("cache_rotated");
    }
    private static void Move(string from,string to,bool replace)
    {if(!MoveFileExW(@"\\?\"+from,@"\\?\"+to,replace?9u:8u))throw new IOException("NATIVE_INTAKE_PUBLICATION",new System.ComponentModel.Win32Exception());}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool MoveFileExW(string from,string to,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetDiskFreeSpaceW(string root,out uint sectorsPerCluster,out uint bytesPerSector,out uint availableClusters,out uint totalClusters);
    private sealed record CachedArchive(string Name,string Digest,long Bytes,DateTime CreatedAt);
    private sealed record Retained(List<CachedArchive> Archives,Dictionary<string,long> Published);
    private static class CacheNative
    {
        [StructLayout(LayoutKind.Sequential)]internal struct FileInfo
        {public uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInfo info);
        [DllImport("advapi32.dll",SetLastError=true)]internal static extern uint GetSecurityInfo(SafeFileHandle handle,uint type,uint flags,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
        [DllImport("advapi32.dll")]internal static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("kernel32.dll")]internal static extern IntPtr LocalFree(IntPtr descriptor);
        [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool SetFileInformationByHandle(SafeFileHandle handle,int kind,ref byte value,int size);
    }
    private sealed record Release(long Id,string Tag,string Version,long AssetId,string Name,long Size,string Sha256,string Url);
    private sealed record CandidateFile(string Path,long Size,string Sha256);
    private sealed record CandidatePlan(List<CandidateFile> Files,long Total,int Schema,string Version,JsonElement Manifest);
    private sealed record SourceEvidence(int Format,string Repository,long ReleaseId,string Tag,long AssetId,string Name,long Bytes,string ArchiveSha256,string BundleDigest);
    private sealed class PinnedStream(WindowsFileBoundary.PrivateReadLease lease):Stream
    {
        private long position;public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;public override long Length=>lease.Length;
        public override long Position{get=>position;set{if(value<0)throw new IOException("NATIVE_INTAKE_SEEK");position=value;}}
        public override int Read(byte[] buffer,int offset,int count)=>Read(buffer.AsSpan(offset,count));
        public override int Read(Span<byte> buffer){var count=lease.Read(position,buffer);position+=count;return count;}
        public override long Seek(long offset,SeekOrigin origin){Position=checked((origin switch{SeekOrigin.Begin=>0,SeekOrigin.Current=>position,SeekOrigin.End=>Length,_=>throw new ArgumentOutOfRangeException(nameof(origin))})+offset);return position;}
        public override void Flush(){}public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing)lease.Dispose();base.Dispose(disposing);}
    }
}

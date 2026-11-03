using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yahad.Install;

namespace Yahad.Authority;

// Created only by the fixed-source JSON provider. The callback is the existing
// bounded HTTPS transport (or trusted private tests), never a URL supplied by IPC.
internal sealed class SystemSourceRelease
{
    internal const string Api="https://api.github.com/repos/"+NativeCandidateIntake.Repository;
    internal long ReleaseId{get;}internal string Tag{get;}internal string Version{get;}internal long AssetId{get;}
    internal string AssetName{get;}internal string AssetUrl{get;}internal long Bytes{get;}internal string ArchiveSha256{get;}
    internal string SourceCommit{get;}internal string SourceProofDigest{get;}
    private SystemSourceRelease(long releaseId,string tag,string version,long assetId,string name,string url,long bytes,string digest,string source)
    {
        ReleaseId=releaseId;Tag=tag;Version=version;AssetId=assetId;AssetName=name;AssetUrl=url;Bytes=bytes;ArchiveSha256=digest;SourceCommit=source;
        SourceProofDigest=SystemCandidateManifest.Hash(JsonSerializer.SerializeToUtf8Bytes(new{Repository=NativeCandidateIntake.Repository,releaseId,tag,version,assetId,name,url,bytes,digest,source}));
    }
    // Only the protected resolver calls this after hashing the source receipt.
    // It restores a prior fixed-source check without requiring network at recovery.
    internal static SystemSourceRelease FromProtectedReceipt(SystemSourceReceipt receipt)
    {
        SystemCandidateManifest.Version(receipt.Version);SystemCandidateManifest.Commit(receipt.SourceCommit);FixedSystemComponents.Digest(receipt.ArchiveSha256);
        if(receipt.Format!=2||receipt.Profile!="full-system-v1"||receipt.Repository!=NativeCandidateIntake.Repository||receipt.ReleaseId<1||receipt.AssetId<1||receipt.Bytes is <1 or >NativeCandidateIntake.MaximumArchiveBytes||(receipt.Tag!="v"+receipt.Version&&receipt.Tag!=receipt.Version)||receipt.AssetName!="yahad-system-native-"+receipt.Version+".zip")throw Error("PROTECTED_SOURCE");
        var source=new SystemSourceRelease(receipt.ReleaseId,receipt.Tag,receipt.Version,receipt.AssetId,receipt.AssetName,Api+"/releases/assets/"+receipt.AssetId,receipt.Bytes,receipt.ArchiveSha256,receipt.SourceCommit);
        if(source.SourceProofDigest!=receipt.SourceProofDigest)throw Error("PROTECTED_SOURCE");return source;
    }
    internal static async Task<SystemSourceRelease> CheckAsync(Func<Uri,int,CancellationToken,Task<byte[]>> readJson,CancellationToken cancellation)
    {
        async Task<JsonElement> Read(string url,int maximum)
        {cancellation.ThrowIfCancellationRequested();var bytes=await readJson(new(url),maximum,cancellation);if(bytes.Length is <1||bytes.Length>maximum)throw Error("SOURCE_BOUND");return NativeJson.Read<JsonElement>(bytes);}
        var release=await Read(Api+"/releases/latest",1024*1024);var id=release.GetProperty("id").GetInt64();var tag=Text(release,"tag_name");var version=tag.StartsWith('v')?tag[1..]:tag;
        SystemCandidateManifest.Version(version);
        if(id<1||Text(release,"url")!=Api+"/releases/"+id||release.GetProperty("draft").ValueKind!=JsonValueKind.False||release.GetProperty("prerelease").ValueKind!=JsonValueKind.False)throw Error("SOURCE_RELEASE");
        var assets=release.GetProperty("assets");if(assets.ValueKind!=JsonValueKind.Array||assets.GetArrayLength()>1000)throw Error("SOURCE_ASSETS");
        JsonElement? selected=null;var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var expected="yahad-system-native-"+version+".zip";
        foreach(var asset in assets.EnumerateArray()){var name=Text(asset,"name");if(!names.Add(name))throw Error("SOURCE_ASSETS");if(name==expected)selected=asset;}
        if(!selected.HasValue)throw Error("SOURCE_ASSET_MISSING");var item=selected.Value;var assetId=item.GetProperty("id").GetInt64();var size=item.GetProperty("size").GetInt64();var digest=Text(item,"digest");var assetUrl=Text(item,"url");
        if(assetId<1||size is <1 or >NativeCandidateIntake.MaximumArchiveBytes||Text(item,"state")!="uploaded"||assetUrl!=Api+"/releases/assets/"+assetId||!Regex.IsMatch(digest,"^sha256:[a-f0-9]{64}$",RegexOptions.CultureInvariant))throw Error("SOURCE_ASSET");
        // target_commitish may be a branch. Resolve the actual fixed repository's
        // tag reference, including annotated-tag chains, with bounded cycle checks.
        var reference=await Read(Api+"/git/ref/tags/"+tag,65536);
        if(Text(reference,"ref")!="refs/tags/"+tag||Text(reference,"url")!=Api+"/git/refs/tags/"+tag)throw Error("SOURCE_TAG");
        var current=reference.GetProperty("object");var seen=new HashSet<string>(StringComparer.Ordinal);
        for(var depth=0;depth<8;depth++)
        {
            var sha=Text(current,"sha");SystemCandidateManifest.Commit(sha);var kind=Text(current,"type");
            if(kind=="commit")
            {if(Text(current,"url")!=Api+"/git/commits/"+sha)throw Error("SOURCE_COMMIT");return new(id,tag,version,assetId,expected,assetUrl,size,digest[7..],sha);}
            if(kind!="tag"||!seen.Add(sha)||Text(current,"url")!=Api+"/git/tags/"+sha)throw Error("SOURCE_TAG_CYCLE");
            var annotated=await Read(Api+"/git/tags/"+sha,65536);
            if(Text(annotated,"sha")!=sha||Text(annotated,"url")!=Api+"/git/tags/"+sha)throw Error("SOURCE_TAG");current=annotated.GetProperty("object");
        }
        throw Error("SOURCE_TAG_DEPTH");
    }
    private static string Text(JsonElement value,string name)=>value.GetProperty(name).GetString()??throw Error("SOURCE_STRING");
    private static IOException Error(string code)=>new("SYSTEM_CANDIDATE_"+code);
}

internal sealed record SystemProtocolRange(int MinimumReader,int MaximumReader,int Writer);
internal sealed class SystemCandidateEnvironment
{
    internal string CurrentVersion{get;}internal int ObservedSchema{get;}internal int WindowsBuild{get;}
    internal IReadOnlyDictionary<string,int> Protocols{get;}
    internal SystemCandidateEnvironment(string currentVersion,int observedSchema,int windowsBuild,IReadOnlyDictionary<string,int> protocols)
    {SystemCandidateManifest.Version(currentVersion);if(observedSchema is <1 or >100000||windowsBuild<1)throw new IOException("SYSTEM_CANDIDATE_ENVIRONMENT");CurrentVersion=currentVersion;ObservedSchema=observedSchema;WindowsBuild=windowsBuild;Protocols=new ReadOnlyDictionary<string,int>(protocols.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal));}
}

internal sealed class SystemCandidateDescriptor
{
    internal string Version{get;}internal string SourceCommit{get;}internal string SystemManifestDigest{get;}internal string ApplicationManifestDigest{get;}
    internal int DatabaseTargetSchema{get;}internal int MinimumWindowsBuild{get;}internal IReadOnlyList<int> DatabaseMigrationsFrom{get;}
    internal IReadOnlyDictionary<string,SystemProtocolRange> Protocols{get;}internal IReadOnlyList<SystemComponentDescriptor> Components{get;}
    private readonly byte[] manifest,application;
    internal byte[] ManifestBytes=>manifest.ToArray();internal byte[] ApplicationManifestBytes=>application.ToArray();
    private SystemCandidateDescriptor(string version,string source,int schema,int minimumWindowsBuild,int[] from,Dictionary<string,SystemProtocolRange> protocols,SystemComponentDescriptor[] components,byte[] manifest,byte[] application)
    {Version=version;SourceCommit=source;DatabaseTargetSchema=schema;MinimumWindowsBuild=minimumWindowsBuild;DatabaseMigrationsFrom=Array.AsReadOnly(from);Protocols=new ReadOnlyDictionary<string,SystemProtocolRange>(protocols);Components=Array.AsReadOnly(components);this.manifest=manifest.ToArray();this.application=application.ToArray();SystemManifestDigest=SystemCandidateManifest.Hash(manifest);ApplicationManifestDigest=SystemCandidateManifest.Hash(application);}
    internal static SystemCandidateDescriptor Validate(byte[] manifest,byte[] application,SystemSourceRelease source,SystemCandidateEnvironment environment,bool requireNewer=true)
        =>ValidateCore(manifest,application,source.Version,source.SourceCommit,environment,requireNewer);
    // Initial package publication is authenticated by the elevated installer,
    // not a fabricated GitHub release receipt. Runtime intake keeps its normal
    // fixed-source proof and strictly-forward version checks above.
    internal static SystemCandidateDescriptor ValidateInstaller(byte[] manifest,byte[] application,string version,string sourceCommit,int schema,int windowsBuild)
    {
        if(version!=ApplicationRelease.Version||sourceCommit!=ApplicationRelease.SourceCommit)throw Error("INSTALLER_SOURCE");
        var protocols=new[]{"system","handoff","authorityRuntime","activation","journal","recovery","policy"}.ToDictionary(x=>x,x=>x=="policy"?2:1,StringComparer.Ordinal);
        return ValidateCore(manifest,application,version,sourceCommit,new(version,schema,windowsBuild,protocols),false);
    }
    private static SystemCandidateDescriptor ValidateCore(byte[] manifest,byte[] application,string version,string sourceCommit,SystemCandidateEnvironment environment,bool requireNewer)
    {
        var source=new{Version=version,SourceCommit=sourceCommit};
        if(manifest.Length is <1 or >8*1024*1024||application.Length is <1 or >8*1024*1024)throw Error("MANIFEST_BOUND");
        var root=NativeJson.Read<JsonElement>(manifest);Fields(root,"format","kind","profile","target","version","sourceCommit","minimumWindowsBuild","applicationManifestSha256","database","protocols","components");
        if(Number(root,"format")!=2||Text(root,"kind")!="windows-system-candidate"||Text(root,"profile")!="full-system-v1"||Text(root,"target")!="win-x64"||Text(root,"version")!=source.Version||Text(root,"sourceCommit")!=source.SourceCommit||Text(root,"applicationManifestSha256")!=SystemCandidateManifest.Hash(application))throw Error("MANIFEST_IDENTITY");
        var minimum=Number(root,"minimumWindowsBuild");if(minimum is <1 or >1000000||environment.WindowsBuild<minimum)throw Error("WINDOWS_BUILD");
        if(requireNewer?System.Version.Parse(source.Version)<=System.Version.Parse(environment.CurrentVersion):System.Version.Parse(source.Version)<System.Version.Parse(environment.CurrentVersion))throw Error("VERSION_ORDER");
        var database=root.GetProperty("database");Fields(database,"targetSchema","migrationsFrom");var schema=Number(database,"targetSchema");var from=database.GetProperty("migrationsFrom");
        if(schema is <1 or >100000||schema<environment.ObservedSchema||from.ValueKind!=JsonValueKind.Array||from.GetArrayLength()>1024)throw Error("SCHEMA");
        var migrations=from.EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        if(migrations.Any(v=>v<1||v>=schema)||!migrations.SequenceEqual(migrations.Distinct().Order())||schema!=environment.ObservedSchema&&!migrations.Contains(environment.ObservedSchema))throw Error("MIGRATION_PATH");
        var protocolJson=root.GetProperty("protocols");Fields(protocolJson,"system","handoff","authorityRuntime","activation","journal","recovery","policy");var protocols=new Dictionary<string,SystemProtocolRange>(StringComparer.Ordinal);
        foreach(var pair in protocolJson.EnumerateObject())
        {
            Fields(pair.Value,"minimumReader","maximumReader","writer");var range=new SystemProtocolRange(Number(pair.Value,"minimumReader"),Number(pair.Value,"maximumReader"),Number(pair.Value,"writer"));
            if(range.MinimumReader<1||range.MaximumReader<range.MinimumReader||range.MaximumReader>1000||range.Writer<range.MinimumReader||range.Writer>range.MaximumReader||!environment.Protocols.TryGetValue(pair.Name,out var current)||current<range.MinimumReader||current>range.MaximumReader||range.Writer!=current)throw Error("PROTOCOL");protocols.Add(pair.Name,range);
        }
        var list=root.GetProperty("components");if(list.ValueKind!=JsonValueKind.Array||list.GetArrayLength()!=4)throw Error("COMPONENTS");var components=new List<SystemComponentDescriptor>();var seen=new HashSet<SystemComponentKind>();long total=0;var fileCount=0;
        foreach(var item in list.EnumerateArray())
        {
            Fields(item,"kind","prefix","entryPoint","entrySha256","files");var name=Text(item,"kind");if(!Enum.TryParse<SystemComponentKind>(name,false,out var kind)||!Enum.IsDefined(kind)||!seen.Add(kind))throw Error("COMPONENT_KIND");
            var fileList=item.GetProperty("files");if(fileList.ValueKind!=JsonValueKind.Array||fileList.GetArrayLength() is <1 or >50000)throw Error("COMPONENT_FILES");
            var files=new List<SystemComponentFile>();var paths=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var file in fileList.EnumerateArray())
            {
                Fields(file,"path","size","sha256");var path=Text(file,"path");SystemCandidateManifest.ComponentMember(path);var size=file.GetProperty("size").GetInt64();var digest=Text(file,"sha256");
                if(++fileCount>50000||(total+=size)>2L*1024*1024*1024||!paths.TryAdd(path,path))throw Error("COMPONENT_BOUND");files.Add(new(path,size,digest));
            }
            var directories=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var path in files.Select(f=>f.RelativePath))for(var at=path.IndexOf('/');at>=0;at=path.IndexOf('/',at+1))
            {var prefix=path[..at];if(paths.ContainsKey(prefix)||directories.TryGetValue(prefix,out var prior)&&prior!=prefix)throw Error("COMPONENT_COLLISION");directories[prefix]=prefix;}
            if(!files.Any(f=>f.RelativePath=="build-identity.json"&&f.Size is >0 and <=65536))throw Error("BUILD_IDENTITY_MISSING");
            components.Add(new(kind,Text(item,"prefix"),Text(item,"entryPoint"),Text(item,"entrySha256"),files));
        }
        var app=NativeJson.Read<JsonElement>(application);
        if(Number(app,"schema")!=1||Text(app,"kind")!="windows-runtime-candidate"||Text(app,"profile")!="full"||Text(app,"target")!="win-x64"||Text(app,"version")!=source.Version||Text(app,"authority")!="application-updater")throw Error("APP_IDENTITY");
        return new(source.Version,source.SourceCommit,schema,minimum,migrations,protocols,components.ToArray(),manifest,application);
    }
    private static void Fields(JsonElement value,params string[] names)
    {if(value.ValueKind!=JsonValueKind.Object||!value.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(names.Order()))throw Error("FIELDS");}
    private static string Text(JsonElement value,string name)=>value.GetProperty(name).GetString()??throw Error("STRING");
    private static int Number(JsonElement value,string name)=>value.GetProperty(name).GetInt32();
    private static IOException Error(string code)=>new("SYSTEM_CANDIDATE_"+code);
}

internal static class SystemCandidateManifest
{
    internal static SystemCandidateDescriptor Validate(byte[] manifest,byte[] application,SystemSourceRelease source,SystemCandidateEnvironment environment,bool requireNewer=true)=>SystemCandidateDescriptor.Validate(manifest,application,source,environment,requireNewer);
    internal static void Version(string value){if(value is null||!Regex.IsMatch(value,@"^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$",RegexOptions.CultureInvariant))throw new IOException("SYSTEM_CANDIDATE_VERSION");}
    internal static void Commit(string value){if(value is null||!Regex.IsMatch(value,"^[a-f0-9]{40}$",RegexOptions.CultureInvariant))throw new IOException("SYSTEM_CANDIDATE_SOURCE_COMMIT");}
    internal static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static void ComponentMember(string name)
    {
        FixedSystemComponents.Member(name);if(name.Split('/').Length>64||Regex.IsMatch(name,@"\.(key|pem|sqlite(?:-wal|-shm)?|backup)$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))throw new IOException("SYSTEM_CANDIDATE_COMPONENT_PATH");
        foreach(var part in name.Split('/'))if(part.StartsWith(".env",StringComparison.OrdinalIgnoreCase)||new[]{"data","datasets","backups","secrets",".git",".authority","grants","enrollment.json","installation.json","update-authority.json","activation.json","bootstrap.json","owner-intent.json","master-key","update-policy","update-pin"}.Contains(part,StringComparer.OrdinalIgnoreCase)||Regex.IsMatch(part,@"^(COM|LPT)[0-9](?:\..*)?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))throw new IOException("SYSTEM_CANDIDATE_SECURITY_PATH");
    }
}

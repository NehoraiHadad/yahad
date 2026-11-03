using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yahad.Install;

namespace Yahad.Authority;

internal sealed record SystemSourceReceipt(int Format,string Profile,string InstallationId,string Repository,long ReleaseId,string Tag,long AssetId,string AssetName,long Bytes,string ArchiveSha256,string SourceCommit,string SourceProofDigest,string SystemManifestDigest,string ApplicationManifestDigest,string ApplicationReceiptDigest,int DatabaseTargetSchema,string Version);

// Staging result, never an authorization capability and never deserialized from
// IPC. The full-policy factory separately binds consent, current state and scope.
internal sealed class StagedSystemCandidate
{
    internal SystemCandidateDescriptor Descriptor{get;}internal SystemSourceRelease Source{get;}
    internal string SystemSlotRoot{get;}internal string ApplicationPayloadRoot{get;}
    internal string SourceReceiptDigest{get;}internal string ApplicationReceiptDigest{get;}
    internal StagedSystemCandidate(SystemCandidateDescriptor descriptor,SystemSourceRelease source,string slot,string payload,string receipt,string appReceipt)
    {Descriptor=descriptor;Source=source;SystemSlotRoot=slot;ApplicationPayloadRoot=payload;SourceReceiptDigest=receipt;ApplicationReceiptDigest=appReceipt;}
}

// Full-profile adapter of the existing intake: source/download/cache are supplied
// by the same NativeCandidateIntake transport. It does not create another HTTP
// client, scheduler, trust switch or activation authority.
internal sealed class SystemCandidateIntake(InstalledStateFiles files,InstalledAuthorityAnchor anchor,InstalledSystemSlots slots,NativeCandidateIntake applicationVerifier,Action requireCurrentFullPolicy,Func<SystemCandidateEnvironment>? observeEnvironment=null):IProtectedSystemCandidateResolver
{
    internal async Task<StagedSystemCandidate?> CheckAndStageAsync(SystemCandidateEnvironment environment,CancellationToken cancellation)
    {requireCurrentFullPolicy();var source=await applicationVerifier.CheckSystemSourceAsync(requireCurrentFullPolicy,cancellation);if(source.Version==environment.CurrentVersion)return null;if(Version.Parse(source.Version)<Version.Parse(environment.CurrentVersion))throw Error("VERSION_ORDER");var archive=await applicationVerifier.AcquireSystemArchiveAsync(source,requireCurrentFullPolicy,cancellation);return await StageVerifiedArchiveAsync(source,archive,environment,cancellation);}
    public SystemCandidatePolicyFacts RequireVerified(string receiptDigest)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(10));var token=deadline.Token;
        requireCurrentFullPolicy();FixedSystemComponents.Digest(receiptDigest);var bytes=files.Read(files.PathOf("intake/system-source-"+receiptDigest+".json"),32768);
        if(SystemCandidateManifest.Hash(bytes)!=receiptDigest)throw Error("SOURCE_RECEIPT_BYTES");var receipt=NativeJson.Read<SystemSourceReceipt>(bytes);
        if(receipt.InstallationId!=anchor.InstallationId)throw Error("SOURCE_INSTALLATION");
        foreach(var digest in new[]{receipt.SystemManifestDigest,receipt.ApplicationManifestDigest,receipt.ApplicationReceiptDigest})FixedSystemComponents.Digest(digest);
        var source=SystemSourceRelease.FromProtectedReceipt(receipt);var manifest=slots.ReadManifest(receipt.SystemManifestDigest);var app=files.Read(files.PathOf("payloads/"+receipt.ApplicationManifestDigest+"/bundle.json"),8*1024*1024);
        if(SystemCandidateManifest.Hash(manifest)!=receipt.SystemManifestDigest||SystemCandidateManifest.Hash(app)!=receipt.ApplicationManifestDigest)throw Error("STAGED_MANIFEST_BYTES");
        var descriptor=SystemCandidateManifest.Validate(manifest,app,source,observeEnvironment?.Invoke()??ObservedEnvironment(),requireNewer:false);
        if(descriptor.DatabaseTargetSchema!=receipt.DatabaseTargetSchema)throw Error("SOURCE_SCHEMA");
        var appReceiptBytes=files.Read(files.PathOf("intake/receipt-"+receipt.ApplicationReceiptDigest+".json"),32768);var appReceipt=NativeJson.Read<IntakeReceipt>(appReceiptBytes);
        if(SystemCandidateManifest.Hash(appReceiptBytes)!=receipt.ApplicationReceiptDigest||appReceipt.Format!=1||appReceipt.InstallationId!=anchor.InstallationId||appReceipt.BundleDigest!=descriptor.ApplicationManifestDigest||appReceipt.Version!=descriptor.Version||appReceipt.Schema!=descriptor.DatabaseTargetSchema||appReceipt.Channel!="stable"||appReceipt.PolicyDigest!=anchor.IntakePolicyDigest||appReceipt.PinDigest!=anchor.IntakePinDigest)throw Error("APP_RECEIPT");
        long payloadBytes=0;foreach(var file in NativeJson.Read<JsonElement>(app).GetProperty("files").EnumerateArray())payloadBytes=checked(payloadBytes+file.GetProperty("size").GetInt64());if(payloadBytes!=appReceipt.PayloadBytes)throw Error("APP_RECEIPT");
        var payload=applicationVerifier.VerifyStagedApplicationCandidate(app,descriptor.Version,descriptor.DatabaseTargetSchema,token);VerifyAppSource(payload,descriptor);VerifyMigrationDeclaration(payload,descriptor);
        slots.Verify(descriptor.SystemManifestDigest,manifest,descriptor.Components,token);VerifyComponentImages(slots.SlotRoot(descriptor.SystemManifestDigest),descriptor,token);requireCurrentFullPolicy();
        return new(descriptor.SystemManifestDigest,descriptor.ApplicationManifestDigest,descriptor.Version,descriptor.SourceCommit,descriptor.DatabaseTargetSchema,descriptor.DatabaseMigrationsFrom,descriptor.Components);
    }
    private SystemCandidateEnvironment ObservedEnvironment()
    {
        var head=new InstalledIntakeSelection(files,anchor,()=>DateTimeOffset.UtcNow).Head();var bytes=files.Read(files.PathOf("payloads/"+head.ReleaseDigest+"/bundle.json"),8*1024*1024);if(SystemCandidateManifest.Hash(bytes)!=head.ReleaseDigest)throw Error("CURRENT_MANIFEST");
        var version=NativeJson.Read<JsonElement>(bytes).GetProperty("version").GetString()??throw Error("CURRENT_VERSION");
        return new(version,head.MaximumSchema,Math.Max(1,Environment.OSVersion.Version.Build),new Dictionary<string,int>{{"system",1},{"handoff",1},{"authorityRuntime",1},{"ownerAuthority",1},{"activation",1},{"journal",1},{"recovery",1},{"policy",2}});
    }
    internal Task<StagedSystemCandidate> StageVerifiedArchiveAsync(SystemSourceRelease source,string archivePath,SystemCandidateEnvironment environment,CancellationToken cancellation)
    {
        requireCurrentFullPolicy();files.ValidateRoot();if(applicationVerifier.StateRoot!=files.Root)throw Error("STATE_ROOT");
        if(!OperatingSystem.IsWindows()||files.IsSingleService||files.IsRuntimeProfile)throw Error("AUTHORITY_REQUIRED");
        var expected=files.PathOf("intake/archive-"+source.ArchiveSha256+".zip");
        if(!string.Equals(archivePath,expected,StringComparison.OrdinalIgnoreCase))throw Error("ARCHIVE_PATH");
        using var operation=files.Lease(files.PathOf("intake"));
        var recovery=new NativeIntakeRecovery(files);recovery.Reconcile(cancellation);
        if(Directory.EnumerateFileSystemEntries(files.PathOf("intake"),"system-available.json.partial-*").Any())throw Error("RECONCILIATION_REQUIRED");
        using var bytes=new InstalledSystemSlots.PinnedStream(files.Open(expected));InstalledSystemSlots.Copy(bytes,Stream.Null,source.Bytes,source.ArchiveSha256,cancellation);bytes.Position=0;
        NativeCandidateIntake.CheckCentralDirectory(bytes);using var archive=new ZipArchive(bytes,ZipArchiveMode.Read,true);
        var entries=Entries(archive);var manifest=NativeCandidateIntake.ReadEntry(entries["system-manifest.json"],8*1024*1024,cancellation);var appManifest=NativeCandidateIntake.ReadEntry(entries["payload/bundle.json"],8*1024*1024,cancellation);
        var descriptor=SystemCandidateManifest.Validate(manifest,appManifest,source,environment);
        var appEntries=entries.Where(p=>p.Key.StartsWith("payload/",StringComparison.Ordinal)).ToDictionary(p=>p.Key[8..],p=>p.Value,StringComparer.Ordinal);
        var app=NativeJson.Read<JsonElement>(appManifest);var expectedNames=new HashSet<string>(StringComparer.Ordinal){"system-manifest.json","payload/bundle.json"};long total=appManifest.LongLength;
        foreach(var file in app.GetProperty("files").EnumerateArray())
        {
            var name=file.GetProperty("path").GetString()??throw Error("APP_PATH");NativeCandidateIntake.ValidatePath(name);
            var size=file.GetProperty("size").GetInt64();if(size<0||size>512L*1024*1024||!expectedNames.Add("payload/"+name)||!entries.TryGetValue("payload/"+name,out var entry)||entry.Length!=size||(total+=size)>2L*1024*1024*1024)throw Error("APP_CLOSURE");
        }
        foreach(var component in descriptor.Components)foreach(var file in component.Files)
        {
            var path=component.SlotRelativeDirectory+"/"+file.RelativePath;
            if(!expectedNames.Add(path)||!entries.TryGetValue(path,out var entry)||entry.Length!=file.Size||(total+=file.Size)>2L*1024*1024*1024)throw Error("SYSTEM_CLOSURE");
        }
        if(expectedNames.Count>50001||!expectedNames.SetEquals(entries.Keys))throw Error("SYSTEM_CLOSURE");
        foreach(var component in descriptor.Components)VerifyComponentIdentity(component,descriptor,entries,cancellation);
        requireCurrentFullPolicy();cancellation.ThrowIfCancellationRequested();
        // This is the SAME app manifest verifier/stager as legacy intake. The full
        // policy, not a legacy same-schema delegation, authorizes schema expansion.
        var payload=applicationVerifier.StageApplicationCandidate(appManifest,source.Version,descriptor.DatabaseTargetSchema,appEntries,cancellation);
        VerifyAppSource(payload,descriptor);
        VerifyMigrationDeclaration(payload,descriptor);
        var slot=slots.Stage(descriptor.SystemManifestDigest,manifest,descriptor.Components,path=>entries[path].Open(),cancellation);
        VerifyComponentImages(slot,descriptor,cancellation);
        requireCurrentFullPolicy();cancellation.ThrowIfCancellationRequested();
        long appBytes=0;foreach(var file in app.GetProperty("files").EnumerateArray())appBytes=checked(appBytes+file.GetProperty("size").GetInt64());
        var receipt=JsonSerializer.SerializeToUtf8Bytes(new IntakeReceipt(1,anchor.InstallationId,descriptor.ApplicationManifestDigest,source.Version,"stable",appBytes,descriptor.DatabaseTargetSchema,anchor.IntakePolicyDigest,anchor.IntakePinDigest));
        var appReceipt=SystemCandidateManifest.Hash(receipt);WriteImmutable(files.PathOf("intake/receipt-"+appReceipt+".json"),receipt);
        var sourceReceipt=JsonSerializer.SerializeToUtf8Bytes(new SystemSourceReceipt(2,"full-system-v1",anchor.InstallationId,NativeCandidateIntake.Repository,source.ReleaseId,source.Tag,source.AssetId,source.AssetName,source.Bytes,source.ArchiveSha256,source.SourceCommit,source.SourceProofDigest,descriptor.SystemManifestDigest,descriptor.ApplicationManifestDigest,appReceipt,descriptor.DatabaseTargetSchema,descriptor.Version));
        var digest=SystemCandidateManifest.Hash(sourceReceipt);WriteImmutable(files.PathOf("intake/system-source-"+digest+".json"),sourceReceipt);
        requireCurrentFullPolicy();var temporary=recovery.Pointer("system-available.json",JsonSerializer.SerializeToUtf8Bytes(new{Format=2,Profile="full-system-v1",anchor.InstallationId,SourceReceiptDigest=digest,descriptor.SystemManifestDigest}));
        requireCurrentFullPolicy();cancellation.ThrowIfCancellationRequested();
        // Never publish the legacy available.json pointer: old enrollment/grants
        // cannot discover/authorize a full-system operation through app-only routing.
        recovery.PublishedPointer(temporary);
        return Task.FromResult(new StagedSystemCandidate(descriptor,source,slot,payload,digest,appReceipt));
    }
    private static Dictionary<string,ZipArchiveEntry> Entries(ZipArchive archive)
    {
        if(archive.Entries.Count is <3 or >50001)throw Error("MEMBERS");var entries=new Dictionary<string,ZipArchiveEntry>(StringComparer.Ordinal);var folded=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in archive.Entries)
        {
            var name=entry.FullName;var type=(entry.ExternalAttributes>>16)&0xf000;
            if(entry.Name.Length==0||type is not(0 or 0x8000)||(entry.ExternalAttributes&0x410)!=0||!folded.Add(name)||!entries.TryAdd(name,entry))throw Error("MEMBER_TYPE");
            if(name=="system-manifest.json"||name=="payload/bundle.json")continue;
            if(name.StartsWith("payload/",StringComparison.Ordinal)){NativeCandidateIntake.ValidatePath(name[8..]);continue;}
            var prefixes=Enum.GetValues<SystemComponentKind>().Select(FixedSystemComponents.SlotPrefix).Where(p=>name.StartsWith(p+"/",StringComparison.Ordinal)).ToArray();
            if(prefixes.Length!=1)throw Error("SECURITY_NAMESPACE");SystemCandidateManifest.ComponentMember(name[(prefixes[0].Length+1)..]);
        }
        if(!entries.ContainsKey("system-manifest.json")||!entries.ContainsKey("payload/bundle.json"))throw Error("MANIFEST_MISSING");return entries;
    }
    private static void VerifyComponentIdentity(SystemComponentDescriptor component,SystemCandidateDescriptor descriptor,Dictionary<string,ZipArchiveEntry> entries,CancellationToken cancellation)
    {
        var bytes=NativeCandidateIntake.ReadEntry(entries[component.SlotRelativeDirectory+"/build-identity.json"],65536,cancellation);var identity=NativeJson.Read<JsonElement>(bytes);
        var artifact=component.Kind switch{SystemComponentKind.RuntimeHost=>"Yahad.ServiceHost",SystemComponentKind.Owner=>"Yahad.Owner",SystemComponentKind.Probe=>"Node",_=>"Yahad.Authority"};
        if(identity.GetProperty("format").GetInt32()!=1||identity.GetProperty("component").GetString()!=component.Kind.ToString()||identity.GetProperty("artifact").GetString()!=artifact||identity.GetProperty("version").GetString()!=descriptor.Version||identity.GetProperty("sourceCommit").GetString()!=descriptor.SourceCommit||identity.GetProperty("target").GetString()!="win-x64"||identity.GetProperty("entry").GetString()!=component.EntryPoint||identity.GetProperty("entrySha256").GetString()!=component.EntrySha256)throw Error("COMPONENT_SOURCE_IDENTITY");
        if(component.Kind==SystemComponentKind.Probe)
        {
            var version=identity.GetProperty("upstreamVersion").GetString()??"";if(!Regex.IsMatch(version,@"^24\.[0-9]{1,5}\.[0-9]{1,5}$",RegexOptions.CultureInvariant)||identity.GetProperty("runtime").GetString()!="node-private"||identity.GetProperty("sourceUrl").GetString()!="https://nodejs.org/dist/v"+version+"/node-v"+version+"-win-x64.zip")throw Error("PROBE_PROVENANCE");return;
        }
        if(identity.GetProperty("runtime").GetString()!="dotnet-private")throw Error("PRIVATE_RUNTIME");
        var stem=Path.GetFileNameWithoutExtension(component.EntryPoint);var runtimeEntry=component.SlotRelativeDirectory+"/"+stem+".runtimeconfig.json";var depsEntry=component.SlotRelativeDirectory+"/"+stem+".deps.json";
        if(!entries.TryGetValue(runtimeEntry,out var runtimeFile)||!entries.TryGetValue(depsEntry,out var depsFile))throw Error("PRIVATE_RUNTIME");
        var runtime=NativeJson.Read<JsonElement>(NativeCandidateIntake.ReadEntry(runtimeFile,65536,cancellation)).GetProperty("runtimeOptions");
        if(runtime.TryGetProperty("framework",out _)||runtime.TryGetProperty("frameworks",out _)||!runtime.GetProperty("includedFrameworks").EnumerateArray().Any(f=>f.GetProperty("name").GetString()=="Microsoft.NETCore.App"))throw Error("PRIVATE_RUNTIME");
        var runtimeVersion=identity.GetProperty("runtimeVersion").GetString()??"";var frameworks=new HashSet<string>(StringComparer.Ordinal);
        if(!Regex.IsMatch(runtimeVersion,@"^(1[0-9]|[2-9][0-9])\.[0-9]{1,5}\.[0-9]{1,5}$",RegexOptions.CultureInvariant))throw Error("PRIVATE_RUNTIME");
        foreach(var framework in runtime.GetProperty("includedFrameworks").EnumerateArray())
        {var name=framework.GetProperty("name").GetString()??"";if(name is not("Microsoft.NETCore.App" or "Microsoft.WindowsDesktop.App")||!frameworks.Add(name)||framework.GetProperty("version").GetString()!=runtimeVersion)throw Error("PRIVATE_RUNTIME");}
        if(component.Kind==SystemComponentKind.Owner&&!frameworks.Contains("Microsoft.WindowsDesktop.App"))throw Error("PRIVATE_RUNTIME");
        var deps=NativeJson.Read<JsonElement>(NativeCandidateIntake.ReadEntry(depsFile,8*1024*1024,cancellation));var target=deps.GetProperty("runtimeTarget").GetProperty("name").GetString()??"";
        if(!target.EndsWith("/win-x64",StringComparison.Ordinal))throw Error("PRIVATE_RUNTIME");
        foreach(var library in deps.GetProperty("targets").GetProperty(target).EnumerateObject())foreach(var kind in new[]{"runtime","native"})if(library.Value.TryGetProperty(kind,out var files))foreach(var file in files.EnumerateObject())
        {var name=file.Name.Split('/')[^1];SystemCandidateManifest.ComponentMember(name);if(!component.Files.Any(f=>f.RelativePath==name))throw Error("RUNTIME_CLOSURE");}
        foreach(var required in new[]{stem+".dll","coreclr.dll","hostfxr.dll","hostpolicy.dll","System.Private.CoreLib.dll"})if(!component.Files.Any(f=>f.RelativePath==required))throw Error("RUNTIME_CLOSURE");
    }
    private void VerifyComponentImages(string slot,SystemCandidateDescriptor descriptor,CancellationToken cancellation)
    {
        // Read with the slot store's protected role boundary, never LoadLibrary or
        // --describe on downloaded code. Only metadata is inspected before handoff.
        foreach(var component in descriptor.Components)foreach(var file in component.Files.Where(f=>new[]{".exe",".dll",".node"}.Contains(Path.GetExtension(f.RelativePath),StringComparer.OrdinalIgnoreCase)))
        {
            cancellation.ThrowIfCancellationRequested();using var input=slots.OpenFile(descriptor.SystemManifestDigest,component,file);using var reader=new PEReader(input);var header=reader.PEHeaders;
            var managed=header.CorHeader is {} clr&&(clr.Flags&CorFlags.ILOnly)!=0&&(clr.Flags&CorFlags.Requires32Bit)==0;
            if(header.PEHeader is null||header.CoffHeader.Machine!=Machine.Amd64&&!(header.CoffHeader.Machine==Machine.I386&&managed&&header.PEHeader.Magic==PEMagic.PE32)||component.Kind==SystemComponentKind.Probe&&managed)throw Error("COMPONENT_ARCHITECTURE");
        }
    }
    private void VerifyMigrationDeclaration(string payload,SystemCandidateDescriptor descriptor)
    {
        // The bundled payload carries no source; the build records the schema declaration.
        var build=NativeJson.Read<JsonElement>(files.Read(Path.Combine(payload,"legal","build-inputs.json"),65536));
        if(!build.TryGetProperty("databaseSchema",out var schema)||schema.ValueKind!=JsonValueKind.Number||!schema.TryGetInt32(out var target)||target!=descriptor.DatabaseTargetSchema||
            !build.TryGetProperty("databaseMigrationsFrom",out var from)||from.ValueKind!=JsonValueKind.Array||from.GetArrayLength()>1024||from.EnumerateArray().Any(v=>v.ValueKind!=JsonValueKind.Number||!v.TryGetInt32(out _)))throw Error("MIGRATION_DECLARATION");
        if(!from.EnumerateArray().Select(v=>v.GetInt32()).SequenceEqual(descriptor.DatabaseMigrationsFrom))throw Error("MIGRATION_DECLARATION");
    }
    private void VerifyAppSource(string payload,SystemCandidateDescriptor descriptor)
    {
        var build=NativeJson.Read<JsonElement>(files.Read(Path.Combine(payload,"legal","build-inputs.json"),65536));
        if(build.GetProperty("appSourceCommit").GetString()!=descriptor.SourceCommit||build.GetProperty("helperSourceCommit").GetString()!=descriptor.SourceCommit||build.GetProperty("packagerSourceCommit").GetString()!=descriptor.SourceCommit)throw Error("APP_SOURCE_COMMIT");
    }
    private void WriteOnce(string path,byte[] bytes)=>applicationVerifier.WriteIntakeRecord(path,bytes);
    private void WriteImmutable(string path,byte[] bytes){if(File.Exists(path)){if(!files.Read(path,32768).SequenceEqual(bytes))throw Error("RECEIPT_COLLISION");}else WriteOnce(path,bytes);}
    private static IOException Error(string code)=>new("SYSTEM_CANDIDATE_"+code);
}

using System.IO;
using System.Text.Json;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Yahad.Service;
using Yahad.Install;
internal sealed class PrivateControlFiles:IInstalledControlFiles
{public byte[] Read(string path,int bound,ICollection<IDisposable> held){var file=WindowsFileBoundary.OpenPrivateRead(path);held.Add(file);return file.Small(bound);}}
internal sealed class FileControl(ProductionFixture fixture):IChildControlFactory
{
    internal int Creates;internal bool HeldAfterClose;internal string? StopError;
    public IChildControl Create(){Creates++;HeldAfterClose=false;StopError=null;return new Channel(this,fixture);}
    private sealed class Channel(FileControl owner,ProductionFixture fixture):IChildControl
    {
        public string Endpoint=>"synthetic-control-no-network-listener";
        private async Task Wait(string name,CancellationToken cancellation)
        {
            while(!File.Exists(Path.Combine(fixture.Data,name)))
            {
                if(File.Exists(fixture.PathOf("worker-error.json")))throw new IOException("SYNTHETIC_WORKER_FAILED "+File.ReadAllText(fixture.PathOf("worker-error.json")));
                await Task.Delay(10,cancellation);
            }
        }
        public async Task<ChildReady> ReadyAsync(int root,CancellationToken cancellation)
        {await Wait("native-ready",cancellation);using var value=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.Data,"native-ready")));if(value.RootElement.GetProperty("pid").GetInt32()!=root||value.RootElement.GetProperty("PATH").GetString()!=""||!value.RootElement.GetProperty("clean").GetBoolean())throw new IOException("STORE_CHILD_ENV_OR_PID");return new((uint)root,"ready");}
        public async Task StopAsync(CancellationToken cancellation)
        {File.WriteAllText(Path.Combine(fixture.Data,"native-stop"),"stop");await Wait("native-closed",cancellation);try{using var wrong=WindowsFileBoundary.AcquireDataLease(fixture.Data);throw new IOException("EARLY_LEASE_RELEASE");}catch(Win32Exception e)when(e.NativeErrorCode==32){owner.HeldAfterClose=true;}catch(Exception e){owner.StopError=e.GetType().Name+":"+e.Message;throw;}File.WriteAllText(Path.Combine(fixture.Data,"native-exit"),"exit");}
        public void Dispose(){}
    }
}
internal sealed class ProductionFixture:IDisposable
{
    internal readonly string Root,Dataset,OriginalDataId=Guid.NewGuid().ToString("N"),InstallationId=Guid.NewGuid().ToString("N"),Payload,Node,Repo;
    internal string DataId,Phase="committed";internal long AnchorEpoch=1;internal string? AnchorDigest,OwnerRelease;internal long? Expiry;
    internal int Schema {get;}
    internal InstalledRecovery? Recovery;internal DateTimeOffset Now=DateTimeOffset.UtcNow;internal readonly FileControl Control;
    internal string Data=>Recovery is null?Path.Combine(Dataset,"data"):Path.Combine(Dataset,"restore-"+Recovery.OperationId,"completed","data");
    internal string Policy=>Path.Combine(Dataset,"update-policy");private string release="";
    private ProductionFixture(string repo)
    {
        Repo=repo;Schema=ReadSourceSchema(repo);Root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());Directory.CreateDirectory(Path.Combine(Root,"datasets"));
        var source=SafeDirectory.CreatePrivateSession(Path.Combine(Root,"datasets"));Dataset=Path.Combine(Root,"datasets",OriginalDataId);
        if(!Path.GetFullPath(source).StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!Path.GetFullPath(Dataset).StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("TEST_MOVE_SCOPE");Directory.Move(source,Dataset);
        DataId=OriginalDataId;Payload=Path.Combine(Root,"payload-stage");Directory.CreateDirectory(Payload);Node=Path.Combine(Payload,"runtime/node/node.exe");Directory.CreateDirectory(Path.GetDirectoryName(Node)!);Control=new(this);
        Directory.CreateDirectory(Path.Combine(Root,"temporary"));Directory.CreateDirectory(Data);File.WriteAllText(Path.Combine(Dataset,"master-key"),Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));File.WriteAllText(Policy,"synthetic-policy");File.WriteAllText(Path.Combine(Dataset,"update-pin"),"synthetic-pin");
    }
    internal static async Task<ProductionFixture> Create(string node,string repo,string helper)
    {
        var f=new ProductionFixture(repo);
        // Node/CreateProcess cannot spawn this non-namespaced native image at
        // MAX_PATH. Diagnose the fixture location before copying or starting it.
        if(Path.Combine(f.Root,"payloads",new string('a',64),"app/native/Yahad.Platform.exe").Length>=260)
            throw new IOException("STORE_FIXTURE_PATH_TOO_LONG: select a shorter private TEMP directory");
        File.Copy(node,f.Node);Copy(helper,Path.Combine(f.Payload,"app/native"));
        void Put(string name,string text){var p=Path.Combine(f.Payload,name);Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllText(p,text);}
        Put("runtime/ffmpeg/ffmpeg.exe","synthetic-ffmpeg-not-executed");Put("runtime/caddy/caddy.exe","synthetic-caddy-not-executed");
        Put("app/package.json","{\"type\":\"module\"}");Put("app/node_modules/tsx/package.json","{\"type\":\"module\",\"exports\":\"./index.mjs\"}");
        Put("app/node_modules/tsx/index.mjs","export * from "+JsonSerializer.Serialize(new Uri(Path.Combine(repo,"node_modules/tsx/dist/loader.mjs")).AbsoluteUri)+";");
        Put("legal/build-inputs.json",JsonSerializer.Serialize(new{databaseSchema=f.Schema}));
        Put("app/src/worker.ts",f.Worker());
        Put("app/src/main.ts","import fs from 'node:fs';try{await import('./worker.ts')}catch(error){fs.writeFileSync("+JsonSerializer.Serialize(f.PathOf("worker-error.json"))+",JSON.stringify({name:error.name,message:error.message,stack:error.stack}));process.exitCode=1}");
        var records=Directory.GetFiles(f.Payload,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(p=>new{path=Path.GetRelativePath(f.Payload,p).Replace('\\','/'),size=new FileInfo(p).Length,sha256=Hash(File.ReadAllBytes(p))}).ToArray();
        var manifest=JsonSerializer.SerializeToUtf8Bytes(new{version=ApplicationRelease.Version,schema=1,kind="windows-runtime-candidate",target="win-x64",profile="full",authority="application-updater",
            launch=new{node="runtime/node/node.exe",entry="app/src/main.ts",helper="app/native/Yahad.Platform.exe",ffmpeg="runtime/ffmpeg/ffmpeg.exe",caddy="runtime/caddy/caddy.exe"},files=records});
        f.release=Hash(manifest);File.WriteAllBytes(Path.Combine(f.Payload,"bundle.json"),manifest);Directory.CreateDirectory(Path.Combine(f.Root,"payloads"));var target=Path.Combine(f.Root,"payloads",f.release);Directory.Move(f.Payload,target);
        f.actualPayload=target;
        await f.Seed(node);f.Publish();return f;
    }
    private string? actualPayload;internal string Package=>actualPayload??Payload;
    internal string PathOf(string name)=>Path.Combine(Root,name);
    internal void ClearSignals()
    {
        foreach(var name in new[]{"native-ready","native-stop","native-closed","native-exit"})if(File.Exists(Path.Combine(Data,name)))File.Delete(Path.Combine(Data,name));
        // Callers clear only after the previous child is observed quiescent.
        if(File.Exists(PathOf("worker-error.json")))File.Delete(PathOf("worker-error.json"));
    }
    internal void Reset(){Recovery=null;DataId=OriginalDataId;Phase="committed";AnchorEpoch=1;AnchorDigest=null;OwnerRelease=null;Expiry=null;if(File.Exists(PathOf("PENDING.JSON")))File.Delete(PathOf("PENDING.JSON"));File.WriteAllText(Policy,"synthetic-policy");File.WriteAllText(Path.Combine(Dataset,"update-pin"),"synthetic-pin");ClearSignals();Publish();}
    internal void ResetRecovery(NativeBackupResult b,NativeRestoreResult restore){Reset();DataId=Guid.NewGuid().ToString("N");Recovery=new(OriginalDataId,b.BackupId,restore.OperationId,b.ReceiptDigest);Phase="recovered";Publish();}
    internal void Publish()
    {
        string Write<T>(string name,T value){var bytes=JsonSerializer.SerializeToUtf8Bytes(value);File.WriteAllBytes(PathOf(name),bytes);return Hash(bytes);}
        var owner=Write("owner-intent.json",new InstalledOwnerIntent(1,InstallationId,1,Guid.NewGuid().ToString("N"),OwnerRelease??release,DataId,Recovery is not null?"recover":"activate","synthetic-owner",Now.AddSeconds(-1).ToUnixTimeSeconds(),Expiry));
        var web=Write("web.json",new InstalledWeb(1,InstallationId,"https://yahad.invalid",32000));
        var head=Write("activation.json",new InstalledActivation(1,InstallationId,1,release,DataId,Schema,Schema,Hash(File.ReadAllBytes(Path.Combine(Dataset,"master-key"))),Hash(File.ReadAllBytes(Policy)),Hash(File.ReadAllBytes(Path.Combine(Dataset,"update-pin"))),owner,web,Phase,Recovery));
        Write("epoch.json",new InstalledEpoch(1,InstallationId,AnchorEpoch,AnchorDigest??head));
    }
    internal InstallationBinding Binding=>new(InstallationId,Hash(File.ReadAllBytes(PathOf("activation.json"))));
    internal InstalledActivationResolver CreateResolver(JobProcessFaultInjection? fault=null)=>new(Root,new PrivateControlFiles(),Control,()=>Now,fault,connectorProfile:RuntimeConnectorProfile.Synthetic);
    internal Task<AuthorizedServiceStart> Resolve(CancellationToken cancellation=default)=>CreateResolver().ResolveAsync(Binding,cancellation);
    private async Task Seed(string node)
    {
        var config=ConfigScript();var script="import fs from 'node:fs';try{const{Store}=await import("+JsonSerializer.Serialize(new Uri(Path.Combine(Repo,"src/store.ts")).AbsoluteUri)+");"+config+"const s=new Store(config);s.close();}catch(e){fs.writeFileSync("+JsonSerializer.Serialize(Path.Combine(Root,"seed-error.json"))+",JSON.stringify({name:e.name,message:e.message}));process.exit(1)}";
        using var job=JobProcess.Start(node,["--import",new Uri(Path.Combine(Repo,"node_modules/tsx/dist/loader.mjs")).AbsoluteUri,"--input-type=module","-e",script],Repo,LaunchEnvironment.Clean(Root));using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(30));if(await job.WaitAsync(bound.Token)!=0)throw new IOException("REAL_STORE_SEED_FAILED "+(File.Exists(Path.Combine(Root,"seed-error.json"))?File.ReadAllText(Path.Combine(Root,"seed-error.json")):"loader or import failure"));await job.WaitForTreeEmptyAsync(bound.Token);
    }
    private string ConfigScript()=>"const config={dataDir:"+JsonSerializer.Serialize(Path.Combine(Dataset,"data"))+",masterKey:Buffer.from(fs.readFileSync("+JsonSerializer.Serialize(Path.Combine(Dataset,"master-key"))+",'utf8').trim(),'base64'),connector:'mock',realAllowed:false,host:'127.0.0.1',port:32000,origin:'https://yahad.invalid',secureCookie:true,maxMedia:16777216,maxStorage:2147483648,childIdleMs:900000,sessionMs:43200000,diagnosticsEnabled:false};";
    private string Worker()=>"import fs from 'node:fs';import path from 'node:path';import{Store}from "+JsonSerializer.Serialize(new Uri(Path.Combine(Repo,"src/store.ts")).AbsoluteUri)+";import{createPlatformFiles}from "+JsonSerializer.Serialize(new Uri(Path.Combine(Repo,"src/platform-files.ts")).AbsoluteUri)+";"+"const data=process.env.DATA_DIR;const release=await createPlatformFiles('win32',path.resolve('native/Yahad.Platform.exe')).acquireDataLock(data);const config={dataDir:data,masterKey:Buffer.from(fs.readFileSync(process.env.MASTER_KEY_FILE,'utf8').trim(),'base64'),connector:'mock',realAllowed:false,host:'127.0.0.1',port:32000,origin:'https://yahad.invalid',secureCookie:true,maxMedia:16777216,maxStorage:2147483648,childIdleMs:900000,sessionMs:43200000,diagnosticsEnabled:false};if(fs.existsSync("+JsonSerializer.Serialize(Path.Combine(Root,"crash-before-store"))+"))process.exit(73);const store=new Store(config);store.db.exec('CREATE TABLE IF NOT EXISTS native_activation_probe(value INTEGER);INSERT INTO native_activation_probe VALUES(1)');fs.writeFileSync(path.join(data,'native-ready.partial'),JSON.stringify({pid:process.pid,PATH:process.env.PATH,clean:!process.env.NODE_OPTIONS&&!process.env.DOTNET_STARTUP_HOOKS}));fs.renameSync(path.join(data,'native-ready.partial'),path.join(data,'native-ready'));const wait=n=>new Promise(resolve=>{const timer=setInterval(()=>{if(fs.existsSync(path.join(data,n))){clearInterval(timer);resolve();}},10)});await wait('native-stop');store.close();await release();fs.writeFileSync(path.join(data,'native-closed'),'closed');await wait('native-exit');";
    internal void SelectPreparedPayload(string prepared)
    {
        var build=NativeJson.Read<JsonElement>(File.ReadAllBytes(Path.Combine(prepared,"legal/build-inputs.json")));
        if(build.GetProperty("databaseSchema").GetInt32()!=Schema)throw new IOException("STORE_FIXTURE_SCHEMA_MISMATCH");
        var manifest=File.ReadAllBytes(Path.Combine(prepared,"bundle.json"));release=Hash(manifest);var target=Path.Combine(Root,"payloads",release);
        Directory.CreateDirectory(target);
        foreach(var path in Directory.GetFiles(prepared,"*",SearchOption.AllDirectories))
        {var destination=Path.Combine(target,Path.GetRelativePath(prepared,path));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(path,destination);}
        actualPayload=target;Publish();
    }
    internal VerifiedProcessSpec RawSpec(WindowsFileBoundary.DataLease lease)=>new(Path.Combine(Package,"runtime/node/node.exe"),Hash(File.ReadAllBytes(Path.Combine(Package,"runtime/node/node.exe"))),["--import","tsx","src/main.ts"],Path.Combine(Package,"app"),Path.Combine(Root,"temporary"),Path.Combine(Package,"runtime/ffmpeg/ffmpeg.exe"),new Dictionary<string,string>{{"DATA_DIR",Data},{"MASTER_KEY_FILE",Path.Combine(Dataset,"master-key")}},lease);
    internal static string Hash(byte[] value)=>Convert.ToHexStringLower(SHA256.HashData(value));
    internal static int ReadSourceSchema(string repo)
    {
        // Store imports db.ts, which re-exports the side-effect-free schema
        // constant. Older source trees declared that same constant in db.ts.
        // Neither path imports Store or opens a database merely to read it.
        var path=Path.Combine(repo,"src/database-schema.ts");if(!File.Exists(path))path=Path.Combine(repo,"src/db.ts");
        var file=new FileInfo(path);if(!file.Exists||file.Length is <1 or >131072)throw new IOException("STORE_FIXTURE_SCHEMA_SOURCE");
        return ParseSourceSchema(File.ReadAllText(path));
    }
    internal static int ParseSourceSchema(string source)
    {
        var matches=Regex.Matches(source,@"(?m)^\s*export\s+const\s+DATABASE_SCHEMA\s*=\s*([1-9][0-9]*)\s*;\s*$",RegexOptions.CultureInvariant);
        if(matches.Count!=1||!int.TryParse(matches[0].Groups[1].Value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var schema)||schema is <1 or >100000)
            throw new IOException("STORE_FIXTURE_SCHEMA_SOURCE");
        return schema;
    }
    private static void Copy(string source,string target){Directory.CreateDirectory(target);foreach(var file in Directory.GetFiles(source))File.Copy(file,Path.Combine(target,Path.GetFileName(file)));}
    public void Dispose(){} // synthetic data stays private; never included in source or runtime artifacts
}

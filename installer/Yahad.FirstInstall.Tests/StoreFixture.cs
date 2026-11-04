using System.IO;
using System.Text.Json;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Yahad.Service;
using Yahad.Install;
internal sealed class FixturePreparationException(bool quiescenceConfirmed,Exception cause):IOException("SYNTHETIC_STORE_PREPARATION_FAILED",cause)
{
    internal bool QuiescenceConfirmed{get;}=quiescenceConfirmed;
}
internal sealed class FileControl(StoreFixture fixture):IChildControlFactory
{
    internal int Creates;internal bool HeldAfterClose;internal string? StopError;
    public IChildControl Create(){Creates++;HeldAfterClose=false;StopError=null;return new Channel(this,fixture);}
    private sealed class Channel(FileControl owner,StoreFixture fixture):IChildControl
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
internal sealed class StoreFixture:IDisposable
{
    private readonly OwnedFixtureDirectory owned;
    private readonly FixtureProcesses processes=new();
    private bool disposed,modulesReleased;
    private bool cleanupAttempted;private Exception? cleanupFailure;
    internal readonly string Root,Dataset,Package,Repo;
    internal int Schema {get;}
    internal readonly FileControl Control;
    internal string Data=>Path.Combine(Dataset,"data");
    private readonly StoreFixtureModules modules;
    private StoreFixture(string repo,StoreFixtureModules modules)
    {
        this.modules=modules;Repo=repo;Schema=ReadSourceSchema(repo);owned=OwnedFixtureDirectory.Create(Path.GetTempPath());Root=owned.Root;
        modules.AcquireUser();
        try{
        Directory.CreateDirectory(Path.Combine(Root,"datasets"));
        var source=PrivateSession.Create(Path.Combine(Root,"datasets"));Dataset=Path.Combine(Root,"datasets",Guid.NewGuid().ToString("N"));
        if(!Path.GetFullPath(source).StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!Path.GetFullPath(Dataset).StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("TEST_MOVE_SCOPE");Directory.Move(source,Dataset);
        Package=Path.Combine(Root,"package");Directory.CreateDirectory(Path.Combine(Package,"runtime/node"));Control=new(this);
        Directory.CreateDirectory(Path.Combine(Root,"temporary"));Directory.CreateDirectory(Data);File.WriteAllText(Path.Combine(Dataset,"master-key"),Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        }catch(Exception failure){modules.ReleaseUser();try{owned.Cleanup(()=>true);}catch(Exception cleanup){throw new AggregateException(failure,cleanup);}throw;}
    }
    internal static async Task<StoreFixture> Create(string node,string repo,string helper)
    {
        var f=new StoreFixture(repo,await StoreFixtureModules.Prepare(node,repo));
        try{
        // Node/CreateProcess cannot spawn this non-namespaced native image at
        // MAX_PATH. Diagnose the fixture location before copying or starting it.
        if(Path.Combine(f.Package,"app/native/Yahad.Platform.exe").Length>=260)
            throw new IOException("STORE_FIXTURE_PATH_TOO_LONG: select a shorter private TEMP directory");
        File.Copy(node,Path.Combine(f.Package,"runtime/node/node.exe"));Copy(helper,Path.Combine(f.Package,"app/native"));
        void Put(string name,string text){var p=Path.Combine(f.Package,name);Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllText(p,text);}
        Put("runtime/ffmpeg/ffmpeg.exe","synthetic-ffmpeg-not-executed");Put("runtime/caddy/caddy.exe","synthetic-caddy-not-executed");
        Put("app/package.json","{\"type\":\"module\"}");
        // Stands in for the server bundle: the package has no loader, so the entry
        // registers the repository tsx before importing the source Store worker.
        var register="const{register}=await import("+JsonSerializer.Serialize(new Uri(Path.Combine(repo,"node_modules/tsx/dist/esm/api/index.mjs")).AbsoluteUri)+");register();";
        Put("app/server/worker.ts",f.Worker());
        Put("app/server/main.mjs","import fs from 'node:fs';try{"+register+"await import('./worker.ts')}catch(error){fs.writeFileSync("+JsonSerializer.Serialize(f.PathOf("worker-error.json"))+",JSON.stringify({name:error.name,message:error.message,stack:error.stack}));process.exitCode=1}");
        await f.Seed(node);return f;
        }catch(Exception failure){try{f.Dispose();}catch(Exception cleanup){throw new AggregateException(failure,cleanup);}throw;}
    }
    internal string PathOf(string name)=>Path.Combine(Root,name);
    internal void ClearSignals()
    {
        foreach(var name in new[]{"native-ready","native-stop","native-closed","native-exit"})if(File.Exists(Path.Combine(Data,name)))File.Delete(Path.Combine(Data,name));
        // Callers clear only after the previous child is observed quiescent.
        if(File.Exists(PathOf("worker-error.json")))File.Delete(PathOf("worker-error.json"));
    }
    internal IPrivateAppLauncher CreateLauncher(VerifiedProcessSpec spec)
        =>processes.Launcher(new ServiceProcessLauncher(spec,Control));
    internal FixtureProcesses.JobOwner StartJob(string image,IEnumerable<string> arguments,string directory,IReadOnlyDictionary<string,string> environment,
        Action<Microsoft.Win32.SafeHandles.SafeProcessHandle,int>? beforeResume=null)
        =>processes.StartJob(image,arguments,directory,environment,beforeResume);
    // Call only after the child's tree is observed empty; reads back what the Store wrote.
    internal async Task<int> ReadSchema(string node)
    {
        var output=PathOf("schema-"+Guid.NewGuid().ToString("N")+".txt");
        const string code="const{DatabaseSync}=require('node:sqlite'),fs=require('node:fs');const db=new DatabaseSync(process.argv[1],{readOnly:true});fs.writeFileSync(process.argv[2],String(db.prepare('SELECT version FROM schema_version').get().version));db.close()";
        using var job=StartJob(node,["-e",code,Path.Combine(Data,"app.sqlite"),output],Root,LaunchEnvironment.Clean(PathOf("temporary")));
        using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        if(await job.WaitAsync(bound.Token)!=0)throw new IOException("STORE_SCHEMA_READ_FAILED");await job.WaitForTreeEmptyAsync(bound.Token);
        return int.Parse(File.ReadAllText(output),System.Globalization.CultureInfo.InvariantCulture);
    }
    private async Task Seed(string node)
    {
        var marker=JsonSerializer.Serialize(Path.Combine(Root,"seed-state.json"));
        var mark="const mark=phase=>fs.writeFileSync("+marker+",JSON.stringify({phase,resources:process.getActiveResourcesInfo()}));";
        try{modules.RequireUnchanged();}catch(Exception failure){throw new FixturePreparationException(true,failure);}
        var config=ConfigScript();var script="import fs from 'node:fs';"+mark+"mark('script-entered');try{const{Store}=await import("+JsonSerializer.Serialize(modules.StoreUri)+");mark('store-imported');"+config+"const s=new Store(config);s.close();mark('store-closed');}catch(e){fs.writeFileSync("+JsonSerializer.Serialize(Path.Combine(Root,"seed-error.json"))+",JSON.stringify({name:e.name,message:e.message}));process.exit(1)}";
        // Fixture preparation is outside the lifecycle/grant deadlines under
        // test. Use the admitted child's private cache/temp, not a second cold
        // cache in Root, and bound slow dependency preparation separately.
        FixtureProcesses.JobOwner seed;
        try{seed=processes.StartJob(node,["--input-type=module","-e",script],Repo,LaunchEnvironment.Clean(PathOf("temporary")));}
        catch(JobStartQuiescenceUnconfirmedException failure){throw await StopPreparation(failure.OwnedProcess,failure);}
        catch(Exception failure){throw new FixturePreparationException(true,failure);}
        using var seedOwner=seed;var job=seed.Process;using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            if(await job.WaitAsync(bound.Token)!=0)throw new IOException("REAL_STORE_SEED_FAILED "+(File.Exists(Path.Combine(Root,"seed-error.json"))?File.ReadAllText(Path.Combine(Root,"seed-error.json")):"loader or import failure"));
            await job.WaitForTreeEmptyAsync(bound.Token);
        }
        catch(Exception failure)
        {
            var stopped=await StopPreparation(job,failure);
            try{Console.Error.WriteLine("FIXTURE_SEED_STATE "+(File.Exists(Path.Combine(Root,"seed-state.json"))?File.ReadAllText(Path.Combine(Root,"seed-state.json")):"script-not-entered"));}catch{}
            BestEffortDiagnostic("FIXTURE_SEED_QUIESCENCE_CONFIRMED "+stopped.QuiescenceConfirmed);
            throw stopped;
        }
    }
    internal static async Task<FixturePreparationException> StopPreparation(JobProcess job,Exception failure)
    {
        using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try{await job.TerminateAndWaitForTreeEmptyAsync(cleanup.Token);}
        catch(Exception cleanupFailure){BestEffortDiagnostic("FIXTURE_SEED_CLEANUP_UNCONFIRMED "+cleanupFailure.GetType().Name);return new(false,failure);}
        return new(true,failure);
    }
    private static void BestEffortDiagnostic(string message){try{Console.Error.WriteLine(message);}catch{}}
    private string ConfigScript()=>"const config={dataDir:"+JsonSerializer.Serialize(Path.Combine(Dataset,"data"))+",masterKey:Buffer.from(fs.readFileSync("+JsonSerializer.Serialize(Path.Combine(Dataset,"master-key"))+",'utf8').trim(),'base64'),connector:'mock',realAllowed:false,host:'127.0.0.1',port:32000,origin:'https://yahad.invalid',secureCookie:true,maxMedia:16777216,maxStorage:2147483648,childIdleMs:900000,sessionMs:43200000,diagnosticsEnabled:false};";
    private string Worker()=>"import fs from 'node:fs';import path from 'node:path';import{Store}from "+JsonSerializer.Serialize(new Uri(Path.Combine(Repo,"src/store.ts")).AbsoluteUri)+";import{createPlatformFiles}from "+JsonSerializer.Serialize(new Uri(Path.Combine(Repo,"src/platform-files.ts")).AbsoluteUri)+";"+"const data=process.env.DATA_DIR;const release=await createPlatformFiles('win32',path.resolve('native/Yahad.Platform.exe')).acquireDataLock(data);const config={dataDir:data,masterKey:Buffer.from(fs.readFileSync(process.env.MASTER_KEY_FILE,'utf8').trim(),'base64'),connector:'mock',realAllowed:false,host:'127.0.0.1',port:32000,origin:'https://yahad.invalid',secureCookie:true,maxMedia:16777216,maxStorage:2147483648,childIdleMs:900000,sessionMs:43200000,diagnosticsEnabled:false};const store=new Store(config);store.db.exec('CREATE TABLE IF NOT EXISTS native_activation_probe(value INTEGER);INSERT INTO native_activation_probe VALUES(1)');fs.writeFileSync(path.join(data,'native-ready.partial'),JSON.stringify({pid:process.pid,PATH:process.env.PATH,clean:!process.env.NODE_OPTIONS&&!process.env.DOTNET_STARTUP_HOOKS}));fs.renameSync(path.join(data,'native-ready.partial'),path.join(data,'native-ready'));const wait=n=>new Promise(resolve=>{const timer=setInterval(()=>{if(fs.existsSync(path.join(data,n))){clearInterval(timer);resolve();}},10)});await wait('native-stop');store.close();await release();fs.writeFileSync(path.join(data,'native-closed'),'closed');await wait('native-exit');";
    internal VerifiedProcessSpec RawSpec(WindowsFileBoundary.DataLease lease)=>new(Path.Combine(Package,"runtime/node/node.exe"),["server/main.mjs"],Path.Combine(Package,"app"),Path.Combine(Root,"temporary"),Path.Combine(Package,"runtime/ffmpeg/ffmpeg.exe"),new Dictionary<string,string>{{"DATA_DIR",Data},{"MASTER_KEY_FILE",Path.Combine(Dataset,"master-key")}},lease);
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
    public void Dispose()
    {
        if(disposed)return;
        if(cleanupAttempted)throw new IOException("FIXTURE_RETAINED_AFTER_CLEANUP_FAILURE",cleanupFailure);
        cleanupAttempted=true;
        try{
        processes.CleanupAsync().GetAwaiter().GetResult();
        if(!modulesReleased){modules.ReleaseUser();modulesReleased=true;}
        owned.Cleanup(()=>processes.Confirmed);disposed=true;
        }catch(Exception failure){cleanupFailure=failure;throw;}
    }
}

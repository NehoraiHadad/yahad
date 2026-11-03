using System.IO;
using System.Text;
using System.Text.Json;
using Yahad.Install;

// Preparation only. Compile the actual Store dependency graph once rather than
// paying a cold tsx transform for every empty-database fixture. The admitted
// child still uses the real tsx loader and source Store in StoreFixture.Worker.
internal sealed class StoreFixtureModules
{
    private static readonly Dictionary<string,Task<StoreFixtureModules>> cache=new(StringComparer.OrdinalIgnoreCase);
    private readonly string node,repo,sourceFingerprint,outputFingerprint,root,output;
    private readonly OwnedFixtureDirectory owned;
    private readonly FixtureProcesses processes;
    private int users;
    internal void AcquireUser()=>Interlocked.Increment(ref users);
    internal void ReleaseUser(){if(Interlocked.Decrement(ref users)<0)throw new IOException("FIXTURE_COMPILER_USER_COUNT");}
    internal string StoreUri=>new Uri(Path.Combine(output,"store.js")).AbsoluteUri;
    private StoreFixtureModules(string node,string repo,string sourceFingerprint,OwnedFixtureDirectory owned,string output,FixtureProcesses processes)
    {this.node=node;this.repo=repo;this.sourceFingerprint=sourceFingerprint;this.owned=owned;root=owned.Root;this.output=output;this.processes=processes;outputFingerprint=OutputFingerprint();}
    internal static IAsyncDisposable OwnInvocation()=>new Invocation();
    internal static async Task TestSharedLifetime()
    {
        var directory=OwnedFixtureDirectory.Create(Path.GetTempPath());var output=Path.Combine(directory.Root,"modules");Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(directory.Root,"package.json"),"{}");File.WriteAllText(Path.Combine(output,"store.js"),"// synthetic; never executed");
        var value=new StoreFixtureModules("","","",directory,output,new());var key="test-lifetime-"+Guid.NewGuid().ToString("N");
        lock(cache)cache.Add(key,Task.FromResult(value));
        var scope=OwnInvocation();value.AcquireUser();value.AcquireUser();
        try
        {
            async Task RefuseActive(){try{await scope.DisposeAsync();}catch(IOException error)when(error.Message=="FIXTURE_COMPILER_USERS_UNCONFIRMED"){if(Directory.Exists(directory.Root))return;}throw new IOException("FIXTURE_SHARED_CACHE_DELETED_EARLY");}
            await RefuseActive();value.ReleaseUser();await RefuseActive();value.ReleaseUser();await scope.DisposeAsync();
            if(Directory.Exists(directory.Root))throw new IOException("FIXTURE_SHARED_CACHE_LEFT_BEHIND");
        }
        finally{while(Volatile.Read(ref value.users)>0)value.ReleaseUser();await scope.DisposeAsync();}
    }
    private sealed class Invocation:IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Task<StoreFixtureModules>[] prepared;lock(cache)prepared=cache.Values.ToArray();
            foreach(var task in prepared)
            {
                if(!task.IsCompleted)throw new IOException("FIXTURE_COMPILER_STILL_ACTIVE");
                if(!task.IsCompletedSuccessfully)continue;
                var value=await task;if(Volatile.Read(ref value.users)!=0)throw new IOException("FIXTURE_COMPILER_USERS_UNCONFIRMED");
                await value.processes.CleanupAsync();value.owned.Cleanup(()=>value.processes.Confirmed);
            }
            lock(cache)cache.Clear();
        }
    }
    internal static async Task<StoreFixtureModules> Prepare(string node,string repo)
    {
        node=Path.GetFullPath(node);repo=Path.GetFullPath(repo);Task<StoreFixtureModules> preparation;
        lock(cache)
        {
            var key=node+"\0"+repo;
            if(!cache.TryGetValue(key,out preparation!))cache[key]=preparation=Compile(node,repo);
        }
        try{var result=await preparation;result.RequireUnchanged();return result;}
        catch(FixturePreparationException){throw;}
        catch(Exception failure){throw new FixturePreparationException(true,failure);}
    }
    internal void RequireUnchanged()
    {
        if(SourceFingerprint(node,repo)!=sourceFingerprint||OutputFingerprint()!=outputFingerprint)
            throw new IOException("STORE_FIXTURE_COMPILED_INPUT_CHANGED");
    }
    private static string Inventory(IEnumerable<string> paths,string relativeTo)
    {
        var text=new StringBuilder();
        foreach(var path in paths.Order(StringComparer.Ordinal))
        {
            SafeDirectory.NoReparseAncestors(path);
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("STORE_FIXTURE_LINKED_INPUT");
            text.Append(Path.GetRelativePath(relativeTo,path)).Append('\0').Append(StoreFixture.Hash(File.ReadAllBytes(path))).Append('\n');
        }
        return StoreFixture.Hash(Encoding.UTF8.GetBytes(text.ToString()));
    }
    private static IReadOnlyList<string> TreeFiles(string root)
    {
        var files=new List<string>();var pending=new Stack<string>();pending.Push(root);
        while(pending.Count>0)
        {
            var directory=pending.Pop();if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("STORE_FIXTURE_LINKED_INPUT");
            foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes=File.GetAttributes(entry);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("STORE_FIXTURE_LINKED_INPUT");
                if((attributes&FileAttributes.Directory)!=0)pending.Push(entry);else files.Add(entry);
            }
        }
        return files;
    }
    private static string SourceFingerprint(string node,string repo)=>Inventory(
        TreeFiles(Path.Combine(repo,"src")).Where(p=>p.EndsWith(".ts",StringComparison.Ordinal)).Concat(new[]{node,
            Path.Combine(repo,"package.json"),Path.Combine(repo,"package-lock.json"),Path.Combine(repo,"tsconfig.json"),
            Path.Combine(repo,"node_modules/typescript/package.json"),Path.Combine(repo,"node_modules/typescript/lib/tsc.js"),Path.Combine(repo,"node_modules/typescript/lib/_tsc.js")}),repo);
    private string OutputFingerprint()=>Inventory(TreeFiles(output).Append(Path.Combine(root,"package.json")),root);
    private static async Task<StoreFixtureModules> Compile(string node,string repo)
    {
        var fingerprint=SourceFingerprint(node,repo);
        // Under the repository's ignored .local tree, so Node resolves the same
        // workspace dependencies; no junction or loader shim is added. These
        // fingerprints cover app/compiler entries and outputs, not npm bytes.
        var parent=Path.Combine(repo,".local");Directory.CreateDirectory(parent);
        var owned=OwnedFixtureDirectory.Create(parent);var processes=new FixtureProcesses();var root=owned.Root;var output=Path.Combine(root,"modules");var temporary=Path.Combine(root,"temporary");
        try{
        Directory.CreateDirectory(output);Directory.CreateDirectory(temporary);File.WriteAllText(Path.Combine(root,"package.json"),"{\"type\":\"module\",\"private\":true}");
        var arguments=new[]{Path.Combine(repo,"node_modules/typescript/lib/tsc.js"),"--target","ES2023","--lib","ES2023,DOM,DOM.Iterable","--module","ESNext","--moduleResolution","Bundler",
            "--strict","--esModuleInterop","--skipLibCheck","--types","node","--allowImportingTsExtensions","false","--noEmit","false","--noEmitOnError","--declaration","false","--sourceMap","false",
            "--rootDir",Path.Combine(repo,"src"),"--outDir",output,Path.Combine(repo,"src/store.ts"),Path.Combine(repo,"src/platform-files.ts")};
        SafeDirectory.NoReparseAncestors(node);SafeDirectory.NoReparseAncestors(arguments[0]);
        FixtureProcesses.JobOwner seed;
        try{seed=processes.StartJob(node,arguments,repo,LaunchEnvironment.Clean(temporary));}
        catch(JobStartQuiescenceUnconfirmedException failure){throw await StoreFixture.StopPreparation(failure.OwnedProcess,failure);}
        catch(Exception failure){throw new FixturePreparationException(true,failure);}
        using var jobOwner=seed;var job=seed.Process;using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            if(await job.WaitAsync(bound.Token)!=0)throw new IOException("STORE_FIXTURE_TYPESCRIPT_COMPILATION_FAILED");
            await job.WaitForTreeEmptyAsync(bound.Token);
        }
        catch(Exception failure){throw await StoreFixture.StopPreparation(job,failure);}
        if(SourceFingerprint(node,repo)!=fingerprint||!File.Exists(Path.Combine(output,"store.js"))||!File.Exists(Path.Combine(output,"platform-files.js")))
            throw new IOException("STORE_FIXTURE_COMPILED_INPUT_CHANGED");
        var result=new StoreFixtureModules(node,repo,fingerprint,owned,output,processes);
        Console.Error.WriteLine("FIXTURE_STORE_COMPILED "+JsonSerializer.Serialize(new{files=Directory.GetFiles(output,"*",SearchOption.AllDirectories).Length,source=fingerprint,emitted=result.outputFingerprint}));
        return result;
        }catch(Exception failure){try{await processes.CleanupAsync();owned.Cleanup(()=>processes.Confirmed);}catch(Exception cleanup){throw new AggregateException(failure,cleanup);}throw;}
    }
}

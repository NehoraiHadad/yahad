using System.IO;
using System.Text.Json;
using Yahad.Install;

// Diagnostic only: imports modules without constructing Store or a provider.
// The trace perturbs module loading; it is not a performance acceptance test.
internal static class ImportTraceProbe
{
    internal static async Task<int> RunAsync(string node,string repo,string output,bool compilerOnly=false)
    {
        output=PrivateSession.Create(output);
        var root=PrivateSession.Create(Path.GetTempPath());
        var trace=Path.Combine(root,"trace.jsonl");var loader=Path.Combine(root,"trace-loader.mjs");
        File.WriteAllText(trace,"");
        File.WriteAllText(loader,"""
import fs from 'node:fs';
let trace,roots,count=0;
export function initialize(data){trace=data.trace;roots=data.roots;}
function mark(phase,url){if(count++<4096)fs.appendFileSync(trace,JSON.stringify({phase,url,time:Date.now()})+'\n');}
export async function load(url,context,nextLoad){
 const watched=typeof url==='string'&&url.length<4096&&roots.some(root=>url.startsWith(root));
 if(watched)mark('load-start',url);
 const result=await nextLoad(url,context);
 if(watched){
  mark('load-end',url);
  if(result.format==='module'&&result.source!=null){
   const source=typeof result.source==='string'?result.source:Buffer.from(result.source).toString('utf8');
   if(!source.startsWith('#!'))return {...result,source:';globalThis.__yahadImportProbe?.("eval-start",'+JSON.stringify(url)+');\n'+source+'\n;globalThis.__yahadImportProbe?.("eval-end",'+JSON.stringify(url)+');\n'};
  }
 }
 return result;
}
""");
        var roots=new[]{new Uri(Path.Combine(repo,"src")+Path.DirectorySeparatorChar).AbsoluteUri,new Uri(Path.Combine(repo,"node_modules")+Path.DirectorySeparatorChar).AbsoluteUri};
        var source="import fs from 'node:fs';import{register}from'node:module';import{fileURLToPath,pathToFileURL}from'node:url';import path from'node:path';const trace="+JsonSerializer.Serialize(trace)+";"+"""
let count=0;globalThis.__yahadImportProbe=(phase,url)=>{if(count++<4096)fs.appendFileSync(trace,JSON.stringify({phase,url,time:Date.now()})+'\n')};
""";
        if(compilerOnly)
            source+="globalThis.__yahadImportProbe('entry','compiler');globalThis.__yahadImportProbe('compiler-import-start','esbuild');const{transformSync}=await import("+JsonSerializer.Serialize(new Uri(Path.Combine(repo,"node_modules/esbuild/lib/main.js")).AbsoluteUri)+");globalThis.__yahadImportProbe('compiler-import-end','esbuild');for(const file of "+JsonSerializer.Serialize(new[]{Path.Combine(repo,"src/store.ts"),Path.Combine(repo,"src/polls/storage.ts")})+"){globalThis.__yahadImportProbe('read-start',file);const text=fs.readFileSync(file,'utf8');globalThis.__yahadImportProbe('read-end',file);globalThis.__yahadImportProbe('transform-start',file);const result=transformSync(text,{loader:'ts',format:'esm',sourcefile:file,sourcemap:'inline'});globalThis.__yahadImportProbe('transform-end',file);if(!result.code)throw Error('EMPTY_TRANSFORM')}globalThis.__yahadImportProbe('imported','compiler-probe-completed');";
        else
            source+="const roots="+JsonSerializer.Serialize(roots)+".map(root=>pathToFileURL(fs.realpathSync(fileURLToPath(root))+path.sep).href);register("+JsonSerializer.Serialize(new Uri(loader).AbsoluteUri)+",{parentURL:import.meta.url,data:{trace,roots}});globalThis.__yahadImportProbe('entry','store');try{await import("+JsonSerializer.Serialize(new Uri(Path.Combine(repo,"src/store.ts")).AbsoluteUri)+");globalThis.__yahadImportProbe('imported','store')}catch(error){globalThis.__yahadImportProbe('error',String(error.code||error.name));process.exitCode=1}";
        JobProcess? job=null;var quiescent=true;var timedOut=false;int? exit=null;string? failureCode=null;
        try
        {
            var arguments=compilerOnly?new[]{"--input-type=module","-e",source}:new[]{"--import",new Uri(Path.Combine(repo,"node_modules/tsx/dist/loader.mjs")).AbsoluteUri,"--input-type=module","-e",source};
            job=JobProcess.Start(node,arguments,repo,LaunchEnvironment.Clean(root));
            using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(45));
            exit=await job.WaitAsync(bound.Token);await job.WaitForTreeEmptyAsync(bound.Token);
        }
        catch(Exception failure)
        {
            failureCode=failure.GetType().Name;timedOut=failure is OperationCanceledException;
            if(job is not null){try{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(30));await job.TerminateAndWaitForTreeEmptyAsync(cleanup.Token);}catch{quiescent=false;}}
        }
        finally{job?.Dispose();}
        File.Copy(trace,Path.Combine(output,"trace.jsonl"));
        var report=JsonSerializer.Serialize(new{diagnosticOnly=true,kind=compilerOnly?"esbuild-transform-only":"store-import-only",timedOut,exit,failureCode,quiescenceConfirmed=quiescent,storeConstructed=false,providerConstructed=false,noEntryMeans="pre-entry or tsx initialization; Store stall not established",root,trace=Path.Combine(output,"trace.jsonl")},new JsonSerializerOptions{WriteIndented=true});
        using(var file=new FileStream(Path.Combine(output,"report.json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)){file.Write(System.Text.Encoding.UTF8.GetBytes(report));}
        Console.WriteLine(report);
        return quiescent?0:1;
    }
}

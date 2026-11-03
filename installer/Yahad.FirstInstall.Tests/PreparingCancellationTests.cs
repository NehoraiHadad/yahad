using System.ComponentModel;
using System.IO;
using System.Security.AccessControl;
using System.Text.Json;
using Yahad.Install;
using Yahad.Setup;

internal static class PreparingCancellationTests
{
    private const string Owner="S-1-5-21-1-2-3-1001";
    private static void Assert(bool value){if(!value)throw new IOException("CANCELLATION_ASSERTION");}
    private static async Task Refuse(Func<Task> run)
    {try{await run();}catch(Exception e)when(e is IOException or UnauthorizedAccessException or Win32Exception){return;}throw new Exception("CANCELLATION_UNEXPECTED_ADMISSION");}
    private static FullInstallState State()
    {
        var id=Guid.NewGuid().ToString("N");var hash=new string('a',64);
        var files=Enumerable.Range(0,10).Select(i=>new InstallMember("payload/file-"+i,1,hash)).ToArray();
        return new(3,id,Guid.NewGuid().ToString("N"),hash,hash,new string('b',40),hash,Owner,47123,6,"preparing",hash,
            FirstOwnerBinding.Create(id,Owner),new(2,true,false,DateTimeOffset.UtcNow.ToUnixTimeSeconds()),files);
    }
    private static PreparingCancellationIntent Intent(FullInstallState? state=null)=>new(5,JsonSerializer.SerializeToUtf8Bytes(state??State()),"setup-stage-"+Guid.NewGuid().ToString("N"),true,true);
    private static Task<SetupResult> Run(Host host,UninstallMode mode=UninstallMode.DeleteData,string owner=Owner)=>PreparingInstallCancellation.RunAsync(owner,mode,host,_=>{},default);
    internal static async Task RunAsync(Func<string,Func<Task>,Task> check)
    {
        await check("inventory hashing propagates cancellation between chunks before any deletion",async()=>
        {
            var bytes=new byte[2*1048576+1];var digest=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            var member=new InstallMember("payload/file",bytes.Length,digest);using var cancellation=new CancellationTokenSource();var reads=0;
            int Read(long offset,byte[] buffer)
            {reads++;var count=(int)Math.Min(buffer.Length,bytes.Length-offset);Array.Copy(bytes,offset,buffer,0,count);if(reads==1)cancellation.Cancel();return count;}
            var canceled=false;try{NativePreparingCancellationHost.RequireFileContents(member,bytes.Length,Read,cancellation.Token);}catch(OperationCanceledException){canceled=true;}Assert(canceled&&reads==1);
            int CompleteRead(long offset,byte[] buffer){var count=(int)Math.Min(buffer.Length,bytes.Length-offset);Array.Copy(bytes,offset,buffer,0,count);return count;}
            NativePreparingCancellationHost.RequireFileContents(member,bytes.Length,CompleteRead,default);
            await Refuse(()=>{NativePreparingCancellationHost.RequireFileContents(member with{Sha256=new string('f',64)},bytes.Length,CompleteRead,default);return Task.CompletedTask;});
            var touched=false;canceled=false;try{NativePreparingCancellationHost.RequireFileContents(member,bytes.Length,(_,_)=>{touched=true;return 0;},new CancellationToken(true));}catch(OperationCanceledException){canceled=true;}Assert(canceled&&!touched);
        });
        await check("provisioned payload-only cancellation stays before enrollment and user data",async()=>
        {
            var state=State() with{RuntimeSid="S-1-5-80-1-2-3-4-5",AuthoritySid="S-1-5-80-5-4-3-2-1",ServicesCompleted=true};
            var intent=Intent(state);intent.Require(Owner);
            var files=state.Files.Select(f=>intent.StagingDirectory+"/"+f.Path).Append("installer-state.json").ToArray();
            string[] directories=[intent.StagingDirectory,intent.StagingDirectory+"/payload","datasets","datasets/"+state.DataId,"datasets/"+state.DataId+"/data"];
            var payload="payloads/"+state.PayloadDigest+"/file-0";string[] payloadDirectories=["payloads","payloads/"+state.PayloadDigest];
            NativePreparingCancellationHost.RequireInventory(intent,files,directories,true);
            NativePreparingCancellationHost.RequireBinaryInventory(intent,[payload],payloadDirectories);
            NativePreparingCancellationHost.RequireBinaryInventory(intent,[],[]);
            // The payload is published under the binary root, never under state.
            await Refuse(()=>{NativePreparingCancellationHost.RequireInventory(intent,files.Append(payload).ToArray(),directories.Concat(payloadDirectories).ToArray(),true);return Task.CompletedTask;});
            foreach(var extra in new[]{"datasets/"+state.DataId+"/data/app.sqlite","datasets/"+state.DataId+"/master-key","Secrets/setup-capability.json","intake/enrollment.json","activation.json","full-installer-plan.json"})
                await Refuse(()=>{NativePreparingCancellationHost.RequireInventory(intent,files.Append(extra).ToArray(),directories,true);return Task.CompletedTask;});
            foreach(var extra in new[]{"payloads/"+new string('f',64)+"/file-0",payload+".install-partial-"+Guid.NewGuid().ToString("N"),"host/Yahad.ServiceHost.exe","Yahad.Setup.exe","bootstrap/probe/node/node.exe"})
                await Refuse(()=>{NativePreparingCancellationHost.RequireBinaryInventory(intent,[payload,extra],payloadDirectories);return Task.CompletedTask;});
            await Refuse(()=>{NativePreparingCancellationHost.RequireBinaryInventory(intent,[payload],[..payloadDirectories,"system-slots"]);return Task.CompletedTask;});
            await Refuse(()=>{NativePreparingCancellationHost.RequireBinaryInventory(Intent(State()),[payload],payloadDirectories);return Task.CompletedTask;});
            await Refuse(()=>{NativePreparingCancellationHost.RequireInventory(intent,files.Skip(1).ToArray(),directories,true);return Task.CompletedTask;});
            foreach(var changed in new[]{state with{PolicyDigest=new string('d',64)},state with{PlanDigest=new string('d',64)},state with{Phase="committed"},state with{ServicesCompleted=false},state with{RuntimeSid="S-1-5-80-5-4-3-2-1"}})
                await Refuse(()=>{Intent(changed).Require(Owner);return Task.CompletedTask;});
            await Refuse(()=>{(intent with{RuntimeCreated=false}).Require(Owner);return Task.CompletedTask;});
            await Refuse(()=>{intent.Require("S-1-5-21-1-2-3-1002");return Task.CompletedTask;});
        });
        await check("pre-enrollment cancellation publishes immutable intent before first deletion and completes roots before receipt",async()=>
        {
            var host=new Host(Intent());var result=await Run(host);Assert(result.Ok&&result.Code=="REMOVED_ALL"&&!result.DataPreserved&&host.Published&&host.Receipt is null&&!host.StateRoot&&!host.BinaryRoot);
        });
        await check("cancellation awaits pending-deletion observation before publishing a plan or deleting files",async()=>
        {
            var wait=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var host=new Host(Intent()){Verification=wait.Task};
            var pending=Run(host);Assert(!pending.IsCompleted&&!host.Published&&host.Mutations==0);
            host.Runtime=false;host.Authority=false;wait.SetResult();await pending;Assert(host.Receipt is null&&!host.StateRoot&&!host.BinaryRoot);
        });
        for(var cut=1;cut<=17;cut++)
        {
            var point=cut;await check("pre-enrollment cancellation resumes bounded deletion after cut "+point,async()=>
            {
                var host=new Host(Intent()){CrashAt=point};await Refuse(()=>Run(host));Assert(host.Receipt is not null);
                host.CrashAt=0;await Run(host);Assert(host.Receipt is null&&!host.Runtime&&!host.Authority&&!host.StateRoot&&!host.BinaryRoot);
            });
        }
        await check("KeepData and wrong original Owner refuse before any cancellation publication or mutation",async()=>
        {
            foreach(var wrong in new[]{false,true})
            {var host=new Host(Intent());await Refuse(()=>Run(host,wrong?UninstallMode.DeleteData:UninstallMode.KeepData,wrong?"S-1-5-21-1-2-3-1002":Owner));Assert(!host.Published&&host.Mutations==0);}
        });
        await check("a locked final cancellation receipt remains the bounded recovery authority until deletion succeeds",async()=>
        {
            var host=new Host(Intent()){FailComplete=true};await Refuse(()=>Run(host));Assert(host.Receipt is not null&&!host.StateRoot&&!host.BinaryRoot);
            var mutations=host.Mutations;host.FailComplete=false;await Run(host);Assert(host.Receipt is null&&host.Mutations==mutations);
        });
        await check("completed cancellation disarms diagnostics before a disconnected final UI notification",async()=>
        {
            var host=new Host(Intent());var armed=false;
            await Refuse(()=>PreparingInstallCancellation.RunAsync(Owner,UninstallMode.DeleteData,host,p=>{if(p.Phase=="removed")throw new IOException("FAKE_PIPE_LOST");},default,_=>armed=true,()=>armed=false));
            Assert(host.Receipt is null&&!armed&&!host.StateRoot&&!host.BinaryRoot);
            var failed=new Host(Intent()){FailComplete=true};
            await Refuse(()=>PreparingInstallCancellation.RunAsync(Owner,UninstallMode.DeleteData,failed,_=>{},default,_=>armed=true,()=>armed=false));
            Assert(failed.Receipt is not null&&armed);
        });
        await check("final asynchronous verification is awaited before diagnostics disarm or completion notification",async()=>
        {
            var wait=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var host=new Host(Intent()){Completion=wait.Task};var armed=false;var notified=false;
            var pending=PreparingInstallCancellation.RunAsync(Owner,UninstallMode.DeleteData,host,p=>{if(p.Phase=="removed")notified=true;},default,_=>armed=true,()=>armed=false);
            Assert(!pending.IsCompleted&&armed&&!notified&&host.Receipt is not null);wait.SetResult();await pending;Assert(!armed&&notified&&host.Receipt is null);
        });
        await check("enrolled, completed, partially bound and changed-source states are never pre-enrollment cancellation",async()=>
        {
            var state=State();foreach(var changed in new[]{state with{Phase="committed"},state with{ServicesCompleted=true},state with{RuntimeSid="S-1-5-80-1-2-3-4-5"},
                state with{RuntimeSid="S-1-5-80-1-2-3-4-5",AuthoritySid="S-1-5-80-5-4-3-2-1"},state with{PlanDigest=new string('d',64)},state with{PolicyDigest=new string('d',64)},
                state with{PreservedBinaries=[new("x",1,new string('d',64))]},state with{Files=state.Files.Append(state.Files[0]).ToArray()}})
            {var host=new Host(Intent(changed));await Refuse(()=>Run(host));Assert(!host.Published&&host.Mutations==0);}
        });
        foreach(var fault in new[]{"data","binary","denied-query","changed-state","changed-file","new-service"})
        await check("pre-enrollment cancellation refuses "+fault+" before a deletion",async()=>
        {
            var host=new Host(Intent()){Fault=fault};await Refuse(()=>Run(host));Assert(host.Mutations==0);
        });
        await check("retry refuses new data or changed state while retaining the same protected cancellation intent",async()=>
        {
            foreach(var fault in new[]{"data","changed-state","changed-file","new-service"})
            {
                var host=new Host(Intent()){CrashAt=1};await Refuse(()=>Run(host));var before=host.Mutations;var intent=host.Receipt;
                host.CrashAt=0;host.Fault=fault;await Refuse(()=>Run(host));Assert(host.Mutations==before&&ReferenceEquals(intent,host.Receipt));
            }
        });
        await check("pre-enrollment administrative scope refuses user, service writers, service readers and foreign ownership",async()=>
        {
            WindowsFileBoundary.ValidatePreparingInstallAcl(new("O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)"),true);
            foreach(var acl in new[]{"O:BAD:P(A;;FA;;;BU)","O:BAD:P(A;;0x1301bf;;;S-1-5-80-1-2-3-4-5)","O:BAD:P(A;;0x1200a9;;;S-1-5-80-1-2-3-4-5)","O:"+Owner+"D:P(A;;FA;;;BA)","O:BAD:(A;;FA;;;BA)"})
                await Refuse(()=>{WindowsFileBoundary.ValidatePreparingInstallAcl(new(acl),true);return Task.CompletedTask;});
        });
    }
    private sealed class Host:IPreparingCancellationHost
    {
        internal PreparingCancellationIntent? Receipt;private readonly PreparingCancellationIntent original;
        internal bool Runtime=true,Authority=true,StateRoot=true,BinaryRoot=true,Published,FailComplete;internal int Mutations,CrashAt;internal string? Fault;
        internal Task? Verification,Completion;
        private readonly HashSet<string> files;private readonly HashSet<string> directories;
        internal Host(PreparingCancellationIntent intent)
        {original=intent;files=intent.State.Files.Select(f=>intent.StagingDirectory+"/"+f.Path).Append("installer-state.json").ToHashSet();directories=[intent.StagingDirectory,intent.StagingDirectory+"/payload"];}
        public PreparingCancellationIntent? ReadIntent()=>Receipt;
        public PreparingCancellationIntent Prepare()=>original;
        public async Task VerifyAsync(PreparingCancellationIntent intent,bool complete,bool servicesRemoved,CancellationToken cancellation)
        {
            if(Verification is{} pending)await pending.WaitAsync(cancellation);
            if(Fault=="denied-query")throw new Win32Exception(5);
            if(Fault is "binary" or "new-service"||servicesRemoved&&(Runtime||Authority))throw new IOException("UNEXPECTED_PUBLISHED_STATE");
            if(Fault=="changed-state")throw new IOException("UPDATE_PREPARING_STATE_CHANGED");
            NativePreparingCancellationHost.RequireInventory(intent,Fault=="data"?files.Append("datasets/unknown/data/app.sqlite").ToArray():files.ToArray(),directories.ToArray(),complete);
            if(Fault=="changed-file")NativePreparingCancellationHost.RequireFile(intent.State.Files[0],1,new string('f',64));
        }
        public void Publish(PreparingCancellationIntent intent){Assert(Mutations==0&&Receipt is null);Receipt=intent;Published=true;}
        private void Cut(){Assert(Receipt is not null);Mutations++;if(Mutations==CrashAt)throw new IOException("SYNTHETIC_CANCELLATION_CUT");}
        public Task RemoveServicesAsync(PreparingCancellationIntent intent,CancellationToken cancellation)
        {if(Runtime){Runtime=false;Cut();}if(Authority){Authority=false;Cut();}return Task.CompletedTask;}
        public void RemoveFiles(PreparingCancellationIntent intent,CancellationToken cancellation)
        {
            Assert(!Runtime&&!Authority);foreach(var name in files.Where(p=>p!="installer-state.json").ToArray()){files.Remove(name);Cut();}
            foreach(var directory in directories.OrderByDescending(p=>p.Length).ToArray()){directories.Remove(directory);Cut();}
            if(files.Remove("installer-state.json"))Cut();if(StateRoot){StateRoot=false;Cut();}if(BinaryRoot){BinaryRoot=false;Cut();}
        }
        public async Task CompleteAsync(PreparingCancellationIntent intent,CancellationToken cancellation){if(Completion is{} pending)await pending.WaitAsync(cancellation);Assert(!Runtime&&!Authority&&!StateRoot&&!BinaryRoot&&files.Count==0&&directories.Count==0);if(FailComplete)throw new IOException("FAKE_RECEIPT_LOCKED");Receipt=null;}
    }
}

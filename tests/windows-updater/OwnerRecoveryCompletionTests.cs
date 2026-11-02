using System.Text.Json;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;

internal static class OwnerRecoveryCompletionTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        foreach(var path in new[]{"previous restart","new-data rollback","normal update"})foreach(var boundary in new[]{"before-rpc-done","after-rpc-done","after-terminal-journal"})
            await check("completion crash "+boundary+" "+path+" preserves independent startup gate",async()=>
            {
                var restart=path=="previous restart";var normal=path=="normal update";
                var f=new PolicyFixture();var binding=WindowsInstallationReader.Parse(f.Files.Read(f.Files.PathOf("bootstrap.json"),1024));var head=f.Intake.Head();
                var anchor=new InstalledAuthorityAnchor(1,binding.InstallationId,ServiceDefinition.Name,InstalledAuthorityReader.AuthorityService,f.Owner.Sid,head.PolicyDigest,head.PinDigest);
                var operation=Guid.NewGuid().ToString("N");var restore=Guid.NewGuid().ToString("N");var done=f.Files.PathOf(".authority/done-"+operation+".json");var notified=false;
                using(var rpc=f.Files.Create(f.Files.PathOf(".authority/rpc-"+operation+".json"))){rpc.Write(JsonSerializer.SerializeToUtf8Bytes(new{Id=operation,Action="install"}));rpc.Flush(true);}
                var plan=new InstalledUpdatePlan(1,binding.InstallationId,operation,head.DataId,binding.ActivationReceiptDigest,head.ReleaseDigest,
                    f.Intake.Select().Catalog.Operation.ReleaseDigest,head.MinimumSchema,head.MinimumSchema,head.MaximumSchema,head.MinimumSchema,head.MaximumSchema,head.PolicyDigest,head.PinDigest,head.MasterKeyDigest);
                using(var journal=new InstalledUpdateJournal(f.Files,binding.InstallationId))
                {
                    journal.Begin(plan);
                    if(restart)journal.Append(UpdateCheckpoint.PreviousStartIntent);
                    else
                    {
                        journal.Append(UpdateCheckpoint.Quiesced);journal.Append(UpdateCheckpoint.BackupVerified,new(Guid.NewGuid().ToString("N"),new string('e',64),head.MinimumSchema));
                        journal.Append(UpdateCheckpoint.ActivationIntent);journal.Append(UpdateCheckpoint.CandidateStartIntent);
                        if(!normal){journal.Append(UpdateCheckpoint.RollbackIntent,restoreOperationId:restore);journal.Append(UpdateCheckpoint.RollbackStartIntent);}
                    }
                    try
                    {
                        UpdateCompletionProtocol.Complete(
                            ()=>{using var output=f.Files.Create(done);output.Write(JsonSerializer.SerializeToUtf8Bytes(new{Id=operation,Phase="healthy"}));output.Flush(true);},
                            ()=>journal.Append(restart?UpdateCheckpoint.Aborted:normal?UpdateCheckpoint.Healthy:UpdateCheckpoint.RollbackComplete),
                            ()=>notified=true,point=>{if(point==boundary)throw new IOException("INJECTED_COMPLETION_CRASH");});
                        throw new Exception("COMPLETION_FAULT_NOT_REACHED");
                    }
                    catch(IOException error)when(error.Message=="INJECTED_COMPLETION_CRASH"){}
                }
                if(notified)throw new Exception("COMPLETION_EVENT_BEFORE_DURABILITY");
                if(File.Exists(done)!=(boundary!="before-rpc-done"))throw new Exception("COMPLETION_DONE_ORDER");
                if(f.Reopen().Phase!=(boundary=="before-rpc-done"?"recovery_required":"idle"))throw new Exception("RPC_GATE_FIXTURE_INVALID");
                if(boundary=="after-terminal-journal")InstalledUpdateTransaction.RequireNoUnfinished(f.Files,anchor);
                else
                {
                    try{InstalledUpdateTransaction.RequireNoUnfinished(f.Files,anchor);throw new Exception("UNFINISHED_RECOVERY_STARTED");}
                    catch(IOException error)when(error.Message=="UPDATE_JOURNAL_UNFINISHED_OPERATION"){}
                    var endpoint=new OwnerRecoveryEndpoint(f.Files,anchor,f.Ledger,(_,_,_)=>throw new Exception("UNEXPECTED_NATIVE_CALL"),
                        ()=>throw new Exception("UNEXPECTED_NATIVE_GATE"),()=>throw new Exception("UNEXPECTED_RUNTIME"),new UnusedChannels(),()=>f.Now);
                    var reply=await endpoint.DispatchAsync(f.Owner,new(1,Guid.NewGuid().ToString("N"),"recovery",JsonSerializer.SerializeToElement(new{})),default);
                    var summary=reply.LastRequest!.Value.Deserialize<OwnerRecoverySummary>()!;
                    if(summary.Operations.All(x=>x.OperationId!=operation))throw new Exception("UNFINISHED_RECOVERY_HIDDEN_AFTER_RPC_DONE");
                }
            });
    }
    private sealed class UnusedChannels:IChildControlFactory{public IChildControl Create()=>throw new Exception("UNEXPECTED_CHANNEL");}
}

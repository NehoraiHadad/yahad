using System.Text.Json;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;

internal static class OwnerRecoveryEndpointTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("owner recovery status is available without an app or native execution",async()=>
        {
            var f=new PolicyFixture();var count=new Counter();var endpoint=Create(f,count);
            var reply=await endpoint.DispatchAsync(f.Owner,Frame("recovery",new{}),default);
            var view=reply.LastRequest!.Value.Deserialize<OwnerRecoverySummary>()!;
            Assert(view.Operations.Length==0&&view.State=="recovery_required"&&count.Offline==0&&reply.Confirmation is null);
            Assert(!JsonSerializer.Serialize(reply).Contains(f.Files.Root,StringComparison.OrdinalIgnoreCase));
        });
        foreach(var mode in new[]{"runtime","wrong_owner","session_zero","noninteractive","missing_pid","missing_created"})await check("owner recovery rejects "+mode+" before touching native host",async()=>
        {
            var f=new PolicyFixture();var count=new Counter();var endpoint=Create(f,count);
            var peer=mode switch{"runtime"=>f.Runtime,"wrong_owner"=>f.Owner with{Sid=f.Runtime.Sid},"session_zero"=>f.Owner with{Session=0},"noninteractive"=>f.Owner with{Interactive=false},"missing_pid"=>f.Owner with{ProcessId=0},_=>f.Owner with{Created=0}};
            await Denied(()=>endpoint.DispatchAsync(peer,Frame("recovery",new{}),default));Assert(count.Offline==0);
        });
        await check("owner recovery refuses unknown operation and caller-selected path or release",async()=>
        {
            var f=new PolicyFixture();var count=new Counter();var endpoint=Create(f,count);
            await Denied(()=>endpoint.DispatchAsync(f.Owner,Frame("recover",new{OperationId=Guid.NewGuid().ToString("N")}),default));
            await Denied(()=>endpoint.DispatchAsync(f.Owner,Frame("recover",new{OperationId="../outside"}),default));
            await Denied(()=>endpoint.DispatchAsync(f.Owner,Frame("recover",new{OperationId=Guid.NewGuid().ToString("N"),Release=new string('a',64)}),default));
            Assert(count.Offline==0);
        });
        await check("owner recovery cannot answer a missing or replayed challenge",async()=>
        {
            var f=new PolicyFixture();var count=new Counter();var endpoint=Create(f,count);
            var answer=new OwnerAnswer(1,Guid.NewGuid().ToString("N"),new string('a',64),true);
            await Denied(()=>endpoint.DispatchAsync(f.Owner,Frame("recovery-answer",answer),default));Assert(count.Offline==0);
        });
        await check("owner recovery preserves partial journal evidence and refuses selection",async()=>
        {
            var f=new PolicyFixture();var count=new Counter();var endpoint=Create(f,count);var id=Guid.NewGuid().ToString("N");
            var directory=f.Files.PathOf(".authority/updates/"+id);Directory.CreateDirectory(directory);var partial=Path.Combine(directory,"00001.json.partial-interrupted");File.WriteAllText(partial,"incomplete");
            var reply=await endpoint.DispatchAsync(f.Owner,Frame("recovery",new{}),default);var view=reply.LastRequest!.Value.Deserialize<OwnerRecoverySummary>()!;
            Assert(view.Error=="recovery_evidence_unavailable"&&view.Operations.Length==0&&File.Exists(partial));
            await Denied(()=>endpoint.DispatchAsync(f.Owner,Frame("recover",new{OperationId=id}),default));Assert(count.Offline==0&&File.Exists(partial));
        });
        foreach(var failure in new[]{"partial","malformed","missing","empty","invalid_delegation"})await check("native owner recovery stays available for "+failure+" mode without defaulting Notify",async()=>
        {
            var f=new PolicyFixture();var mode=f.Files.PathOf("intake/mode.json");
            if(failure=="partial")File.WriteAllText(mode+".partial-"+Guid.NewGuid().ToString("N"),"interrupted");
            else if(failure=="missing")File.Move(mode,f.Files.PathOf(".authority/mode-before-test.json"));
            else if(failure=="invalid_delegation")
            {var binding=WindowsInstallationReader.Parse(f.Files.Read(f.Files.PathOf("bootstrap.json"),1024));File.WriteAllBytes(mode,JsonSerializer.SerializeToUtf8Bytes(new NativeUpdateMode(1,binding.InstallationId,"auto","invalid",true)));}
            else File.WriteAllText(mode,failure=="empty"?"":"{");
            var (composition,anchor)=Composition(f,(_,_)=>{});Assert(composition.Router.Phase=="recovery_required");
            await Denied(()=>composition.Router.DispatchAsync(AuthorityEndpointRole.Runtime,f.Runtime,JsonSerializer.SerializeToUtf8Bytes(Frame("status",new{}))));
            var endpoint=new OwnerRecoveryEndpoint(composition.Files,anchor,composition.Ledger,(_,_,_)=>throw new Exception("UNEXPECTED_NATIVE_CALL"),
                ()=>throw new Exception("UNEXPECTED_GATE"),()=>throw new Exception("UNEXPECTED_RUNTIME"),new UnusedChannels(),()=>f.Now);
            composition.Router.ConfigureRecovery(endpoint);
            var reply=await composition.Router.DispatchAsync(AuthorityEndpointRole.Owner,f.Owner,JsonSerializer.SerializeToUtf8Bytes(Frame("recovery",new{})));
            Assert(reply.Mode=="unavailable"&&reply.Phase=="recovery_required");
        });
        foreach(var denied in new[]{"role","access"})await check("native mode availability fallback propagates "+denied+" denial",()=>
        {
            var f=new PolicyFixture();var refused=false;
            try
            {
                _=Composition(f,(path,_)=>
                {if(path==f.Files.PathOf("intake/mode.json")){if(denied=="role")throw new IOException("INSTALLED_ROLE_ACCESS");throw new System.ComponentModel.Win32Exception(5);}});
            }
            catch(IOException error)when(error.Message=="INSTALLED_ROLE_ACCESS"){refused=true;}
            catch(System.ComponentModel.Win32Exception error)when(error.NativeErrorCode==5){refused=true;}
            Assert(refused);return Task.CompletedTask;
        });
    }
    private static OwnerRecoveryEndpoint Create(PolicyFixture f,Counter count)
    {
        var binding=WindowsInstallationReader.Parse(f.Files.Read(f.Files.PathOf("bootstrap.json"),1024));
        var anchor=new InstalledAuthorityAnchor(1,binding.InstallationId,ServiceDefinition.Name,InstalledAuthorityReader.AuthorityService,f.Owner.Sid,new string('a',64),new string('a',64));
        return new(f.Files,anchor,f.Ledger,(_,_,_)=>{count.Offline++;throw new IOException("SYNTHETIC_NO_NATIVE_RECOVERY_PROOF");},
            ()=>throw new Exception("UNEXPECTED_NATIVE_GATE"),()=>throw new Exception("UNEXPECTED_RUNTIME_START"),new UnusedChannels(),()=>f.Now);
    }
    private static (InstalledAuthorityComposition,InstalledAuthorityAnchor) Composition(PolicyFixture f,Action<string,InstalledFileRole> roles)
    {
        var binding=WindowsInstallationReader.Parse(f.Files.Read(f.Files.PathOf("bootstrap.json"),1024));
        var anchor=new InstalledAuthorityAnchor(1,binding.InstallationId,ServiceDefinition.Name,InstalledAuthorityReader.AuthorityService,f.Owner.Sid,new string('a',64),new string('a',64));
        var bytes=JsonSerializer.SerializeToUtf8Bytes(anchor);File.WriteAllBytes(f.Files.PathOf("installation.json"),bytes);
        var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var composition=InstalledAuthorityComposition.PrivateFixture(f.Files.Root,hash,f.Runtime.Sid,"","",roles,
            ()=>throw new Exception("UNEXPECTED_HOST"),_=>throw new Exception("UNEXPECTED_STOP"),new UnusedChannels(),_=>throw new Exception("UNEXPECTED_RUNTIME"),()=>f.Now);
        return(composition,anchor);
    }
    private static AuthorityFrame Frame(string method,object body)=>new(1,Guid.NewGuid().ToString("N"),method,JsonSerializer.SerializeToElement(body));
    private static void Assert(bool value){if(!value)throw new Exception("OWNER_RECOVERY_ASSERTION");}
    private static async Task Denied(Func<Task> action)
    {try{await action();}catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception){return;}throw new Exception("OWNER_RECOVERY_UNEXPECTED_ADMISSION");}
    private sealed class Counter{internal int Offline;}
    private sealed class UnusedChannels:IChildControlFactory{public IChildControl Create()=>throw new Exception("UNEXPECTED_RECOVERY_CHANNEL");}
}

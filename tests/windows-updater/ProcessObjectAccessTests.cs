using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;
using Yahad.Install;
using Yahad.Authority;
using Yahad.Service;

// In-memory descriptors and sequencing adapters only. No process/token handle,
// security descriptor publication, real pipe or service operation is executed.
internal static class ProcessObjectAccessTests
{
    private const string Runtime="S-1-5-80-1-2-3-4-5",Authority="S-1-5-80-5-4-3-2-1",Owner="S-1-5-21-1-2-3-1001",Other="S-1-5-21-5-6-7-1001",Logon="S-1-5-5-100-200";
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        foreach(var (sid,attributes,expected) in new (string,uint,bool)[]{
            ("S-1-5-32-544",4,true),("S-1-5-32-544",7,true),
            ("S-1-5-32-544",0,false),("S-1-5-32-544",2,false),
            ("S-1-5-32-544",0x10,false),("S-1-5-32-544",0x14,false),
            ("S-1-5-32-545",4,false)})
        await check("query-only administrator group observation "+sid+" attributes "+attributes,()=>
        {Assert(WindowsInstalledProcessObjectKernel.EnabledAdministrator(sid,attributes)==expected);return Task.CompletedTask;});
        foreach(var actor in new[]{Authority,UpdaterServicePrincipals.RecoveryA.Value,UpdaterServicePrincipals.RecoveryB.Value})
        await check("full custody grants exact fixed actors and Runtime read only from "+actor,()=>
        {
            var kernel=new Kernel(actor);InstalledProcessObjectAccess.PublishFullCustodyAccess(actor,Authority,kernel);
            foreach(var sid in UpdaterServicePrincipals.All(new(Authority)).Where(s=>s.Value!=actor))
                Assert(Mask(kernel.Process,sid.Value)==0x101141&&Mask(kernel.Token,sid.Value)==8);
            Assert(Mask(kernel.Process,Runtime)==0x101000&&Mask(kernel.Token,Runtime)==8&&Mask(kernel.Process,Owner)==0);
            var writes=kernel.Writes.Count;InstalledProcessObjectAccess.PublishFullCustodyAccess(actor,Authority,kernel);Assert(writes==kernel.Writes.Count);
            return Task.CompletedTask;
        });
        await check("full Runtime grants every updater only runtime control and query token",()=>
        {
            var kernel=new Kernel(Runtime);InstalledProcessObjectAccess.PublishFullRuntimeAccess(Runtime,Authority,kernel);
            foreach(var sid in UpdaterServicePrincipals.All(new(Authority)))Assert(Mask(kernel.Process,sid.Value)==0x101101&&Mask(kernel.Token,sid.Value)==8);
            Assert(Mask(kernel.Process,Owner)==0);return Task.CompletedTask;
        });
        foreach(var actor in new[]{Runtime,Owner,"S-1-5-80-9-9-9-9-9"})
        await check("full custody refuses unlisted actor before writes "+actor,async()=>
        {
            var kernel=new Kernel(actor);await Reject(()=>{InstalledProcessObjectAccess.PublishFullCustodyAccess(actor,Authority,kernel);return Task.CompletedTask;});Assert(kernel.Writes.Count==0);
        });
        await check("Authority custody supports different logons with only the fixed same-user process mask and token query",()=>
        {
            var main=new Kernel(Authority);var helper=new Kernel(Authority);Remove(main.Process,Authority);Remove(main.Token,Authority);Remove(helper.Process,Authority);Remove(helper.Token,Authority);
            var helperLogon="S-1-5-5-300-400";Remove(helper.Process,Logon);Remove(helper.Token,Logon);Add(helper.Process,helperLogon,0x1fffff);Add(helper.Token,helperLogon,0xf01ff);helper.Identity=helper.Identity with{CurrentPid=102,ObjectPid=102,Created=203,TokenId=304,LogonSid=helperLogon};
            var mainBefore=Clone(main.Process);var helperBefore=Clone(helper.Process);
            Assert((Mask(mainBefore,Authority)|Mask(mainBefore,helperLogon))==0&&(Mask(helperBefore,Authority)|Mask(helperBefore,Logon))==0);
            InstalledProcessObjectAccess.PublishAuthorityCustodyAccess(Authority,main);InstalledProcessObjectAccess.PublishAuthorityCustodyAccess(Authority,helper);
            foreach(var kernel in new[]{main,helper})
            {
                Assert(Mask(kernel.Process,Authority)==0x101141&&Mask(kernel.Token,Authority)==8);
                Assert((Mask(kernel.Process,Authority)&0x101141)==0x101141&&(Mask(kernel.Process,Authority)&0x101040)==0x101040);
                Assert(Mask(kernel.Process,Runtime)==0&&Mask(kernel.Process,Owner)==0&&Mask(kernel.Token,Runtime)==0&&Mask(kernel.Token,Owner)==0);
            }
            Prefix(mainBefore,main.Process);Prefix(helperBefore,helper.Process);return Task.CompletedTask;
        });
        await check("Authority custody preserves default actor-full access without rewriting either object",()=>
        {
            var kernel=new Kernel(Authority);var process=Bytes(kernel.Process);var token=Bytes(kernel.Token);InstalledProcessObjectAccess.PublishAuthorityCustodyAccess(Authority,kernel);
            Assert(kernel.Writes.Count==0&&process.AsSpan().SequenceEqual(Bytes(kernel.Process))&&token.AsSpan().SequenceEqual(Bytes(kernel.Token)));return Task.CompletedTask;
        });
        foreach(var actor in new[]{Runtime,Owner})
            await check("custody publication cannot grant DUP_HANDLE to non-Authority actor "+actor,async()=>
            {
                var kernel=new Kernel(actor);await Reject(()=>{InstalledProcessObjectAccess.PublishAuthorityCustodyAccess(actor,kernel);return Task.CompletedTask;});Assert(kernel.Writes.Count==0);
            });
        await check("Authority custody still rejects nonminimal service privileges before either object write",async()=>
        {
            var kernel=new Kernel(Authority);kernel.Identity=kernel.Identity with{Privileges=["SeChangeNotifyPrivilege","SeDebugPrivilege"]};
            await Reject(()=>{InstalledProcessObjectAccess.PublishAuthorityCustodyAccess(Authority,kernel);return Task.CompletedTask;});Assert(kernel.Writes.Count==0);
        });
        await check("own Runtime process grants Authority only query sync set-quota terminate and token query",()=>
        {
            var kernel=new Kernel(Runtime);var process=Clone(kernel.Process);var token=Clone(kernel.Token);
            InstalledProcessObjectAccess.PublishRuntimePeerAccess(Runtime,Authority,kernel);
            Assert(Mask(kernel.Process,Authority)==0x101101&&Mask(kernel.Token,Authority)==8&&kernel.PublishToken&&kernel.Disposed);
            Prefix(process,kernel.Process);Prefix(token,kernel.Token);Assert((Mask(kernel.Process,Authority)&0xc0afe)==0);return Task.CompletedTask;
        });
        await check("own Authority process publishes read-only peer identity to Runtime only",()=>
        {
            var kernel=new Kernel(Authority);InstalledProcessObjectAccess.PublishAuthorityPeerAccess(Authority,Runtime,kernel);
            Assert(Mask(kernel.Process,Runtime)==0x101000&&Mask(kernel.Token,Runtime)==8&&Mask(kernel.Process,Owner)==0&&Mask(kernel.Token,Owner)==0);return Task.CompletedTask;
        });
        await check("own client publication leaves token DACL untouched because peer identity uses pipe identification",()=>
        {
            var kernel=new Kernel(Owner);var token=Bytes(kernel.Token);InstalledProcessObjectAccess.PublishClientPeerAccess(Owner,Authority,kernel);
            Assert(Mask(kernel.Process,Authority)==0x101000&&token.AsSpan().SequenceEqual(Bytes(kernel.Token))&&!kernel.PublishToken&&!kernel.Writes.Contains(InstalledOwnObject.Token));return Task.CompletedTask;
        });
        await check("private unprotected default descriptors preserve actual logon-SID full and other read-only ACEs",()=>
        {
            var kernel=new Kernel(Runtime);Add(kernel.Process,Other,0x121400);Add(kernel.Token,Other,0x20008);
            var process=Clone(kernel.Process);var token=Clone(kernel.Token);InstalledProcessObjectAccess.PublishRuntimePeerAccess(Runtime,Authority,kernel);
            Prefix(process,kernel.Process);Prefix(token,kernel.Token);Assert((kernel.Process.ControlFlags&ControlFlags.DiscretionaryAclProtected)==0&&Mask(kernel.Process,Logon)==0x1fffff&&Mask(kernel.Token,Other)==0x20008);return Task.CompletedTask;
        });
        await check("own-object peer publication is idempotent without duplicating or broadening ACEs",()=>
        {
            var kernel=new Kernel(Runtime);InstalledProcessObjectAccess.PublishRuntimePeerAccess(Runtime,Authority,kernel);var writes=kernel.Writes.Count;
            var process=Bytes(kernel.Process);var token=Bytes(kernel.Token);InstalledProcessObjectAccess.PublishRuntimePeerAccess(Runtime,Authority,kernel);
            Assert(kernel.Writes.Count==writes&&process.AsSpan().SequenceEqual(Bytes(kernel.Process))&&token.AsSpan().SequenceEqual(Bytes(kernel.Token)));return Task.CompletedTask;
        });
        foreach(var fault in new[]{"world-read","world-all","other-vm-read","other-vm-write","other-dup-handle","other-token-dup","other-token-impersonate","other-token-adjust","other-token-writedac","peer-all","fake-logon-full","null-dacl"})
            await check("own-object publication refuses unsafe preexisting "+fault+" before any write",async()=>
            {
                var kernel=new Kernel(Runtime);
                switch(fault)
                {
                    case "world-read":Add(kernel.Process,"S-1-1-0",0x1000);break;
                    case "world-all":Add(kernel.Process,"S-1-1-0",0x1fffff);break;
                    case "other-vm-read":Add(kernel.Process,Other,0x10);break;
                    case "other-vm-write":Add(kernel.Process,Other,0x20);break;
                    case "other-dup-handle":Add(kernel.Process,Other,0x40);break;
                    case "other-token-dup":Add(kernel.Token,Other,2);break;
                    case "other-token-impersonate":Add(kernel.Token,Other,4);break;
                    case "other-token-adjust":Add(kernel.Token,Other,0x80);break;
                    case "other-token-writedac":Add(kernel.Token,Other,0x40000);break;
                    case "peer-all":Add(kernel.Process,Authority,0x1fffff);break;
                    case "fake-logon-full":Add(kernel.Process,"S-1-5-5-111-333",0x1fffff);break;
                    case "null-dacl":kernel.Process=new(ControlFlags.DiscretionaryAclPresent,new SecurityIdentifier(Runtime),null,null,null);break;
                }
                await Reject(()=>Publish(kernel));Assert(kernel.Writes.Count==0&&kernel.Disposed);
            });
        foreach(var fault in new[]{"pid","created","tokenid","actor","thread","container","admin","privileges","service"})
            await check("own-object Runtime publication refuses spoofed "+fault+" identity",async()=>
            {
                var kernel=new Kernel(Runtime);var i=kernel.Identity;kernel.Identity=fault switch
                {
                    "pid"=>i with{ObjectPid=999},"created"=>i with{Created=0},"tokenid"=>i with{TokenId=0},"actor"=>i with{UserSid=Other},"thread"=>i with{ThreadToken=true},
                    "container"=>i with{AppContainer=true},"admin"=>i with{Administrator=true},"privileges"=>i with{Privileges=["SeChangeNotifyPrivilege","SeDebugPrivilege"]},"service"=>i with{ServiceLogon=false},_=>throw new Exception()
                };
                await Reject(()=>Publish(kernel));Assert(kernel.Writes.Count==0);
            });
        foreach(var fault in new[]{"runtime-peer","authority-peer"})
            await check("own-object publisher cannot redirect immutable role "+fault,async()=>
            {
                var kernel=new Kernel(fault=="runtime-peer"?Runtime:Authority);
                await Reject(()=>
                {
                    if(fault=="runtime-peer")InstalledProcessObjectAccess.PublishRuntimePeerAccess(Runtime,Other,kernel);
                    else InstalledProcessObjectAccess.PublishAuthorityPeerAccess(Authority,fault=="authority-peer"?Other:Runtime,kernel);return Task.CompletedTask;
                });Assert(kernel.Writes.Count==0);
            });
        await check("token generation replacement prevents publishing rights onto a stale primary token",async()=>
        {
            var kernel=new Kernel(Runtime){ChangeIdentityAt=4};await Reject(()=>Publish(kernel));Assert(kernel.Writes.SequenceEqual(new[]{InstalledOwnObject.Process})&&Mask(kernel.Token,Authority)==0&&kernel.Disposed);
        });
        await check("concurrent descriptor changes are not overwritten with a stale own-object snapshot",async()=>
        {
            var kernel=new Kernel(Runtime){ChangeProcessAtSecondRead=true};await Reject(()=>Publish(kernel));Assert(kernel.Writes.Count==0&&Mask(kernel.Process,Other)==0x1000);
        });
        await check("failed token DACL publication retains only the already approved minimal process grant",async()=>
        {
            var kernel=new Kernel(Runtime){FailTokenWrite=true};await Reject(()=>Publish(kernel));Assert(Mask(kernel.Process,Authority)==0x101101&&Mask(kernel.Token,Authority)==0&&kernel.Disposed);
        });
        await check("own-object readback cannot silently accept changed ownership",async()=>
        {
            var kernel=new Kernel(Runtime){ChangeOwnerOnWrite=true};await Reject(()=>Publish(kernel));Assert(kernel.Writes.SequenceEqual(new[]{InstalledOwnObject.Process}));
        });
        await check("a preserved deny ACE cannot be bypassed by appending required peer access",async()=>
        {
            var kernel=new Kernel(Runtime);kernel.Process.DiscretionaryAcl!.InsertAce(0,new CommonAce(AceFlags.None,AceQualifier.AccessDenied,0x1000,new SecurityIdentifier("S-1-1-0"),false,null));
            await Reject(()=>Publish(kernel));Assert(kernel.Writes.Count==0);
        });
        await check("an unrelated deny ACE is preserved without granting forbidden VM access",()=>
        {
            var kernel=new Kernel(Runtime);kernel.Process.DiscretionaryAcl!.InsertAce(0,new CommonAce(AceFlags.None,AceQualifier.AccessDenied,0x20,new SecurityIdentifier("S-1-1-0"),false,null));
            var before=Clone(kernel.Process);InstalledProcessObjectAccess.PublishRuntimePeerAccess(Runtime,Authority,kernel);Prefix(before,kernel.Process);return Task.CompletedTask;
        });
        await check("Runtime high-rights pin waits until the same fixed SCM process sent HELLO",async()=>
        {
            var source=new TaskCompletionSource<RuntimeAdmissionMessage>(TaskCreationOptions.RunContinuationsAsynchronously);var pins=0;
            var pending=RuntimeWaitingHostPin.AfterHelloAsync(Binding,55,c=>source.Task.WaitAsync(c),()=>((uint)2,(uint)55),()=>55,frame=>{pins++;return 7;},default);
            Assert(!pending.IsCompleted&&pins==0);source.SetResult(Hello);var ready=await pending;Assert(pins==1&&ready.Pin==7&&ready.Hello==Hello);
        });
        foreach(var fault in new[]{"scm-pid","scm-state","pipe-pid","binding","frame-pid","created","kind","format","id"})
            await check("Runtime pre-pin HELLO refuses changed "+fault+" without opening high-rights handle",async()=>
            {
                var source=new TaskCompletionSource<RuntimeAdmissionMessage>(TaskCreationOptions.RunContinuationsAsynchronously);var pins=0;uint pid=55,state=2,peer=55;
                var pending=RuntimeWaitingHostPin.AfterHelloAsync(Binding,55,c=>source.Task.WaitAsync(c),()=>(state,pid),()=>peer,frame=>{pins++;return 7;},default);
                var hello=Hello;if(fault=="scm-pid")pid=56;if(fault=="scm-state")state=1;if(fault=="pipe-pid")peer=56;
                hello=fault switch{"binding"=>hello with{Binding=Binding with{ActivationReceiptDigest=new('9',64)}},"frame-pid"=>hello with{Host=new(56,66)},"created"=>hello with{Host=new(55,0)},"kind"=>hello with{Kind="ready"},"format"=>hello with{Format=2},"id"=>hello with{Id="invalid"},_=>hello};
                source.SetResult(hello);await Reject(async()=>{_ =await pending;});Assert(pins==0);
            });
        await check("cancelled waiting-host HELLO never races a high-rights pin",async()=>
        {
            var source=new TaskCompletionSource<RuntimeAdmissionMessage>(TaskCreationOptions.RunContinuationsAsynchronously);using var cancelled=new CancellationTokenSource();var pins=0;
            var pending=RuntimeWaitingHostPin.AfterHelloAsync(Binding,55,c=>source.Task.WaitAsync(c),()=>((uint)2,(uint)55),()=>55,frame=>{pins++;return 7;},cancelled.Token);
            cancelled.Cancel();await Reject(async()=>{_ =await pending;});Assert(pins==0);
        });
    }
    private static readonly InstallationBinding Binding=new(new('a',32),new('b',64));
    private static RuntimeAdmissionMessage Hello=>new(1,"hello",new('c',32),Binding:Binding,Host:new(55,66));
    private static Task Publish(Kernel kernel){InstalledProcessObjectAccess.PublishRuntimePeerAccess(Runtime,Authority,kernel);return Task.CompletedTask;}
    private static byte[] Bytes(RawSecurityDescriptor value){var bytes=new byte[value.BinaryLength];value.GetBinaryForm(bytes,0);return bytes;}
    private static RawSecurityDescriptor Clone(RawSecurityDescriptor value)=>new(Bytes(value),0);
    private static void Add(RawSecurityDescriptor value,string sid,int mask)=>value.DiscretionaryAcl!.InsertAce(value.DiscretionaryAcl.Count,new CommonAce(AceFlags.None,AceQualifier.AccessAllowed,mask,new SecurityIdentifier(sid),false,null));
    private static void Remove(RawSecurityDescriptor value,string sid)
    {for(var i=value.DiscretionaryAcl!.Count-1;i>=0;i--)if(value.DiscretionaryAcl[i] is CommonAce ace&&ace.SecurityIdentifier.Value==sid)value.DiscretionaryAcl.RemoveAce(i);}
    private static int Mask(RawSecurityDescriptor value,string sid)
    {var mask=0;foreach(GenericAce raw in value.DiscretionaryAcl!)if(raw is CommonAce ace&&ace.AceQualifier==AceQualifier.AccessAllowed&&ace.SecurityIdentifier.Value==sid)mask|=ace.AccessMask;return mask;}
    private static void Prefix(RawSecurityDescriptor before,RawSecurityDescriptor after)
    {
        Assert(before.Owner==after.Owner&&before.Group==after.Group&&before.ControlFlags==after.ControlFlags&&after.DiscretionaryAcl!.Count>=before.DiscretionaryAcl!.Count);
        for(var i=0;i<before.DiscretionaryAcl!.Count;i++){var a=before.DiscretionaryAcl[i];var b=after.DiscretionaryAcl![i];var first=new byte[a.BinaryLength];var second=new byte[b.BinaryLength];a.GetBinaryForm(first,0);b.GetBinaryForm(second,0);Assert(first.AsSpan().SequenceEqual(second));}
    }
    private static void Assert(bool condition){if(!condition)throw new Exception("PROCESS_OBJECT_ACCESS_ASSERTION");}
    private static async Task Reject(Func<Task> work)
    {try{await work();}catch(Exception error)when(error is IOException or UnauthorizedAccessException or OperationCanceledException or Win32Exception){return;}throw new Exception("PROCESS_OBJECT_ACCESS_UNEXPECTED_SUCCESS");}
    private sealed class Kernel:IInstalledProcessObjectKernel,IInstalledOwnObjectSession
    {
        internal InstalledOwnProcessIdentity Identity;internal RawSecurityDescriptor Process,Token;internal bool PublishToken,Disposed,ChangeProcessAtSecondRead,FailTokenWrite,ChangeOwnerOnWrite;
        internal int ChangeIdentityAt;private int identityReads,processReads;internal readonly List<InstalledOwnObject> Writes=[];
        internal Kernel(string actor)
        {
            Identity=new(101,101,202,303,actor,Runtime,Authority,Logon,actor!=Owner,false,false,false,["SeChangeNotifyPrivilege"]);
            Process=new($"O:{actor}G:SYD:(A;;0x1fffff;;;SY)(A;;0x1fffff;;;BA)(A;;0x1fffff;;;{actor})(A;;0x1fffff;;;{Logon})");
            Token=new($"O:{actor}G:SYD:(A;;0xf01ff;;;SY)(A;;0xf01ff;;;BA)(A;;0xf01ff;;;{actor})(A;;0xf01ff;;;{Logon})");
        }
        public IInstalledOwnObjectSession OpenCurrent(bool publishToken){PublishToken=publishToken;Disposed=false;return this;}
        public InstalledOwnProcessIdentity ObserveIdentity(){identityReads++;return identityReads==ChangeIdentityAt?Identity with{TokenId=304}:Identity;}
        public RawSecurityDescriptor Read(InstalledOwnObject kind)
        {if(kind==InstalledOwnObject.Process&&++processReads==2&&ChangeProcessAtSecondRead)Add(Process,Other,0x1000);return Clone(kind==InstalledOwnObject.Process?Process:Token);}
        public void WriteDacl(InstalledOwnObject kind,RawSecurityDescriptor descriptor)
        {
            if(kind==InstalledOwnObject.Token){Assert(PublishToken);if(FailTokenWrite)throw new IOException("FAKE_TOKEN_WRITE_DENIED");Token=Clone(descriptor);}
            else{Process=Clone(descriptor);if(ChangeOwnerOnWrite)Process.Owner=new SecurityIdentifier(Other);}Writes.Add(kind);
        }
        public void Dispose()=>Disposed=true;
    }
}

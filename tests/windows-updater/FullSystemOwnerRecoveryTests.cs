using System.Text.Json;
using Yahad.Authority;
using Yahad.Service;
namespace Yahad.Production.Tests;

// Protected policy/ledger/endpoint with a synthetic recovery delegate. No SCM,
// installed service or family data is created by these protocol tests.
internal static class FullSystemOwnerRecoveryTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("native full recovery observes pending work while app is stopped and requires exact Windows Owner",async()=>
        {
            var f=new Fixture();var status=await f.Send("recovery",new{});Assert(status.LastRequest?.GetProperty("Operations").GetArrayLength()==1&&f.Starts==0);
            await Reject(()=>f.Send("recover",new{OperationId=f.Operation},new AuthorityPeer("S-1-5-21-999-998-997-1001",33,44,1,true)));
            await Reject(()=>f.Send("recover",new{OperationId=Guid.NewGuid().ToString("N")}));Assert(f.Starts==0);
        });
        await check("one exact full recovery approval resumes only its protected operation and cannot enable Auto",async()=>
        {
            var f=new Fixture();var confirmation=(await f.Send("recover",new{OperationId=f.Operation})).Confirmation!;
            Assert(confirmation.Scope=="reconcile-system");_=new OwnerConfirmationState(confirmation,f.PolicyFixture.Installation,()=>f.PolicyFixture.Now);
            await Reject(()=>f.Ledger.AuthenticateAsync(confirmation.Digest,"activate-system",default));
            var before=f.PolicyFixture.Files.Read(f.PolicyFixture.Files.PathOf("intake/mode.json"),8192);
            var answer=new OwnerAnswer(1,confirmation.Challenge,confirmation.Digest,true);await f.Send("recovery-answer",answer);await f.Endpoint.Execution!;
            Assert(f.Starts==1&&f.Completed==1&&(await f.Send("recovery",new{})).Phase=="healthy");
            Assert(f.PolicyFixture.Files.Read(f.PolicyFixture.Files.PathOf("intake/mode.json"),8192).AsSpan().SequenceEqual(before));
            await Reject(()=>f.Send("recovery-answer",answer));Assert(f.Starts==1);
        });
        await check("changed recovery observations require a fresh challenge and never run a stale answer",async()=>
        {
            var f=new Fixture();var first=(await f.Send("recover",new{OperationId=f.Operation})).Confirmation!;
            f.PolicyFixture.Put("activation.json",new{synthetic="changed observation"});
            await Reject(()=>f.Send("recovery-answer",new OwnerAnswer(1,first.Challenge,first.Digest,true)));Assert(f.Starts==0);
            var second=(await f.Send("recover",new{OperationId=f.Operation})).Confirmation!;Assert(second.Challenge!=first.Challenge&&second.Digest!=first.Digest);
            await f.Send("recovery-answer",new OwnerAnswer(1,second.Challenge,second.Digest,false));Assert(f.Starts==0);
        });
        await check("an expired native recovery question can be refreshed without restarting the service",async()=>
        {
            var f=new Fixture();var first=(await f.Send("recover",new{OperationId=f.Operation})).Confirmation!;
            f.PolicyFixture.Now=f.PolicyFixture.Now.AddMinutes(11);
            await Reject(()=>f.Send("recovery-answer",new OwnerAnswer(1,first.Challenge,first.Digest,true)));Assert(f.Starts==0);
            var second=(await f.Send("recover",new{OperationId=f.Operation})).Confirmation!;Assert(second.Challenge!=first.Challenge&&second.ExpiresUnix>f.PolicyFixture.Now.ToUnixTimeSeconds());
        });
        await check("an unchanged failed recovery can receive one fresh attempt without reusing a consumed approval",async()=>
        {
            var f=new Fixture();f.Fail=true;var first=(await f.Send("recover",new{OperationId=f.Operation})).Confirmation!;
            var old=new OwnerAnswer(1,first.Challenge,first.Digest,true);await f.Send("recovery-answer",old);await f.Endpoint.Execution!;
            Assert(f.Starts==1&&f.Completed==0&&(await f.Send("recovery",new{})).Phase=="recovery_required");
            var next=(await f.Send("recover",new{OperationId=f.Operation})).Confirmation!;Assert(next.Digest!=first.Digest);
            await Reject(()=>f.Send("recovery-answer",old));f.Fail=false;
            await f.Send("recovery-answer",new OwnerAnswer(1,next.Challenge,next.Digest,true));await f.Endpoint.Execution!;
            Assert(f.Starts==2&&f.Completed==1);
        });
    }
    private static void Assert(bool condition){if(!condition)throw new IOException("FULL_OWNER_RECOVERY_ASSERTION");}
    private static async Task Reject(Func<Task> work){try{await work();}catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){return;}throw new IOException("FULL_OWNER_RECOVERY_EXPECTED_REJECTION");}
    private sealed class Fixture
    {
        internal readonly FullSystemPolicyTests.Fixture PolicyFixture=new();internal readonly string Operation=Guid.NewGuid().ToString("N");
        internal readonly OwnerApprovalLedger Ledger;internal readonly FullSystemOwnerRecoveryEndpoint Endpoint;
        internal int Starts,Completed;internal bool Fail;private string phase="recovery_required";
        internal Fixture()
        {
            _=PolicyFixture.Open().Authorize(Operation,PolicyFixture.Receipt,PolicyFixture.Observed);
            Ledger=new(PolicyFixture.Files,PolicyFixture.Installation,PolicyFixture.Owner,()=>PolicyFixture.Now);
            var anchor=new InstalledAuthorityAnchor(1,PolicyFixture.Installation,ServiceDefinition.Name,InstalledAuthorityReader.AuthorityService,PolicyFixture.Owner,new('a',64),new('b',64),null,PolicyFixture.Digest);
            Endpoint=new(PolicyFixture.Files,anchor,Ledger,()=>Operation,(operation,c)=>{Assert(operation==Operation);Starts++;if(Fail)throw new IOException("SYNTHETIC_RECOVERY_BUSY");phase="healthy";return Task.CompletedTask;},()=>phase,
                (operation,result,c)=>{Assert(operation==Operation&&result=="healthy");Completed++;return Task.CompletedTask;},()=>PolicyFixture.Now);
        }
        internal Task<EndpointReply> Send(string method,object body,AuthorityPeer? peer=null)
            =>Endpoint.DispatchAsync(peer??new(PolicyFixture.Owner,11,22,1,true),new(1,Guid.NewGuid().ToString("N"),method,JsonSerializer.SerializeToElement(body)),default);
    }
}

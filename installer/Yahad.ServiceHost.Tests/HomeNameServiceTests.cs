using System.Diagnostics;
using System.Text;
using Yahad.Install;
using Yahad.Service;

// The host's side of the home name (docs/HOME-NAME.md 3): its two records, the Runtime's
// environment gate, and the LAN service's renewal around the proxy child.
internal static class HomeNameServiceTests
{
    private const string Label="k2abcdefgh7j";
    private static string Name=>HomeNameRelease.NameFor(Label);
    public static async Task Run(Func<string,Func<Task>,Task> check)
    {
        void Assert(bool value,string reason="assertion failed"){if(!value)throw new Exception(reason);}
        void Rejects(Action action){try{action();}catch(Exception error)when(error is IOException or System.Text.Json.JsonException){return;}throw new Exception("expected refusal");}
        byte[] Bytes(string text)=>Encoding.UTF8.GetBytes(text);

        await check("home-name.json is strict: format 1, a name of this zone, a private address, nothing else",()=>
        {
            var valid=InstalledHomeNameRecord.Parse(Bytes("{\"Format\":1,\"Name\":\""+Name+"\",\"Address\":\"192.168.4.5\"}"));
            Assert(valid==new InstalledHomeName(1,Name,"192.168.4.5")&&InstalledHomeNameRecord.Parse(InstalledHomeNameRecord.Encode(valid))==valid);
            foreach(var text in new[]{"{\"Format\":2,\"Name\":\""+Name+"\",\"Address\":\"192.168.4.5\"}","{\"Format\":1,\"Name\":\""+Name+"\",\"Address\":\"192.168.4.5\",\"Extra\":1}",
                "{\"Format\":1,\"Name\":null,\"Address\":\"192.168.4.5\"}","{\"Format\":1,\"Name\":\""+Name+"\",\"Address\":\"8.8.8.8\"}","{\"Format\":1,\"Name\":\""+Name+"\"}",
                "{\"Format\":1,\"Name\":\""+Label+".other.example.com\",\"Address\":\"192.168.4.5\"}","{\"Format\":1,\"Name\":\""+Name+"\",\"Address\":\"192.168.4.5\"}"+new string(' ',4096),"","[]"})
                Rejects(()=>InstalledHomeNameRecord.Parse(Bytes(text)));
            Rejects(()=>InstalledHomeNameRecord.Encode(new(1,"x.example.com","192.168.4.5")));
            return Task.CompletedTask;
        });
        await check("home-name-owner.json is strict: known phase, label and name together, a failure code only when failed",()=>
        {
            foreach(var phase in new[]{"registered","issued","configured","active","removed"})
            {var owner=new HomeNameOwner(1,phase,Label,Name,null);Assert(HomeNameOwnerRecord.Parse(HomeNameOwnerRecord.Encode(owner))==owner);}
            Assert(HomeNameOwnerRecord.Parse(HomeNameOwnerRecord.Encode(new(1,"failed","","","HOME_NAME_RESOLVE_FAILED"))).Failure=="HOME_NAME_RESOLVE_FAILED");
            Assert(HomeNameOwnerRecord.Parse(HomeNameOwnerRecord.Encode(new(1,"removed","","",null))).Label=="");
            foreach(var bad in new HomeNameOwner[]{new(2,"active",Label,Name,null),new(1,"paused",Label,Name,null),new(1,"active","","",null),new(1,"active",Label,"x.example.com",null),
                new(1,"active",Label,Name,"HOME_NAME_FAILED"),new(1,"failed",Label,Name,null),new(1,"failed",Label,Name,"lower case"),new(1,"failed",Label,Name,new string('A',65)),new(1,"registered","short","",null)})
                Rejects(()=>HomeNameOwnerRecord.Encode(bad));
            Rejects(()=>HomeNameOwnerRecord.Parse(Bytes("{\"Format\":1,\"Phase\":\"active\",\"Label\":\""+Label+"\",\"Name\":\""+Name+"\",\"Failure\":null,\"Secret\":\"x\"}")));
            Rejects(()=>HomeNameOwnerRecord.Parse(Bytes("{\"Format\":1,\"Phase\":null,\"Label\":\""+Label+"\",\"Name\":\""+Name+"\",\"Failure\":null}")));
            Rejects(()=>HomeNameOwnerRecord.Parse([]));
            return Task.CompletedTask;
        });
        await check("Runtime uses the home name only with private-IP HTTPS, a matching record and the app's declaration",()=>
        {
            var record=InstalledHomeNameRecord.Encode(new(1,Name,"192.168.4.5"));var declared=Bytes("{\"format\":1,\"homeName\":true}");
            var https=new Uri("https://192.168.4.5");
            Assert(InstalledHomeNameRecord.Select(https,()=>record,()=>declared)==new InstalledHomeName(1,Name,"192.168.4.5"));
            foreach(var origin in new[]{"http://192.168.4.5","https://192.168.4.6","https://192.168.4.5:8443","https://8.8.8.8","http://127.0.0.1:47123"})
                Assert(InstalledHomeNameRecord.Select(new Uri(origin),()=>record,()=>declared) is null,origin);
            foreach(var capabilities in new[]{null,"{\"homeName\":false}","{\"homeName\":\"true\"}","{\"format\":1}","{\"homeName\":true,\"homeName\":true}","[true]","{","{\"a\":{\"b\":{\"c\":{\"d\":{}}}},\"homeName\":true}"})
                Assert(InstalledHomeNameRecord.Select(https,()=>record,()=>capabilities is null?null:Bytes(capabilities)) is null,capabilities??"null");
            foreach(var stored in new byte[]?[]{null,[],Bytes("{"),InstalledHomeNameRecord.Encode(new(1,Name,"192.168.4.6"))})
                Assert(InstalledHomeNameRecord.Select(https,()=>stored,()=>declared) is null);
            // A reader that fails is the IP origin, never a refused start.
            Assert(InstalledHomeNameRecord.Select(https,()=>throw new UnauthorizedAccessException(),()=>declared) is null);
            Assert(InstalledHomeNameRecord.Select(https,()=>record,()=>throw new IOException("x")) is null);
            return Task.CompletedTask;
        });
        await check("Runtime environment with the home name: the name is the origin and the address stays admitted as the home address",()=>
        {
            var https=new Uri("https://192.168.4.5");var home=new InstalledHomeNetwork(1,"http://192.168.4.5/","AB:CD");
            var plain=new Dictionary<string,string>{["APP_ORIGIN"]="https://192.168.4.5"};InstalledLanEnvironment.Apply(plain,https,47123,home,null);
            Assert(plain.Count==4&&plain["APP_ORIGIN"]=="https://192.168.4.5"&&plain["APP_ADDITIONAL_ORIGINS"]=="http://127.0.0.1:47123"&&!plain.ContainsKey("APP_HOME_ADDRESS"));
            var named=new Dictionary<string,string>{["APP_ORIGIN"]="https://192.168.4.5"};InstalledLanEnvironment.Apply(named,https,47123,home,new(1,Name,"192.168.4.5"));
            Assert(named["APP_ORIGIN"]=="https://"+Name&&named["APP_ADDITIONAL_ORIGINS"]=="https://192.168.4.5,http://127.0.0.1:47123"&&named["APP_HOME_ADDRESS"]=="https://192.168.4.5");
            // The join page and fingerprint are the same with or without the name.
            Assert(named["APP_JOIN_PAGE"]==plain["APP_JOIN_PAGE"]&&named["APP_HOME_CERTIFICATE_SHA256"]==plain["APP_HOME_CERTIFICATE_SHA256"]);
            return Task.CompletedTask;
        });

        async Task Until(Func<bool> condition){var watch=Stopwatch.StartNew();while(!condition()){if(watch.Elapsed>TimeSpan.FromSeconds(5))throw new TimeoutException("condition");await Task.Delay(5);}}
        LanProxyServiceController Controller(Admission admission,FakeRenewal renewal){renewal.Owner=admission;return new(admission,TimeSpan.FromMilliseconds(1),renewal:renewal,
            renewalFirst:TimeSpan.Zero,renewalEvery:TimeSpan.FromHours(1),renewalVerifyBudget:TimeSpan.FromSeconds(2));}
        async Task<(ServiceRunResult Result,List<ServiceState> States)> RunUntil(LanProxyServiceController controller,Func<bool> condition)
        {
            var states=new List<ServiceState>();var run=controller.RunAsync(r=>{lock(states)states.Add(r.State);});
            await Until(condition);Assert(controller.Control(1)==0);
            var result=await run.WaitAsync(TimeSpan.FromSeconds(5));return(result,states);
        }
        await check("LAN renewal: nothing due starts no ACME and no replacement",async()=>
        {
            var admission=new Admission();var renewal=new FakeRenewal{IsDue=false};
            var (result,states)=await RunUntil(Controller(admission,renewal),()=>renewal.Dues>0);
            Assert(result==new ServiceRunResult(ServiceFailure.None,true)&&renewal.Stages==0&&renewal.Swaps==0&&admission.Starts==1&&admission.AllStoppedAndDisposed);
            Assert(states.SequenceEqual([ServiceState.StartPending,ServiceState.Running,ServiceState.StopPending,ServiceState.Stopped]));
        });
        await check("LAN renewal: a staged certificate replaces the child through the confirmed stop and is served",async()=>
        {
            var admission=new Admission();var renewal=new FakeRenewal();
            var (result,_)=await RunUntil(Controller(admission,renewal),()=>renewal.Verifies==1);
            Assert(result==new ServiceRunResult(ServiceFailure.None,true));
            Assert(renewal.Stages==1&&renewal.Swaps==1&&renewal.SwapBacks==0&&admission.Starts==2&&admission.AllStoppedAndDisposed);
            // The old child was stopped and its tree observed empty before the swap.
            Assert(renewal.ChildrenStoppedAtSwap==1);
        });
        await check("LAN renewal: a new certificate that is not served is swapped back and the child started again",async()=>
        {
            var admission=new Admission();var renewal=new FakeRenewal{Serves=false};
            var (result,_)=await RunUntil(Controller(admission,renewal),()=>admission.Starts==3);
            Assert(result==new ServiceRunResult(ServiceFailure.None,true)&&renewal.Swaps==1&&renewal.SwapBacks==1&&admission.AllStoppedAndDisposed);
            Assert(renewal.ChildrenStoppedAtSwapBack==2);
        });
        await check("LAN renewal: a new certificate that fails to start is swapped back and the child started again",async()=>
        {
            var admission=new Admission{Fails=attempt=>attempt==2};var renewal=new FakeRenewal();
            var (result,_)=await RunUntil(Controller(admission,renewal),()=>admission.Starts==2);
            Assert(result==new ServiceRunResult(ServiceFailure.None,true)&&admission.Attempts==3&&renewal.Swaps==1&&renewal.SwapBacks==1&&admission.AllStoppedAndDisposed);
        });
        await check("LAN renewal: a swap that fails starts the child again on the previous certificate",async()=>
        {
            var admission=new Admission();var renewal=new FakeRenewal{SwapFails=true};
            var (result,_)=await RunUntil(Controller(admission,renewal),()=>admission.Starts==2);
            Assert(result==new ServiceRunResult(ServiceFailure.None,true)&&renewal.Swaps==1&&renewal.SwapBacks==0&&renewal.Verifies==0&&admission.AllStoppedAndDisposed);
        });
        await check("LAN renewal: a failed staging leaves the running child alone",async()=>
        {
            var admission=new Admission();var renewal=new FakeRenewal{Stage=_=>Task.FromException(new IOException("HOME_NAME_GONE"))};
            var (result,_)=await RunUntil(Controller(admission,renewal),()=>renewal.Stages==1);
            await Task.Delay(20);
            Assert(result==new ServiceRunResult(ServiceFailure.None,true)&&renewal.Swaps==0&&admission.Starts==1&&admission.AllStoppedAndDisposed);
        });
        await check("LAN renewal: STOP during ACME is not held up by it and cancels it",async()=>
        {
            var admission=new Admission();var cancelled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var renewal=new FakeRenewal{Stage=async c=>{try{await Task.Delay(Timeout.Infinite,c);}finally{cancelled.TrySetResult();}}};
            var watch=Stopwatch.StartNew();
            var (result,_)=await RunUntil(Controller(admission,renewal),()=>renewal.Stages==1);
            Assert(result==new ServiceRunResult(ServiceFailure.None,true)&&cancelled.Task.IsCompleted&&renewal.Swaps==0&&admission.Starts==1&&watch.Elapsed<TimeSpan.FromSeconds(3));
        });
        await check("LAN renewal: a lost address during ACME still stops the proxy",async()=>
        {
            var admission=new Admission();var renewal=new FakeRenewal{Stage=c=>Task.Delay(Timeout.Infinite,c)};
            var controller=Controller(admission,renewal);var run=controller.RunAsync(_=>{});
            await Until(()=>renewal.Stages==1);admission.AddressPresent=false;
            var result=await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(result==new ServiceRunResult(ServiceFailure.ActivationUnavailable,true)&&renewal.Swaps==0&&admission.AllStoppedAndDisposed);
        });
    }

    private sealed class Admission:ILanProxyAdmission
    {
        internal volatile bool AddressPresent=true;internal readonly List<Child> Children=[];
        internal Func<int,bool> Fails=_=>false;internal int Attempts;
        internal int Starts{get{lock(Children)return Children.Count;}}
        internal int Stopped{get{lock(Children)return Children.Count(c=>c.Disposed);}}
        internal bool AllStoppedAndDisposed{get{lock(Children)return Children.All(c=>c.Disposed);}}
        public Task<ILanProxyProcess> StartAsync(CancellationToken cancellation)
        {
            if(Fails(Interlocked.Increment(ref Attempts)))return Task.FromException<ILanProxyProcess>(new IOException("HOME_NAME_CERTIFICATE_KEY"));
            var child=new Child();lock(Children)Children.Add(child);return Task.FromResult<ILanProxyProcess>(child);
        }
        public bool SelectedAddressPresent()=>AddressPresent;
    }
    private sealed class Child:ILanProxyProcess
    {
        private readonly TaskCompletionSource<int> exit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;private bool empty;
        public Task<int> WaitForExitAsync(CancellationToken cancellation)=>exit.Task.WaitAsync(cancellation);
        public Task StopAsync(CancellationToken cancellation){empty=true;exit.TrySetResult(1);return Task.CompletedTask;}
        public void Dispose(){if(!empty)throw new InvalidOperationException("disposed before its tree was empty");Disposed=true;}
    }
    private sealed class FakeRenewal:ILanHomeNameRenewal
    {
        internal Admission? Owner;
        internal bool IsDue=true,Serves=true,SwapFails;internal Func<CancellationToken,Task> Stage=_=>Task.CompletedTask;
        internal volatile int Dues,Stages,Swaps,SwapBacks,Verifies;internal int ChildrenStoppedAtSwap=-1,ChildrenStoppedAtSwapBack=-1;
        public bool Due(DateTimeOffset now){Dues++;var due=IsDue;IsDue=false;return due;}
        public Task StageAsync(CancellationToken cancellation){Stages++;return Stage(cancellation);}
        public void Swap(){Swaps++;ChildrenStoppedAtSwap=Stopped();if(SwapFails)throw new IOException("locked");}
        public void SwapBack(){SwapBacks++;ChildrenStoppedAtSwapBack=Stopped();}
        public Task<bool> ServesLiveAsync(CancellationToken cancellation){Verifies++;return Task.FromResult(Serves);}
        private int Stopped()=>Owner?.Stopped??-1;
    }
}

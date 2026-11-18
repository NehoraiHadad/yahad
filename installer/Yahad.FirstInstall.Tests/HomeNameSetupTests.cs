using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Yahad.Install;
using Yahad.Service;
using Yahad.Setup;

// Setup's name transaction (docs/HOME-NAME.md 3) over fake remote and environment, in a private directory:
// phase order and resume, the record-after-Caddyfile ordering, failure back to the IP path, 410, and removal.
internal static class HomeNameSetupTests
{
    private const string Address="192.168.4.5",LabelA="k2abcdefgh7j",LabelB="m3abcdefgh7j";
    private static void Assert(bool value,string reason="HOME_NAME_SETUP_ASSERTION"){if(!value)throw new Exception(reason);}
    private static AcmeCertificate Certificate(string name,DateTimeOffset notBefore,DateTimeOffset notAfter)
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request=new CertificateRequest("CN="+name,key,HashAlgorithmName.SHA256);
        var san=new SubjectAlternativeNameBuilder();san.AddDnsName(name);request.CertificateExtensions.Add(san.Build());
        using var certificate=request.CreateSelfSigned(notBefore,notAfter);
        return new(PemEncoding.WriteString("CERTIFICATE",certificate.RawData)+"\n",key.ExportPkcs8PrivateKeyPem()+"\n",notBefore,notAfter);
    }

    // One private state directory, its paths, fakes that check the ordering at every call, and the transaction over them.
    private sealed class Fixture
    {
        internal readonly HomeNamePaths Paths;internal readonly Remote Remote;internal readonly Environment Environment;
        internal readonly HomeNameStore Store;internal DateTimeOffset Now=DateTimeOffset.UtcNow;
        internal Fixture(string root)
        {
            var directory=Path.Combine(root,"home-name-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            Paths=new(Path.Combine(directory,"home-name-owner.json"),Path.Combine(directory,"home-name.json"),Path.Combine(directory,"Caddyfile"),Path.Combine(directory,"name"));
            Directory.CreateDirectory(Paths.Store);Store=new(Paths.Store);
            File.WriteAllText(Paths.Caddyfile,"ip\n");
            Remote=new(this);Environment=new(this);
        }
        internal HomeNameSetup Setup()=>new(Remote,Environment,Paths,site=>Encoding.UTF8.GetBytes(site is null?"ip\n":"ip\nname "+site.Name+" "+site.CertificateFile+"\n"),
            ()=>Now,TimeSpan.FromMilliseconds(200),TimeSpan.FromSeconds(5));
        internal string Caddyfile=>File.ReadAllText(Paths.Caddyfile);
        internal bool Committed=>File.Exists(Paths.NameRecord);
        internal HomeNameOwner? Owner=>File.Exists(Paths.Owner)?HomeNameOwnerRecord.Parse(File.ReadAllBytes(Paths.Owner)):null;
        // Never the reverse: a committed name is always one the Caddyfile serves.
        internal void Ordering()
        {
            if(!Committed)return;
            var name=InstalledHomeNameRecord.Parse(File.ReadAllBytes(Paths.NameRecord)).Name;
            Assert(Caddyfile.Contains("name "+name+" ",StringComparison.Ordinal),"HOME_NAME_RECORD_BEFORE_SITE");
        }
    }
    private sealed class Remote(Fixture fixture):IHomeNameRemote
    {
        internal readonly Queue<string> Labels=new([LabelA,LabelB]);internal readonly List<string> Calls=[];
        internal int GoneOnSetAddress;internal Exception? IssueFailure,DeleteFailure,AddressFailure;
        internal TimeSpan Validity=TimeSpan.FromDays(90);
        public Task<HomeNameCredential> RegisterAsync(CancellationToken cancellation)
        {fixture.Ordering();Calls.Add("register");return Task.FromResult(new HomeNameCredential(Labels.Dequeue(),new string('A',43)));}
        public Task SetAddressAsync(HomeNameCredential credential,string address,CancellationToken cancellation)
        {
            fixture.Ordering();Calls.Add("address "+credential.Label+" "+address);
            // The credential is on disk before any call that uses it.
            Assert(fixture.Store.ReadCredential()==credential,"HOME_NAME_CREDENTIAL_NOT_PERSISTED");
            if(GoneOnSetAddress>0){GoneOnSetAddress--;throw new HomeNameGoneException();}
            if(AddressFailure is {} failure)return Task.FromException(failure);
            return Task.CompletedTask;
        }
        public Task<AcmeCertificate> IssueAsync(HomeNameCredential credential,string name,AcmeAccountKey account,CancellationToken cancellation)
        {
            fixture.Ordering();Calls.Add("issue "+name);Assert(File.Exists(fixture.Store.AccountFile));
            if(IssueFailure is {} failure)return Task.FromException<AcmeCertificate>(failure);
            return Task.FromResult(Certificate(name,fixture.Now.AddMinutes(-1),fixture.Now+Validity));
        }
        public Task DeleteNameAsync(HomeNameCredential credential,CancellationToken cancellation)
        {Calls.Add("delete "+credential.Label);return DeleteFailure is {} failure?Task.FromException(failure):Task.CompletedTask;}
    }
    private sealed class Environment(Fixture fixture):IHomeNameEnvironment
    {
        internal readonly List<string> Calls=[];internal IReadOnlyList<string> Resolved=[Address];internal Exception? ProbeFailure;internal int FailProxyRestarts;
        public Task<IReadOnlyList<string>> ResolveAsync(string name,CancellationToken cancellation){fixture.Ordering();Calls.Add("resolve");return Task.FromResult(Resolved);}
        public Task ProbeAsync(string name,CancellationToken cancellation)
        {
            fixture.Ordering();Calls.Add("probe");
            // The name is probed only once the restarted proxy serves its site.
            Assert(fixture.Caddyfile.Contains("name "+name+" ",StringComparison.Ordinal));
            return ProbeFailure is {} failure?Task.FromException(failure):Task.CompletedTask;
        }
        public Task RestartProxyAsync(CancellationToken cancellation)
        {
            fixture.Ordering();Calls.Add("proxy:"+(fixture.Caddyfile.Contains("name ",StringComparison.Ordinal)?"name":"ip"));
            if(FailProxyRestarts>0){FailProxyRestarts--;return Task.FromException(new IOException("SERVICE_START_FAILED"));}
            return Task.CompletedTask;
        }
        public Task RestartRuntimeAsync(CancellationToken cancellation)
        {
            // The Runtime is restarted onto a committed, active name, or back to the IP origin after a failure.
            fixture.Ordering();Calls.Add("runtime:"+(fixture.Committed?"name":"ip"));
            Assert(fixture.Committed?fixture.Owner?.Phase=="active":fixture.Owner?.Phase=="failed");return Task.CompletedTask;
        }
    }

    internal static async Task RunAsync(Func<string,Func<Task>,Task> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"yahad-home-name-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var nameA=HomeNameRelease.NameFor(LabelA);var nameB=HomeNameRelease.NameFor(LabelB);
        try
        {
            await check("home name transaction registers, points, issues, serves and checks the name before committing it",async()=>
            {
                var f=new Fixture(root);var outcome=await f.Setup().RunAsync(Address,default);
                Assert(outcome==new HomeNameOutcome(nameA,null));
                Assert(f.Remote.Calls.SequenceEqual(["register","address "+LabelA+" "+Address,"issue "+nameA]));
                Assert(f.Environment.Calls.SequenceEqual(["proxy:name","resolve","probe","runtime:name"]));
                Assert(f.Owner==new HomeNameOwner(1,"active",LabelA,nameA,null)&&InstalledHomeNameRecord.Parse(File.ReadAllBytes(f.Paths.NameRecord))==new InstalledHomeName(1,nameA,Address));
                Assert(f.Store.Read(f.Store.Live,nameA) is not null&&f.Caddyfile.Contains("name "+nameA+" "+HomeNameStore.CertificateFile(f.Store.Live),StringComparison.Ordinal));
                // Committed: a rerun only checks with the name service that the label is still there.
                Assert(await f.Setup().RunAsync(Address,default)==new HomeNameOutcome(nameA,null)&&f.Environment.Calls.Count==4);
                Assert(f.Remote.Calls.Count==4&&f.Remote.Calls[3]=="address "+LabelA+" "+Address);
                Assert(f.Setup().ActiveName()==nameA&&!f.Setup().Unfinished());
            });
            await check("home name failure goes back to the IP Caddyfile without home-name.json and records the code; a rerun resumes",async()=>
            {
                var f=new Fixture(root);f.Environment.ProbeFailure=new IOException("HOME_NAME_HTTPS_FAILED");
                Assert(await f.Setup().RunAsync(Address,default)==new HomeNameOutcome(null,"HOME_NAME_HTTPS_FAILED"));
                Assert(f.Caddyfile=="ip\n"&&!f.Committed&&f.Owner==new HomeNameOwner(1,"failed",LabelA,nameA,"HOME_NAME_HTTPS_FAILED"));
                Assert(f.Environment.Calls.SequenceEqual(["proxy:name","resolve","probe","proxy:ip"])&&f.Setup().Unfinished()&&f.Setup().ActiveName() is null);
                // The credential and the certificate are kept, so the rerun neither registers nor issues again.
                f.Environment.ProbeFailure=null;f.Remote.Calls.Clear();f.Environment.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,default)==new HomeNameOutcome(nameA,null));
                Assert(f.Remote.Calls.SequenceEqual(["address "+LabelA+" "+Address])&&f.Owner!.Phase=="active"&&f.Committed);
            });
            await check("home name resolver that does not return the selected address fails as DNS rebind protection would",async()=>
            {
                var f=new Fixture(root);f.Environment.Resolved=["127.0.0.1"];
                Assert(await f.Setup().RunAsync(Address,default)==new HomeNameOutcome(null,"HOME_NAME_RESOLVE_FAILED"));
                Assert(!f.Environment.Calls.Contains("probe")&&f.Caddyfile=="ip\n"&&!f.Committed&&f.Owner!.Failure=="HOME_NAME_RESOLVE_FAILED");
                f.Environment.Resolved=[];Assert((await f.Setup().RunAsync(Address,default)).Failure=="HOME_NAME_RESOLVE_FAILED");
            });
            await check("home name rerun from every intermediate phase first goes back to the IP path, then resumes",async()=>
            {
                foreach(var phase in new[]{"registered","issued","configured"})
                {
                    var f=new Fixture(root);await f.Setup().RunAsync(Address,default);
                    // A crash after the phase: the Caddyfile may name the site, home-name.json may not exist yet.
                    File.Delete(f.Paths.NameRecord);File.WriteAllBytes(f.Paths.Owner,HomeNameOwnerRecord.Encode(new(1,phase,LabelA,nameA,null)));
                    f.Remote.Calls.Clear();f.Environment.Calls.Clear();Assert(f.Setup().Unfinished());
                    Assert(await f.Setup().RunAsync(Address,default)==new HomeNameOutcome(nameA,null));
                    Assert(f.Remote.Calls.SequenceEqual(["address "+LabelA+" "+Address])&&f.Environment.Calls.SequenceEqual(["proxy:name","resolve","probe","runtime:name"]));
                }
                // A credential on disk without any owner record (a crash right after registering): unfinished,
                // and the label is reused, never registered again. The same after an earlier removal.
                var g=new Fixture(root);g.Store.WriteCredential(new(LabelB,new string('A',43)));Assert(g.Setup().Unfinished());
                var removed=new Fixture(root);await removed.Setup().RemoveAsync(true,default);File.WriteAllBytes(removed.Paths.Owner,HomeNameOwnerRecord.Encode(new(1,"removed","","",null)));
                Assert(!removed.Setup().Unfinished());removed.Store.WriteCredential(new(LabelB,new string('A',43)));Assert(removed.Setup().Unfinished());
                Assert(await g.Setup().RunAsync(Address,default)==new HomeNameOutcome(nameB,null)&&!g.Remote.Calls.Contains("register"));
            });
            await check("home name rerun reissues a certificate that is due and keeps one that is not",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,default);var first=f.Store.Read(f.Store.Live,nameA)!;
                f.Setup().Suspend();f.Remote.Calls.Clear();await f.Setup().RunAsync(Address,default);
                Assert(!f.Remote.Calls.Any(c=>c.StartsWith("issue",StringComparison.Ordinal))&&f.Store.Read(f.Store.Live,nameA)!.LeafSha256==first.LeafSha256);
                f.Now=f.Now.AddDays(61);f.Setup().Suspend();f.Remote.Calls.Clear();await f.Setup().RunAsync(Address,default);
                Assert(f.Remote.Calls.Contains("issue "+nameA)&&f.Store.Read(f.Store.Live,nameA)!.LeafSha256!=first.LeafSha256);
            });
            await check("home name 410 discards the credential and its certificates and registers once more",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,default);f.Setup().Suspend();
                f.Remote.GoneOnSetAddress=1;f.Remote.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,default)==new HomeNameOutcome(nameB,null));
                Assert(f.Remote.Calls.SequenceEqual(["address "+LabelA+" "+Address,"register","address "+LabelB+" "+Address,"issue "+nameB]));
                Assert(f.Store.ReadCredential()!.Label==LabelB&&f.Owner!.Name==nameB&&f.Store.Read(f.Store.Live,nameB) is not null);
                // Gone twice: a failure, not a loop.
                var g=new Fixture(root);g.Remote.GoneOnSetAddress=2;
                Assert(await g.Setup().RunAsync(Address,default)==new HomeNameOutcome(null,"HOME_NAME_GONE")&&g.Remote.Calls.Count(c=>c=="register")==2);
            });
            await check("active home name is checked on a rerun: a label gone at the name service is registered again",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,default);
                f.Remote.GoneOnSetAddress=1;f.Remote.Calls.Clear();f.Environment.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,default)==new HomeNameOutcome(nameB,null));
                Assert(f.Remote.Calls.SequenceEqual(["address "+LabelA+" "+Address,"register","address "+LabelB+" "+Address,"issue "+nameB]));
                Assert(f.Environment.Calls.SequenceEqual(["proxy:name","resolve","probe","runtime:name"])&&f.Owner==new HomeNameOwner(1,"active",LabelB,nameB,null));
                Assert(InstalledHomeNameRecord.Parse(File.ReadAllBytes(f.Paths.NameRecord)).Name==nameB);
            });
            await check("active home name stays when the name service cannot be reached, and is redone when its certificate expired",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,default);
                f.Remote.AddressFailure=new HttpRequestException("offline");f.Remote.Calls.Clear();f.Environment.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,default)==new HomeNameOutcome(nameA,null)&&f.Environment.Calls.Count==0&&f.Committed);
                f.Remote.AddressFailure=null;f.Now=f.Now.AddDays(91);f.Remote.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,default)==new HomeNameOutcome(nameA,null)&&f.Remote.Calls.SequenceEqual(["address "+LabelA+" "+Address,"issue "+nameA]));
                Assert(f.Environment.Calls.SequenceEqual(["proxy:name","resolve","probe","runtime:name"]));
            });
            await check("active home name that fails its redo sends the Runtime back to the IP origin",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,default);
                f.Now=f.Now.AddDays(91);f.Remote.IssueFailure=new AcmeException("ACME_RATE_LIMITED");f.Environment.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,default)==new HomeNameOutcome(null,"ACME_RATE_LIMITED"));
                Assert(f.Environment.Calls.SequenceEqual(["proxy:ip","runtime:ip"])&&!f.Committed&&f.Caddyfile=="ip\n"&&f.Owner!.Phase=="failed");
            });
            await check("home name failure codes come from the name service, the CA or the step, never free text",async()=>
            {
                var f=new Fixture(root);f.Remote.IssueFailure=new AcmeException("ACME_RATE_LIMITED");
                Assert((await f.Setup().RunAsync(Address,default)).Failure=="ACME_RATE_LIMITED"&&f.Owner!.Phase=="failed"&&f.Owner.Label==LabelA);
                Assert(HomeNameSetup.Code(new HomeNameServiceException("HOME_NAME_RATE_LIMITED",429))=="HOME_NAME_RATE_LIMITED");
                Assert(HomeNameSetup.Code(new IOException("free text with spaces"))=="HOME_NAME_FAILED"&&HomeNameSetup.Code(new InvalidOperationException("X"))=="HOME_NAME_FAILED");
                Assert(HomeNameSetup.Code(new TaskCanceledException())=="HOME_NAME_TIMEOUT");
            });
            await check("home name that cannot get back to the IP path fails Setup with both causes",async()=>
            {
                var f=new Fixture(root);f.Environment.ProbeFailure=new IOException("HOME_NAME_HTTPS_FAILED");f.Environment.FailProxyRestarts=2;
                try{await f.Setup().RunAsync(Address,default);}
                catch(AggregateException both){Assert(both.InnerExceptions.Count==2&&!f.Committed);return;}
                throw new Exception("expected failure");
            });
            await check("home name suspend keeps label, credential and certificate; removal deletes the name and keeps the ACME account",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,default);
                f.Setup().Suspend();
                Assert(!f.Committed&&f.Owner==new HomeNameOwner(1,"issued",LabelA,nameA,null)&&f.Store.ReadCredential() is not null&&f.Store.Read(f.Store.Live,nameA) is not null);
                var account=File.ReadAllBytes(f.Store.AccountFile);
                f.Remote.DeleteFailure=new HttpRequestException("offline");
                await f.Setup().RemoveAsync(true,default);
                Assert(f.Remote.Calls.Last()=="delete "+LabelA&&f.Caddyfile=="ip\n"&&!f.Committed&&f.Store.ReadCredential() is null&&!Directory.Exists(f.Store.Live));
                Assert(f.Owner==new HomeNameOwner(1,"removed","","",null)&&File.ReadAllBytes(f.Store.AccountFile).SequenceEqual(account));
                // Removed is not unfinished: a run without the owner's choice leaves it alone.
                Assert(!f.Setup().Unfinished()&&f.Setup().ActiveName() is null);
                // Nothing to remove: no call, no record.
                var g=new Fixture(root);await g.Setup().RemoveAsync(true,default);Assert(g.Remote.Calls.Count==0&&g.Owner is null);
            });
            await check("home name choice travels beside the LAN request and only with an Enable request",async()=>
            {
                var owner=new SetupOwner("S-1-5-21-1-2-3-1001","Owner");
                var request=new LanSetupRequest(LanSetupAction.Enable,"home",Address,true,true);
                var host=new ChoiceHost();await new InstallFlow(host).RunAsync(owner,false,_=>{},default,lan:request,homeName:true);
                Assert(host.HomeName==true&&ReferenceEquals(host.Request,request));
                foreach(var lan in new LanSetupRequest?[]{null,new(LanSetupAction.Disable)})
                {
                    var refused=new ChoiceHost();
                    try{await new InstallFlow(refused).RunAsync(owner,false,_=>{},default,lan:lan,homeName:true);throw new Exception("expected refusal");}
                    catch(IOException error){Assert(error.Message=="LAN_ACTION_INVALID"&&refused.Calls==0);}
                }
            });
        }
        finally{try{Directory.Delete(root,true);}catch(IOException){}catch(UnauthorizedAccessException){}}
    }
    private sealed class ChoiceHost:ISetupHostOperations
    {
        internal int Calls;internal bool? HomeName;internal LanSetupRequest? Request;
        public Task PrepareAsync(SetupOwner owner,Action<SetupProgress> progress,CancellationToken cancellation){Calls++;return Task.CompletedTask;}
        public Task ProvisionAsync(Action<SetupProgress> progress,CancellationToken cancellation){Calls++;return Task.CompletedTask;}
        public Task ConfigureLanAsync(LanSetupRequest request,bool homeName,Action<SetupProgress> progress,CancellationToken cancellation){Calls++;Request=request;HomeName=homeName;return Task.CompletedTask;}
        public Task<SetupResult> StartAndObserveAsync(Action<SetupProgress> progress,CancellationToken cancellation){Calls++;return Task.FromResult(new SetupResult(true,"READY"));}
        public Task CommitAsync(Action<SetupProgress> progress,CancellationToken cancellation){Calls++;return Task.CompletedTask;}
        public Task<SetupResult> UninstallAsync(Action<SetupProgress> progress,CancellationToken cancellation,UninstallMode mode=UninstallMode.KeepData){Calls++;return Task.FromResult(new SetupResult(true,"REMOVED"));}
    }
}

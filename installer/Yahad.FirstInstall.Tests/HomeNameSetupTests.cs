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
    private const string Address="192.168.4.5",LabelA="k2abcdefgh7j",LabelB="m3abcdefgh7j",Chosen="hadad-home",Other="hadad";
    private static HomeNameCredential Credential(string label)=>new(label,new string('A',43));
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
        // Never the reverse: a committed name is always one the Caddyfile serves, from a certificate that is there.
        internal void Ordering()
        {
            if(!Committed)return;
            var name=InstalledHomeNameRecord.Parse(File.ReadAllBytes(Paths.NameRecord)).Name;
            Assert(Caddyfile.Contains("name "+name+" ",StringComparison.Ordinal),"HOME_NAME_RECORD_BEFORE_SITE");
            Assert(Store.Read(Store.Live,name) is not null,"HOME_NAME_RECORD_WITHOUT_CERTIFICATE");
        }
    }
    private sealed class Remote(Fixture fixture):IHomeNameRemote
    {
        internal readonly Queue<string> Labels=new([LabelA,LabelB]);internal readonly List<string> Calls=[];
        // Every label the service holds, in any status: it never hands one out twice (409 label_taken).
        internal readonly HashSet<string> Held=[];
        // Each delete with the name committed when it was asked (null: none), so a test sees it came after the commit.
        internal readonly List<(string Label,string? Committed)> Deletes=[];
        internal int GoneOnSetAddress;internal Exception? IssueFailure,DeleteFailure,AddressFailure;
        internal TimeSpan Validity=TimeSpan.FromDays(90);
        public Task<HomeNameCredential> RegisterAsync(string? label,CancellationToken cancellation)
        {
            fixture.Ordering();Calls.Add(label is null?"register":"register "+label);
            if(label is not null&&!Held.Add(label))return Task.FromException<HomeNameCredential>(new HomeNameLabelException(HomeNameLabelException.Taken,409));
            if(label is null){label=Labels.Dequeue();Held.Add(label);}
            return Task.FromResult(new HomeNameCredential(label,new string('A',43)));
        }
        public Task SetAddressAsync(HomeNameCredential credential,string address,CancellationToken cancellation)
        {
            fixture.Ordering();Calls.Add("address "+credential.Label+" "+address);
            // The credential is on disk before any call that uses it: the current one, or a rename's claim.
            Assert(fixture.Store.ReadCredential()==credential||fixture.Store.ReadClaim()==credential,"HOME_NAME_CREDENTIAL_NOT_PERSISTED");
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
        {
            Calls.Add("delete "+credential.Label);
            Deletes.Add((credential.Label,fixture.Committed?InstalledHomeNameRecord.Parse(File.ReadAllBytes(fixture.Paths.NameRecord)).Name:null));
            return DeleteFailure is {} failure?Task.FromException(failure):Task.CompletedTask;
        }
    }
    private sealed class Environment(Fixture fixture):IHomeNameEnvironment
    {
        internal readonly List<string> Calls=[];internal IReadOnlyList<string> Resolved=[Address];internal Exception? ProbeFailure;internal int FailProxyRestarts,HangProxyRestarts;
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
            // A restart that does not finish until its budget is spent.
            if(HangProxyRestarts>0){HangProxyRestarts--;return Task.Delay(Timeout.Infinite,cancellation);}
            return Task.CompletedTask;
        }
        public Task RestartRuntimeAsync(CancellationToken cancellation)
        {
            // The Runtime is restarted onto a committed, active name, or back to the IP origin after a failure.
            cancellation.ThrowIfCancellationRequested();
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
                var f=new Fixture(root);var outcome=await f.Setup().RunAsync(Address,null,default);
                Assert(outcome==new HomeNameOutcome(nameA,null));
                Assert(f.Remote.Calls.SequenceEqual(["register","address "+LabelA+" "+Address,"issue "+nameA]));
                Assert(f.Environment.Calls.SequenceEqual(["proxy:name","resolve","probe","runtime:name"]));
                Assert(f.Owner==new HomeNameOwner(1,"active",LabelA,nameA,null)&&InstalledHomeNameRecord.Parse(File.ReadAllBytes(f.Paths.NameRecord))==new InstalledHomeName(1,nameA,Address));
                Assert(f.Store.Read(f.Store.Live,nameA) is not null&&f.Caddyfile.Contains("name "+nameA+" "+HomeNameStore.CertificateFile(f.Store.Live),StringComparison.Ordinal));
                // Committed: a rerun only checks with the name service that the label is still there.
                Assert(await f.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(nameA,null)&&f.Environment.Calls.Count==4);
                Assert(f.Remote.Calls.Count==4&&f.Remote.Calls[3]=="address "+LabelA+" "+Address);
                Assert(f.Setup().ActiveName()==nameA&&!f.Setup().Unfinished());
            });
            await check("home name failure goes back to the IP Caddyfile without home-name.json and records the code; a rerun resumes",async()=>
            {
                var f=new Fixture(root);f.Environment.ProbeFailure=new IOException("HOME_NAME_HTTPS_FAILED");
                Assert(await f.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(null,"HOME_NAME_HTTPS_FAILED"));
                Assert(f.Caddyfile=="ip\n"&&!f.Committed&&f.Owner==new HomeNameOwner(1,"failed",LabelA,nameA,"HOME_NAME_HTTPS_FAILED"));
                Assert(f.Environment.Calls.SequenceEqual(["proxy:name","resolve","probe","proxy:ip"])&&f.Setup().Unfinished()&&f.Setup().ActiveName() is null);
                // The credential and the certificate are kept, so the rerun neither registers nor issues again.
                f.Environment.ProbeFailure=null;f.Remote.Calls.Clear();f.Environment.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(nameA,null));
                Assert(f.Remote.Calls.SequenceEqual(["address "+LabelA+" "+Address])&&f.Owner!.Phase=="active"&&f.Committed);
            });
            await check("home name resolver that does not return the selected address fails as DNS rebind protection would",async()=>
            {
                var f=new Fixture(root);f.Environment.Resolved=["127.0.0.1"];
                Assert(await f.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(null,"HOME_NAME_RESOLVE_FAILED"));
                Assert(!f.Environment.Calls.Contains("probe")&&f.Caddyfile=="ip\n"&&!f.Committed&&f.Owner!.Failure=="HOME_NAME_RESOLVE_FAILED");
                f.Environment.Resolved=[];Assert((await f.Setup().RunAsync(Address,null,default)).Failure=="HOME_NAME_RESOLVE_FAILED");
            });
            await check("home name rerun from every intermediate phase first goes back to the IP path, then resumes",async()=>
            {
                foreach(var phase in new[]{"registered","issued","configured"})
                {
                    var f=new Fixture(root);await f.Setup().RunAsync(Address,null,default);
                    // A crash after the phase: the Caddyfile may name the site, home-name.json may not exist yet.
                    File.Delete(f.Paths.NameRecord);File.WriteAllBytes(f.Paths.Owner,HomeNameOwnerRecord.Encode(new(1,phase,LabelA,nameA,null)));
                    f.Remote.Calls.Clear();f.Environment.Calls.Clear();Assert(f.Setup().Unfinished());
                    Assert(await f.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(nameA,null));
                    Assert(f.Remote.Calls.SequenceEqual(["address "+LabelA+" "+Address])&&f.Environment.Calls.SequenceEqual(["proxy:name","resolve","probe","runtime:name"]));
                }
                // A credential on disk without any owner record (a crash right after registering): unfinished,
                // and the label is reused, never registered again. The same after an earlier removal.
                var g=new Fixture(root);g.Store.WriteCredential(new(LabelB,new string('A',43)));Assert(g.Setup().Unfinished());
                var removed=new Fixture(root);await removed.Setup().RemoveAsync(true,default);File.WriteAllBytes(removed.Paths.Owner,HomeNameOwnerRecord.Encode(new(1,"removed","","",null)));
                Assert(!removed.Setup().Unfinished());removed.Store.WriteCredential(new(LabelB,new string('A',43)));Assert(removed.Setup().Unfinished());
                Assert(await g.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(nameB,null)&&!g.Remote.Calls.Contains("register"));
            });
            await check("home name rerun reissues a certificate that is due and keeps one that is not",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,null,default);var first=f.Store.Read(f.Store.Live,nameA)!;
                f.Setup().Suspend();f.Remote.Calls.Clear();await f.Setup().RunAsync(Address,null,default);
                Assert(!f.Remote.Calls.Any(c=>c.StartsWith("issue",StringComparison.Ordinal))&&f.Store.Read(f.Store.Live,nameA)!.LeafSha256==first.LeafSha256);
                f.Now=f.Now.AddDays(61);f.Setup().Suspend();f.Remote.Calls.Clear();await f.Setup().RunAsync(Address,null,default);
                Assert(f.Remote.Calls.Contains("issue "+nameA)&&f.Store.Read(f.Store.Live,nameA)!.LeafSha256!=first.LeafSha256);
            });
            await check("home name 410 discards the credential and its certificates and registers once more",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,null,default);f.Setup().Suspend();
                f.Remote.GoneOnSetAddress=1;f.Remote.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(nameB,null));
                Assert(f.Remote.Calls.SequenceEqual(["address "+LabelA+" "+Address,"register","address "+LabelB+" "+Address,"issue "+nameB]));
                Assert(f.Store.ReadCredential()!.Label==LabelB&&f.Owner!.Name==nameB&&f.Store.Read(f.Store.Live,nameB) is not null);
                // Gone twice: a failure, not a loop.
                var g=new Fixture(root);g.Remote.GoneOnSetAddress=2;
                Assert(await g.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(null,"HOME_NAME_GONE")&&g.Remote.Calls.Count(c=>c=="register")==2);
            });
            await check("active home name is checked on a rerun: a label gone at the name service is registered again",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,null,default);
                f.Remote.GoneOnSetAddress=1;f.Remote.Calls.Clear();f.Environment.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(nameB,null));
                Assert(f.Remote.Calls.SequenceEqual(["address "+LabelA+" "+Address,"register","address "+LabelB+" "+Address,"issue "+nameB]));
                Assert(f.Environment.Calls.SequenceEqual(["proxy:name","resolve","probe","runtime:name"])&&f.Owner==new HomeNameOwner(1,"active",LabelB,nameB,null));
                Assert(InstalledHomeNameRecord.Parse(File.ReadAllBytes(f.Paths.NameRecord)).Name==nameB);
            });
            await check("active home name stays when the name service cannot be reached, and is redone when its certificate expired",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,null,default);
                f.Remote.AddressFailure=new HttpRequestException("offline");f.Remote.Calls.Clear();f.Environment.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(nameA,null)&&f.Environment.Calls.Count==0&&f.Committed);
                f.Remote.AddressFailure=null;f.Now=f.Now.AddDays(91);f.Remote.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(nameA,null)&&f.Remote.Calls.SequenceEqual(["address "+LabelA+" "+Address,"issue "+nameA]));
                Assert(f.Environment.Calls.SequenceEqual(["proxy:name","resolve","probe","runtime:name"]));
            });
            await check("active home name that fails its redo sends the Runtime back to the IP origin",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,null,default);
                f.Now=f.Now.AddDays(91);f.Remote.IssueFailure=new AcmeException("ACME_RATE_LIMITED");f.Environment.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(null,"ACME_RATE_LIMITED"));
                Assert(f.Environment.Calls.SequenceEqual(["proxy:ip","runtime:ip"])&&!f.Committed&&f.Caddyfile=="ip\n"&&f.Owner!.Phase=="failed");
            });
            await check("active home name whose redo cannot restart the proxy still sends the Runtime back to the IP origin",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,null,default);
                f.Now=f.Now.AddDays(91);f.Remote.IssueFailure=new AcmeException("ACME_RATE_LIMITED");f.Environment.FailProxyRestarts=1;f.Environment.Calls.Clear();
                try{await f.Setup().RunAsync(Address,null,default);throw new Exception("expected failure");}
                catch(AggregateException both){Assert(both.InnerExceptions.Count==2);}
                Assert(f.Environment.Calls.SequenceEqual(["proxy:ip","runtime:ip"])&&!f.Committed&&f.Caddyfile=="ip\n");
            });
            await check("active home name whose redo spends the proxy's cleanup budget still restarts the Runtime on its own budget",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,null,default);
                f.Now=f.Now.AddDays(91);f.Remote.IssueFailure=new AcmeException("ACME_RATE_LIMITED");f.Environment.HangProxyRestarts=1;f.Environment.Calls.Clear();
                try{await f.Setup().RunAsync(Address,null,default);throw new Exception("expected failure");}
                catch(AggregateException both){Assert(both.InnerExceptions.Count==2&&both.InnerExceptions[1] is OperationCanceledException);}
                Assert(f.Environment.Calls.SequenceEqual(["proxy:ip","runtime:ip"])&&!f.Committed);
            });
            await check("home name removal also discards a credential that no owner phase followed",async()=>
            {
                var f=new Fixture(root);File.WriteAllBytes(f.Paths.Owner,HomeNameOwnerRecord.Encode(new(1,"removed","","",null)));
                f.Store.WriteCredential(new(LabelA,new string('A',43)));Assert(f.Setup().Unfinished());
                await f.Setup().RemoveAsync(true,default);
                Assert(f.Remote.Calls.SequenceEqual(["delete "+LabelA])&&f.Store.ReadCredential() is null&&!f.Setup().Unfinished());
                var g=new Fixture(root);g.Store.WriteCredential(new(LabelB,new string('A',43)));
                await g.Setup().RemoveAsync(true,default);
                Assert(g.Remote.Calls.SequenceEqual(["delete "+LabelB])&&g.Store.ReadCredential() is null&&!g.Setup().Unfinished()&&g.Owner!.Phase=="removed");
            });
            await check("home name failure codes come from the name service, the CA or the step, never free text",async()=>
            {
                var f=new Fixture(root);f.Remote.IssueFailure=new AcmeException("ACME_RATE_LIMITED");
                Assert((await f.Setup().RunAsync(Address,null,default)).Failure=="ACME_RATE_LIMITED"&&f.Owner!.Phase=="failed"&&f.Owner.Label==LabelA);
                Assert(HomeNameSetup.Code(new HomeNameServiceException("HOME_NAME_RATE_LIMITED",429))=="HOME_NAME_RATE_LIMITED");
                Assert(HomeNameSetup.Code(new IOException("free text with spaces"))=="HOME_NAME_FAILED"&&HomeNameSetup.Code(new InvalidOperationException("X"))=="HOME_NAME_FAILED");
                Assert(HomeNameSetup.Code(new TaskCanceledException())=="HOME_NAME_TIMEOUT");
            });
            await check("home name that cannot get back to the IP path fails Setup with both causes",async()=>
            {
                var f=new Fixture(root);f.Environment.ProbeFailure=new IOException("HOME_NAME_HTTPS_FAILED");f.Environment.FailProxyRestarts=2;
                try{await f.Setup().RunAsync(Address,null,default);}
                catch(AggregateException both){Assert(both.InnerExceptions.Count==2&&!f.Committed);return;}
                throw new Exception("expected failure");
            });
            await check("home name suspend keeps label, credential and certificate; removal deletes the name and keeps the ACME account",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,null,default);
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
            var nameC=HomeNameRelease.NameFor(Chosen);var nameO=HomeNameRelease.NameFor(Other);
            await check("home name with a chosen label registers exactly that label",async()=>
            {
                var f=new Fixture(root);
                Assert(await f.Setup().RunAsync(Address,Chosen,default)==new HomeNameOutcome(nameC,null));
                Assert(f.Remote.Calls.SequenceEqual(["register "+Chosen,"address "+Chosen+" "+Address,"issue "+nameC,"address "+Chosen+" "+Address]));
                Assert(f.Owner==new HomeNameOwner(1,"active",Chosen,nameC,null)&&f.Store.ReadClaim() is null&&f.Store.ReadRetired().Count==0);
                Assert(f.Store.Read(f.Store.Live,nameC) is not null&&f.Caddyfile.Contains("name "+nameC+" ",StringComparison.Ordinal));
                try{await f.Setup().RunAsync(Address,"Hadad",default);throw new Exception("expected refusal");}
                catch(ArgumentException error){Assert(error.Message==HomeNameLabelException.Invalid);}
            });
            await check("home name rename prepares the new label beside the active name, switches, and deletes the old label only after the commit",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,null,default);f.Remote.Calls.Clear();f.Environment.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,Chosen,default)==new HomeNameOutcome(nameC,null));
                Assert(f.Remote.Calls.SequenceEqual(["address "+LabelA+" "+Address,"register "+Chosen,"address "+Chosen+" "+Address,"issue "+nameC,"address "+Chosen+" "+Address,"delete "+LabelA]));
                Assert(f.Environment.Calls.SequenceEqual(["proxy:name","resolve","probe","runtime:name"]));
                Assert(f.Remote.Deletes.SequenceEqual([(LabelA,(string?)nameC)]));
                Assert(f.Owner==new HomeNameOwner(1,"active",Chosen,nameC,null)&&f.Store.ReadCredential()!.Label==Chosen);
                Assert(f.Store.ReadClaim() is null&&!Directory.Exists(f.Store.Claimed)&&f.Store.ReadRetired().Count==0&&f.Store.Read(f.Store.Live,nameC) is not null);
            });
            await check("home name rename to a taken label leaves the active name exactly as it was",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,null,default);f.Remote.Held.Add(Other);
                var caddyfile=f.Caddyfile;var record=File.ReadAllBytes(f.Paths.NameRecord);f.Remote.Calls.Clear();f.Environment.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,Other,default)==new HomeNameOutcome(nameA,HomeNameLabelException.Taken));
                Assert(f.Remote.Calls.SequenceEqual(["address "+LabelA+" "+Address,"register "+Other])&&f.Environment.Calls.Count==0);
                Assert(f.Caddyfile==caddyfile&&File.ReadAllBytes(f.Paths.NameRecord).SequenceEqual(record)&&f.Owner==new HomeNameOwner(1,"active",LabelA,nameA,null));
                Assert(f.Store.ReadClaim() is null&&f.Store.ReadRetired().Count==0&&f.Store.ReadCredential()!.Label==LabelA);
                Assert(HomeNameSetup.FailureText(HomeNameLabelException.Taken,nameA).StartsWith("השם כבר תפוס. אפשר לבחור שם אחר ולהריץ את ההתקנה שוב.",StringComparison.Ordinal));
                // Not active (a LAN change suspended it): the current label is redone, and the refusal is still reported.
                f.Setup().Suspend();f.Remote.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,Other,default)==new HomeNameOutcome(nameA,HomeNameLabelException.Taken)&&f.Committed&&f.Owner!.Phase=="active");
                Assert(f.Remote.Calls.SequenceEqual(["register "+Other,"address "+LabelA+" "+Address]));
                // No name yet: only the IP path, with the code.
                var g=new Fixture(root);g.Remote.Held.Add(Other);
                Assert(await g.Setup().RunAsync(Address,Other,default)==new HomeNameOutcome(null,HomeNameLabelException.Taken));
                Assert(!g.Committed&&g.Caddyfile=="ip\n"&&g.Owner==new HomeNameOwner(1,"failed","","",HomeNameLabelException.Taken)&&g.Store.ReadCredential() is null);
            });
            await check("home name rename that fails after the switch goes back to the IP path; the old label is kept until a rerun commits the new one",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,null,default);f.Environment.ProbeFailure=new IOException("HOME_NAME_HTTPS_FAILED");
                Assert(await f.Setup().RunAsync(Address,Chosen,default)==new HomeNameOutcome(null,"HOME_NAME_HTTPS_FAILED"));
                Assert(!f.Committed&&f.Caddyfile=="ip\n"&&f.Owner==new HomeNameOwner(1,"failed",Chosen,nameC,"HOME_NAME_HTTPS_FAILED"));
                Assert(f.Remote.Deletes.Count==0&&f.Store.ReadRetired().Single().Label==LabelA&&f.Setup().Unfinished());
                // A run without the owner's choice resumes toward the new label and only then deletes the old one.
                f.Environment.ProbeFailure=null;f.Remote.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(nameC,null));
                Assert(f.Remote.Calls.SequenceEqual(["address "+Chosen+" "+Address,"delete "+LabelA])&&f.Remote.Deletes.SequenceEqual([(LabelA,(string?)nameC)])&&f.Store.ReadRetired().Count==0);
            });
            await check("home name rename back to a label given up but not yet deleted takes it back without registering",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,null,default);f.Environment.ProbeFailure=new IOException("HOME_NAME_HTTPS_FAILED");
                await f.Setup().RunAsync(Address,Chosen,default);f.Environment.ProbeFailure=null;f.Remote.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,LabelA,default)==new HomeNameOutcome(nameA,null));
                Assert(!f.Remote.Calls.Any(c=>c.StartsWith("register",StringComparison.Ordinal))&&f.Remote.Calls.Last()=="delete "+Chosen);
                Assert(f.Owner==new HomeNameOwner(1,"active",LabelA,nameA,null)&&f.Store.ReadRetired().Count==0&&f.Store.ReadClaim() is null);
            });
            await check("home name rename resumes from every crash point without registering again",async()=>
            {
                static string Leaf(AcmeCertificate certificate){using var leaf=X509Certificate2.CreateFromPem(certificate.CertificatePem);return leaf.GetCertHashString(HashAlgorithmName.SHA256);}
                // 1. After the claim was registered and stored, before anything else: the claim is reused.
                var f=new Fixture(root);await f.Setup().RunAsync(Address,null,default);f.Store.WriteClaim(Credential(Chosen));f.Remote.Held.Add(Chosen);f.Remote.Calls.Clear();
                Assert(await f.Setup().RunAsync(Address,Chosen,default)==new HomeNameOutcome(nameC,null)&&!f.Remote.Calls.Any(c=>c.StartsWith("register",StringComparison.Ordinal)));
                // A run without the owner's choice keeps the active name and the stored claim (nothing is deleted).
                var k=new Fixture(root);await k.Setup().RunAsync(Address,null,default);k.Store.WriteClaim(Credential(Chosen));k.Remote.Calls.Clear();
                Assert(await k.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(nameA,null)&&k.Store.ReadClaim()==Credential(Chosen)&&k.Remote.Deletes.Count==0);
                // A claim for another label than the one now chosen is given up and deleted once the new name commits.
                k.Remote.Calls.Clear();
                Assert(await k.Setup().RunAsync(Address,Other,default)==new HomeNameOutcome(nameO,null));
                Assert(k.Remote.Deletes.Select(d=>d.Label).Order(StringComparer.Ordinal).SequenceEqual(new[]{Chosen,LabelA}.Order(StringComparer.Ordinal))&&k.Remote.Deletes.All(d=>d.Committed==nameO));
                // 2. Uncommitted, the claim certified, the old label retired and credential.json already the claim (Promote's commit point).
                // 3. The same with the claim's certificate already moved to next/.
                foreach(var moved in new[]{false,true})
                {
                    var g=new Fixture(root);await g.Setup().RunAsync(Address,null,default);g.Setup().Suspend();File.WriteAllText(g.Paths.Caddyfile,"ip\n");
                    var certificate=Certificate(nameC,g.Now.AddMinutes(-1),g.Now.AddDays(90));
                    g.Store.Retire(Credential(LabelA));g.Store.WriteClaim(Credential(Chosen));
                    if(moved)g.Store.Stage(certificate);else g.Store.StageClaim(certificate);
                    g.Store.WriteCredential(Credential(Chosen));g.Remote.Calls.Clear();
                    Assert(await g.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(nameC,null));
                    Assert(g.Remote.Calls.SequenceEqual(["address "+Chosen+" "+Address,"delete "+LabelA]),string.Join(",",g.Remote.Calls));
                    Assert(g.Owner==new HomeNameOwner(1,"active",Chosen,nameC,null)&&g.Store.ReadClaim() is null&&g.Store.ReadRetired().Count==0&&g.Store.Read(g.Store.Live,nameC)!.LeafSha256==Leaf(certificate));
                }
            });
            await check("home name repair runs keep the chosen label",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,Chosen,default);f.Remote.Calls.Clear();
                // Committed: only checked. The same when the window shows the current label unchanged.
                Assert(await f.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(nameC,null)&&f.Remote.Calls.SequenceEqual(["address "+Chosen+" "+Address]));
                Assert(await f.Setup().RunAsync(Address,Chosen,default)==new HomeNameOutcome(nameC,null)&&!f.Remote.Calls.Any(c=>c.StartsWith("register",StringComparison.Ordinal)));
                // Suspended by a LAN change, or failed: redone on the same label.
                f.Setup().Suspend();Assert(await f.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(nameC,null));
                f.Now=f.Now.AddDays(91);f.Remote.IssueFailure=new AcmeException("ACME_RATE_LIMITED");
                Assert((await f.Setup().RunAsync(Address,null,default)).Failure=="ACME_RATE_LIMITED"&&f.Owner!.Label==Chosen);
                f.Remote.IssueFailure=null;Assert(await f.Setup().RunAsync(Address,null,default)==new HomeNameOutcome(nameC,null));
                Assert(!f.Remote.Calls.Any(c=>c.StartsWith("register",StringComparison.Ordinal))&&f.Remote.Deletes.Count==0);
            });
            await check("home name removal deletes every label this home holds: current, claim and retired",async()=>
            {
                var f=new Fixture(root);await f.Setup().RunAsync(Address,null,default);
                f.Store.WriteClaim(Credential(Chosen));f.Store.Retire(Credential(Other));f.Remote.Calls.Clear();
                await f.Setup().RemoveAsync(true,default);
                Assert(f.Remote.Calls.Order(StringComparer.Ordinal).SequenceEqual(new[]{"delete "+LabelA,"delete "+Chosen,"delete "+Other}.Order(StringComparer.Ordinal)));
                Assert(f.Store.ReadCredential() is null&&f.Store.ReadClaim() is null&&f.Store.ReadRetired().Count==0&&f.Owner!.Phase=="removed");
            });
            await check("home name choice travels beside the LAN request and only with an Enable request",async()=>
            {
                var owner=new SetupOwner("S-1-5-21-1-2-3-1001","Owner");
                var request=new LanSetupRequest(LanSetupAction.Enable,"home",Address,true,true);
                var host=new ChoiceHost();await new InstallFlow(host).RunAsync(owner,false,_=>{},default,lan:request,homeName:true);
                Assert(host.HomeName==true&&host.Label is null&&ReferenceEquals(host.Request,request));
                var chosen=new ChoiceHost();await new InstallFlow(chosen).RunAsync(owner,false,_=>{},default,lan:request,homeName:true,homeNameLabel:Chosen);
                Assert(chosen.HomeName==true&&chosen.Label==Chosen);
                // A label only with the option, and only one the rule allows (the window lowercases what was typed).
                foreach(var (option,label) in new[]{(false,Chosen),(true,"Hadad-Home"),(true,"ab"),(true,"www"),(true,"a--b")})
                {
                    var refused=new ChoiceHost();
                    try{await new InstallFlow(refused).RunAsync(owner,false,_=>{},default,lan:request,homeName:option,homeNameLabel:label);throw new Exception("expected refusal");}
                    catch(IOException error){Assert(error.Message=="LAN_ACTION_INVALID"&&refused.Calls==0);}
                }
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
        internal int Calls;internal bool? HomeName;internal string? Label;internal LanSetupRequest? Request;
        public Task PrepareAsync(SetupOwner owner,Action<SetupProgress> progress,CancellationToken cancellation){Calls++;return Task.CompletedTask;}
        public Task ProvisionAsync(Action<SetupProgress> progress,CancellationToken cancellation){Calls++;return Task.CompletedTask;}
        public Task ConfigureLanAsync(LanSetupRequest request,bool homeName,string? label,Action<SetupProgress> progress,CancellationToken cancellation){Calls++;Request=request;HomeName=homeName;Label=label;return Task.CompletedTask;}
        public Task<SetupResult> StartAndObserveAsync(Action<SetupProgress> progress,CancellationToken cancellation){Calls++;return Task.FromResult(new SetupResult(true,"READY"));}
        public Task CommitAsync(Action<SetupProgress> progress,CancellationToken cancellation){Calls++;return Task.CompletedTask;}
        public Task<SetupResult> UninstallAsync(Action<SetupProgress> progress,CancellationToken cancellation,UninstallMode mode=UninstallMode.KeepData){Calls++;return Task.FromResult(new SetupResult(true,"REMOVED"));}
    }
}

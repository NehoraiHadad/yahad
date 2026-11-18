using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Yahad.Install;

// The home name's build inputs, its Caddyfile site and the name/ store (docs/HOME-NAME.md 3).
internal static class HomeNameTests
{
    internal const string Label="k2abcdefgh7j",OtherLabel="m3abcdefgh7j";
    internal static string Name=>HomeNameRelease.NameFor(Label);
    /// <summary>A self-signed leaf for the name, as the store would keep one: chain PEM and PKCS#8 key PEM.</summary>
    internal static AcmeCertificate Certificate(string name,DateTimeOffset notBefore,DateTimeOffset notAfter)
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request=new CertificateRequest("CN="+name,key,HashAlgorithmName.SHA256);
        var san=new SubjectAlternativeNameBuilder();san.AddDnsName(name);request.CertificateExtensions.Add(san.Build());
        using var certificate=request.CreateSelfSigned(notBefore,notAfter);
        return new(PemEncoding.WriteString("CERTIFICATE",certificate.RawData)+"\n",key.ExportPkcs8PrivateKeyPem()+"\n",notBefore,notAfter);
    }
    public static async Task Run(string root,Func<string,Func<Task>,Task> check)
    {
        void Assert(bool value){if(!value)throw new Exception("assertion failed");}
        void Rejects<T>(Action action,string? code=null) where T:Exception
        {try{action();}catch(T error){if(code is not null&&error.Message!=code)throw new Exception("wrong code "+error.Message);return;}throw new Exception("expected "+typeof(T).Name);}
        var now=DateTimeOffset.UtcNow;
        string Fresh(string name){var path=Path.Combine(root,"home-name-"+name);Directory.CreateDirectory(path);return path;}

        await check("home name build inputs are the release's service and zone, and only a whole pair is accepted",()=>
        {
            Assert(HomeNameRelease.Configured&&HomeNameRelease.Service=="https://yahad-names.nehoraihadad.com"&&HomeNameRelease.Zone=="home.nehoraihadad.com");
            Assert(HomeNameRelease.AcmeDirectory.AbsoluteUri=="https://acme-v02.api.letsencrypt.org/directory"&&HomeNameRelease.AcmeProfile=="classic");
            Assert(HomeNameRelease.Require("","")==("",""));
            foreach(var (service,zone) in new[]{("https://names.example.com",""),("","home.example.com"),("http://names.example.com","home.example.com"),
                ("https://names.example.com/v1","home.example.com"),("https://u@names.example.com","home.example.com"),("https://names.example.com?x","home.example.com"),
                ("https://names.example.com","Home.Example.com"),("https://names.example.com","home..example.com")})
                Rejects<InvalidOperationException>(()=>HomeNameRelease.Require(service,zone),"HOME_NAME_CONFIGURATION");
            return Task.CompletedTask;
        });
        await check("home name labels and names are exactly one 12-character base32 label of the zone",()=>
        {
            Assert(HomeNameRelease.IsLabel(Label)&&Name==Label+".home.nehoraihadad.com"&&HomeNameRelease.IsName(Name));
            foreach(var label in new[]{"","k2abcdefgh7","k2abcdefgh7jk","K2abcdefgh7j","k2abcdefgh1j","k2abcdefgh-j"})
            {Assert(!HomeNameRelease.IsLabel(label));Rejects<IOException>(()=>HomeNameRelease.NameFor(label),"HOME_NAME_INVALID");}
            foreach(var name in new[]{Label+".home.nehoraihadad.com.",Label+".other.example.com","x."+Name,"home.nehoraihadad.com",Label.ToUpperInvariant()+".home.nehoraihadad.com"})
                Assert(!HomeNameRelease.IsName(name));
            return Task.CompletedTask;
        });

        const string id="0123456789abcdef0123456789abcdef";
        LanDeploymentPlan Plan(bool help,LanHomeNameSite? site=null)=>LanDeployment.Create(id,[new LanChoice("home","selected","192.168.4.5")],"home","192.168.4.5",47123,
            @"C:\Program Files\Yahad\caddy.exe",@"C:\ProgramData\Yahad\data",@"C:\ProgramData\Yahad\join-public",help,site);
        string Sha(string text)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        LanHomeNameSite Site()=>new(Name,@"C:\ProgramData\Yahad\name\live\cert.pem",@"C:\ProgramData\Yahad\name\live\key.pem");
        await check("Caddyfile without a home name is byte-identical to the release before it",()=>
        {
            // Digests of release 0.6.8's LanDeployment output for these inputs, produced by lan-caddyfile.cs at that commit.
            Assert(Sha(Plan(true).Caddyfile)=="3e75380318492bd15d1803d8666a46121b4e14bde2d6a3ce497561a58537b4ce");
            Assert(Sha(Plan(false).Caddyfile)=="d6a638c86f31844d385243032156ab17c1f32ae15c290639d01e1fceb8b940cf");
            return Task.CompletedTask;
        });
        await check("Caddyfile name site binds the same address, uses the live files and carries the same guarded route",()=>
        {
            foreach(var help in new[]{true,false})
            {
                string plain=Plan(help).Caddyfile,named=Plan(help,Site()).Caddyfile;
                var start=named.IndexOf("https://"+Name+" {\n",StringComparison.Ordinal);Assert(start>0);
                // Everything else is unchanged: the IP site before it and the HTTP helper after it.
                var end=help?named.IndexOf("http://192.168.4.5 {",StringComparison.Ordinal):named.Length;
                Assert(plain==named[..start]+named[end..]);
                var block=named[start..end];
                Assert(block.StartsWith("https://"+Name+" {\n    bind 192.168.4.5\n    tls \"C:/ProgramData/Yahad/name/live/cert.pem\" \"C:/ProgramData/Yahad/name/live/key.pem\"\n",StringComparison.Ordinal));
                // The route after the tls line is the IP site's own, owner-only 404 guard first.
                var ipStart=plain.IndexOf("https://192.168.4.5 {\n",StringComparison.Ordinal);var ipRoute=plain[plain.IndexOf("    tls internal\n",ipStart,StringComparison.Ordinal)..];
                ipRoute=ipRoute["    tls internal\n".Length..(ipRoute.IndexOf("\n}\n",StringComparison.Ordinal)+3)];
                Assert(block.EndsWith(ipRoute,StringComparison.Ordinal)&&ipRoute.Contains("respond @ownerOnly 404",StringComparison.Ordinal));
                Assert(named.Split("bind ").Skip(1).All(b=>b.StartsWith("192.168.4.5\n",StringComparison.Ordinal)));
            }
            Assert(Plan(true,Site()).FirewallRules.SequenceEqual(Plan(true).FirewallRules)&&Plan(true,Site()).Origin=="https://192.168.4.5");
            Rejects<ArgumentException>(()=>Plan(true,Site() with{Name="bad name"}));
            Rejects<ArgumentException>(()=>Plan(true,Site() with{Name="a.example.com\n    tls internal"}));
            return Task.CompletedTask;
        });

        await check("name store reads a certificate only for its name and with its own key",()=>
        {
            var store=new HomeNameStore(Fresh("read"));Assert(store.Read(store.Live,Name) is null&&!store.IsComplete(store.Live));
            store.Stage(Certificate(Name,now.AddDays(-1),now.AddDays(89)));
            var read=store.Read(store.Next,Name)!;Assert(read.NotAfter>now.AddDays(88)&&read.LeafSha256.Length==64&&store.IsComplete(store.Next));
            Rejects<IOException>(()=>store.Read(store.Next,HomeNameRelease.NameFor(OtherLabel)),"HOME_NAME_CERTIFICATE_NAME");
            // A key that is not the leaf's.
            var other=Certificate(Name,now,now.AddDays(90));File.Delete(HomeNameStore.KeyFile(store.Next));
            File.WriteAllText(HomeNameStore.KeyFile(store.Next),other.KeyPem);
            Rejects<IOException>(()=>store.Read(store.Next,Name),"HOME_NAME_CERTIFICATE_KEY");Assert(!store.IsComplete(store.Next));
            File.WriteAllText(HomeNameStore.KeyFile(store.Next),"not a key");Rejects<IOException>(()=>store.Read(store.Next,Name),"HOME_NAME_CERTIFICATE_KEY");
            return Task.CompletedTask;
        });
        await check("name store swap keeps the previous certificate and swap back restores it",()=>
        {
            var store=new HomeNameStore(Fresh("swap"));
            store.Stage(Certificate(Name,now.AddDays(-60),now.AddDays(30)));store.Swap();var first=store.Read(store.Live,Name)!;
            Assert(!Directory.Exists(store.Next)&&!File.Exists(store.JournalFile));
            store.Stage(Certificate(Name,now,now.AddDays(90)));store.Swap();var second=store.Read(store.Live,Name)!;
            Assert(second.LeafSha256!=first.LeafSha256&&store.Read(store.Prev,Name)!.LeafSha256==first.LeafSha256);
            store.SwapBack();Assert(store.Read(store.Live,Name)!.LeafSha256==first.LeafSha256&&!Directory.Exists(store.Prev)&&!File.Exists(store.JournalFile));
            Rejects<IOException>(()=>store.SwapBack(),"HOME_NAME_PREVIOUS_MISSING");
            // An incomplete stage never replaces live.
            Directory.CreateDirectory(store.Next);File.WriteAllText(HomeNameStore.CertificateFile(store.Next),"x");
            Rejects<IOException>(()=>store.Swap(),"HOME_NAME_STAGE_INCOMPLETE");Assert(store.Read(store.Live,Name)!.LeafSha256==first.LeafSha256);
            return Task.CompletedTask;
        });
        await check("name store recovers every interrupted swap point to a complete live certificate",()=>
        {
            var older=Certificate(Name,now.AddDays(-60),now.AddDays(30));var newer=Certificate(Name,now,now.AddDays(90));
            HomeNameStore Prepared(string name){var store=new HomeNameStore(Fresh("recover-"+name));store.Stage(older);store.Swap();store.Stage(newer);return store;}
            void Journal(HomeNameStore store)=>File.WriteAllText(store.JournalFile,"{\"format\":1}");
            string Leaf(AcmeCertificate certificate)=>AcmeCertificateCheck.ParseChain(certificate.CertificatePem)[0].GetCertHashString(HashAlgorithmName.SHA256);
            // Crash after the journal, before any rename: live is untouched.
            var a=Prepared("journal");Journal(a);a.Recover();
            Assert(a.Read(a.Live,Name)!.LeafSha256==Leaf(older)&&!File.Exists(a.JournalFile));
            // Crash after live→prev, before next→live: the complete next becomes live.
            var b=Prepared("moved");Journal(b);Directory.Move(b.Live,b.Prev);b.Recover();
            Assert(b.Read(b.Live,Name)!.LeafSha256==Leaf(newer)&&!File.Exists(b.JournalFile));
            // Same point with a damaged next: prev comes back.
            var c=Prepared("damaged");Journal(c);Directory.Move(c.Live,c.Prev);File.Delete(HomeNameStore.KeyFile(c.Next));c.Recover();
            Assert(c.Read(c.Live,Name)!.LeafSha256==Leaf(older));
            // Crash after next→live, before the journal went: nothing to restore.
            var d=Prepared("done");d.Swap();Journal(d);d.Recover();Assert(d.Read(d.Live,Name)!.LeafSha256==Leaf(newer)&&!File.Exists(d.JournalFile));
            // Same point with a next that cannot be moved (a file in it is open): prev comes back.
            var f=Prepared("locked");Journal(f);Directory.Move(f.Live,f.Prev);
            using(new FileStream(HomeNameStore.KeyFile(f.Next),FileMode.Open,FileAccess.Read,FileShare.Read))f.Recover();
            Assert(f.Read(f.Live,Name)!.LeafSha256==Leaf(older)&&!File.Exists(f.JournalFile));
            // A swap whose next→live fails puts the previous certificate back before it throws.
            var g=Prepared("swap-fails");
            using(new FileStream(HomeNameStore.KeyFile(g.Next),FileMode.Open,FileAccess.Read,FileShare.Read))
                try{g.Swap();throw new Exception("expected the swap to fail");}catch(IOException){}catch(UnauthorizedAccessException){}
            Assert(g.Read(g.Live,Name)!.LeafSha256==Leaf(older));g.Recover();
            Assert(!File.Exists(g.JournalFile)&&g.Read(g.Live,Name)!.LeafSha256==Leaf(older)&&g.IsComplete(g.Next));
            // No journal: recovery touches nothing.
            var e=Prepared("none");Directory.Move(e.Live,e.Prev+"-aside");e.Recover();Assert(!Directory.Exists(e.Live));
            return Task.CompletedTask;
        });
        await check("name store keeps the credential strict and clears the name but not the ACME account",()=>
        {
            var store=new HomeNameStore(Fresh("credential"));Assert(store.ReadCredential() is null);
            var credential=new HomeNameCredential(Label,new string('A',43));store.WriteCredential(credential);Assert(store.ReadCredential()==credential);
            Rejects<ArgumentException>(()=>store.WriteCredential(credential with{Label="short"}));
            foreach(var text in new[]{"{\"label\":\""+Label+"\",\"secret\":\""+new string('A',43)+"\",\"extra\":1}","{\"label\":\""+Label+"\"}","{\"label\":\"x\",\"secret\":\""+new string('A',43)+"\"}","[]",""})
            {File.WriteAllText(store.CredentialFile,text);Rejects<IOException>(()=>store.ReadCredential());}
            store.WriteCredential(credential);
            using(var account=store.Account()){}
            var key=File.ReadAllBytes(store.AccountFile);using(var again=store.Account()){}Assert(File.ReadAllBytes(store.AccountFile).SequenceEqual(key));
            store.Stage(Certificate(Name,now,now.AddDays(90)));store.Swap();store.Clear();
            Assert(store.ReadCredential() is null&&!Directory.Exists(store.Live)&&File.ReadAllBytes(store.AccountFile).SequenceEqual(key));
            return Task.CompletedTask;
        });
    }
}

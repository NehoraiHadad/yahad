using System.Buffers.Text;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Yahad.Install;

// ACME client and name service client. Pure tests run always; the Pebble run is opt-in:
// YAHAD_TEST_PEBBLE=<path to pebble.exe> (v2.10.3).
internal static class AcmeTests
{
    // RFC 7515 A.3: the ES256 example key and its signed JWS.
    private const string RfcX="f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU",RfcY="x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0",RfcD="jpsQnnGQmL-YBIffH1136cspYG6-0iY7X1fCE9-E9LI"; // ggignore
    private const string RfcSigningInput="eyJhbGciOiJFUzI1NiJ9.eyJpc3MiOiJqb2UiLA0KICJleHAiOjEzMDA4MTkzODAsDQogImh0dHA6Ly9leGFtcGxlLmNvbS9pc19yb290Ijp0cnVlfQ";
    private const string RfcSignature="DtEhU3ljbEg8L38VWAfUAqOyKAM6-Xx-F4GawxaepmXFCgfTjDxw5djxLa8ISlSApmWQxfKTUJqPP3-Kg6NU1Q";
    // Computed independently (OpenSSL dgst over the canonical JWK, and Node's crypto JWK export).
    private const string RfcThumbprint="oKIywvGUpTVTyxMQ3bwIIeQUudfr_CkLMjCE19ECD-U";
    // RFC 8555 7.5.1 example token; digest computed with OpenSSL: base64url(sha256(token "." RfcThumbprint)).
    // Public RFC test vectors, not credentials.
    private const string RfcToken="evaGxfADs6pSRb2LAv9IZf17Dt3juxGJ-PCt92wr-oA"; // ggignore
    private const string RfcDns01="ZaJgFtIV4WA0t2MzN7kbSk0sfRd2GZjaxRqaSTAK6lI"; // ggignore
    private const string Name="k2abcdefgh7j.home.example.com";

    public static async Task Run(Func<string,Func<Task>,Task> check)
    {
        void Assert(bool value){if(!value)throw new Exception("assertion failed");}
        void Rejects<T>(Action action,string? code=null) where T:Exception
        {try{action();}catch(T error){if(code is not null&&!error.Message.StartsWith(code,StringComparison.Ordinal))throw new Exception("wrong code "+error.Message);return;}throw new Exception("expected "+typeof(T).Name);}
        async Task RejectsAsync<T>(Func<Task> action,string code) where T:Exception
        {try{await action();}catch(T error){if(!error.Message.StartsWith(code,StringComparison.Ordinal))throw new Exception("wrong code "+error.Message);return;}throw new Exception("expected "+code);}
        AcmeAccountKey RfcKey()
        {
            using var key=ECDsa.Create(new ECParameters{Curve=ECCurve.NamedCurves.nistP256,D=Base64Url.DecodeFromChars(RfcD),Q=new ECPoint{X=Base64Url.DecodeFromChars(RfcX),Y=Base64Url.DecodeFromChars(RfcY)}});
            return AcmeAccountKey.Import(key.ExportPkcs8PrivateKey());
        }

        await check("ACME account JWK and thumbprint match RFC 7638 for the RFC 7515 ES256 key",()=>
        {
            using var key=RfcKey();
            Assert(key.Jwk=="{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\""+RfcX+"\",\"y\":\""+RfcY+"\"}");
            Assert(key.Thumbprint==RfcThumbprint);
            using var again=AcmeAccountKey.Import(key.Export());Assert(again.Thumbprint==RfcThumbprint);
            using var fresh=AcmeAccountKey.Create();using var restored=AcmeAccountKey.Import(fresh.Export());Assert(fresh.Thumbprint==restored.Thumbprint&&fresh.Thumbprint!=RfcThumbprint);
            return Task.CompletedTask;
        });
        await check("ACME account key refuses other curves and malformed bytes",()=>
        {
            using var p384=ECDsa.Create(ECCurve.NamedCurves.nistP384);Rejects<IOException>(()=>AcmeAccountKey.Import(p384.ExportPkcs8PrivateKey()),"ACME_ACCOUNT_KEY_INVALID");
            using var k1=ECDsa.Create(ECCurve.CreateFromFriendlyName("secp256k1"));Rejects<IOException>(()=>AcmeAccountKey.Import(k1.ExportPkcs8PrivateKey()),"ACME_ACCOUNT_KEY_INVALID");
            using var rsa=RSA.Create(2048);Rejects<IOException>(()=>AcmeAccountKey.Import(rsa.ExportPkcs8PrivateKey()),"ACME_ACCOUNT_KEY_INVALID");
            Rejects<IOException>(()=>AcmeAccountKey.Import([1,2,3]),"ACME_ACCOUNT_KEY_INVALID");
            using var good=AcmeAccountKey.Create();Rejects<IOException>(()=>AcmeAccountKey.Import([..good.Export(),0]),"ACME_ACCOUNT_KEY_INVALID");
            return Task.CompletedTask;
        });
        await check("ES256 signatures are raw r||s and verify against the RFC 7515 A.3 vector",()=>
        {
            using var key=ECDsa.Create(new ECParameters{Curve=ECCurve.NamedCurves.nistP256,Q=new ECPoint{X=Base64Url.DecodeFromChars(RfcX),Y=Base64Url.DecodeFromChars(RfcY)}});
            var input=Encoding.ASCII.GetBytes(RfcSigningInput);
            Assert(key.VerifyData(input,Base64Url.DecodeFromChars(RfcSignature),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            using var account=RfcKey();var signature=account.Sign(input);
            Assert(signature.Length==64&&key.VerifyData(input,signature,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            return Task.CompletedTask;
        });
        await check("ACME JWS carries jwk before the account exists, kid after, and an empty payload for POST-as-GET",()=>
        {
            using var account=RfcKey();
            using var verifier=ECDsa.Create(new ECParameters{Curve=ECCurve.NamedCurves.nistP256,Q=new ECPoint{X=Base64Url.DecodeFromChars(RfcX),Y=Base64Url.DecodeFromChars(RfcY)}});
            (JsonElement Header,string Payload) Open(byte[] body)
            {
                using var document=JsonDocument.Parse(body);var root=document.RootElement;
                Assert(root.EnumerateObject().Select(p=>p.Name).SequenceEqual(["protected","payload","signature"]));
                var protectedPart=root.GetProperty("protected").GetString()!;var payloadPart=root.GetProperty("payload").GetString()!;
                var signature=Base64Url.DecodeFromChars(root.GetProperty("signature").GetString()!);
                Assert(signature.Length==64&&verifier.VerifyData(Encoding.ASCII.GetBytes(protectedPart+"."+payloadPart),signature,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
                using var header=JsonDocument.Parse(Base64Url.DecodeFromChars(protectedPart));
                return (header.RootElement.Clone(),Encoding.UTF8.GetString(Base64Url.DecodeFromChars(payloadPart)));
            }
            var (first,payload)=Open(AcmeJws.Sign(account,"https://ca.test/new-acct","nonce-1",null,"{\"termsOfServiceAgreed\":true}"u8,false));
            Assert(first.GetProperty("alg").GetString()=="ES256"&&first.GetProperty("nonce").GetString()=="nonce-1"&&first.GetProperty("url").GetString()=="https://ca.test/new-acct");
            Assert(first.GetProperty("jwk").GetRawText()==account.Jwk&&!first.TryGetProperty("kid",out _)&&payload=="{\"termsOfServiceAgreed\":true}");
            var (later,empty)=Open(AcmeJws.Sign(account,"https://ca.test/order/1","nonce-2","https://ca.test/acct/7",[],true));
            Assert(later.GetProperty("kid").GetString()=="https://ca.test/acct/7"&&!later.TryGetProperty("jwk",out _)&&empty=="");
            return Task.CompletedTask;
        });
        await check("dns-01 TXT value is base64url SHA-256 of the key authorization",()=>
        {
            Assert(AcmeJws.Dns01Value(RfcToken,RfcThumbprint)==RfcDns01);
            foreach(var token in new[]{"short","with=padding+and/slash0000000","",new string('a',257)})Rejects<IOException>(()=>AcmeJws.Dns01Value(token,RfcThumbprint),"ACME_TOKEN_INVALID");
            return Task.CompletedTask;
        });
        await check("renewal is due from two thirds of the certificate's own lifetime",()=>
        {
            var start=new DateTimeOffset(2026,11,1,0,0,0,TimeSpan.Zero);
            Assert(!AcmeCertificateCheck.RenewalDue(start,start.AddDays(90),start.AddDays(60).AddSeconds(-1)));
            Assert(AcmeCertificateCheck.RenewalDue(start,start.AddDays(90),start.AddDays(60)));
            Assert(!AcmeCertificateCheck.RenewalDue(start,start.AddDays(45),start.AddDays(29)));Assert(AcmeCertificateCheck.RenewalDue(start,start.AddDays(45),start.AddDays(30)));
            Assert(!AcmeCertificateCheck.RenewalDue(start,start.AddDays(6),start));Assert(AcmeCertificateCheck.RenewalDue(start,start.AddDays(6),start.AddDays(4)));
            Assert(AcmeCertificateCheck.RenewalDue(start,start.AddDays(90),start.AddDays(200)));
            Rejects<ArgumentException>(()=>AcmeCertificateCheck.RenewalDue(start,start,start));
            return Task.CompletedTask;
        });
        await check("ACME names are lowercase host names only",()=>
        {
            AcmeName.Require(Name);
            foreach(var bad in new[]{"*."+Name,"K2ABCDEFGH7J.home.example.com","localhost","192.168.1.5","a..b.com","-a.example.com","a.example.com.","a_b.example.com",new string('a',64)+".com"})
                Rejects<ArgumentException>(()=>AcmeName.Require(bad),"ACME_NAME_INVALID");
            return Task.CompletedTask;
        });

        using var root=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rootCertificate=SelfSignedRoot(root,"CN=Yahad ACME test root");
        var trust=AcmeTrust.Custom([rootCertificate]);
        var now=DateTimeOffset.UtcNow;
        string Leaf(ECDsa subjectKey,Action<SubjectAlternativeNameBuilder> names,bool serverAuth=true,DateTimeOffset? from=null,DateTimeOffset? until=null,bool san=true)
        {
            var request=new CertificateRequest("CN=leaf",subjectKey,HashAlgorithmName.SHA256);
            if(san){var builder=new SubjectAlternativeNameBuilder();names(builder);request.CertificateExtensions.Add(builder.Build());}
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(serverAuth?"1.3.6.1.5.5.7.3.1":"1.3.6.1.5.5.7.3.2")],false));
            using var leaf=request.Create(rootCertificate,from??now.AddMinutes(-5),until??now.AddDays(6),[1,2,3,4,5,6,7,8]);
            return PemEncoding.WriteString("CERTIFICATE",leaf.RawData)+"\n"+PemEncoding.WriteString("CERTIFICATE",rootCertificate.RawData)+"\n";
        }
        await check("issued chain is accepted only with the single requested SAN, our key, current validity and server auth",()=>
        {
            using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);using var other=ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var good=AcmeCertificateCheck.Validate(Leaf(key,b=>b.AddDnsName(Name)),Name,key,trust,now);
            Assert(good.KeyPem.StartsWith("-----BEGIN PRIVATE KEY-----",StringComparison.Ordinal)&&good.CertificatePem.Split("BEGIN CERTIFICATE").Length==3);
            Assert(Math.Abs((good.NotAfter-now.AddDays(6)).TotalSeconds)<2&&!good.ToString().Contains("PRIVATE",StringComparison.Ordinal));
            using(var parsed=X509Certificate2.CreateFromPem(good.CertificatePem,good.KeyPem))Assert(parsed.HasPrivateKey);
            Rejects<IOException>(()=>AcmeCertificateCheck.Validate(Leaf(key,b=>b.AddDnsName("other.home.example.com")),Name,key,trust,now),"ACME_CERT_NAME");
            Rejects<IOException>(()=>AcmeCertificateCheck.Validate(Leaf(key,b=>{b.AddDnsName(Name);b.AddDnsName("extra.home.example.com");}),Name,key,trust,now),"ACME_CERT_NAME");
            Rejects<IOException>(()=>AcmeCertificateCheck.Validate(Leaf(key,b=>{b.AddDnsName(Name);b.AddIpAddress(IPAddress.Parse("192.168.1.5"));}),Name,key,trust,now),"ACME_CERT_NAME");
            Rejects<IOException>(()=>AcmeCertificateCheck.Validate(Leaf(key,b=>b.AddDnsName("*.home.example.com")),Name,key,trust,now),"ACME_CERT_NAME");
            Rejects<IOException>(()=>AcmeCertificateCheck.Validate(Leaf(key,_=>{},san:false),Name,key,trust,now),"ACME_CERT_NAME");
            Rejects<IOException>(()=>AcmeCertificateCheck.Validate(Leaf(other,b=>b.AddDnsName(Name)),Name,key,trust,now),"ACME_CERT_KEY");
            Rejects<IOException>(()=>AcmeCertificateCheck.Validate(Leaf(key,b=>b.AddDnsName(Name),from:now.AddDays(-10),until:now.AddMinutes(-1)),Name,key,trust,now),"ACME_CERT_VALIDITY");
            Rejects<IOException>(()=>AcmeCertificateCheck.Validate(Leaf(key,b=>b.AddDnsName(Name),from:now.AddMinutes(10)),Name,key,trust,now),"ACME_CERT_VALIDITY");
            Rejects<IOException>(()=>AcmeCertificateCheck.Validate(Leaf(key,b=>b.AddDnsName(Name),serverAuth:false),Name,key,trust,now),"ACME_CERT_USAGE");
            Rejects<ArgumentException>(()=>AcmeCertificateCheck.Validate(Leaf(key,b=>b.AddDnsName("*.home.example.com")),"*.home.example.com",key,trust,now),"ACME_NAME_INVALID");
            return Task.CompletedTask;
        });
        await check("issued chain must build to the configured trust and contain only certificates",()=>
        {
            using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);using var otherRoot=ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var stranger=SelfSignedRoot(otherRoot,"CN=Unrelated root");
            var pem=Leaf(key,b=>b.AddDnsName(Name));
            Rejects<IOException>(()=>AcmeCertificateCheck.Validate(pem,Name,key,AcmeTrust.Custom([stranger]),now),"ACME_CERT_CHAIN");
            // The system store does not know the synthetic root.
            Rejects<IOException>(()=>AcmeCertificateCheck.Validate(pem,Name,key,AcmeTrust.System,now),"ACME_CERT_CHAIN");
            Rejects<IOException>(()=>AcmeCertificateCheck.Validate(pem+key.ExportPkcs8PrivateKeyPem(),Name,key,trust,now),"ACME_CERT_PEM");
            Rejects<IOException>(()=>AcmeCertificateCheck.Validate("junk\n"+pem,Name,key,trust,now),"ACME_CERT_PEM");
            Rejects<IOException>(()=>AcmeCertificateCheck.Validate("",Name,key,trust,now),"ACME_CERT_PEM");
            Rejects<IOException>(()=>AcmeCertificateCheck.Validate(string.Concat(Enumerable.Repeat(pem,3)),Name,key,trust,now),"ACME_CERT_PEM");
            return Task.CompletedTask;
        });

        var credential=new HomeNameCredential("abcdefgh2345",Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));
        await check("name service calls send the bearer credential and exact JSON bodies",async()=>
        {
            var seen=new List<(string Method,string Path,string? Auth,string? Body)>();
            var handler=new FakeHandler(async request=>
            {
                seen.Add((request.Method.Method,request.RequestUri!.AbsolutePath,request.Headers.Authorization?.ToString(),request.Content is null?null:await request.Content.ReadAsStringAsync()));
                return request.RequestUri.AbsolutePath=="/base/v1/register"?Json(201,"{\"label\":\""+credential.Label+"\",\"secret\":\""+credential.Secret+"\"}"):new HttpResponseMessage(HttpStatusCode.NoContent);
            });
            using var client=new HomeNameServiceClient(new Uri("https://names.example.test/base"),handler);
            var registered=await client.RegisterAsync(CancellationToken.None);Assert(registered==credential&&!registered.ToString().Contains(credential.Secret,StringComparison.Ordinal));
            await client.SetAddressAsync(credential,"192.168.4.5",CancellationToken.None);
            await client.SetChallengeAsync(credential,[RfcDns01],CancellationToken.None);
            await client.ClearChallengeAsync(credential,CancellationToken.None);await client.DeleteNameAsync(credential,CancellationToken.None);
            var bearer="Bearer "+credential.Label+"."+credential.Secret;
            Assert(seen.Select(s=>s.Method+" "+s.Path).SequenceEqual(["POST /base/v1/register","PUT /base/v1/address","PUT /base/v1/challenge","DELETE /base/v1/challenge","DELETE /base/v1/name"]));
            Assert(seen[0].Auth is null&&seen.Skip(1).All(s=>s.Auth==bearer));
            Assert(seen[1].Body=="{\"ipv4\":\"192.168.4.5\"}"&&seen[2].Body=="{\"values\":[\""+RfcDns01+"\"]}"&&seen[3].Body is null);
            Rejects<ArgumentException>(()=>client.SetAddressAsync(credential,"8.8.8.8",CancellationToken.None),"HOME_NAME_ADDRESS_INVALID");
            Rejects<ArgumentException>(()=>client.SetChallengeAsync(credential,[],CancellationToken.None),"HOME_NAME_CHALLENGE_INVALID");
            Rejects<ArgumentException>(()=>client.SetChallengeAsync(credential,["a","b","c"],CancellationToken.None),"HOME_NAME_CHALLENGE_INVALID");
            Rejects<ArgumentException>(()=>new HomeNameServiceClient(new Uri("http://names.example.test/"),new FakeHandler(_=>throw new Exception())),"HOME_NAME_SERVICE_URL");
        });
        await check("name service maps 410 to a distinct gone error and never puts the secret in a message",async()=>
        {
            async Task<Exception> Fail(HttpResponseMessage answer,Func<HomeNameServiceClient,Task> call)
            {
                using var client=new HomeNameServiceClient(new Uri("https://names.example.test/"),new FakeHandler(_=>Task.FromResult(answer)));
                try{await call(client);}catch(Exception error){Assert(!error.ToString().Contains(credential.Secret,StringComparison.Ordinal));return error;}
                throw new Exception("expected failure");
            }
            Assert(await Fail(new HttpResponseMessage(HttpStatusCode.Gone),c=>c.SetAddressAsync(credential,"10.0.0.2",CancellationToken.None)) is HomeNameGoneException{Code:"HOME_NAME_GONE",Status:410});
            Assert(await Fail(new HttpResponseMessage(HttpStatusCode.Gone),c=>c.SetChallengeAsync(credential,[RfcDns01],CancellationToken.None)) is HomeNameGoneException);
            Assert(await Fail(new HttpResponseMessage(HttpStatusCode.Unauthorized),c=>c.ClearChallengeAsync(credential,CancellationToken.None)) is HomeNameServiceException{Code:"HOME_NAME_HTTP",Status:401} and not HomeNameGoneException);
            var redirect=new HttpResponseMessage(HttpStatusCode.Found);redirect.Headers.Location=new Uri("https://elsewhere.example.test/");
            Assert(await Fail(redirect,c=>c.ClearChallengeAsync(credential,CancellationToken.None)) is HomeNameServiceException{Code:"HOME_NAME_HTTP",Status:302});
            Assert(await Fail(Json(201,"{\"label\":\"abcdefgh2345\",\"secret\":\""+credential.Secret+"\",\"extra\":1}"),c=>c.RegisterAsync(CancellationToken.None)) is HomeNameServiceException{Code:"HOME_NAME_REGISTER_INVALID"});
            Assert(await Fail(Json(201,"{\"label\":\"ABC\",\"secret\":\""+credential.Secret+"\"}"),c=>c.RegisterAsync(CancellationToken.None)) is HomeNameServiceException{Code:"HOME_NAME_REGISTER_INVALID"});
            Assert(await Fail(Json(200,"{\"pad\":\""+new string('x',5000)+"\"}"),c=>c.RegisterAsync(CancellationToken.None)) is HomeNameServiceException{Code:"HOME_NAME_RESPONSE_BOUND"});
            Assert(await Fail(new HttpResponseMessage(HttpStatusCode.InternalServerError),c=>c.DeleteNameAsync(credential,CancellationToken.None)) is HomeNameServiceException{Status:500});
            // DELETE /v1/name is idempotent: an already-gone label is removed.
            using var gone=new HomeNameServiceClient(new Uri("https://names.example.test/"),new FakeHandler(_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone))));
            await gone.DeleteNameAsync(credential,CancellationToken.None);
        });

        var pebble=Environment.GetEnvironmentVariable("YAHAD_TEST_PEBBLE");
        if(string.IsNullOrEmpty(pebble)){Console.WriteLine("SKIP ACME issuance against Pebble (set YAHAD_TEST_PEBBLE to pebble.exe v2.10.3)");return;}
        await check("ACME issuance against Pebble: order, dns-01, finalize, download and validate",()=>PebbleAsync(pebble,Assert,RejectsAsync<IOException>));
    }

    private static async Task PebbleAsync(string pebble,Action<bool> Assert,Func<Func<Task>,string,Task> rejects)
    {
        var directory=Directory.CreateTempSubdirectory("yahad-pebble-").FullName;
        // Pebble's own TLS listener uses a throwaway CA; its issuing root comes from its management API.
        using var listenerCaKey=ECDsa.Create(ECCurve.NamedCurves.nistP256);using var listenerCa=SelfSignedRoot(listenerCaKey,"CN=Pebble listener test CA");
        using(var listenerKey=ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var request=new CertificateRequest("CN=127.0.0.1",listenerKey,HashAlgorithmName.SHA256);
            var names=new SubjectAlternativeNameBuilder();names.AddIpAddress(IPAddress.Loopback);names.AddDnsName("localhost");request.CertificateExtensions.Add(names.Build());
            using var listener=request.Create(listenerCa,DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddDays(1),[9,8,7,6]);
            File.WriteAllText(Path.Combine(directory,"cert.pem"),PemEncoding.WriteString("CERTIFICATE",listener.RawData)+"\n");
            File.WriteAllText(Path.Combine(directory,"key.pem"),listenerKey.ExportPkcs8PrivateKeyPem()+"\n");
        }
        int FreePort(){using var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();return ((IPEndPoint)socket.LocalEndpoint).Port;}
        int api=FreePort(),management=FreePort();
        File.WriteAllText(Path.Combine(directory,"pebble-config.json"),"{\"pebble\":{\"listenAddress\":\"127.0.0.1:"+api+"\",\"managementListenAddress\":\"127.0.0.1:"+management+"\","+
            "\"certificate\":\"cert.pem\",\"privateKey\":\"key.pem\",\"httpPort\":5002,\"tlsPort\":5001,\"ocspResponderURL\":\"\",\"externalAccountBindingRequired\":false,"+
            "\"retryAfter\":{\"authz\":1,\"order\":1},\"keyAlgorithm\":\"ecdsa\",\"profiles\":{\"default\":{\"description\":\"default\",\"validityPeriod\":7776000},\"shortlived\":{\"description\":\"short\",\"validityPeriod\":518400}}}}");
        var start=new ProcessStartInfo(pebble){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=directory,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add("-config");start.ArgumentList.Add("pebble-config.json");
        start.Environment["PEBBLE_VA_ALWAYS_VALID"]="1";start.Environment["PEBBLE_VA_NOSLEEP"]="1";
        // Reject a fifth of nonces so the badNonce retry path runs.
        start.Environment["PEBBLE_WFE_NONCEREJECT"]="20";
        using var process=Process.Start(start)!;var log=new StringBuilder();
        process.OutputDataReceived+=(_,e)=>{lock(log)log.AppendLine(e.Data);};process.ErrorDataReceived+=(_,e)=>{lock(log)log.AppendLine(e.Data);};
        process.BeginOutputReadLine();process.BeginErrorReadLine();
        HttpMessageHandler Handler()=>new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,UseCookies=false,SslOptions=new SslClientAuthenticationOptions{RemoteCertificateValidationCallback=(_,certificate,_,_)=>
        {
            if(certificate is null)return false;
            using var chain=new X509Chain();chain.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;chain.ChainPolicy.CustomTrustStore.Add(listenerCa);chain.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;
            return chain.Build(X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
        }}};
        try
        {
            using var probe=new HttpClient(Handler()){Timeout=TimeSpan.FromSeconds(5)};
            string rootPem="";
            for(var attempt=0;;attempt++)
            {
                try{rootPem=await probe.GetStringAsync("https://127.0.0.1:"+management+"/roots/0");break;}
                catch(HttpRequestException) when(attempt<100&&!process.HasExited){await Task.Delay(100);}
                catch(Exception error){throw new Exception("pebble did not start: "+error.Message+"\n"+log);}
            }
            using var pebbleRoot=X509Certificate2.CreateFromPem(rootPem);
            var options=new AcmeOptions{PropagationDelay=TimeSpan.Zero,PollInterval=TimeSpan.FromMilliseconds(200),Deadline=TimeSpan.FromMinutes(2),Trust=AcmeTrust.Custom([pebbleRoot]),Profile="shortlived"};
            var directoryUrl=new Uri("https://127.0.0.1:"+api+"/dir");
            using var account=AcmeAccountKey.Create();
            var published=new List<string>();var clears=0;
            Task Publish(IReadOnlyList<string> values,CancellationToken _){Assert(values.Count==1&&values[0].Length==43&&AcmeJws.IsBase64Url(values[0]));published.Add(values[0]);return Task.CompletedTask;}
            Task Clear(CancellationToken _){clears++;return Task.CompletedTask;}
            try
            {
                using(var client=new AcmeClient(directoryUrl,account,Handler()))
                {
                    var issued=await client.IssueAsync(Name,Publish,Clear,options,CancellationToken.None);
                    Assert(issued.ChallengeCleared&&published.Count==1&&clears==1);
                    var lifetime=issued.Certificate.NotAfter-issued.Certificate.NotBefore;Assert(lifetime>TimeSpan.FromDays(5.9)&&lifetime<=TimeSpan.FromDays(6));
                    Assert(!AcmeCertificateCheck.RenewalDue(issued.Certificate.NotBefore,issued.Certificate.NotAfter,DateTimeOffset.UtcNow));
                    using var served=X509Certificate2.CreateFromPem(issued.Certificate.CertificatePem,issued.Certificate.KeyPem);
                    Assert(served.HasPrivateKey&&served.GetNameInfo(X509NameType.DnsName,false)==Name);
                    Console.WriteLine("  pebble leaf "+served.Subject+" "+served.NotBefore.ToUniversalTime().ToString("O")+" .. "+served.NotAfter.ToUniversalTime().ToString("O"));
                }
                // The stored account key finds the same account again (newAccount answers 200), default profile.
                using(var again=AcmeAccountKey.Import(account.Export()))using(var client=new AcmeClient(directoryUrl,again,Handler()))
                {
                    var issued=await client.IssueAsync(Name,Publish,Clear,options,CancellationToken.None);
                    Assert(issued.Certificate.NotAfter>DateTimeOffset.UtcNow);
                }
                using(var client=new AcmeClient(directoryUrl,account,Handler()))
                    await rejects(()=>client.IssueAsync(Name,Publish,Clear,new AcmeOptions{Trust=options.Trust,Profile="missing"},CancellationToken.None),"ACME_PROFILE_UNSUPPORTED");
                // A chain that does not build to the configured trust is refused after download, and the TXT value is still cleared.
                var (publishedBefore,clearsBefore)=(published.Count,clears);
                using(var client=new AcmeClient(directoryUrl,account,Handler()))
                    await rejects(()=>client.IssueAsync(Name,Publish,Clear,new AcmeOptions{PropagationDelay=TimeSpan.Zero,PollInterval=TimeSpan.FromMilliseconds(200),Trust=AcmeTrust.System},CancellationToken.None),"ACME_CERT_CHAIN");
                Assert(clears-clearsBefore==published.Count-publishedBefore);
            }
            catch(Exception error) when(error is not OutOfMemoryException){throw new Exception(error.Message+"\n--- pebble log ---\n"+log,error);}
        }
        finally{if(!process.HasExited){process.Kill();await process.WaitForExitAsync();}try{Directory.Delete(directory,true);}catch(IOException){}}
    }

    private static X509Certificate2 SelfSignedRoot(ECDsa key,string subject)
    {
        var request=new CertificateRequest(subject,key,HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign|X509KeyUsageFlags.CrlSign,true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey,false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30),DateTimeOffset.UtcNow.AddDays(30));
    }
    private static HttpResponseMessage Json(int status,string body)=>new((HttpStatusCode)status){Content=new StringContent(body,Encoding.UTF8,"application/json")};
    private sealed class FakeHandler(Func<HttpRequestMessage,Task<HttpResponseMessage>> answer):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>answer(request);
    }
}

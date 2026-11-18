using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Yahad.Install;

/// <summary>Bounds and choices for one issuance. Every wait is bounded; <see cref="Deadline"/> bounds the whole run.</summary>
internal sealed class AcmeOptions
{
    internal TimeSpan Deadline{get;init;}=TimeSpan.FromMinutes(10);
    /// <summary>Wait between publishing the TXT value and asking the CA to validate it.</summary>
    internal TimeSpan PropagationDelay{get;init;}=TimeSpan.FromSeconds(30);
    /// <summary>The shortest wait between polls, also used when the CA sends no Retry-After.</summary>
    internal TimeSpan PollInterval{get;init;}=TimeSpan.FromSeconds(2);
    /// <summary>A longer Retry-After than this fails the run instead of waiting.</summary>
    internal TimeSpan MaximumRetryAfter{get;init;}=TimeSpan.FromSeconds(60);
    internal int MaximumPolls{get;init;}=30;
    internal TimeSpan RequestTimeout{get;init;}=TimeSpan.FromSeconds(30);
    /// <summary>An ACME profile name (draft-ietf-acme-profiles); null orders the CA's default.</summary>
    internal string? Profile{get;init;}
    internal AcmeTrust Trust{get;init;}=AcmeTrust.System;
}

/// <summary>A failed ACME step. <see cref="ProblemType"/> is the CA's RFC 7807 type when it sent one (e.g. rateLimited).</summary>
internal sealed class AcmeException(string code,string? problemType=null,int? status=null):IOException(problemType is null?code:code+" "+problemType)
{
    internal string Code{get;}=code;
    internal string? ProblemType{get;}=problemType;
    internal int? Status{get;}=status;
}

/// <summary>The certificate, and whether the clear-challenge callback completed (a stale TXT value is harmless but should be retried).</summary>
internal sealed record AcmeIssuance(AcmeCertificate Certificate,bool ChallengeCleared);

/// <summary>
/// Minimal RFC 8555 client: one dns identifier, dns-01, ES256 account key. It never
/// writes a file; the caller stores the account key and the returned certificate.
/// </summary>
internal sealed class AcmeClient:IDisposable
{
    internal const int MaximumResponseBytes=65536,MaximumBadNonceRetries=5;
    private const string ProblemPrefix="urn:ietf:params:acme:error:";
    private readonly HttpClient client;
    private readonly string directory;
    private readonly AcmeAccountKey account;
    private string? nonce,kid,newNonce,newAccount,newOrder;
    private TimeSpan requestTimeout=TimeSpan.FromSeconds(30);
    internal AcmeClient(Uri directory,AcmeAccountKey account):this(directory,account,
        new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None,ConnectTimeout=TimeSpan.FromSeconds(15)}){}
    /// <summary>For tests: a handler that trusts a test CA's TLS listener. The handler is owned (disposed) by the client.</summary>
    internal AcmeClient(Uri directory,AcmeAccountKey account,HttpMessageHandler handler)
    {
        this.directory=Https(directory.OriginalString);this.account=account;
        client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent","Yahad-ACME");
    }

    /// <summary>
    /// Orders, validates (dns-01) and downloads a certificate for <paramref name="name"/> with a new P-256 key.
    /// <paramref name="setChallenge"/> publishes the TXT values; <paramref name="clearChallenge"/> runs in
    /// finally once publishing was attempted, with its own 30-second bound, even after cancellation.
    /// </summary>
    internal async Task<AcmeIssuance> IssueAsync(string name,Func<IReadOnlyList<string>,CancellationToken,Task> setChallenge,
        Func<CancellationToken,Task> clearChallenge,AcmeOptions options,CancellationToken cancellation)
    {
        AcmeName.Require(name);
        if(options.Profile is {} profile&&(profile.Length is 0 or >64||!profile.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.')))throw new ArgumentException("ACME_PROFILE_INVALID");
        if(options.MaximumPolls<1||options.PollInterval<=TimeSpan.Zero||options.PropagationDelay<TimeSpan.Zero||options.RequestTimeout<=TimeSpan.Zero||options.Deadline<=TimeSpan.Zero)throw new ArgumentException("ACME_OPTIONS_INVALID");
        requestTimeout=options.RequestTimeout;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(options.Deadline);
        var published=false;var cleared=true;
        try
        {
            var token=deadline.Token;
            await DirectoryAsync(options.Profile,token);
            await AccountAsync(token);
            var created=await PostAsync(newOrder!,Payload(json=>
            {
                json.WriteStartArray("identifiers");json.WriteStartObject();json.WriteString("type","dns");json.WriteString("value",name);json.WriteEndObject();json.WriteEndArray();
                if(options.Profile is {} p)json.WriteString("profile",p);
            }),[201],token);
            var orderUrl=Https(created.Location??throw new AcmeException("ACME_ORDER_LOCATION"));
            var order=created.Json();RequireIdentifier(order,name,"identifiers");
            if(Text(order,"status") is "invalid")throw new AcmeException("ACME_ORDER_INVALID");
            var authorizations=Array(order,"authorizations");if(authorizations.Count!=1)throw new AcmeException("ACME_ORDER_AUTHORIZATIONS");
            var authorizationUrl=Https(authorizations[0].GetString()??"");var finalizeUrl=Https(Text(order,"finalize"));
            var authorization=(await PostAsync(authorizationUrl,null,[200],token)).Json();
            RequireIdentifier(authorization,name,null);
            if(authorization.TryGetProperty("wildcard",out var wildcard)&&wildcard.ValueKind!=JsonValueKind.False)throw new AcmeException("ACME_AUTHORIZATION_WILDCARD");
            switch(Text(authorization,"status"))
            {
                case "valid":break;
                case "pending":
                    var challenges=Array(authorization,"challenges").Where(c=>c.ValueKind==JsonValueKind.Object&&c.TryGetProperty("type",out var t)&&t.ValueKind==JsonValueKind.String&&t.GetString()=="dns-01").ToList();
                    if(challenges.Count!=1)throw new AcmeException("ACME_CHALLENGE_MISSING");
                    var challengeUrl=Https(Text(challenges[0],"url"));
                    var value=AcmeJws.Dns01Value(Text(challenges[0],"token"),account.Thumbprint);
                    published=true;await setChallenge([value],token);
                    await Task.Delay(options.PropagationDelay,token);
                    if(Text(challenges[0],"status") is "pending")await PostAsync(challengeUrl,"{}"u8.ToArray(),[200],token);
                    await PollAsync(authorizationUrl,"valid",["pending"],"ACME_AUTHORIZATION",options,token);
                    break;
                default:throw new AcmeException("ACME_AUTHORIZATION_STATUS");
            }
            order=await PollAsync(orderUrl,"ready",["pending"],"ACME_ORDER",options,token);
            using var certificateKey=ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request=new CertificateRequest(new X500DistinguishedName(""),certificateKey,HashAlgorithmName.SHA256);
            var san=new SubjectAlternativeNameBuilder();san.AddDnsName(name);
            // With an empty subject the SAN extension must be critical (RFC 5280 4.2.1.6).
            request.CertificateExtensions.Add(san.Build(critical:true));
            var csr=request.CreateSigningRequest();
            await PostAsync(finalizeUrl,Payload(json=>json.WriteString("csr",System.Buffers.Text.Base64Url.EncodeToString(csr))),[200],token);
            order=await PollAsync(orderUrl,"valid",["processing","ready"],"ACME_ORDER",options,token);
            var chain=await PostAsync(Https(Text(order,"certificate")),null,[200],token,accept:"application/pem-certificate-chain");
            if(chain.MediaType!="application/pem-certificate-chain")throw new AcmeException("ACME_CERTIFICATE_TYPE");
            string pem;try{pem=new System.Text.UTF8Encoding(false,true).GetString(chain.Body);}catch(System.Text.DecoderFallbackException){throw new AcmeException("ACME_CERTIFICATE_TYPE");}
            var certificate=AcmeCertificateCheck.Validate(pem,name,certificateKey,options.Trust,DateTimeOffset.UtcNow);
            if(published){cleared=await ClearAsync(clearChallenge);published=false;}
            return new(certificate,cleared);
        }
        catch(OperationCanceledException) when(deadline.IsCancellationRequested&&!cancellation.IsCancellationRequested){throw new AcmeException("ACME_DEADLINE");}
        finally{if(published)await ClearAsync(clearChallenge);}
    }
    private static async Task<bool> ClearAsync(Func<CancellationToken,Task> clearChallenge)
    {
        using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // A failed clear never hides the issuance's own outcome; the caller learns of it from AcmeIssuance.
        try{await clearChallenge(bound.Token);return true;}catch(Exception error) when(error is not OutOfMemoryException){return false;}
    }

    private async Task DirectoryAsync(string? profile,CancellationToken cancellation)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,directory);
        var response=await SendAsync(request,cancellation);
        if(response.Status!=200)throw Problem(response,"ACME_DIRECTORY");
        var json=response.Json();
        newNonce=Https(Text(json,"newNonce"));newAccount=Https(Text(json,"newAccount"));newOrder=Https(Text(json,"newOrder"));
        if(profile is not null&&!(json.TryGetProperty("meta",out var meta)&&meta.ValueKind==JsonValueKind.Object&&meta.TryGetProperty("profiles",out var profiles)&&profiles.ValueKind==JsonValueKind.Object&&profiles.TryGetProperty(profile,out _)))
            throw new AcmeException("ACME_PROFILE_UNSUPPORTED");
    }
    private async Task AccountAsync(CancellationToken cancellation)
    {
        // Idempotent: an existing account for this key answers 200 with its URL. No contact is sent.
        var response=await PostAsync(newAccount!,Payload(json=>json.WriteBoolean("termsOfServiceAgreed",true)),[200,201],cancellation);
        var location=Https(response.Location??throw new AcmeException("ACME_ACCOUNT_LOCATION"));
        if(Text(response.Json(),"status")!="valid")throw new AcmeException("ACME_ACCOUNT_STATUS");
        kid=location;
    }
    private async Task<string> NonceAsync(CancellationToken cancellation)
    {
        if(nonce is {} saved){nonce=null;return saved;}
        using var request=new HttpRequestMessage(HttpMethod.Head,newNonce);
        var response=await SendAsync(request,cancellation);
        if(response.Status is not(200 or 204))throw Problem(response,"ACME_NONCE");
        if(nonce is not {} fresh)throw new AcmeException("ACME_NONCE_MISSING");
        nonce=null;return fresh;
    }
    private async Task<JsonElement> PollAsync(string url,string done,string[] waiting,string code,AcmeOptions options,CancellationToken cancellation)
    {
        for(var poll=0;poll<options.MaximumPolls;poll++)
        {
            var response=await PostAsync(url,null,[200],cancellation);var json=response.Json();var status=Text(json,"status");
            if(status==done)return json;
            if(!waiting.Contains(status))throw new AcmeException(code+"_STATUS",ChallengeProblem(json));
            var wait=response.RetryAfter??options.PollInterval;
            if(wait>options.MaximumRetryAfter)throw new AcmeException("ACME_RETRY_AFTER_BOUND");
            await Task.Delay(wait<options.PollInterval?options.PollInterval:wait,cancellation);
        }
        throw new AcmeException(code+"_POLL_LIMIT");
    }
    // The first challenge error type in an authorization, or an order's own error type.
    private static string? ChallengeProblem(JsonElement json)
    {
        static string? Type(JsonElement holder)=>holder.TryGetProperty("error",out var error)&&error.ValueKind==JsonValueKind.Object&&error.TryGetProperty("type",out var type)&&type.ValueKind==JsonValueKind.String?type.GetString():null;
        if(Type(json) is {} own)return own;
        if(json.TryGetProperty("challenges",out var challenges)&&challenges.ValueKind==JsonValueKind.Array)
            foreach(var challenge in challenges.EnumerateArray())if(challenge.ValueKind==JsonValueKind.Object&&Type(challenge) is {} type)return type;
        return null;
    }

    // A JWS POST; a null payload is POST-as-GET. badNonce is retried with the nonce the error carried
    // (or a fresh one when it carried none).
    private async Task<Response> PostAsync(string url,byte[]? payload,int[] expected,CancellationToken cancellation,string accept="application/json")
    {
        for(var attempt=0;;attempt++)
        {
            var body=AcmeJws.Sign(account,url,await NonceAsync(cancellation),url==newAccount?null:kid??throw new InvalidOperationException("ACME_NO_ACCOUNT"),payload??[],payload is null);
            using var request=new HttpRequestMessage(HttpMethod.Post,url){Content=new ByteArrayContent(body)};
            request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/jose+json");request.Headers.Accept.ParseAdd(accept);
            var response=await SendAsync(request,cancellation);
            if(expected.Contains(response.Status))return response;
            if(response.ProblemType==ProblemPrefix+"badNonce"&&attempt<MaximumBadNonceRetries)continue;
            throw Problem(response,"ACME_HTTP");
        }
    }
    private async Task<Response> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(requestTimeout);
        try
        {
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            nonce=response.Headers.TryGetValues("Replay-Nonce",out var values)&&values.ToList() is [var value]&&AcmeJws.IsBase64Url(value)&&value.Length<=256?value:null;
            if(response.Content.Headers.ContentEncoding.Count!=0)throw new AcmeException("ACME_CONTENT_ENCODING");
            if(response.Content.Headers.ContentLength>MaximumResponseBytes)throw new AcmeException("ACME_RESPONSE_BOUND");
            await using var input=await response.Content.ReadAsStreamAsync(timeout.Token);using var output=new MemoryStream();
            var chunk=new byte[16384];int n;
            while((n=await input.ReadAsync(chunk,timeout.Token))>0){if(output.Length+n>MaximumResponseBytes)throw new AcmeException("ACME_RESPONSE_BOUND");output.Write(chunk,0,n);}
            var retry=response.Headers.RetryAfter is {} after?after.Delta??(after.Date is {} date?date-DateTimeOffset.UtcNow:null):null;
            var media=response.Content.Headers.ContentType?.MediaType;var status=(int)response.StatusCode;
            string? problem=null;
            if(status>=400&&media=="application/problem+json")
                try{using var document=JsonDocument.Parse(output.ToArray(),DocumentOptions);if(document.RootElement.ValueKind==JsonValueKind.Object&&document.RootElement.TryGetProperty("type",out var type)&&type.ValueKind==JsonValueKind.String)problem=type.GetString();}
                catch(JsonException){}
            return new(status,output.ToArray(),response.Headers.Location?.OriginalString,retry<TimeSpan.Zero?TimeSpan.Zero:retry,media,problem);
        }
        catch(OperationCanceledException) when(timeout.IsCancellationRequested&&!cancellation.IsCancellationRequested){throw new AcmeException("ACME_TIMEOUT");}
    }
    private static AcmeException Problem(Response response,string code)=>new(code,response.ProblemType is {} type&&type.Length<=128?type:null,response.Status);

    private static readonly JsonDocumentOptions DocumentOptions=new(){MaxDepth=16,AllowDuplicateProperties=false};
    private sealed record Response(int Status,byte[] Body,string? Location,TimeSpan? RetryAfter,string? MediaType,string? ProblemType)
    {
        internal JsonElement Json()
        {
            if(MediaType!="application/json")throw new AcmeException("ACME_RESPONSE_TYPE",null,Status);
            try{using var document=JsonDocument.Parse(Body,DocumentOptions);if(document.RootElement.ValueKind!=JsonValueKind.Object)throw new AcmeException("ACME_RESPONSE_INVALID");return document.RootElement.Clone();}
            catch(JsonException){throw new AcmeException("ACME_RESPONSE_INVALID");}
        }
    }
    private static byte[] Payload(Action<Utf8JsonWriter> write)
    {
        using var buffer=new MemoryStream();
        using(var json=new Utf8JsonWriter(buffer)){json.WriteStartObject();write(json);json.WriteEndObject();}
        return buffer.ToArray();
    }
    private static string Text(JsonElement json,string name)
        =>json.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String&&value.GetString() is {Length:>0 and <=2048} text?text:throw new AcmeException("ACME_RESPONSE_INVALID");
    private static List<JsonElement> Array(JsonElement json,string name)
        =>json.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.Array&&value.GetArrayLength()<=16?value.EnumerateArray().ToList():throw new AcmeException("ACME_RESPONSE_INVALID");
    // An order lists identifiers; an authorization has one identifier. Either must be exactly ours.
    private static void RequireIdentifier(JsonElement json,string name,string? listName)
    {
        var identifiers=listName is null?(json.TryGetProperty("identifier",out var one)?[one]:[]):Array(json,listName);
        if(identifiers is not [var identifier]||identifier.ValueKind!=JsonValueKind.Object||Text(identifier,"type")!="dns"||Text(identifier,"value")!=name)throw new AcmeException("ACME_IDENTIFIER_MISMATCH");
    }
    // Every URL the client follows, including those the CA hands out, is absolute HTTPS.
    private static string Https(string url)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!=Uri.UriSchemeHttps||uri.UserInfo.Length!=0||uri.Fragment.Length!=0)throw new AcmeException("ACME_URL_INVALID");
        return url;
    }
    public void Dispose()=>client.Dispose();
}

using System.Net;
using System.Net.Http.Headers;

namespace Yahad.Install;

/// <summary>The name service credential (docs/HOME-NAME.md 1). The secret authenticates every call; it is never logged or put in a message.</summary>
internal sealed record HomeNameCredential(string Label,string Secret)
{
    public override string ToString()=>"HomeNameCredential { Label = "+Label+" }";
    /// <summary>Label: 12 base32 characters (lowercase). Secret: 256 bits as 43 base64url characters.</summary>
    internal static bool IsValid(HomeNameCredential value)
        =>value.Label.Length==12&&value.Label.All(c=>c is >='a' and <='z' or >='2' and <='7')&&value.Secret.Length==43&&AcmeJws.IsBase64Url(value.Secret);
}
internal sealed record HomeNameAddress(string Ipv4);
internal sealed record HomeNameChallenge(string[] Values);

/// <summary>A failed name service call. The message is the code alone and never carries the credential.</summary>
internal class HomeNameServiceException(string code,int? status=null):IOException(code)
{
    internal string Code{get;}=code;
    internal int? Status{get;}=status;
}
/// <summary>410: the label is tombstoned or expired. The caller discards the credential and registers again.</summary>
internal sealed class HomeNameGoneException():HomeNameServiceException("HOME_NAME_GONE",410);

/// <summary>Client of the name service Worker. HTTPS only, no proxy, no redirects, bounded time and response size.</summary>
internal sealed class HomeNameServiceClient:IDisposable
{
    internal const int MaximumResponseBytes=4096;
    internal static readonly TimeSpan RequestTimeout=TimeSpan.FromSeconds(15);
    private readonly HttpClient client;
    private readonly Uri service;
    internal HomeNameServiceClient(Uri service):this(service,
        new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None,ConnectTimeout=TimeSpan.FromSeconds(10)}){}
    /// <summary>For tests. The handler is owned (disposed) by the client.</summary>
    internal HomeNameServiceClient(Uri service,HttpMessageHandler handler)
    {
        if(!service.IsAbsoluteUri||service.Scheme!=Uri.UriSchemeHttps||service.UserInfo.Length!=0||service.Query.Length!=0||service.Fragment.Length!=0)throw new ArgumentException("HOME_NAME_SERVICE_URL");
        this.service=service.AbsolutePath.EndsWith('/')?service:new Uri(service.AbsoluteUri+"/");
        client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent","Yahad-HomeName");
    }
    internal async Task<HomeNameCredential> RegisterAsync(CancellationToken cancellation)
    {
        var (status,body,media)=await SendAsync(HttpMethod.Post,"v1/register",null,null,cancellation);
        if(status is not(200 or 201)||media!="application/json")throw new HomeNameServiceException("HOME_NAME_REGISTER",status);
        HomeNameCredential credential;
        try{credential=NativeJson.ReadCamel(body,NativeCamelJsonContext.Default.HomeNameCredential);}
        catch(Exception error) when(error is System.Text.Json.JsonException or IOException){throw new HomeNameServiceException("HOME_NAME_REGISTER_INVALID",status);}
        if(!HomeNameCredential.IsValid(credential))throw new HomeNameServiceException("HOME_NAME_REGISTER_INVALID",status);
        return credential;
    }
    /// <summary>Points the label's A record at a private (RFC 1918) IPv4 address.</summary>
    internal Task SetAddressAsync(HomeNameCredential credential,string ipv4,CancellationToken cancellation)
    {
        if(!NetworkPlan.IsPrivateLanV4(ipv4))throw new ArgumentException("HOME_NAME_ADDRESS_INVALID");
        return ExpectAsync(HttpMethod.Put,"v1/address",credential,NativeJson.WriteCamel(new HomeNameAddress(ipv4),NativeCamelJsonContext.Default.HomeNameAddress),cancellation);
    }
    /// <summary>Replaces the label's _acme-challenge TXT values (1-2 dns-01 digests).</summary>
    internal Task SetChallengeAsync(HomeNameCredential credential,IReadOnlyList<string> values,CancellationToken cancellation)
    {
        if(values.Count is < 1 or > 2||values.Any(v=>v.Length!=43||!AcmeJws.IsBase64Url(v)))throw new ArgumentException("HOME_NAME_CHALLENGE_INVALID");
        return ExpectAsync(HttpMethod.Put,"v1/challenge",credential,NativeJson.WriteCamel(new HomeNameChallenge([..values]),NativeCamelJsonContext.Default.HomeNameChallenge),cancellation);
    }
    internal Task ClearChallengeAsync(HomeNameCredential credential,CancellationToken cancellation)=>ExpectAsync(HttpMethod.Delete,"v1/challenge",credential,null,cancellation);
    /// <summary>Removes the name. Idempotent: a label that is already gone (410) counts as removed.</summary>
    internal async Task DeleteNameAsync(HomeNameCredential credential,CancellationToken cancellation)
    {try{await ExpectAsync(HttpMethod.Delete,"v1/name",credential,null,cancellation);}catch(HomeNameGoneException){}}

    private async Task ExpectAsync(HttpMethod method,string path,HomeNameCredential credential,byte[]? json,CancellationToken cancellation)
    {
        var (status,_,_)=await SendAsync(method,path,credential,json,cancellation);
        if(status is not(200 or 204))throw new HomeNameServiceException("HOME_NAME_HTTP",status);
    }
    private async Task<(int Status,byte[] Body,string? Media)> SendAsync(HttpMethod method,string path,HomeNameCredential? credential,byte[]? json,CancellationToken cancellation)
    {
        if(credential is not null&&!HomeNameCredential.IsValid(credential))throw new ArgumentException("HOME_NAME_CREDENTIAL_INVALID");
        using var request=new HttpRequestMessage(method,new Uri(service,path));
        if(credential is not null)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",credential.Label+"."+credential.Secret);
        if(json is not null){request.Content=new ByteArrayContent(json);request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/json");}
        request.Headers.Accept.ParseAdd("application/json");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            var status=(int)response.StatusCode;
            if(status==410)throw new HomeNameGoneException();
            if(response.Content.Headers.ContentEncoding.Count!=0||response.Content.Headers.ContentLength>MaximumResponseBytes)throw new HomeNameServiceException("HOME_NAME_RESPONSE_BOUND",status);
            await using var input=await response.Content.ReadAsStreamAsync(timeout.Token);using var output=new MemoryStream();
            var chunk=new byte[4096];int n;
            while((n=await input.ReadAsync(chunk,timeout.Token))>0){if(output.Length+n>MaximumResponseBytes)throw new HomeNameServiceException("HOME_NAME_RESPONSE_BOUND",status);output.Write(chunk,0,n);}
            return (status,output.ToArray(),response.Content.Headers.ContentType?.MediaType);
        }
        catch(OperationCanceledException) when(timeout.IsCancellationRequested&&!cancellation.IsCancellationRequested){throw new HomeNameServiceException("HOME_NAME_TIMEOUT");}
        // The transport message names the host at most; it never contains request headers.
        catch(HttpRequestException error){throw new HomeNameServiceException("HOME_NAME_UNREACHABLE",error.StatusCode is {} code?(int)code:null);}
    }
    public void Dispose()=>client.Dispose();
}

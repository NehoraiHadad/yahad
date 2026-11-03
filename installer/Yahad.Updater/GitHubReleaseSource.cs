using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
namespace Yahad.Updater;

internal sealed record GitHubAsset(string Name,long Size,string BrowserDownloadUrl,string? Digest=null);
internal sealed record GitHubRelease(string TagName,bool Draft,bool Prerelease,GitHubAsset[] Assets);
// The fixed public source. No setting, argument or environment value can point it
// anywhere else, and the release JSON is the only thing parsed unauthenticated.
internal sealed class GitHubReleaseSource:IDisposable
{
    internal const string Latest="https://api.github.com/repos/NehoraiHadad/yahad/releases/latest";
    internal const int MaximumListBytes=262144,MaximumRedirects=5;
    private static readonly HashSet<string> Hosts=new(StringComparer.Ordinal){"api.github.com","github.com","objects.githubusercontent.com","release-assets.githubusercontent.com"};
    private static readonly JsonSerializerOptions ListOptions=new(){MaxDepth=8,PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true};
    private readonly HttpClient client;
    internal GitHubReleaseSource():this(new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None,ConnectTimeout=TimeSpan.FromSeconds(30)}){}
    internal GitHubReleaseSource(HttpMessageHandler handler){client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};}
    internal async Task<GitHubRelease> LatestAsync(CancellationToken cancellation)
        =>ParseRelease(await GetBytesAsync(Latest,MaximumListBytes,"application/vnd.github+json",cancellation));
    internal static GitHubRelease ParseRelease(byte[] bytes)
    {
        if(bytes.Length>MaximumListBytes)throw new IOException("GITHUB_RESPONSE_BOUND");GitHubRelease? value;
        try{value=JsonSerializer.Deserialize<GitHubRelease>(bytes,ListOptions);}catch(JsonException){throw new IOException("GITHUB_LIST_INVALID");}
        if(value is null||value.Assets.Length>1000)throw new IOException("GITHUB_LIST_INVALID");
        return value;
    }
    internal Task<byte[]> GetAssetAsync(string url,int maximum,CancellationToken cancellation)=>GetBytesAsync(url,maximum,"application/octet-stream",cancellation);
    private async Task<byte[]> GetBytesAsync(string url,int maximum,string accept,CancellationToken cancellation)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var response=await GetAsync(url,accept,deadline.Token);
        if(response.Content.Headers.ContentLength>maximum)throw new IOException("GITHUB_RESPONSE_BOUND");
        await using var input=await response.Content.ReadAsStreamAsync(deadline.Token);using var output=new MemoryStream();
        var chunk=new byte[65536];int n;
        while((n=await input.ReadAsync(chunk,deadline.Token))>0){if(output.Length+n>maximum)throw new IOException("GITHUB_RESPONSE_BOUND");output.Write(chunk,0,n);}
        return output.ToArray();
    }
    // Reads no byte past the signed size and writes only what it hashed.
    internal async Task DownloadAsync(string url,Stream output,long size,string sha256,CancellationToken cancellation)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromHours(2));
        using var response=await GetAsync(url,"application/octet-stream",deadline.Token);
        if(response.Content.Headers.ContentLength is {} length&&length!=size)throw new IOException("RELEASE_SETUP_SIZE");
        await using var input=await response.Content.ReadAsStreamAsync(deadline.Token);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var chunk=new byte[1048576];long total=0;int n;
        while((n=await input.ReadAsync(chunk,deadline.Token))>0)
        {if((total+=n)>size)throw new IOException("RELEASE_SETUP_SIZE");hash.AppendData(chunk,0,n);await output.WriteAsync(chunk.AsMemory(0,n),deadline.Token);}
        if(total!=size)throw new IOException("RELEASE_SETUP_SIZE");
        if(Convert.ToHexStringLower(hash.GetHashAndReset())!=sha256)throw new IOException("RELEASE_SETUP_HASH");
    }
    private async Task<HttpResponseMessage> GetAsync(string url,string accept,CancellationToken cancellation)
    {
        var next=new Uri(url);
        for(var count=0;count<=MaximumRedirects;count++)
        {
            SafeUrl(next);using var request=new HttpRequestMessage(HttpMethod.Get,next);
            request.Headers.TryAddWithoutValidation("User-Agent","Yahad-Updater");request.Headers.TryAddWithoutValidation("Accept",accept);request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version","2022-11-28");
            var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellation);
            if(response.StatusCode==HttpStatusCode.OK)
            {if(response.Content.Headers.ContentEncoding.Count!=0){response.Dispose();throw new IOException("GITHUB_CONTENT_ENCODING");}return response;}
            var status=(int)response.StatusCode;var location=response.Headers.Location;response.Dispose();
            if(status is not(301 or 302 or 303 or 307 or 308)||location is null)throw new HttpRequestException("GITHUB_HTTP");
            next=new Uri(next,location);
        }
        throw new IOException("GITHUB_REDIRECT_LIMIT");
    }
    internal static void SafeUrl(Uri uri)
    {if(!uri.IsAbsoluteUri||uri.Scheme!="https"||!Hosts.Contains(uri.IdnHost)||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.Fragment.Length!=0)throw new IOException("GITHUB_URL");}
    public void Dispose()=>client.Dispose();
}

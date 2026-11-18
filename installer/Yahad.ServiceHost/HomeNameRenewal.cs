using System.Net.Security;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Yahad.Install;

namespace Yahad.Service;

// home-name-owner.json (docs/HOME-NAME.md 3): Setup's record of the name transaction, beside
// owner.json under the install ACL. The LAN service only reads it. Label and Name are empty only
// when the transaction failed or was removed before a label existed.
internal sealed record HomeNameOwner(int Format,string Phase,string Label,string Name,string? Failure);
internal static partial class HomeNameOwnerRecord
{
    internal const string FileName="home-name-owner.json";
    internal const string Registered="registered",Issued="issued",Configured="configured",Active="active",Failed="failed",Removed="removed";
    private static readonly string[] Phases=[Registered,Issued,Configured,Active,Failed,Removed];
    internal static byte[] Encode(HomeNameOwner value)=>System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Require(value),ServiceHostJsonContext.Default.HomeNameOwner);
    internal static HomeNameOwner Parse(byte[] bytes)
    {
        if(bytes.Length is <1 or >4096)throw new IOException("HOME_NAME_OWNER_BOUND");
        return Require(NativeJson.Read(bytes,ServiceHostJsonContext.Default.HomeNameOwner));
    }
    internal static bool IsFailureCode(string? code)=>code is not null&&FailureCode().IsMatch(code);
    private static HomeNameOwner Require(HomeNameOwner value)
    {
        if(value.Format!=1||value.Phase is null||!Phases.Contains(value.Phase)||value.Label is null||value.Name is null)throw new IOException("HOME_NAME_OWNER_FIELDS");
        if(value.Label.Length==0){if(value.Name.Length!=0||value.Phase is not(Failed or Removed))throw new IOException("HOME_NAME_OWNER_FIELDS");}
        else if(HomeNameRelease.NameFor(value.Label)!=value.Name)throw new IOException("HOME_NAME_OWNER_FIELDS");
        if(value.Phase==Failed?!IsFailureCode(value.Failure):value.Failure is not null)throw new IOException("HOME_NAME_OWNER_FIELDS");
        return value;
    }
    [GeneratedRegex("^[A-Z0-9_]{1,64}$",RegexOptions.CultureInvariant)]private static partial Regex FailureCode();
}

/// <summary>What the LAN service does for the active name's certificate (docs/HOME-NAME.md 3, renewal). Tests replace it.</summary>
internal interface ILanHomeNameRenewal
{
    /// <summary>Whether the active name's certificate is due now. False without an active name.</summary>
    bool Due(DateTimeOffset now);
    /// <summary>A new certificate into next/, both files flushed and checked. Never touches live/ or the running proxy.</summary>
    Task StageAsync(CancellationToken cancellation);
    void Swap();
    void SwapBack();
    /// <summary>Whether the running proxy serves the live leaf for the name.</summary>
    Task<bool> ServesLiveAsync(CancellationToken cancellation);
}

internal sealed class WindowsLanHomeNameRenewal(Func<string?> address,IHomeNameRemote remote):ILanHomeNameRenewal
{
    private string? name;
    private static HomeNameStore Store=>new(LanProxyPaths.Name);
    public bool Due(DateTimeOffset now)
    {
        name=null;
        if(!HomeNameRelease.Configured||!File.Exists(LanProxyPaths.HomeNameOwner))return false;
        var owner=HomeNameOwnerRecord.Parse(NativeInstallFiles.ReadRecord(LanProxyPaths.HomeNameOwner,4096));
        // A missing live certificate is Setup's to repair: without one there is nothing to swap back to.
        if(owner.Phase!=HomeNameOwnerRecord.Active||Store.Read(Store.Live,owner.Name) is not {} live)return false;
        if(!AcmeCertificateCheck.RenewalDue(live.NotBefore,live.NotAfter,now))return false;
        name=owner.Name;return true;
    }
    public async Task StageAsync(CancellationToken cancellation)
    {
        var target=name??throw new InvalidOperationException("HOME_NAME_NOT_DUE");var store=Store;
        // 410 here leaves the name as it is: a new label needs Setup, which owns the Caddyfile and the records.
        var credential=store.ReadCredential()??throw new IOException("HOME_NAME_CREDENTIAL_MISSING");
        if(HomeNameRelease.NameFor(credential.Label)!=target)throw new IOException("HOME_NAME_CREDENTIAL_MISMATCH");
        using(var account=store.Account())store.Stage(await remote.IssueAsync(credential,target,account,cancellation));
        if(store.Read(store.Next,target) is null)throw new IOException("HOME_NAME_STAGE_INCOMPLETE");
    }
    public void Swap()=>Store.Swap();
    public void SwapBack()=>Store.SwapBack();
    // The proxy's own listener on the selected address, asked for the name: the leaf it presents
    // must be live's. Trust is not the question here; the leaf was checked when it was issued.
    public async Task<bool> ServesLiveAsync(CancellationToken cancellation)
    {
        if(name is not {} target||address() is not {} ip||Store.Read(Store.Live,target)?.LeafSha256 is not {} expected)return false;
        while(true)
        {
            try
            {
                using var tcp=new TcpClient();await tcp.ConnectAsync(ip,LanDeployment.HttpsPort,cancellation);
                string? presented=null;
                await using var tls=new SslStream(tcp.GetStream(),false);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost=target,
                    RemoteCertificateValidationCallback=(_,certificate,_,_)=>{presented=certificate?.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);return true;}},cancellation);
                if(presented==expected)return true;
            }
            catch(Exception error)when(error is SocketException or IOException or System.Security.Authentication.AuthenticationException){}
            await Task.Delay(250,cancellation);
        }
    }
}

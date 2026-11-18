using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yahad.Install;

// The home name's build-time inputs (docs/HOME-NAME.md, Configuration): installer/Directory.Build.props
// compiles them into every image as assembly metadata, like InfrastructureRelease. Both empty: the
// option is not offered and nothing below is used.
internal static class HomeNameRelease
{
    internal static readonly Uri AcmeDirectory=new("https://acme-v02.api.letsencrypt.org/directory");
    internal const string AcmeProfile="classic";
    private static readonly (string Service,string Zone) Configuration=Require(Metadata("YahadHomeNameService"),Metadata("YahadHomeNameZone"));
    internal static string Service=>Configuration.Service;
    internal static string Zone=>Configuration.Zone;
    internal static bool Configured=>Service.Length!=0;
    /// <summary>A label the name service hands out: 12 lowercase base32 characters.</summary>
    internal static bool IsLabel(string? label)=>label is {Length:12}&&label.All(c=>c is >='a' and <='z' or >='2' and <='7');
    internal static string NameFor(string label)=>Configured&&IsLabel(label)?label+"."+Zone:throw new IOException("HOME_NAME_INVALID");
    /// <summary>Exactly one label of this build's zone.</summary>
    internal static bool IsName(string? name)=>Configured&&name is not null&&name.EndsWith("."+Zone,StringComparison.Ordinal)&&IsLabel(name[..^(Zone.Length+1)]);
    // Both empty, or an HTTPS origin and a host name; anything else is a broken build input.
    internal static (string Service,string Zone) Require(string service,string zone)
    {
        if(service.Length==0&&zone.Length==0)return("","");
        if(!Uri.TryCreate(service,UriKind.Absolute,out var uri)||uri.Scheme!=Uri.UriSchemeHttps||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0||
            uri.AbsolutePath!="/"||service.TrimEnd('/')!=uri.GetLeftPart(UriPartial.Authority))throw new InvalidOperationException("HOME_NAME_CONFIGURATION");
        try{AcmeName.Require(zone);}catch(ArgumentException){throw new InvalidOperationException("HOME_NAME_CONFIGURATION");}
        return(service,zone);
    }
    private static string Metadata(string key)
        =>typeof(HomeNameRelease).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a=>a.Key==key) is {} found?found.Value??"":throw new IOException("INFRASTRUCTURE_IDENTITY_MISSING");
}

internal sealed record HomeNameAccount(int Format,string Key)
{public override string ToString()=>"HomeNameAccount { Format = "+Format+" }";}
internal sealed record HomeNameSwapJournal(int Format);
/// <summary>A stored certificate that passed its checks: the validity of its leaf and the leaf's SHA-256.</summary>
internal sealed record HomeNameCertificate(DateTimeOffset NotBefore,DateTimeOffset NotAfter,string LeafSha256);

/// <summary>
/// The name/ subtree (docs/HOME-NAME.md 3), the only one the LAN service may write: credential.json,
/// account.json, live/{cert.pem,key.pem}, next/ for staging and swap.json, the journal of a swap.
/// Nothing here follows a link: the service could have left one.
/// </summary>
internal sealed class HomeNameStore(string root)
{
    internal const int MaximumPem=65536;
    internal string Root=>root;
    internal string CredentialFile=>Path.Combine(root,"credential.json");
    internal string AccountFile=>Path.Combine(root,"account.json");
    internal string JournalFile=>Path.Combine(root,"swap.json");
    internal string Live=>Path.Combine(root,"live");
    internal string Next=>Path.Combine(root,"next");
    internal string Prev=>Path.Combine(root,"prev");
    // A swap back first renames the certificate it gives up, so live is never half deleted.
    private string Failed=>Path.Combine(root,"failed");
    internal static string CertificateFile(string directory)=>Path.Combine(directory,"cert.pem");
    internal static string KeyFile(string directory)=>Path.Combine(directory,"key.pem");

    internal void EnsureRoot(string? acl){if(!Directory.Exists(root))NativeInstallFiles.NewDirectory(root,acl);}
    internal HomeNameCredential? ReadCredential()
    {
        if(!File.Exists(CredentialFile))return null;
        HomeNameCredential value;
        try{value=NativeJson.ReadCamel(NativeInstallFiles.ReadRecord(CredentialFile,1024),NativeCamelJsonContext.Default.HomeNameCredential);}
        catch(JsonException){throw new IOException("HOME_NAME_CREDENTIAL_INVALID");}
        return HomeNameCredential.IsValid(value)?value:throw new IOException("HOME_NAME_CREDENTIAL_INVALID");
    }
    internal void WriteCredential(HomeNameCredential credential)
    {
        if(!HomeNameCredential.IsValid(credential))throw new ArgumentException("HOME_NAME_CREDENTIAL_INVALID");
        NativeInstallFiles.Publish(CredentialFile,NativeJson.WriteCamel(credential,NativeCamelJsonContext.Default.HomeNameCredential));
    }
    internal void DiscardCredential(){if(File.Exists(CredentialFile))WindowsFileBoundary.DeleteNoFollow(CredentialFile,false);}
    /// <summary>The ACME account key, created and stored on first use.</summary>
    internal AcmeAccountKey Account()
    {
        if(File.Exists(AccountFile))
        {
            HomeNameAccount value;
            try{value=NativeJson.ReadCamel(NativeInstallFiles.ReadRecord(AccountFile,4096),NativeCamelJsonContext.Default.HomeNameAccount);}
            catch(JsonException){throw new IOException("HOME_NAME_ACCOUNT_INVALID");}
            if(value.Format!=1)throw new IOException("HOME_NAME_ACCOUNT_INVALID");
            byte[] key;try{key=Convert.FromBase64String(value.Key);}catch(FormatException){throw new IOException("HOME_NAME_ACCOUNT_INVALID");}
            return AcmeAccountKey.Import(key);
        }
        var created=AcmeAccountKey.Create();
        try{NativeInstallFiles.PublishNew(AccountFile,NativeJson.WriteCamel(new HomeNameAccount(1,Convert.ToBase64String(created.Export())),NativeCamelJsonContext.Default.HomeNameAccount));return created;}
        catch{created.Dispose();throw;}
    }

    /// <summary>
    /// The certificate in <paramref name="directory"/>: null when either file is absent; otherwise the chain parses, the key is
    /// the leaf's, and (unless <paramref name="name"/> is null) the leaf's only SAN is the name. Anything else throws.
    /// </summary>
    internal HomeNameCertificate? Read(string directory,string? name)
    {
        if(!File.Exists(CertificateFile(directory))||!File.Exists(KeyFile(directory)))return null;
        var chain=AcmeCertificateCheck.ParseChain(Text(CertificateFile(directory)));
        try
        {
            var leaf=chain[0];
            if(name is not null&&(AcmeCertificateCheck.DnsNames(leaf) is not [var only]||only!=name))throw new IOException("HOME_NAME_CERTIFICATE_NAME");
            using var key=ECDsa.Create();
            try{key.ImportFromPem(Text(KeyFile(directory)));}catch(Exception error)when(error is CryptographicException or ArgumentException){throw new IOException("HOME_NAME_CERTIFICATE_KEY");}
            if(!leaf.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))throw new IOException("HOME_NAME_CERTIFICATE_KEY");
            return new(new(leaf.NotBefore.ToUniversalTime()),new(leaf.NotAfter.ToUniversalTime()),leaf.GetCertHashString(HashAlgorithmName.SHA256));
        }
        finally{foreach(var certificate in chain)certificate.Dispose();}
    }
    /// <summary>Both files present and a matching certificate and key.</summary>
    internal bool IsComplete(string directory)
    {
        try{return Read(directory,null) is not null;}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException){return false;}
    }
    /// <summary>A new certificate into next/: both files written and flushed before anything may use them.</summary>
    internal void Stage(AcmeCertificate certificate)
    {
        RemoveDirectory(Next);NativeInstallFiles.NewDirectory(Next);
        NativeInstallFiles.WriteNew(KeyFile(Next),Encoding.UTF8.GetBytes(certificate.KeyPem));
        NativeInstallFiles.WriteNew(CertificateFile(Next),Encoding.UTF8.GetBytes(certificate.CertificatePem));
    }
    /// <summary>
    /// live→prev, next→live under the journal. A crash at any point is repaired by <see cref="Recover"/>. When it throws,
    /// the previous certificate is live again, or (if even that move failed) the journal makes <see cref="Recover"/> settle it.
    /// next→live is the commit point: a journal left after it is removed by the next <see cref="Recover"/>.
    /// </summary>
    internal void Swap()
    {
        if(!IsComplete(Next))throw new IOException("HOME_NAME_STAGE_INCOMPLETE");
        WriteJournal();RemoveDirectory(Prev);
        if(Directory.Exists(Live))Directory.Move(Live,Prev);
        try{Directory.Move(Next,Live);}
        catch{if(!Directory.Exists(Live)&&Directory.Exists(Prev))Directory.Move(Prev,Live);throw;}
        DropJournal();
    }
    /// <summary>Puts prev back as live after a new certificate failed: live→failed, prev→live, then failed goes.</summary>
    internal void SwapBack()
    {
        if(!Directory.Exists(Prev))throw new IOException("HOME_NAME_PREVIOUS_MISSING");
        WriteJournal();RemoveDirectory(Next);RemoveDirectory(Failed);
        if(Directory.Exists(Live))Directory.Move(Live,Failed);
        Directory.Move(Prev,Live);WindowsFileBoundary.DeleteNoFollow(JournalFile,false);RemoveDirectory(Failed);
    }
    /// <summary>
    /// Before every Caddy start: with the journal present, a missing live is restored from next if that
    /// is complete and can be moved, otherwise from prev; then the journal goes.
    /// </summary>
    internal void Recover()
    {
        if(!File.Exists(JournalFile))return;
        if(!Directory.Exists(Live))
        {
            var promoted=false;
            if(IsComplete(Next))
                try{Directory.Move(Next,Live);promoted=true;}
                catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
            if(!promoted&&Directory.Exists(Prev))Directory.Move(Prev,Live);
        }
        // live/ is settled; the rest is cleanup that must not keep Caddy from starting.
        DropJournal();
        try{RemoveDirectory(Failed);}catch(Exception error)when(Undeletable(error)){}
    }
    /// <summary>
    /// Removes the journal once live/ is settled. One that cannot be removed now is harmless: it only matters while
    /// live/ is missing, and every swap writes its own before it moves anything. The next Recover retries.
    /// </summary>
    private void DropJournal(){try{WindowsFileBoundary.DeleteNoFollow(JournalFile,false);}catch(Exception error)when(Undeletable(error)){}}
    private static bool Undeletable(Exception error)=>error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;
    /// <summary>The name is gone: credential and certificates go, the ACME account key stays.</summary>
    internal void Clear()
    {
        if(!Directory.Exists(root))return;
        if(File.Exists(JournalFile))WindowsFileBoundary.DeleteNoFollow(JournalFile,false);
        foreach(var directory in new[]{Next,Prev,Failed,Live})RemoveDirectory(directory);
        DiscardCredential();
    }
    private void WriteJournal()=>NativeInstallFiles.Publish(JournalFile,NativeJson.WriteCamel(new HomeNameSwapJournal(1),NativeCamelJsonContext.Default.HomeNameSwapJournal));
    private static string Text(string path)
    {
        try{return new UTF8Encoding(false,true).GetString(NativeInstallFiles.ReadRecord(path,MaximumPem));}
        catch(DecoderFallbackException){throw new IOException("HOME_NAME_CERTIFICATE_PEM");}
    }
    // A link is removed as a link; a directory's contents first, without following anything.
    private static void RemoveDirectory(string path)
    {
        if(!Directory.Exists(path))return;
        if(!new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
            foreach(var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if(entry is DirectoryInfo directory)RemoveDirectory(directory.FullName);
                else WindowsFileBoundary.DeleteNoFollow(entry.FullName,false);
            }
        WindowsFileBoundary.DeleteNoFollow(path,true);
    }
}

/// <summary>What the name path asks of the outside world: the name service and the CA. Tests replace it.</summary>
internal interface IHomeNameRemote
{
    Task<HomeNameCredential> RegisterAsync(CancellationToken cancellation);
    Task SetAddressAsync(HomeNameCredential credential,string address,CancellationToken cancellation);
    Task<AcmeCertificate> IssueAsync(HomeNameCredential credential,string name,AcmeAccountKey account,CancellationToken cancellation);
    Task DeleteNameAsync(HomeNameCredential credential,CancellationToken cancellation);
}
/// <summary>The configured name service and Let's Encrypt (production, classic profile). No proxy, no redirects.</summary>
internal sealed class HomeNameRemote:IHomeNameRemote
{
    private static Uri Service=>HomeNameRelease.Configured?new(HomeNameRelease.Service):throw new IOException("HOME_NAME_NOT_CONFIGURED");
    public async Task<HomeNameCredential> RegisterAsync(CancellationToken cancellation)
    {using var client=new HomeNameServiceClient(Service);return await client.RegisterAsync(cancellation);}
    public async Task SetAddressAsync(HomeNameCredential credential,string address,CancellationToken cancellation)
    {using var client=new HomeNameServiceClient(Service);await client.SetAddressAsync(credential,address,cancellation);}
    public async Task DeleteNameAsync(HomeNameCredential credential,CancellationToken cancellation)
    {using var client=new HomeNameServiceClient(Service);await client.DeleteNameAsync(credential,cancellation);}
    // A challenge value left behind (ChallengeCleared false) is harmless: the next order replaces it.
    public async Task<AcmeCertificate> IssueAsync(HomeNameCredential credential,string name,AcmeAccountKey account,CancellationToken cancellation)
    {
        using var names=new HomeNameServiceClient(Service);using var acme=new AcmeClient(HomeNameRelease.AcmeDirectory,account);
        var issued=await acme.IssueAsync(name,(values,c)=>names.SetChallengeAsync(credential,values,c),c=>names.ClearChallengeAsync(credential,c),
            new AcmeOptions{Profile=HomeNameRelease.AcmeProfile},cancellation);
        return issued.Certificate;
    }
}

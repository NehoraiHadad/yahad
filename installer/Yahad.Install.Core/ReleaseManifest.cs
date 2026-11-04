using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
namespace Yahad.Install;

// A release is yahad-windows-<v>.json plus its detached signature. The signed
// message is a fixed prefix and the raw manifest bytes, so nothing is parsed
// until the bytes are authenticated.
internal sealed record ReleaseSetupFile(string Name,string Sha256,long Bytes);
internal sealed record ReleaseManifest(int Format,string Product,string Version,string SourceCommit,ReleaseSetupFile Setup,int DatabaseSchema,string MinimumFromVersion,string KeyId,long CreatedUnix)
{
    internal const int MaximumBytes=4096;
    internal const long MaximumSetupBytes=536870912;
    private static readonly JsonSerializerOptions Options=new()
    {
        MaxDepth=4,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true,AllowDuplicateProperties=false
    };
    internal static ReleaseManifest Parse(byte[] bytes)
    {
        if(bytes.Length is <1 or >MaximumBytes)throw new IOException("RELEASE_MANIFEST_INVALID");
        ReleaseManifest? value;
        try{value=JsonSerializer.Deserialize<ReleaseManifest>(bytes,Options);}catch(JsonException){throw new IOException("RELEASE_MANIFEST_INVALID");}
        if(value is null||value.Format!=1||value.Product!="yahad-windows"||!IsVersion(value.Version)||!IsVersion(value.MinimumFromVersion)||
            !Regex.IsMatch(value.SourceCommit,@"\A[a-f0-9]{40}\z")||!Regex.IsMatch(value.KeyId,@"\A[a-f0-9]{16}\z")||value.Setup.Name!=SetupName(value.Version)||
            !Regex.IsMatch(value.Setup.Sha256,@"\A[a-f0-9]{64}\z")||value.Setup.Bytes is <1 or >MaximumSetupBytes||value.DatabaseSchema is <1 or >1024||value.CreatedUnix<1)
            throw new IOException("RELEASE_MANIFEST_INVALID");
        return value;
    }
    internal static string ManifestName(string version)=>"yahad-windows-"+version+".json";
    internal static string SetupName(string version)=>"Yahad.Setup-"+version+".exe";
    internal static bool IsVersion(string value)=>value.Length<=40&&Regex.IsMatch(value,@"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z",RegexOptions.CultureInvariant);
    // Canonical digits: a longer component is the larger one, whatever its size.
    internal static int Compare(string left,string right)
    {
        if(!IsVersion(left)||!IsVersion(right))throw new IOException("RELEASE_MANIFEST_INVALID");
        foreach(var(a,b)in left.Split('.').Zip(right.Split('.')))
        {var order=a.Length!=b.Length?a.Length.CompareTo(b.Length):string.CompareOrdinal(a,b);if(order!=0)return Math.Sign(order);}
        return 0;
    }
}
internal static class ReleaseSignature
{
    internal const string Prefix="yahad-release-v1\n";
    private const string NistP256="1.2.840.10045.3.1.7";
    // ECDSA P-256 over SHA-256 with the 64-byte IEEE P1363 r||s form. Anything
    // malformed is simply not a valid signature.
    internal static bool Verify(byte[] spki,byte[] manifest,byte[] signature)
    {
        if(signature.Length!=64)return false;
        using var key=ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(spki,out var read);
            if(read!=spki.Length||key.ExportParameters(false).Curve.Oid.Value!=NistP256)return false;
        }
        catch(CryptographicException){return false;}
        return key.VerifyData((byte[])[..Encoding.ASCII.GetBytes(Prefix),..manifest],signature,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
// The key this build was compiled to trust, if any. A build without one still
// installs, but it writes no installed key and every upgrade is refused.
internal static class TrustedReleaseKey
{
    internal const string InstalledName="release-key.spki";
    internal const int SpkiBytes=91;
    private static readonly byte[]? Compiled=Load();
    internal static byte[]? Embedded=>Compiled;
    private static byte[]? Load()
    {
        using var input=typeof(TrustedReleaseKey).Assembly.GetManifestResourceStream(InstalledName);if(input is null)return null;
        if(input.Length!=SpkiBytes)throw new IOException("RELEASE_KEY_EMBEDDED_INVALID");
        var bytes=new byte[SpkiBytes];input.ReadExactly(bytes);return bytes;
    }
    internal static string Id(byte[] spki)=>Convert.ToHexStringLower(SHA256.HashData(spki))[..16];
    internal static string Digest(byte[] spki)=>Convert.ToHexStringLower(SHA256.HashData(spki));
    // The installed key is the root of trust. The running image must carry the
    // same one, so a Setup built for another key cannot upgrade this machine.
    internal static byte[] Require(byte[]? installed,byte[]? embedded)
    {
        if(installed is null||embedded is null||installed.Length!=SpkiBytes||!installed.AsSpan().SequenceEqual(embedded))throw new IOException("RELEASE_KEY_UNTRUSTED");
        return installed;
    }
}
internal static class ReleaseVerification
{
    // Signature first, on raw bytes; then the strict parse; then what the
    // authenticated fields say about this installation. installedSchema is
    // null when the running application could not be asked.
    internal static ReleaseManifest Verify(byte[] trustedKey,byte[] manifest,byte[]? signature,string installedVersion,int? installedSchema)
    {
        if(manifest.Length is <1 or >ReleaseManifest.MaximumBytes||signature is null||!ReleaseSignature.Verify(trustedKey,manifest,signature))throw new IOException("RELEASE_SIGNATURE_INVALID");
        var value=ReleaseManifest.Parse(manifest);
        if(value.KeyId!=TrustedReleaseKey.Id(trustedKey))throw new IOException("RELEASE_KEY_UNTRUSTED");
        if(ReleaseManifest.Compare(value.Version,installedVersion)<=0)throw new IOException("RELEASE_NOT_NEWER");
        if(ReleaseManifest.Compare(installedVersion,value.MinimumFromVersion)<0||installedSchema>value.DatabaseSchema)throw new IOException("RELEASE_UNSUPPORTED_FROM");
        return value;
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
namespace Yahad.Install;

// Canonical semver core: no leading zeros, a longer component is the larger one.
internal static class SemVer
{
    internal static bool IsVersion(string value)=>value.Length<=40&&Regex.IsMatch(value,@"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z",RegexOptions.CultureInvariant);
    internal static int Compare(string left,string right)
    {
        if(!IsVersion(left)||!IsVersion(right))throw new IOException("RELEASE_MANIFEST_INVALID");
        foreach(var(a,b)in left.Split('.').Zip(right.Split('.')))
        {var order=a.Length!=b.Length?a.Length.CompareTo(b.Length):string.CompareOrdinal(a,b);if(order!=0)return Math.Sign(order);}
        return 0;
    }
}

// docs/WINDOWS-APP-PACKAGE-PLAN.md 5.1: one signed manifest for both tracks,
// yahad-windows-release-<v>.json plus a detached signature. The release version is
// the app version and SourceCommit the app's commit; the infrastructure half names
// its own version, contract range, commit and tree. The signed message is a fixed
// prefix and the raw manifest bytes, so nothing is parsed before authentication.
internal sealed record ReleaseFile(string Name,string Sha256,long Bytes);
internal sealed record ReleaseInfrastructure(string Version,int ContractMin,int ContractMax,string SourceCommit,string TreeSha256,ReleaseFile Setup);
internal sealed record ReleaseApp(ReleaseFile Archive,string TreeSha256,int RequiresContract,int DatabaseSchema,int[] DatabaseMigrationsFrom);
internal sealed record ReleaseMinimumFrom(string App,string Infrastructure);
internal sealed record ReleaseManifest(int Format,string Product,string Version,string SourceCommit,string KeyId,long CreatedUnix,
    ReleaseInfrastructure Infrastructure,ReleaseApp App,ReleaseMinimumFrom MinimumFrom)
{
    internal const int CurrentFormat=2,MaximumBytes=4096,MaximumMigrationSources=32;
    internal const string ProductName="yahad-windows-release";
    internal const long MaximumSetupBytes=536870912,MaximumAppArchiveBytes=2147483648;
    private static readonly JsonSerializerOptions Options=new()
    {
        MaxDepth=5,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true,AllowDuplicateProperties=false
    };
    internal static string ManifestName(string version)=>"yahad-windows-release-"+version+".json";
    internal static string SignatureName(string version)=>ManifestName(version)+".sig";
    internal static string SetupName(string version)=>"Yahad.Setup-"+version+".exe";
    internal static string AppArchiveName(string version)=>"yahad-windows-app-"+version+".zip";
    internal static ReleaseManifest Parse(byte[] bytes)
    {
        if(bytes.Length is <1 or >MaximumBytes)throw new IOException("RELEASE_MANIFEST_INVALID");
        ReleaseManifest? value;
        try{value=JsonSerializer.Deserialize<ReleaseManifest>(bytes,Options);}catch(JsonException){throw new IOException("RELEASE_MANIFEST_INVALID");}
        if(value is null||!value.Valid())throw new IOException("RELEASE_MANIFEST_INVALID");
        return value;
    }
    // Shape only. Whether the app's contract fits the release's own infrastructure is a
    // release defect reported by selection (APP_NEEDS_INFRASTRUCTURE, 9.1), not a parse
    // failure, so it is not checked here.
    private bool Valid()
    {
        var i=Infrastructure;var a=App;var m=MinimumFrom;
        if(Format!=CurrentFormat||Product!=ProductName||!SemVer.IsVersion(Version)||!IsCommit(SourceCommit)||!Regex.IsMatch(KeyId,@"\A[a-f0-9]{16}\z")||CreatedUnix<1)return false;
        if(!SemVer.IsVersion(i.Version)||!InfrastructureRelease.IsContract(i.ContractMin)||!InfrastructureRelease.IsContract(i.ContractMax)||i.ContractMin>i.ContractMax||
            !IsCommit(i.SourceCommit)||!TreeRecord.IsDigest(i.TreeSha256)||!File(i.Setup,SetupName(Version),MaximumSetupBytes))return false;
        if(!File(a.Archive,AppArchiveName(Version),MaximumAppArchiveBytes)||!TreeRecord.IsDigest(a.TreeSha256)||!InfrastructureRelease.IsContract(a.RequiresContract)||
            a.DatabaseSchema is <1 or >1024||a.DatabaseMigrationsFrom.Length>MaximumMigrationSources||
            a.DatabaseMigrationsFrom.Any(s=>s<1||s>=a.DatabaseSchema)||a.DatabaseMigrationsFrom.Distinct().Count()!=a.DatabaseMigrationsFrom.Length)return false;
        // A release may not admit a source newer than itself.
        return SemVer.IsVersion(m.App)&&SemVer.IsVersion(m.Infrastructure)&&SemVer.Compare(m.App,Version)<=0&&SemVer.Compare(m.Infrastructure,i.Version)<=0;
    }
    private static bool IsCommit(string value)=>Regex.IsMatch(value,@"\A[a-f0-9]{40}\z");
    private static bool File(ReleaseFile file,string name,long maximum)=>file.Name==name&&TreeRecord.IsDigest(file.Sha256)&&file.Bytes>=1&&file.Bytes<=maximum;
    internal bool AcceptsSchema(int recordedSchema)=>recordedSchema==App.DatabaseSchema||App.DatabaseMigrationsFrom.Contains(recordedSchema);
}
internal static class ReleaseSignature
{
    internal const string Prefix="yahad-release-v2\n";
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
    // Signature first, on raw bytes; then the strict parse; then the key id. What the
    // authenticated release means for this machine is ReleaseSelection's question.
    internal static ReleaseManifest Verify(byte[] trustedKey,byte[] manifest,byte[]? signature)
    {
        if(manifest.Length is <1 or >ReleaseManifest.MaximumBytes||signature is null||!ReleaseSignature.Verify(trustedKey,manifest,signature))throw new IOException("RELEASE_SIGNATURE_INVALID");
        var value=ReleaseManifest.Parse(manifest);
        if(value.KeyId!=TrustedReleaseKey.Id(trustedKey))throw new IOException("RELEASE_KEY_UNTRUSTED");
        return value;
    }
}

internal enum ReleaseTrack{Setup,App}
// docs/WINDOWS-APP-PACKAGE-PLAN.md 9.1, shared by the Updater (which track to stage)
// and the engine (which track it is being asked to apply). null: not an update for
// this machine. Throws the release-defect and admission codes of 9.4.
internal static class ReleaseSelection
{
    internal static ReleaseTrack? Select(ReleaseManifest release,ActivePointer installed)
    {
        var i=release.Infrastructure;
        // A release whose app cannot run on its own infrastructure is defective on every machine.
        if(release.App.RequiresContract<i.ContractMin||release.App.RequiresContract>i.ContractMax)throw new IOException("APP_NEEDS_INFRASTRUCTURE");
        var infra=SemVer.Compare(i.Version,installed.Infrastructure.Version);
        if(infra<0)return null;
        if(infra>0){RequireFrom(release,installed);return ReleaseTrack.Setup;}
        if(SemVer.Compare(release.Version,installed.App.Version)<=0)return null;
        // One infrastructure version names exactly one tree (8.1).
        if(i.TreeSha256!=installed.Infrastructure.TreeSha256||i.ContractMin!=installed.Infrastructure.ContractMin||i.ContractMax!=installed.Infrastructure.ContractMax)
            throw new IOException("INFRA_IDENTITY_MISMATCH");
        RequireFrom(release,installed);return ReleaseTrack.App;
    }
    internal static void RequireFrom(ReleaseManifest release,ActivePointer installed)
    {
        if(SemVer.Compare(installed.App.Version,release.MinimumFrom.App)<0||SemVer.Compare(installed.Infrastructure.Version,release.MinimumFrom.Infrastructure)<0)
            throw new IOException("RELEASE_UNSUPPORTED_FROM");
    }
}

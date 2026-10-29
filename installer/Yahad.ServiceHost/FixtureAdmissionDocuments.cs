using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yahad.Service;

internal sealed record FixtureSignedEnvelope(string KeyId,string Payload,string Signature);
internal sealed record AdmissionHead(int Format,string InstallationId,long Epoch,string ReleaseDigest,string SelectionDigest,
    string IntentDigest,string OwnerDigest,string? RecoveryDigest,string PolicyDigest,string PinDigest);
internal sealed record AdmissionFile(string Path,long Bytes,string Sha256);
internal sealed record AdmissionRelease(int Format,string ReleaseId,int MinimumSchema,int MaximumSchema,string NodeSha256,
    string Entry,AdmissionFile[] Files);
internal sealed record AdmissionSelection(int Format,string InstallationId,long Epoch,string ReleaseId,string DataId,string IdentityDigest);
internal sealed record AdmissionDataIdentity(int Format,string InstallationId,string DataId,string MasterKeyDigest);
internal sealed record AdmissionIntent(int Format,string InstallationId,long Epoch,string Nonce,string Phase,string ReleaseDigest,
    string SelectionDigest,string DataId,string PolicyDigest,string PinDigest);
internal sealed record AdmissionOwner(int Format,string InstallationId,long Epoch,string Nonce,string Operation,string ReleaseDigest,
    string SelectionDigest,string DataId,string PolicyDigest,string PinDigest,long ExpiresUnix);
internal sealed record AdmissionRecovery(int Format,string InstallationId,long Epoch,string Phase,string ReleaseDigest,
    string SelectionDigest,string DataId,string OwnerDigest,string BackupDigest,int Schema,string DataTreeDigest);

// Test-profile verifier only: pinned test public keys are injected in memory.
// No document-selected key/algorithm, enrollment, private-key persistence or
// compatibility claim with the production updater's release-signature format.
internal sealed class FixtureDocumentVerifier:IDisposable
{
    internal const string Profile="yahad-fixture-admission-v1";
    private readonly string keyId;private readonly ECDsa key;
    private static readonly JsonSerializerOptions Strict=new(){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    internal FixtureDocumentVerifier(string keyId,byte[] publicKey)
    {
        this.keyId=keyId;key=ECDsa.Create();key.ImportSubjectPublicKeyInfo(publicKey,out var read);
        if(read!=publicKey.Length||key.KeySize!=256)throw new IOException("ADMISSION_TEST_TRUST");
    }
    internal static byte[] Message(string purpose,byte[] payload)=>Encoding.UTF8.GetBytes(Profile+"\n"+purpose+"\n").Concat(payload).ToArray();
    internal T Verify<T>(byte[] bytes,string purpose)
    {
        if(bytes.Length is <1 or >1024*1024)throw new IOException("ADMISSION_DOCUMENT_BOUND");
        NoDuplicates(bytes);
        var envelope=JsonSerializer.Deserialize<FixtureSignedEnvelope>(bytes,Strict)??throw new IOException("ADMISSION_ENVELOPE");
        if(envelope.KeyId!=keyId)throw new IOException("ADMISSION_UNKNOWN_SIGNER");
        var payload=Convert.FromBase64String(envelope.Payload);var signature=Convert.FromBase64String(envelope.Signature);
        if(signature.Length!=64||!key.VerifyData(Message(purpose,payload),signature,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation))throw new IOException("ADMISSION_SIGNATURE");
        NoDuplicates(payload);
        return JsonSerializer.Deserialize<T>(payload,Strict)??throw new IOException("ADMISSION_PAYLOAD");
    }
    private static void NoDuplicates(byte[] bytes)
    {
        using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=32});
        void Check(JsonElement value)
        {
            if(value.ValueKind==JsonValueKind.Object)
            {var names=new HashSet<string>(StringComparer.Ordinal);foreach(var property in value.EnumerateObject()){if(!names.Add(property.Name))throw new IOException("ADMISSION_DUPLICATE_FIELD");Check(property.Value);}}
            else if(value.ValueKind==JsonValueKind.Array)foreach(var element in value.EnumerateArray())Check(element);
        }
        Check(document.RootElement);
    }
    public void Dispose()=>key.Dispose();
}

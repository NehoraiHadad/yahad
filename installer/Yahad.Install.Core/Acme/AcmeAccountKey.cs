using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yahad.Install;

// The ACME account key (RFC 8555 6.2): ES256 on P-256 only. Its stored form is the
// PKCS#8 private key, which the caller keeps on disk under its own ACL.
internal sealed class AcmeAccountKey:IDisposable
{
    private readonly ECDsa key;
    private AcmeAccountKey(ECDsa key)
    {
        this.key=key;
        var point=key.ExportParameters(false);
        Jwk=Encoding.UTF8.GetString(JwkBytes(Base64Url.EncodeToString(point.Q.X),Base64Url.EncodeToString(point.Q.Y)));
        Thumbprint=Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(Jwk)));
    }
    /// <summary>The public JWK in RFC 7638 canonical form (members crv, kty, x, y in that order, no whitespace).</summary>
    internal string Jwk{get;}
    /// <summary>RFC 7638 thumbprint: base64url(SHA-256(canonical JWK)).</summary>
    internal string Thumbprint{get;}
    internal static AcmeAccountKey Create()=>new(ECDsa.Create(ECCurve.NamedCurves.nistP256));
    internal static AcmeAccountKey Import(ReadOnlySpan<byte> pkcs8)
    {
        var key=ECDsa.Create();
        try{key.ImportPkcs8PrivateKey(pkcs8,out var read);if(read!=pkcs8.Length)throw new IOException("ACME_ACCOUNT_KEY_INVALID");}
        catch(CryptographicException){key.Dispose();throw new IOException("ACME_ACCOUNT_KEY_INVALID");}
        catch{key.Dispose();throw;}
        if(key.KeySize!=256||key.ExportParameters(false).Curve.Oid.Value!=ECCurve.NamedCurves.nistP256.Oid.Value){key.Dispose();throw new IOException("ACME_ACCOUNT_KEY_INVALID");}
        return new(key);
    }
    /// <summary>The PKCS#8 private key bytes. Secret: store under an ACL, never log.</summary>
    internal byte[] Export()=>key.ExportPkcs8PrivateKey();
    /// <summary>ES256 over the JWS signing input, as JWS needs it: raw r||s (64 bytes).</summary>
    internal byte[] Sign(ReadOnlySpan<byte> input)=>key.SignData(input,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    internal static byte[] JwkBytes(string x,string y)
    {
        using var buffer=new MemoryStream();
        using(var json=new Utf8JsonWriter(buffer)){json.WriteStartObject();json.WriteString("crv","P-256");json.WriteString("kty","EC");json.WriteString("x",x);json.WriteString("y",y);json.WriteEndObject();}
        return buffer.ToArray();
    }
    public void Dispose()=>key.Dispose();
}

internal static class AcmeJws
{
    /// <summary>
    /// A flattened JWS (RFC 8555 6.2) signed with the account key. Exactly one of
    /// <paramref name="kid"/> and the account's JWK identifies the key. A null payload
    /// is POST-as-GET (empty payload).
    /// </summary>
    internal static byte[] Sign(AcmeAccountKey key,string url,string nonce,string? kid,ReadOnlySpan<byte> payload,bool postAsGet)
    {
        using var header=new MemoryStream();
        using(var json=new Utf8JsonWriter(header))
        {
            json.WriteStartObject();json.WriteString("alg","ES256");
            if(kid is null){json.WritePropertyName("jwk");json.WriteRawValue(key.Jwk,skipInputValidation:false);}else json.WriteString("kid",kid);
            json.WriteString("nonce",nonce);json.WriteString("url",url);json.WriteEndObject();
        }
        var protectedPart=Base64Url.EncodeToString(header.ToArray());
        var payloadPart=postAsGet?"":Base64Url.EncodeToString(payload);
        var signature=Base64Url.EncodeToString(key.Sign(Encoding.ASCII.GetBytes(protectedPart+"."+payloadPart)));
        using var body=new MemoryStream();
        using(var json=new Utf8JsonWriter(body)){json.WriteStartObject();json.WriteString("protected",protectedPart);json.WriteString("payload",payloadPart);json.WriteString("signature",signature);json.WriteEndObject();}
        return body.ToArray();
    }
    /// <summary>The dns-01 TXT value (RFC 8555 8.1, 8.4): base64url(SHA-256(token "." thumbprint)).</summary>
    internal static string Dns01Value(string token,string thumbprint)
    {
        if(!IsBase64Url(token)||token.Length<22||token.Length>256)throw new IOException("ACME_TOKEN_INVALID");
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(token+"."+thumbprint)));
    }
    internal static bool IsBase64Url(string value)=>value.Length>0&&value.All(c=>c is >='A' and <='Z' or >='a' and <='z' or >='0' and <='9' or '-' or '_');
}

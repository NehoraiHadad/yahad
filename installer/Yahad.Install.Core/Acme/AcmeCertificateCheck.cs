using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Yahad.Install;

/// <summary>An issued certificate: the validated chain (leaf first) and its new private key, both PEM.</summary>
internal sealed record AcmeCertificate(string CertificatePem,string KeyPem,DateTimeOffset NotBefore,DateTimeOffset NotAfter)
{
    // The key is secret; a record's generated ToString would print it.
    public override string ToString()=>"AcmeCertificate { NotBefore = "+NotBefore.ToString("O")+", NotAfter = "+NotAfter.ToString("O")+" }";
}

/// <summary>Which roots an issued chain must build to. Production uses the system store; tests pass their own CA (e.g. Pebble's).</summary>
internal sealed class AcmeTrust
{
    internal static readonly AcmeTrust System=new(null);
    private AcmeTrust(X509Certificate2Collection? roots){Roots=roots;}
    internal X509Certificate2Collection? Roots{get;}
    internal static AcmeTrust Custom(X509Certificate2Collection roots)
    {if(roots.Count==0)throw new ArgumentException("ACME_TRUST_EMPTY");return new(roots);}
}

internal static class AcmeCertificateCheck
{
    internal const int MaximumChainCertificates=5;
    internal const string ServerAuth="1.3.6.1.5.5.7.3.1";
    /// <summary>Renewal is due from two thirds of the certificate's own lifetime.</summary>
    internal static bool RenewalDue(DateTimeOffset notBefore,DateTimeOffset notAfter,DateTimeOffset now)
    {
        if(notAfter<=notBefore)throw new ArgumentException("ACME_VALIDITY_INVALID");
        return now>=notBefore+(notAfter-notBefore)*2/3;
    }
    /// <summary>
    /// Checks a downloaded chain before it is used: the leaf's only SAN is <paramref name="name"/>, its key is
    /// <paramref name="key"/>, it is valid at <paramref name="now"/>, it is for server authentication, and the
    /// chain builds to <paramref name="trust"/>. Returns the chain re-encoded as clean PEM (leaf first).
    /// </summary>
    internal static AcmeCertificate Validate(string pemChain,string name,ECDsa key,AcmeTrust trust,DateTimeOffset now)
    {
        AcmeName.Require(name);
        var chain=ParseChain(pemChain);
        try
        {
            var leaf=chain[0];
            if(DnsNames(leaf) is not [var only]||only!=name)throw new IOException("ACME_CERT_NAME");
            if(!leaf.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))throw new IOException("ACME_CERT_KEY");
            var notBefore=new DateTimeOffset(leaf.NotBefore.ToUniversalTime());var notAfter=new DateTimeOffset(leaf.NotAfter.ToUniversalTime());
            if(now<notBefore||now>=notAfter)throw new IOException("ACME_CERT_VALIDITY");
            if(!leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e=>e.EnhancedKeyUsages.Cast<Oid>().Any(o=>o.Value==ServerAuth)))throw new IOException("ACME_CERT_USAGE");
            using(var builder=new X509Chain())
            {
                var policy=builder.ChainPolicy;
                // Revocation is not consulted: the certificate was issued moments ago,
                // and Let's Encrypt no longer runs OCSP.
                policy.RevocationMode=X509RevocationMode.NoCheck;policy.VerificationTime=now.UtcDateTime;policy.VerificationTimeIgnored=false;
                policy.ApplicationPolicy.Add(new Oid(ServerAuth));
                foreach(var issuer in chain.Skip(1))policy.ExtraStore.Add(issuer);
                if(trust.Roots is {} roots){policy.TrustMode=X509ChainTrustMode.CustomRootTrust;policy.CustomTrustStore.AddRange(roots);}
                else policy.TrustMode=X509ChainTrustMode.System;
                try{if(!builder.Build(leaf))throw new IOException("ACME_CERT_CHAIN");}
                finally{foreach(var element in builder.ChainElements)element.Certificate.Dispose();}
            }
            var pem=new StringBuilder();
            foreach(var certificate in chain)pem.Append(PemEncoding.WriteString("CERTIFICATE",certificate.RawData)).Append('\n');
            return new(pem.ToString(),key.ExportPkcs8PrivateKeyPem()+"\n",notBefore,notAfter);
        }
        finally{foreach(var certificate in chain)certificate.Dispose();}
    }
    internal static List<X509Certificate2> ParseChain(string pem)
    {
        var chain=new List<X509Certificate2>();var rest=pem.AsMemory();
        try
        {
            while(PemEncoding.TryFind(rest.Span,out var fields))
            {
                if(!rest.Span[fields.Label].SequenceEqual("CERTIFICATE")||rest.Span[..fields.Location.Start].Trim().Length!=0)throw new IOException("ACME_CERT_PEM");
                if(chain.Count==MaximumChainCertificates)throw new IOException("ACME_CERT_PEM");
                var der=Convert.FromBase64String(rest.Span[fields.Base64Data].ToString());
                try{chain.Add(X509CertificateLoader.LoadCertificate(der));}catch(CryptographicException){throw new IOException("ACME_CERT_PEM");}
                rest=rest[fields.Location.End..];
            }
            if(chain.Count==0||rest.Span.Trim().Length!=0)throw new IOException("ACME_CERT_PEM");
            return chain;
        }
        catch{foreach(var certificate in chain)certificate.Dispose();throw;}
    }
    // Every GeneralName in the (single) SAN extension; a name of any other kind is reported as
    // a non-name entry so that "exactly one SAN" counts all of them.
    internal static List<string?> DnsNames(X509Certificate2 certificate)
    {
        var extensions=certificate.Extensions.Where(e=>e.Oid?.Value=="2.5.29.17").ToList();
        if(extensions.Count!=1)throw new IOException("ACME_CERT_NAME");
        var names=new List<string?>();
        try
        {
            var reader=new AsnReader(extensions[0].RawData,AsnEncodingRules.DER);var sequence=reader.ReadSequence();reader.ThrowIfNotEmpty();
            var dns=new Asn1Tag(TagClass.ContextSpecific,2);
            while(sequence.HasData)
            {
                var tag=sequence.PeekTag();
                if(tag==dns)names.Add(sequence.ReadCharacterString(UniversalTagNumber.IA5String,dns));
                else{sequence.ReadEncodedValue();names.Add(null);}
            }
        }
        catch(AsnContentException){throw new IOException("ACME_CERT_NAME");}
        return names;
    }
}

internal static class AcmeName
{
    /// <summary>A lowercase LDH host name with at least two labels; never a wildcard or an IP literal.</summary>
    internal static void Require(string name)
    {
        var labels=name.Split('.');
        if(name.Length>253||labels.Length<2||labels.Any(l=>l.Length is 0 or >63||l[0]=='-'||l[^1]=='-'||!l.All(c=>c is >='a' and <='z' or >='0' and <='9' or '-'))||labels[^1].All(char.IsAsciiDigit))
            throw new ArgumentException("ACME_NAME_INVALID");
    }
}

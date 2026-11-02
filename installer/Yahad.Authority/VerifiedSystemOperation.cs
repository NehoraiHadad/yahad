using Yahad.Install;
namespace Yahad.Authority;

// An authorization result, not a request DTO. Only the full policy issuer calls
// CreateAuthorized after validating explicit enrollment, the current mode grant
// and the staged source-bound candidate. Helpers reauthorize protected records;
// they never deserialize this capability or infer one from a manifest.
internal sealed class VerifiedSystemOperation
{
    public string InstallationId{get;}public string OperationId{get;}public string ManifestDigest{get;}public string ApplicationBundleDigest{get;}public string PolicyDigest{get;}
    public string GrantId{get;}public string GrantBindingDigest{get;}public string FromVersion{get;}public string ToVersion{get;}
    public string BootstrapFromVersion{get;}
    public long ExpectedBootstrapEpoch{get;}public string PreviousActivationDigest{get;}public int SourceSchema{get;}public int TargetSchema{get;}
    public string SourceCommit{get;}public IReadOnlyList<SystemComponentDescriptor> Components{get;}
    private VerifiedSystemOperation(string installationId,string operationId,string manifestDigest,string applicationBundleDigest,string policyDigest,string grantId,string grantBindingDigest,
        string fromVersion,string toVersion,long expectedBootstrapEpoch,string previousActivationDigest,int sourceSchema,int targetSchema,string sourceCommit,IReadOnlyList<SystemComponentDescriptor> components,string bootstrapFromVersion)
    {
        InstallationId=installationId;OperationId=operationId;ManifestDigest=manifestDigest;ApplicationBundleDigest=applicationBundleDigest;PolicyDigest=policyDigest;GrantId=grantId;GrantBindingDigest=grantBindingDigest;
        FromVersion=fromVersion;ToVersion=toVersion;ExpectedBootstrapEpoch=expectedBootstrapEpoch;PreviousActivationDigest=previousActivationDigest;
        SourceSchema=sourceSchema;TargetSchema=targetSchema;SourceCommit=sourceCommit;Components=components;BootstrapFromVersion=bootstrapFromVersion;
    }
    internal static VerifiedSystemOperation CreateAuthorized(string installationId,string operationId,string manifestDigest,string applicationBundleDigest,string policyDigest,
        string grantId,string grantBindingDigest,string fromVersion,string toVersion,long expectedBootstrapEpoch,string previousActivationDigest,
        int sourceSchema,int targetSchema,string sourceCommit,IEnumerable<SystemComponentDescriptor> components,string? bootstrapFromVersion=null)
    {
        foreach(var id in new[]{installationId,operationId,grantId})
            if(!Guid.TryParseExact(id,"N",out var value)||value.ToString("N")!=id)throw new IOException("SYSTEM_OPERATION_ID");
        foreach(var digest in new[]{manifestDigest,applicationBundleDigest,policyDigest,grantBindingDigest,previousActivationDigest})FixedSystemComponents.Digest(digest);
        static Version VersionOf(string value)
        {
            if(value is null||!System.Text.RegularExpressions.Regex.IsMatch(value,@"^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$"))throw new IOException("SYSTEM_OPERATION_VERSION");
            return Version.Parse(value);
        }
        bootstrapFromVersion??=fromVersion;
        if(VersionOf(toVersion)<=VersionOf(fromVersion)||VersionOf(toVersion)<VersionOf(bootstrapFromVersion)||expectedBootstrapEpoch<0||expectedBootstrapEpoch==long.MaxValue||sourceSchema is <1 or >100000||targetSchema<sourceSchema||targetSchema>100000||
            sourceCommit is null||!System.Text.RegularExpressions.Regex.IsMatch(sourceCommit,"^[a-f0-9]{40}$"))throw new IOException("SYSTEM_OPERATION_BINDING");
        var copy=components.Select(c=>new SystemComponentDescriptor(c.Kind,c.SlotRelativeDirectory,c.EntryPoint,c.EntrySha256,c.Files)).Take(6).ToArray();
        if(copy.Length!=5||copy.Select(c=>c.Kind).Distinct().Count()!=5||!Enum.GetValues<SystemComponentKind>().All(kind=>copy.Any(c=>c.Kind==kind)))throw new IOException("SYSTEM_OPERATION_COMPONENTS");
        return new(installationId,operationId,manifestDigest,applicationBundleDigest,policyDigest,grantId,grantBindingDigest,fromVersion,toVersion,expectedBootstrapEpoch,previousActivationDigest,sourceSchema,targetSchema,sourceCommit,Array.AsReadOnly(copy),bootstrapFromVersion);
    }
}

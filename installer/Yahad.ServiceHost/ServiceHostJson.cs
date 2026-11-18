using System.Text.Json.Serialization;

namespace Yahad.Service;

// The host's native records, read by NativeJson's strict variant.
[JsonSourceGenerationOptions(UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,RespectRequiredConstructorParameters=true)]
[JsonSerializable(typeof(InstalledLaunchConfiguration))][JsonSerializable(typeof(LanProxyReceipt))][JsonSerializable(typeof(InstalledHomeNetwork))][JsonSerializable(typeof(InstalledHomeName))][JsonSerializable(typeof(HomeNameOwner))]
internal sealed partial class ServiceHostJsonContext:JsonSerializerContext;
// --describe: installer/windows/application_version.py reads these names in this order.
internal sealed record ServiceHostDescription(string Artifact,string ServiceName,string? Version,string InfrastructureVersion,int ContractMin,int ContractMax,string SourceCommit,string RuntimeVersion,
    bool ScmEntryImplemented,bool CanInstallService,string LaunchConfiguration,bool FamilyInstallationReady,bool ReleaseReady);
[JsonSourceGenerationOptions(PropertyNamingPolicy=JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ServiceHostDescription))]
internal sealed partial class ServiceHostDescriptionJsonContext:JsonSerializerContext;

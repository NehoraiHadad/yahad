using System.Text.Json.Serialization;

namespace Yahad.Updater;

// The GitHub release JSON, the only thing parsed unauthenticated: snake_case names,
// required constructor parameters and non-null annotations enforced, bounded depth.
[JsonSourceGenerationOptions(MaxDepth=8,PropertyNamingPolicy=JsonKnownNamingPolicy.SnakeCaseLower,RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true)]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class GitHubJsonContext:JsonSerializerContext;
// --describe: installer/windows/application_version.py reads these names in this order.
internal sealed record UpdaterDescription(string Artifact,string ServiceName,string? Version,string InfrastructureVersion,int ContractMin,int ContractMax,string SourceCommit);
[JsonSourceGenerationOptions(PropertyNamingPolicy=JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdaterDescription))]
internal sealed partial class UpdaterDescriptionJsonContext:JsonSerializerContext;

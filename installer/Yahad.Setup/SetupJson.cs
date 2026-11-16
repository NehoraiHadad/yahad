using System.Text.Json;
using System.Text.Json.Serialization;
using Yahad.Service;

namespace Yahad.Setup;

// Setup's native records, read by NativeJson's strict variant (NativeJsonContext's
// options). Their metadata is generated at build time, like the services' records.
[JsonSourceGenerationOptions(UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,RespectRequiredConstructorParameters=true)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(InstallPackageManifest))]
[JsonSerializable(typeof(InstallerState))]
[JsonSerializable(typeof(RemovalIntent))]
[JsonSerializable(typeof(UpgradeState))]
[JsonSerializable(typeof(InstallBackup.Binding))]
[JsonSerializable(typeof(DatabaseBackup.Entry[]))]
[JsonSerializable(typeof(LanSetupRequest))]
[JsonSerializable(typeof(LanRemovalIntent))]
[JsonSerializable(typeof(LanOwnedState))]
[JsonSerializable(typeof(LanProxyReceipt))]
internal sealed partial class SetupJsonContext:JsonSerializerContext;

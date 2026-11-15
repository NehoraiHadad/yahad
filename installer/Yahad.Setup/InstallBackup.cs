using System.IO;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;

// The copy a reinstall over kept data takes before the new app's first start (8.3),
// bound to the attempt that took it: this installation and dataset, the schema the
// record keeps the data at, and the pair being installed. The binding is published
// after the whole copy, so its presence means a whole copy of this attempt's data.
// Only the in-progress record's own copy is ever put back; any other is stale.
internal static class InstallBackup
{
    private const string BindingName="attempt.json",ManifestName="manifest.json";
    internal sealed record Binding(string InstallationId,string DataId,int KeptSchema,string InfrastructureTreeSha256,string AppTreeSha256);
    internal static Binding Of(InstallerState state)
        =>new(state.InstallationId,state.DataId,state.Active.App.DatabaseSchema,state.Active.Infrastructure.TreeSha256,state.Active.App.TreeSha256);
    internal static byte[] Encode(InstallerState state)=>JsonSerializer.SerializeToUtf8Bytes(Of(state));
    internal static bool Matches(byte[] binding,InstallerState state)
    {
        try{return NativeJson.Read<Binding>(binding)==Of(state);}
        catch(JsonException){return false;}
    }
    internal static bool BoundTo(string backup,InstallerState state)
    {
        var binding=Path.Combine(backup,BindingName);
        return File.Exists(Path.Combine(backup,ManifestName))&&File.Exists(binding)&&Matches(NativeInstallFiles.ReadRecord(binding,65536),state);
    }
    // Any earlier copy is removed first (DatabaseBackup.Create).
    internal static void Create(string data,string backup,InstallerState state)
    {DatabaseBackup.Create(data,backup);NativeInstallFiles.PublishNew(Path.Combine(backup,BindingName),Encode(state));}
}

using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

// Published by the existing sole updater after its configured intake policy.
// The app protocol has no publisher route, keys, URLs or trust switches.
internal sealed record NativeUpdateMode(int Format,string InstallationId,string Mode,string? DelegationId,bool ModeControlAllowed,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? GrantKind=null);

internal sealed record IntakeReceipt(int Format,string InstallationId,string BundleDigest,string Version,string Channel,long PayloadBytes,int Schema,string PolicyDigest,string PinDigest);
internal sealed class InstalledIntakeSelection(InstalledStateFiles files,InstalledAuthorityAnchor anchor)
{
    private byte[] Read(string name,int max=16384)=>files.Read(files.PathOf(name),max);
    internal InstalledActivation Head()
    {
        var bytes=Read("activation.json");var h=NativeJson.Read<InstalledActivation>(bytes);var digest=InstalledActivationResolver.Hash(bytes);
        var epoch=NativeJson.Read<InstalledEpoch>(Read("epoch.json"));var b=WindowsInstallationReader.Parse(Read("bootstrap.json"));
        if(h.InstallationId!=anchor.InstallationId||h.Format!=1||h.Phase!="committed"||epoch!=new InstalledEpoch(1,anchor.InstallationId,h.Epoch,digest)||b!=new InstallationBinding(anchor.InstallationId,digest)||
            h.PolicyDigest!=anchor.IntakePolicyDigest||h.PinDigest!=anchor.IntakePinDigest)throw new IOException("INTAKE_INSTALLED_ANCHOR");return h;
    }
    internal NativeUpdateMode Mode()
    {
        if(Directory.EnumerateFiles(files.PathOf("intake"),"mode.json.partial-*").Any())throw new IOException("INTAKE_MODE_RECONCILIATION_REQUIRED");
        var m=NativeJson.Read<NativeUpdateMode>(Read("intake/mode.json"));
        if(m.InstallationId!=anchor.InstallationId||m.Mode is not("auto" or "notify")||(m.Mode=="auto")!=(m.DelegationId is not null)||m.Format!=2||m.GrantKind!="system"||anchor.FullSystemPolicyDigest is null)throw new IOException("INTAKE_MODE_UNCONFIGURED");if(m.DelegationId is not null)InstalledActivationResolver.Id(m.DelegationId);return m;
    }
}

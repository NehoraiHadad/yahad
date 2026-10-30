using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

// Published by the existing sole updater after its configured intake policy.
// The app/owner protocol has no publisher route, keys, URLs or trust switches.
internal sealed record IntakeReceipt(int Format,string InstallationId,string BundleDigest,string Version,string Channel,long PayloadBytes,int Schema,string PolicyDigest,string PinDigest);
internal sealed record IntakePointer(int Format,string InstallationId,string ReceiptDigest);
internal sealed record IntakeSelection(CatalogOperation Catalog,string Target,string HeadDigest,string ReceiptDigest);
internal sealed record NativeUpdateMode(int Format,string InstallationId,string Mode,string? DelegationId,bool ModeControlAllowed);
internal sealed class InstalledIntakeSelection(InstalledStateFiles files,InstalledAuthorityAnchor anchor,Func<DateTimeOffset> clock)
{
    private byte[] Read(string name,int max=16384)=>files.Read(files.PathOf(name),max);
    internal InstalledActivation Head()
    {
        var bytes=Read("activation.json");var h=NativeJson.Read<InstalledActivation>(bytes);var digest=InstalledActivationResolver.Hash(bytes);
        var epoch=NativeJson.Read<InstalledEpoch>(Read("epoch.json"));var b=WindowsInstallationReader.Parse(Read("bootstrap.json"));
        if(h.InstallationId!=anchor.InstallationId||h.Format!=1||h.Phase!="committed"||epoch!=new InstalledEpoch(1,anchor.InstallationId,h.Epoch,digest)||b!=new InstallationBinding(anchor.InstallationId,digest)||
            h.PolicyDigest!=anchor.IntakePolicyDigest||h.PinDigest!=anchor.IntakePinDigest)throw new IOException("INTAKE_INSTALLED_ANCHOR");return h;
    }
    internal IntakeSelection Select(string? target=null)
    {
        var head=Head();var headDigest=InstalledActivationResolver.Hash(Read("activation.json"));
        var pointer=NativeJson.Read<IntakePointer>(Read("intake/available.json"));InstalledActivationResolver.Digest(pointer.ReceiptDigest);
        if(pointer.Format!=1||pointer.InstallationId!=anchor.InstallationId||target is not null&&target!=pointer.ReceiptDigest)throw new IOException("INTAKE_TARGET_CHANGED");
        var receiptBytes=Read("intake/receipt-"+pointer.ReceiptDigest+".json");
        if(InstalledActivationResolver.Hash(receiptBytes)!=pointer.ReceiptDigest)throw new IOException("INTAKE_RECEIPT_BYTES");var receipt=NativeJson.Read<IntakeReceipt>(receiptBytes);
        InstalledActivationResolver.Digest(receipt.BundleDigest);
        if(receipt.Format!=1||receipt.InstallationId!=anchor.InstallationId||receipt.PolicyDigest!=anchor.IntakePolicyDigest||receipt.PinDigest!=anchor.IntakePinDigest||
            receipt.Channel is not("stable" or "preview")||receipt.PayloadBytes is <1 or >2147483648||receipt.Schema<head.MinimumSchema||receipt.Schema>100000)throw new IOException("INTAKE_RECEIPT_SCOPE");
        var manifestBytes=Read("payloads/"+receipt.BundleDigest+"/bundle.json",16*1024*1024);
        if(InstalledActivationResolver.Hash(manifestBytes)!=receipt.BundleDigest)throw new IOException("INTAKE_MANIFEST_BYTES");
        var manifest=NativeJson.Read<JsonElement>(manifestBytes);
        if(manifest.GetProperty("version").GetString()!=receipt.Version||manifest.GetProperty("target").GetString()!="win-x64"||manifest.GetProperty("kind").GetString()!="windows-runtime-candidate")throw new IOException("INTAKE_VERSION_BINDING");
        long total=0;var count=0;foreach(var entry in manifest.GetProperty("files").EnumerateArray()){var n=entry.GetProperty("size").GetInt64();if(n<0||n>536870912||++count>50000||(total+=n)>2147483648)throw new IOException("INTAKE_PAYLOAD_BOUND");}
        if(total!=receipt.PayloadBytes)throw new IOException("INTAKE_PAYLOAD_IDENTITY");
        var build=NativeJson.Read<JsonElement>(Read("payloads/"+receipt.BundleDigest+"/legal/build-inputs.json",65536));
        if(build.GetProperty("databaseSchema").GetInt32()!=receipt.Schema)throw new IOException("INTAKE_SCHEMA_BINDING");
        var currentManifest=NativeJson.Read<JsonElement>(Read("payloads/"+head.ReleaseDigest+"/bundle.json",16*1024*1024));
        var currentVersion=currentManifest.GetProperty("version").GetString()!;
        if(!ValidVersion(receipt.Version)||!ValidVersion(currentVersion)||Version.Parse(receipt.Version)<Version.Parse(currentVersion))throw new IOException("INTAKE_DOWNGRADE_OR_VERSION");
        var webBytes=Read("web.json");if(InstalledActivationResolver.Hash(webBytes)!=head.WebDigest)throw new IOException("INTAKE_WEB_BINDING");var web=NativeJson.Read<InstalledWeb>(webBytes);
        var op=new ProtectedOperation(1,"activate",anchor.InstallationId,head.Epoch,headDigest,receipt.BundleDigest,head.DataId,head.MinimumSchema,receipt.Schema,head.MasterKeyDigest,head.PolicyDigest,head.PinDigest,web,head.Recovery,clock().AddMinutes(10).ToUnixTimeSeconds(),Guid.NewGuid().ToString("N"));
        var facts=new OperationFacts(currentVersion,receipt.Version,receipt.Channel,web.Origin,total,receipt.PolicyDigest,receipt.PinDigest);
        return new(new(1,op,facts),pointer.ReceiptDigest,headDigest,pointer.ReceiptDigest);
    }
    internal void RequireUnchanged(IntakeSelection selection)
    {var current=Select(selection.Target);if(current.HeadDigest!=selection.HeadDigest||current.ReceiptDigest!=selection.ReceiptDigest||current.Catalog.Operation.ReleaseDigest!=selection.Catalog.Operation.ReleaseDigest)throw new IOException("INTAKE_SELECTION_CHANGED");}
    internal NativeUpdateMode Mode()
    {
        if(Directory.EnumerateFiles(files.PathOf("intake"),"mode.json.partial-*").Any())throw new IOException("INTAKE_MODE_RECONCILIATION_REQUIRED");
        var m=NativeJson.Read<NativeUpdateMode>(Read("intake/mode.json"));
        if(m.Format!=1||m.InstallationId!=anchor.InstallationId||m.Mode is not("auto" or "notify")||(m.Mode=="auto")!=(m.DelegationId is not null))throw new IOException("INTAKE_MODE_UNCONFIGURED");if(m.DelegationId is not null)InstalledActivationResolver.Id(m.DelegationId);return m;
    }
    internal RoutineDelegation DelegationTemplate()
    {var d=NativeJson.Read<RoutineDelegation>(Read("intake/delegation-template.json"));if(d.Subject!=anchor.OwnerSid||d.InstallationId!=anchor.InstallationId||d.PolicyDigest!=anchor.IntakePolicyDigest||d.PinDigest!=anchor.IntakePinDigest)throw new IOException("INTAKE_DELEGATION_TEMPLATE");return d;}
    private static bool ValidVersion(string value)=>System.Text.RegularExpressions.Regex.IsMatch(value,"^[0-9]{1,5}\\.[0-9]{1,5}\\.[0-9]{1,5}$");
}

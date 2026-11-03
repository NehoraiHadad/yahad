using System.Text.Json;
using Yahad.Install;
namespace Yahad.Service;

// One application service. No update broker, owner pipe or privileged runtime
// updater is needed to admit the installer-published, administrator-owned release.
internal sealed record FirstInstallReceipt(int Format,string InstallationId,string DataId,string ReleaseDigest,string Version,string SeedDigest,int Port,string PackageDigest,string Phase,string RetryDigest);
internal sealed record FirstInstallSeed(int Format,string InstallationId,string DataId,string ReleaseDigest,string Nonce);
internal sealed record FreshDataAuthorization(string InstallationId,string DataId,string ReleaseDigest,Action Consume,Action? Begin=null);
internal static class FirstInstallProfile
{
    internal const string Product="Yahad.FirstInstall";
    internal static string StateRoot=>Path.Combine(MachineFolders.Required(Environment.SpecialFolder.CommonApplicationData),Product);
    internal static string BinaryRoot=>Path.Combine(MachineFolders.Required(Environment.SpecialFolder.ProgramFiles),Product);
    internal static string ReceiptPath=>Path.Combine(StateRoot,"installation.json");
    internal static FirstInstallReceipt ReadReceipt()
    {
        using var file=WindowsFileBoundary.OpenInstalledRead(ReceiptPath);var r=NativeJson.Read<FirstInstallReceipt>(file.Small(4096));
        return ValidateReceipt(r);
    }
    internal static FirstInstallReceipt ValidateReceipt(FirstInstallReceipt r)
    {
        if(r.Format!=2)throw new IOException("FIRST_INSTALL_FORMAT_REQUIRES_REPAIR");
        InstalledActivationResolver.Id(r.InstallationId);InstalledActivationResolver.Id(r.DataId);InstalledActivationResolver.Digest(r.ReleaseDigest);InstalledActivationResolver.Digest(r.SeedDigest);InstalledActivationResolver.Digest(r.PackageDigest);InstalledActivationResolver.Digest(r.RetryDigest);
        if(r.Format!=2||r.Version!=ApplicationRelease.Version||r.Port is <1024 or >65535||r.Phase!="committed")throw new IOException("FIRST_INSTALL_NOT_COMMITTED");return r;
    }
}

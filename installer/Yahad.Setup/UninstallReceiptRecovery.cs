using System.IO;
using Yahad.Install;

namespace Yahad.Setup;

internal sealed record ReceiptRecoveryEvidence(string ServiceSid,bool ServiceOwnedAndCleanStopped,string HostSha256,long HostBytes,string SetupSha256);

// The legacy receipt contributes no path, account, hash or removal authority.
// Every field must equal independent protected state; only its confirmed phase
// may differ. This operation protects one receipt and never continues removal.
internal static class UninstallReceiptRecovery
{
    internal static void Require(InstallerState? trusted,InstallerState? candidate,OwnerProcess owner,ReceiptRecoveryEvidence proof)
    {
        static bool Hex(string value,int length)=>value is not null&&value.Length==length&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
        if(trusted is null||candidate is null||trusted.Format!=1||!Guid.TryParseExact(trusted.InstallationId,"N",out _)||!Guid.TryParseExact(trusted.DataId,"N",out _)||trusted.PayloadSchema is <1 or >64)
            throw new ReceiptRecoveryRefusal(ReceiptRecoveryCheck.StateShape);
        if(trusted.Phase!="committed"||trusted.OwnerSid!=owner.Sid||trusted.ServiceSid!=proof.ServiceSid||!proof.ServiceOwnedAndCleanStopped)
            throw new ReceiptRecoveryRefusal(ReceiptRecoveryCheck.StateAuthority);
        if(!Hex(trusted.SourceCommit,40)||!Hex(trusted.PayloadDigest,64)||!Hex(trusted.PackageDigest,64)||!Hex(trusted.SetupSha256,64))
            throw new ReceiptRecoveryRefusal(ReceiptRecoveryCheck.StateDigests);
        if(trusted.Files is null||candidate.Files is null||trusted.Files.Length!=candidate.Files.Length||trusted.Files.Length is <10 or >50000||trusted.Files.Select(f=>f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=trusted.Files.Length)
            throw new ReceiptRecoveryRefusal(ReceiptRecoveryCheck.ManifestShape);
        if(candidate.Phase!=UninstallChoice.Phase(UninstallMode.DeleteData)||!UninstallChoice.SameInstallation(trusted,candidate))
            throw new ReceiptRecoveryRefusal(ReceiptRecoveryCheck.ReceiptIdentity);
        foreach(var member in trusted.Files)InstallPackage.Name(member.Path);
        var hosts=trusted.Files.Where(f=>f.Path=="host/Yahad.ServiceHost.exe").ToArray();
        if(hosts.Length!=1||!Hex(hosts[0].Sha256,64))throw new ReceiptRecoveryRefusal(ReceiptRecoveryCheck.HostRecord);
        if(hosts[0].Sha256!=proof.HostSha256||hosts[0].Size!=proof.HostBytes)throw new ReceiptRecoveryRefusal(ReceiptRecoveryCheck.HostDigest);
        if(trusted.SetupSha256!=proof.SetupSha256)throw new ReceiptRecoveryRefusal(ReceiptRecoveryCheck.SetupDigest);
    }
    internal static void Protect(InstallerState? trusted,InstallerState? candidate,OwnerProcess owner,ReceiptRecoveryEvidence proof,Action protect,Func<InstallerState> strictRead,Action requireUnchanged)
    {
        Require(trusted,candidate,owner,proof);
        protect();
        var reread=strictRead();
        if(trusted is null||!UninstallChoice.SameInstallation(trusted,reread)||reread.Phase!=UninstallChoice.Phase(UninstallMode.DeleteData))throw new ReceiptRecoveryRefusal(ReceiptRecoveryCheck.PostWriteIdentity);
        requireUnchanged();
    }
}

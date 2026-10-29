using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Setup;

// Reinstall restores only the exact removed binaries. It cannot create any
// data identity, key, authorization grant or activation record.
internal static class PreservedInstallation
{
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static void Require(InstallerState s,InstallPackageManifest package,OwnerProcess owner,Func<string,byte[]> read)
    {
        if(s.OwnerSid!=owner.Sid)throw new UnauthorizedAccessException("INSTALL_OWNER_CHANGED");
        if(s.Phase is not("uninstalled" or "reinstalling")||s.PackageDigest!=package.ZipSha256||s.PayloadDigest!=package.PayloadDigest||s.SourceCommit!=package.SourceCommit||s.SetupSha256!=owner.ImageSha256)throw new IOException("INSTALL_UPGRADE_REQUIRED");
        var receipt=NativeJson.Read<FirstInstallReceipt>(read("installation.json"));
        if(receipt.Format!=2||receipt.InstallationId!=s.InstallationId||receipt.DataId!=s.DataId||receipt.ReleaseDigest!=s.PayloadDigest||receipt.PackageDigest!=s.PackageDigest||receipt.Port!=s.Port||receipt.Version!="0.5.3"||receipt.Phase!="committed")throw new IOException("REINSTALL_RECEIPT_CHANGED");
        var activationBytes=read("activation.json");var activation=NativeJson.Read<InstalledActivation>(activationBytes);
        var epoch=NativeJson.Read<InstalledEpoch>(read("epoch.json"));var bootstrap=NativeJson.Read<JsonElement>(read("bootstrap.json"));
        if(activation.Format!=1||activation.InstallationId!=s.InstallationId||activation.DataId!=s.DataId||activation.ReleaseDigest!=s.PayloadDigest||activation.Phase!="committed"||activation.Recovery is not null||activation.MinimumSchema!=s.PayloadSchema||activation.MaximumSchema!=s.PayloadSchema||epoch.Format!=1||epoch.InstallationId!=s.InstallationId||epoch.Epoch!=activation.Epoch||epoch.ActivationDigest!=Hash(activationBytes)||bootstrap.GetProperty("installationId").GetString()!=s.InstallationId||bootstrap.GetProperty("activationReceiptDigest").GetString()!=Hash(activationBytes))throw new IOException("REINSTALL_ACTIVATION_CHANGED");
        var intentBytes=read("owner-intent.json");var intent=NativeJson.Read<InstalledOwnerIntent>(intentBytes);
        if(Hash(intentBytes)!=activation.OwnerIntentDigest||intent.Subject!=s.OwnerSid||intent.InstallationId!=s.InstallationId||intent.DataId!=s.DataId||intent.ReleaseDigest!=s.PayloadDigest||intent.Epoch!=activation.Epoch||intent.Operation!="activate")throw new IOException("REINSTALL_OWNER_CHANGED");
        if(Hash(read("web.json"))!=activation.WebDigest||Hash(read("datasets/"+s.DataId+"/master-key"))!=activation.MasterKeyDigest||Hash(read("datasets/"+s.DataId+"/update-policy"))!=activation.PolicyDigest||Hash(read("datasets/"+s.DataId+"/update-pin"))!=activation.PinDigest)throw new IOException("REINSTALL_IDENTITY_CHANGED");
    }
    internal static void VerifyDirectory(string root,IEnumerable<InstallMember> records,string prefix)
    {
        var expected=records.Where(f=>f.Path.StartsWith(prefix,StringComparison.Ordinal)).ToDictionary(f=>f.Path[prefix.Length..],StringComparer.OrdinalIgnoreCase);var seen=0;
        void Walk(string directory)
        {
            SafeDirectory.NoReparseAncestors(directory);
            foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if((File.GetAttributes(entry)&FileAttributes.ReparsePoint)!=0)throw new IOException("INSTALL_TARGET_CHANGED");
                if(Directory.Exists(entry)){Walk(entry);continue;}
                var relative=Path.GetRelativePath(root,entry).Replace('\\','/');
                if(!expected.TryGetValue(relative,out var record))throw new IOException("INSTALL_TARGET_CHANGED");
                using var file=new FileStream(entry,FileMode.Open,FileAccess.Read,FileShare.Read);
                if(file.Length!=record.Size||Convert.ToHexStringLower(SHA256.HashData(file))!=record.Sha256)throw new IOException("INSTALL_TARGET_CHANGED");seen++;
            }
        }
        Walk(root);if(seen!=expected.Count)throw new IOException("INSTALL_TARGET_CHANGED");
    }
}

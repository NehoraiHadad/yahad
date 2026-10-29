using System.Text.Json;
using Yahad.Install;
namespace Yahad.Service;

// Two administrator-created, service-read/delete-only grants form a monotonic
// first-start record. Both present: never started. Only retry present: attempt
// interrupted. Neither present: initialized; a missing DB must never reset it.
// Runtime never creates/recreates either grant. Callbacks execute only while
// admission continuously owns the actual exclusive data lease.
internal static class FirstInstallInitialization
{
 internal static FreshDataAuthorization? Read(string root,FirstInstallReceipt receipt,
     Func<string,byte[]> read,Action<string,string> consume)
 {
  var seed=Path.Combine(root,"initial-seed.json");var retry=Path.Combine(root,"initial-retry.json");
  if(!File.Exists(retry)){if(File.Exists(seed))throw new IOException("FIRST_INITIALIZATION_INCONSISTENT");return null;}
  void Validate(string path,string digest)
  {
   var bytes=read(path);var record=NativeJson.Read<FirstInstallSeed>(bytes);
   if(InstalledActivationResolver.Hash(bytes)!=digest||record.Format!=1||record.InstallationId!=receipt.InstallationId||record.DataId!=receipt.DataId||record.ReleaseDigest!=receipt.ReleaseDigest)throw new IOException("FIRST_SEED_BINDING");
   InstalledActivationResolver.Id(record.Nonce);
  }
  Validate(retry,receipt.RetryDigest);if(File.Exists(seed))Validate(seed,receipt.SeedDigest);
  return new(receipt.InstallationId,receipt.DataId,receipt.ReleaseDigest,
   ()=>consume(retry,receipt.RetryDigest),
   ()=>{if(File.Exists(seed))consume(seed,receipt.SeedDigest);});
 }
}

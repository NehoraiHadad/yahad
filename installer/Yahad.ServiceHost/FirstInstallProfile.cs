using System.Text.Json;
using Yahad.Install;
namespace Yahad.Service;

// One application service. No update broker, owner pipe or privileged runtime
// updater is needed to admit the installer-published, administrator-owned release.
internal sealed record FirstInstallReceipt(int Format,string InstallationId,string DataId,string ReleaseDigest,string Version,string SeedDigest,int Port,string PackageDigest,string Phase);
internal sealed record FirstInstallSeed(int Format,string InstallationId,string DataId,string ReleaseDigest,string Nonce);
internal sealed record FreshDataAuthorization(string InstallationId,string DataId,string ReleaseDigest,Action Consume);
internal static class FirstInstallProfile
{
    internal const string Product="Yahad.FirstInstall";
    internal static string StateRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),Product);
    internal static string BinaryRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Product);
    internal static string ReceiptPath=>Path.Combine(StateRoot,"installation.json");
    internal static FirstInstallReceipt ReadReceipt()
    {
        using var file=WindowsFileBoundary.OpenInstalledRead(ReceiptPath);var r=NativeJson.Read<FirstInstallReceipt>(file.Small(4096));
        InstalledActivationResolver.Id(r.InstallationId);InstalledActivationResolver.Id(r.DataId);InstalledActivationResolver.Digest(r.ReleaseDigest);InstalledActivationResolver.Digest(r.SeedDigest);InstalledActivationResolver.Digest(r.PackageDigest);
        if(r.Format!=1||r.Version!="0.5.3"||r.Port is <1024 or >65535||r.Phase!="committed")throw new IOException("FIRST_INSTALL_NOT_COMMITTED");return r;
    }
}
internal sealed class FirstInstallInstallationReader:IInstallationReader
{
    public InstallationBinding Read()
    {
        var receipt=FirstInstallProfile.ReadReceipt();using var file=WindowsFileBoundary.OpenInstalledRead(Path.Combine(FirstInstallProfile.StateRoot,"bootstrap.json"));
        var binding=WindowsInstallationReader.Parse(file.Small(1024));if(binding.InstallationId!=receipt.InstallationId)throw new IOException("FIRST_INSTALL_BINDING");return binding;
    }
}
internal sealed class FirstInstallActivationResolver:IInstalledActivationResolver,IObservedAdmission
{
    public TimeSpan AdmissionBudget=>TimeSpan.FromMinutes(5);public event Action? ObservedWork;
    public async Task<AuthorizedServiceStart> ResolveAsync(InstallationBinding binding,CancellationToken cancellation)
    {
        var r=FirstInstallProfile.ReadReceipt();if(r.InstallationId!=binding.InstallationId)throw new IOException("FIRST_INSTALL_IDENTITY");
        var root=FirstInstallProfile.StateRoot;
        using(var headFile=WindowsFileBoundary.OpenInstalledRead(Path.Combine(root,"activation.json")))
        {var head=NativeJson.Read<InstalledActivation>(headFile.Small(16384));if(head.DataId!=r.DataId||head.ReleaseDigest!=r.ReleaseDigest||InstalledActivationResolver.Hash(headFile.Small(16384))!=binding.ActivationReceiptDigest)throw new IOException("FIRST_INSTALL_SELECTION");}
        FreshDataAuthorization? fresh=null;
        var seedPath=Path.Combine(root,"initial-seed.json");
        if(File.Exists(seedPath))
        {
            var bytes=WindowsFileBoundary.ReadInitialSeed(seedPath);var seed=NativeJson.Read<FirstInstallSeed>(bytes);
            if(InstalledActivationResolver.Hash(bytes)!=r.SeedDigest||seed.Format!=1||seed.InstallationId!=r.InstallationId||seed.DataId!=r.DataId||seed.ReleaseDigest!=r.ReleaseDigest)throw new IOException("FIRST_SEED_BINDING");
            InstalledActivationResolver.Id(seed.Nonce);
            fresh=new(r.InstallationId,r.DataId,r.ReleaseDigest,()=>WindowsFileBoundary.ConsumeInitialSeed(seedPath,r.SeedDigest));
        }
        var resolver=new InstalledActivationResolver(root,new AdminInstalledControlFiles(),new LocalChildControlFactory(),()=>DateTimeOffset.UtcNow,
            stateFiles:InstalledStateFiles.SingleService(root),fresh:fresh);
        void Observed()=>ObservedWork?.Invoke();resolver.ObservedWork+=Observed;
        try{return await resolver.ResolveAsync(binding,cancellation);}finally{resolver.ObservedWork-=Observed;}
    }
}

using Yahad.Install;
namespace Yahad.Service;

internal sealed record RuntimeHostIdentity(int ProcessId,long Created);
internal sealed record RuntimeAdmissionBinding(InstallationBinding Binding,string ReleaseDigest,string Nonce,int ProcessId,long Created)
{
    internal void Validate()
    {
        if(Binding is null)throw new IOException("RUNTIME_ADMISSION_BINDING");
        InstalledActivationResolver.Id(Binding.InstallationId);InstalledActivationResolver.Digest(Binding.ActivationReceiptDigest);
        InstalledActivationResolver.Digest(ReleaseDigest);InstalledActivationResolver.Id(Nonce);
        if(ProcessId<1||Created<=0)throw new IOException("RUNTIME_ADMISSION_IDENTITY");
    }
}

// Implementations authenticate the actual authority server and keep its admission
// live. No implementation is selected by production Program or by a wire flag.
internal interface IRuntimeAdmissionChannel
{
    Task<RuntimeAdmissionBinding> WaitForAdmissionAsync(InstallationBinding binding,RuntimeHostIdentity host,CancellationToken cancellation);
    void RequireLaunch(RuntimeAdmissionBinding admission);
    Task ReportReadyAsync(RuntimeAdmissionBinding admission,CancellationToken cancellation);
}

internal sealed class WaitingRuntimeAuthorizer(IServiceStartAuthorizer installed,IInstallationReader installation,
    IRuntimeAdmissionChannel channel,Func<RuntimeHostIdentity> observeHost,
    Func<RuntimeAdmissionBinding,string,CancellationToken,Task<AuthorizedServiceStart>>? purposeStart=null):IServiceStartAuthorizer,IObservedAdmission
{
    private int started;
    public TimeSpan AdmissionBudget=>installed is IObservedAdmission observed?observed.AdmissionBudget:TimeSpan.FromSeconds(25);
    public event Action? ObservedWork
    {add{if(installed is IObservedAdmission observed)observed.ObservedWork+=value;}remove{if(installed is IObservedAdmission observed)observed.ObservedWork-=value;}}
    public async Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)
    {
        if(Interlocked.Exchange(ref started,1)!=0)throw new IOException("RUNTIME_ADMISSION_REPLAY");
        cancellation.ThrowIfCancellationRequested();var binding=installation.Read();var host=observeHost();
        var admission=await channel.WaitForAdmissionAsync(binding,host,cancellation);admission.Validate();
        void Require()
        {
            cancellation.ThrowIfCancellationRequested();
            if(admission.Binding!=binding||admission.ProcessId!=host.ProcessId||admission.Created!=host.Created||observeHost()!=host||installation.Read()!=binding)
                throw new UnauthorizedAccessException("RUNTIME_ADMISSION_BINDING");
            channel.RequireLaunch(admission);
        }
        Require();var purpose=(channel as IRuntimePurposeAdmissionChannel)?.Purpose??"admit";
        if(purpose is not("admit" or "offline-migrate" or "probation"))throw new IOException("RUNTIME_PURPOSE_UNKNOWN");
        if(purpose=="offline-migrate")
        {
            // Dispatch before resolving normal app state: pending DB migration
            // deliberately has no launchable activation/web/provider grant.
            var migration=await (purposeStart??throw new IOException("RUNTIME_PURPOSE_UNCONFIGURED"))(admission,purpose,cancellation);
            try{Require();if(migration.Release.Id!=admission.ReleaseDigest)throw new IOException("MIGRATION_RELEASE_BINDING");return migration;}
            catch{migration.Dispose();throw;}
        }
        var grant=purpose=="probation"?await (purposeStart??throw new IOException("RUNTIME_PURPOSE_UNCONFIGURED"))(admission,purpose,cancellation):await installed.AuthorizeAsync(cancellation);
        try
        {
            Require();if(grant.Release!=new AppRelease(admission.ReleaseDigest))throw new IOException("RUNTIME_ADMISSION_RELEASE");
            return new(grant.Release,new AdmittedLauncher(grant.Launcher,admission,channel,Require),grant.Recovery,grant);
        }
        catch{grant.Dispose();throw;}
    }
    private sealed class AdmittedLauncher(IPrivateAppLauncher inner,RuntimeAdmissionBinding admission,IRuntimeAdmissionChannel channel,Action require):IPrivateAppLauncher
    {
        public Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();require();
            if(release!=new AppRelease(admission.ReleaseDigest))throw new IOException("RUNTIME_ADMISSION_RELEASE");
            return inner.StartAsync(release,cancellation);
        }
        public async Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken cancellation)
        {
            await inner.WaitForHealthyAsync(session,cancellation);require();
            await channel.ReportReadyAsync(admission,cancellation);
        }
    }
}

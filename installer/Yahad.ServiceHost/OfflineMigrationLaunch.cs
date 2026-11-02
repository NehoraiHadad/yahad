using System.Buffers.Binary;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Service;

internal sealed record OfflineMigrationAdmission(RuntimeAdmissionBinding Runtime,string OperationId,string SourceDataId,
    string NewDataId,int SourceSchema,int TargetSchema,string EntryDigest,string OutputNonce)
{
    internal void Validate()
    {
        Runtime.Validate();InstalledActivationResolver.Id(OperationId);InstalledActivationResolver.Id(SourceDataId);InstalledActivationResolver.Id(NewDataId);
        InstalledActivationResolver.Id(OutputNonce);InstalledActivationResolver.Digest(EntryDigest);
        if(SourceDataId==NewDataId||SourceSchema is <1 or >100000||TargetSchema<=SourceSchema||TargetSchema>100000)throw new IOException("MIGRATION_ADMISSION_SCOPE");
    }
}

internal interface IRuntimePurposeAdmissionChannel
{
    string Purpose{get;}
    Task ReportMigrationAsync(RuntimeAdmissionBinding admission,MigrationSandboxObservation observation,CancellationToken cancellation);
    Task ReportMigrationExitAsync(RuntimeAdmissionBinding admission,int code,CancellationToken cancellation);
}
// Purpose events travel only on the already authenticated, fixed SCM pipe.
// They cannot carry executable paths, commands or authority capabilities.
internal sealed record RuntimePurposeMessage(int Format,string Kind,string Id,RuntimeAdmissionBinding Admission,
    MigrationSandboxObservation? Observation=null,int? ExitCode=null);
internal static class RuntimePurposeWire
{
    internal static async Task WriteAsync(Stream stream,RuntimePurposeMessage message,CancellationToken cancellation)
    {
        Validate(message);var bytes=JsonSerializer.SerializeToUtf8Bytes(message);if(bytes.Length>32768)throw new IOException("RUNTIME_PURPOSE_BOUND");
        var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);
        await stream.WriteAsync(header,cancellation);await stream.WriteAsync(bytes,cancellation);await stream.FlushAsync(cancellation);
    }
    internal static async Task<RuntimePurposeMessage> ReadAsync(Stream stream,CancellationToken cancellation)
    {
        var header=new byte[4];await stream.ReadExactlyAsync(header,cancellation);var count=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(count is <2 or >32768)throw new IOException("RUNTIME_PURPOSE_BOUND");var bytes=new byte[count];await stream.ReadExactlyAsync(bytes,cancellation);
        var value=NativeJson.Read<RuntimePurposeMessage>(bytes);Validate(value);return value;
    }
    internal static void Validate(RuntimePurposeMessage message)
    {
        message.Admission.Validate();InstalledActivationResolver.Id(message.Id);
        if(message.Format!=2||message.Id!=message.Admission.Nonce||(message.Kind switch
        {"migration-started"=>message.Observation is null||message.ExitCode is not null,"migration-completed"=>message.Observation is not null||message.ExitCode is null,
            "migration-started-accepted" or "migration-completed-accepted"=>message.Observation is not null||message.ExitCode is not null,_=>true}))
            throw new IOException("RUNTIME_PURPOSE_FRAME");
    }
    internal static async Task ReportAndWaitAsync(Stream stream,RuntimePurposeMessage message,string acknowledgement,Action requireCurrent,CancellationToken cancellation)
    {
        if(acknowledgement!=(message.Kind switch{"migration-started"=>"migration-started-accepted","migration-completed"=>"migration-completed-accepted",_=>throw new IOException("MIGRATION_ACK_METHOD")}))
            throw new IOException("MIGRATION_ACK_METHOD");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(15));
        requireCurrent();await WriteAsync(stream,message,deadline.Token);var reply=await ReadAsync(stream,deadline.Token);requireCurrent();
        if(reply.Kind!=acknowledgement||reply.Id!=message.Id||reply.Admission!=message.Admission)throw new UnauthorizedAccessException("MIGRATION_ACK_BINDING");
    }
}

internal sealed class InstalledMigrationSpecResolver(InstalledStateFiles files):IInstalledMigrationSpecResolver
{
    internal MigrationLaunchDescriptor Read(RuntimeAdmissionBinding admission)
    {
        admission.Validate();var path=files.PathOf(Path.Combine("migration-staging",admission.Nonce,"descriptor.json"));
        var descriptor=NativeJson.Read<MigrationLaunchDescriptor>(files.Read(path,32768));descriptor.Plan.Validate();
        if(descriptor.Plan.OperationId!=admission.Nonce||descriptor.Plan.InstallationId!=admission.Binding.InstallationId||descriptor.Plan.PayloadDigest!=admission.ReleaseDigest)
            throw new UnauthorizedAccessException("MIGRATION_DESCRIPTOR_BINDING");
        NativeMigrationAdmission.SpecForLaunch(files,descriptor).Validate();return descriptor;
    }
    internal OfflineMigrationAdmission Admission(RuntimeAdmissionBinding runtime)
    {var d=Read(runtime);var p=d.Plan;return new(runtime,p.OperationId,p.SourceDataId,p.NewDataId,p.SourceSchema,p.TargetSchema,p.EntryDigest,d.Nonce);}
    public Task<VerifiedMigrationSpec> ResolveAsync(OfflineMigrationAdmission admission,CancellationToken cancellation)
    {cancellation.ThrowIfCancellationRequested();return Task.FromResult(NativeMigrationAdmission.SpecForLaunch(files,Read(admission.Runtime)));}
}

internal sealed class InstalledOfflineMigrationStart(IInstallationReader installation,Func<RuntimeHostIdentity> host,
    IRuntimeAdmissionChannel channel,IRuntimePurposeAdmissionChannel purposes,RuntimeAdmissionBinding binding,
    InstalledMigrationSpecResolver resolver,IMigrationProcessFactory processes):IServiceStartAuthorizer
{
    public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();if(purposes.Purpose!="offline-migrate")throw new IOException("MIGRATION_PURPOSE_REQUIRED");
        var admission=resolver.Admission(binding);var launch=new WaitingOfflineMigrationLaunch(installation,host,new ExistingAdmission(channel,admission),resolver,processes);
        var launcher=new Launcher(launch,binding,channel,purposes);return Task.FromResult(new AuthorizedServiceStart(new(binding.ReleaseDigest),launcher,launcher,purpose:ServiceRunPurpose.FiniteOfflineTask));
    }
    private sealed class ExistingAdmission(IRuntimeAdmissionChannel channel,OfflineMigrationAdmission expected):IOfflineMigrationAdmissionChannel
    {
        public Task<OfflineMigrationAdmission> WaitAsync(InstallationBinding installation,RuntimeHostIdentity host,CancellationToken cancellation)
        {cancellation.ThrowIfCancellationRequested();RequireCurrent(expected);return Task.FromResult(expected);}
        public void RequireCurrent(OfflineMigrationAdmission admission)
        {if(admission!=expected)throw new UnauthorizedAccessException("MIGRATION_ADMISSION_CHANGED");channel.RequireLaunch(admission.Runtime);}
    }
    private sealed class Launcher(WaitingOfflineMigrationLaunch launch,RuntimeAdmissionBinding binding,IRuntimeAdmissionChannel channel,
        IRuntimePurposeAdmissionChannel purposes):IPrivateAppLauncher,IAppRecoveryBarrier
    {
        public Task RequireStartAllowedAsync(AppRelease release,CancellationToken cancellation)
        {cancellation.ThrowIfCancellationRequested();channel.RequireLaunch(binding);if(release.Id!=binding.ReleaseDigest)throw new IOException("MIGRATION_RELEASE_BINDING");return Task.CompletedTask;}
        public async Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken cancellation)
        {await RequireStartAllowedAsync(release,cancellation);return new Session(await launch.StartAsync(cancellation),binding,purposes);}
        public Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken cancellation)=>((Session)session).ReportHealthyAsync(cancellation);
    }
    internal sealed class Session(IMigrationChildSession inner,RuntimeAdmissionBinding binding,IRuntimePurposeAdmissionChannel purposes):IContainedAppSession
    {
        internal IMigrationChildSession Inner=>inner;internal TaskCompletionSource Reported{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim report=new(1,1);private bool exitReported;
        internal async Task ReportHealthyAsync(CancellationToken cancellation)
        {
            try{await purposes.ReportMigrationAsync(binding,inner.Observe(),cancellation);Reported.TrySetResult();}
            catch(Exception failure){Reported.TrySetException(failure);throw;}
        }
        public Task RequestGracefulStopAsync(CancellationToken cancellation)=>inner.RequestGracefulStopAsync(cancellation);
        public async Task<int> WaitForRootExitAsync(CancellationToken cancellation)
        {
            var code=await inner.WaitForRootExitAsync(cancellation);await Reported.Task.WaitAsync(cancellation);await inner.WaitForTreeEmptyAsync(cancellation);
            await report.WaitAsync(cancellation);try{if(!exitReported){using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));await purposes.ReportMigrationExitAsync(binding,code,deadline.Token);exitReported=true;}}finally{report.Release();}return code;
        }
        public Task WaitForTreeEmptyAsync(CancellationToken cancellation)=>inner.WaitForTreeEmptyAsync(cancellation);
        public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation)=>inner.TerminateAndWaitForTreeEmptyAsync(cancellation);
        public ValueTask DisposeAsync()=>inner.DisposeAsync();
    }
}
internal interface IOfflineMigrationAdmissionChannel
{
    Task<OfflineMigrationAdmission> WaitAsync(InstallationBinding installation,RuntimeHostIdentity host,CancellationToken cancellation);
    void RequireCurrent(OfflineMigrationAdmission admission);
}
internal interface IInstalledMigrationSpecResolver
{
    // Reads the protected operation and verified new-copy scope. The wire DTO
    // never supplies an executable, command, filesystem path or package SID.
    Task<VerifiedMigrationSpec> ResolveAsync(OfflineMigrationAdmission admission,CancellationToken cancellation);
}

// A typed purpose of the existing waiting runtime host. In particular, this
// does not call the normal installed activation resolver or launch the web app.
internal sealed class WaitingOfflineMigrationLaunch(IInstallationReader installation,Func<RuntimeHostIdentity> observeHost,
    IOfflineMigrationAdmissionChannel channel,IInstalledMigrationSpecResolver resolver,IMigrationProcessFactory processes)
{
    private int issued;
    internal async Task<IMigrationChildSession> StartAsync(CancellationToken cancellation)
    {
        if(Interlocked.Exchange(ref issued,1)!=0)throw new IOException("MIGRATION_ADMISSION_REPLAY");
        var binding=installation.Read();var host=observeHost();var admission=await channel.WaitAsync(binding,host,cancellation);admission.Validate();
        void Require()
        {
            cancellation.ThrowIfCancellationRequested();channel.RequireCurrent(admission);
            if(admission.Runtime.Binding!=binding||installation.Read()!=binding||observeHost()!=host||admission.Runtime.ProcessId!=host.ProcessId||admission.Runtime.Created!=host.Created)
                throw new UnauthorizedAccessException("MIGRATION_ADMISSION_GENERATION");
        }
        Require();var spec=await resolver.ResolveAsync(admission,cancellation);Require();
        if(spec.Plan.InstallationId!=binding.InstallationId||spec.Plan.OperationId!=admission.OperationId||spec.Plan.SourceDataId!=admission.SourceDataId||
            spec.Plan.NewDataId!=admission.NewDataId||spec.Plan.SourceSchema!=admission.SourceSchema||spec.Plan.TargetSchema!=admission.TargetSchema||
            spec.Plan.EntryDigest!=admission.EntryDigest||spec.Plan.PayloadDigest!=admission.Runtime.ReleaseDigest||spec.Nonce!=admission.OutputNonce)
            throw new UnauthorizedAccessException("MIGRATION_ADMISSION_PLAN");
        // The factory transfers every created process/job before reporting a
        // failed launch. Its caller must retain that owner on uncertain cleanup.
        return await processes.StartAsync(spec,cancellation);
    }
}

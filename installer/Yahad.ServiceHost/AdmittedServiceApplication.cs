using System.Runtime.InteropServices;
using System.Security.Principal;
using Yahad.Install;
namespace Yahad.Service;

// Concrete dual-service runtime composition. Program deliberately continues to
// select its existing first-install profile; this path requires the immutable
// dual-service anchor/layout and installer-provisioned cross-process rights.
internal static class AdmittedServiceApplication
{
    internal static int RunInstalled()
    {
        var runtime=(SecurityIdentifier)new NTAccount(ServiceDefinition.Account).Translate(typeof(SecurityIdentifier));
        var authority=(SecurityIdentifier)new NTAccount("NT SERVICE",WindowsFixedRuntimeService.AuthorityName).Translate(typeof(SecurityIdentifier));
        ServiceIdentityPolicy.Require(new WindowsServiceTokenReader().Read(),runtime.Value);
        if(!WindowsInstalledRuntimeProfile.UpdaterAnchorPresent())throw new IOException("RUNTIME_UPDATER_NOT_ENROLLED");
        var root=Path.GetDirectoryName(WindowsInstallationReader.DescriptorPath)!;
        var (roles,anchorBytes)=InstalledUpdaterLayout.ReadRoles(authority,runtime);
        var value=NativeJson.Read<RuntimeAnchor>(anchorBytes);
        InstalledActivationResolver.Id(value.InstallationId);InstalledActivationResolver.Digest(value.IntakePolicyDigest);InstalledActivationResolver.Digest(value.IntakePinDigest);
        if(value.Format!=1||value.RuntimeService!=ServiceDefinition.Name||value.AuthorityService!=WindowsFixedRuntimeService.AuthorityName||value.ProbeSha256 is null)throw new IOException("RUNTIME_INSTALLATION_ANCHOR");
        InstalledActivationResolver.Digest(value.ProbeSha256);
        var probe=InstalledUpdaterLayout.ProbeExecutable;
        if(value.FullSystemPolicyDigest is not null)
        {
            InstalledActivationResolver.Digest(value.FullSystemPolicyDigest);
            // Initial full-profile provisioning runs as this actual Runtime
            // account, rather than the different administrator who approved UAC.
            _=InstalledMigrationProfile.EnsureForCurrentRuntime(value.InstallationId,value.FullSystemPolicyDigest);
        }
        InstalledProcessObjectAccess.PublishRuntimePeerAccess(runtime.Value,authority.Value,roles.HasRecoveryHelpers);
        var baselineFiles=InstalledStateFiles.InstalledRuntime(root,roles,probe,value.ProbeSha256);
        var binaryFiles=InstalledStateFiles.InstalledRuntime(InstalledUpdaterLayout.BinaryRoot,roles,probe,value.ProbeSha256);
        var selectedProbe=InstalledOperationalProbe.Require(baselineFiles,binaryFiles,value.InstallationId,value.FullSystemPolicyDigest,probe,value.ProbeSha256);
        var files=InstalledStateFiles.InstalledRuntime(root,roles,selectedProbe.Executable,selectedProbe.Sha256);
        var reader=new RoleInstallationReader(files,value.InstallationId);
        FreshDataAuthorization? fresh=null;
        if(File.Exists(Path.Combine(root,"initial-seed.json"))||File.Exists(Path.Combine(root,"initial-retry.json")))
        {
            var receipt=FirstInstallProfile.ValidateReceipt(NativeJson.Read<FirstInstallReceipt>(files.Read(files.PathOf("installation.json"),4096)));
            if(receipt.InstallationId!=value.InstallationId)throw new IOException("FIRST_INSTALL_IDENTITY");
            fresh=FirstInstallInitialization.Read(root,receipt,WindowsFileBoundary.ReadInitialSeed,WindowsFileBoundary.ConsumeInitialSeed);
        }
        var resolver=new InstalledActivationResolver(root,new RoleControlFiles(files),new LocalChildControlFactory(),()=>DateTimeOffset.UtcNow,stateFiles:files,fresh:fresh);
        return Run(new NativeScmApi(),new InstalledStartAuthorizer(new WindowsServiceTokenReader(),reader,resolver),reader,Environment.Exit,files,
            (error,steps)=>ServiceStartupDiagnostic.TryRecord(StartupDiagnosticRole.Runtime,error,steps));
    }
    internal static int Run(IScmApi api,IServiceStartAuthorizer installed,IInstallationReader installation,Action<int> unsafeExit,InstalledStateFiles? purposeFiles=null,Action<Exception,StartupStep[]>? reportFailure=null)
    {
        var result=new ServiceRunResult(ServiceFailure.StatusTransport,false);
        api.Dispatch(ServiceDefinition.Name,arguments=>
        {
            ServiceController? controller=null;
            ProbationRuntimeBinding? probation=null;WindowsRuntimeAdmissionChannel? activeChannel=null;
            async Task<AuthorizedServiceStart> Purpose(RuntimeAdmissionBinding admission,string kind,CancellationToken cancellation)
            {
                var files=purposeFiles??throw new IOException("RUNTIME_PURPOSE_NOT_PROVISIONED");
                var purposeChannel=activeChannel??throw new IOException("RUNTIME_CHANNEL_UNAVAILABLE");
                if(kind=="offline-migrate")return await new InstalledOfflineMigrationStart(installation,ObserveCurrentHost,purposeChannel,purposeChannel,admission,
                    new InstalledMigrationSpecResolver(files),NativeMigrationSandbox.Installed()).AuthorizeAsync(cancellation);
                if(kind!="probation")throw new IOException("RUNTIME_PURPOSE_UNKNOWN");
                var verifiedProbation=InstalledProbationDescriptor.Read(files,admission);probation=verifiedProbation;
                var probationResolver=new InstalledActivationResolver(files.Root,new RoleControlFiles(files),new LocalChildControlFactory(),()=>DateTimeOffset.UtcNow,
                    stateFiles:files,processFactory:spec=>new ProbationProcessLauncher(spec,verifiedProbation));
                return await new InstalledStartAuthorizer(new WindowsServiceTokenReader(),installation,probationResolver).AuthorizeAsync(cancellation);
            }
            using var channel=new WindowsRuntimeAdmissionChannel(
                (id,c)=>controller?.PrepareUpdateAsync(id,c)??throw new IOException("RUNTIME_CONTROLLER_UNAVAILABLE"),
                (id,c)=>controller?.CancelUpdateAsync(id,c)??throw new IOException("RUNTIME_CONTROLLER_UNAVAILABLE"),
                ()=>controller?.Control(ServiceDefinition.Stop),
                (id,c)=>controller?.ReleaseEffectsAsync(probation??throw new IOException("PROBATION_NOT_ADMITTED"),id,c)??throw new IOException("RUNTIME_CONTROLLER_UNAVAILABLE"),purposeFiles?.Roles?.HasRecoveryHelpers==true);
            activeChannel=channel;
            controller=new(new WaitingRuntimeAuthorizer(installed,installation,channel,ObserveCurrentHost,Purpose),
                reportFailure:reportFailure is null?null:error=>reportFailure(error,controller!.StartupSteps));
            api.Register(ServiceDefinition.Name,controller.Control);
            if(arguments.Length!=1||arguments[0]!=ServiceDefinition.Name)
            {api.Publish(new(ServiceState.Stopped,0,0,0,1066,(uint)ServiceFailure.ArgumentsRejected));result=new(ServiceFailure.ArgumentsRejected,true);return;}
            result=controller.RunAsync(api.Publish).GetAwaiter().GetResult();
            if(!result.QuiescenceConfirmed||result.Failure==ServiceFailure.StatusTransport)unsafeExit(1);
        });
        return result.Failure==ServiceFailure.None&&result.QuiescenceConfirmed?0:1;
    }
    private static RuntimeHostIdentity ObserveCurrentHost()
    {
        if(!GetProcessTimes(GetCurrentProcess(),out var created,out _,out _,out _))throw new System.ComponentModel.Win32Exception();
        return new(Environment.ProcessId,created);
    }
    private sealed record RuntimeAnchor(int Format,string InstallationId,string RuntimeService,string AuthorityService,string OwnerSid,string IntakePolicyDigest,string IntakePinDigest,string? ProbeSha256=null,string? FullSystemPolicyDigest=null);
    private sealed class RoleInstallationReader(InstalledStateFiles files,string installation):IInstallationReader
    {
        public InstallationBinding Read()
        {var result=WindowsInstallationReader.Parse(files.Read(files.PathOf("bootstrap.json"),1024));if(result.InstallationId!=installation)throw new IOException("RUNTIME_INSTALLATION_CHANGED");return result;}
    }
    private sealed class RoleControlFiles(InstalledStateFiles files):IInstalledControlFiles
    {
        public byte[] Read(string path,int maximum,ICollection<IDisposable> retained)
        {var value=files.Open(path);retained.Add(value);return value.Small(maximum);}
    }
    [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessTimes(IntPtr process,out long created,out long exit,out long kernel,out long user);
}

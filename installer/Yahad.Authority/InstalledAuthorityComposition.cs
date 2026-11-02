using System.Security.Principal;
using System.ComponentModel;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed class InstalledAuthorityComposition
{
    internal InstalledStateFiles Files{get;}internal InstalledAuthorityAnchor Anchor{get;}internal AuthorityEndpointRouter Router{get;}
    internal OwnerApprovalLedger Ledger{get;}
    private InstalledAuthorityComposition(InstalledStateFiles files,InstalledAuthorityAnchor anchor,string runtimeSid,Func<RuntimeAuthorityGate> oldGate,
        Func<CancellationToken,Task> quiesce,IChildControlFactory channels,Func<IAuthorityRuntimeLifecycle> runtime,Func<DateTimeOffset> clock,
        Func<string,CancellationToken,Task<bool>>? prepareUpdate=null,Func<string,CancellationToken,Task>? cancelPreparation=null,
        Func<CatalogOperation,string,InstalledUpdateTransaction>? transaction=null)
    {
        Files=files;Anchor=anchor;files.ValidateRoot();
        var ledger=Ledger=new OwnerApprovalLedger(files,anchor.InstallationId,anchor.OwnerSid,clock);var intake=new InstalledIntakeSelection(files,anchor,clock);
        var modeAvailable=true;
        try{_=intake.Mode();}
        catch(JsonException){modeAvailable=false;}
        catch(FileNotFoundException){modeAvailable=false;}
        catch(DirectoryNotFoundException){modeAvailable=false;}
        catch(Win32Exception error)when(error.NativeErrorCode is 2 or 3){modeAvailable=false;}
        catch(IOException error)when(error.Message is "INTAKE_MODE_RECONCILIATION_REQUIRED" or "INTAKE_MODE_UNCONFIGURED" or "INSTALLED_IDENTIFIER" or "NATIVE_DUPLICATE_FIELD" or "RECOVERY_FILE_BOUND" or "RECOVERY_SHORT_READ")
        {modeAvailable=false;}
        // Only malformed/missing policy is an availability state. Native role,
        // token, ownership and DACL denials still propagate without repair.
        Router=new(files,anchor,runtimeSid,intake,ledger,selection=>
        {
            var gate=oldGate();var writer=new ProtectedStateTransactionWriter(files,anchor.InstallationId,c=>{c.ThrowIfCancellationRequested();gate.RequireQuiescence();return Task.CompletedTask;},ledger,channels,clock);
            return new(runtimeSid,new PrivateOperationCatalog(selection),ledger,writer,gate,runtime());
        },quiesce,prepareUpdate:prepareUpdate,cancelPreparation:cancelPreparation,transaction:transaction);
        if(!modeAvailable)Router.MarkBootRecoveryRequired(); // Never invent Notify or admit a scheduled update.
    }
    // Test-only entry: a real private root plus synthetic service-role metadata.
    // This is not reachable from Program, protocol frames, environment or CLI.
    internal static InstalledAuthorityComposition PrivateFixture(string root,string anchorDigest,string runtimeSid,string probe,string probeDigest,
        Action<string,InstalledFileRole> roleObservation,Func<RuntimeAuthorityGate> oldGate,Func<CancellationToken,Task> quiesce,IChildControlFactory channels,
        Func<InstallationBinding,IServiceStartAuthorizer> runtime,Func<DateTimeOffset> clock,
        Func<string,CancellationToken,Task<bool>>? prepareUpdate=null,Func<string,CancellationToken,Task>? cancelPreparation=null,
        Func<CatalogOperation,string,InstalledUpdateTransaction>? transaction=null)
    {
        var files=InstalledStateFiles.Private(root,probe,probeDigest,roleObservation);var bytes=files.Read(files.PathOf("installation.json"),4096);
        if(InstalledActivationResolver.Hash(bytes)!=anchorDigest)throw new IOException("AUTHORITY_COMPOSITION_ANCHOR");var anchor=InstalledAuthorityReader.Parse(bytes);
        return new(files,anchor,runtimeSid,oldGate,quiesce,channels,()=>new RuntimeHealthCoordinator(runtime),clock,prepareUpdate,cancelPreparation,transaction);
    }
    // Complete installed construction. Call only after the normal-host action is
    // approved: this verifies actual service token/file roles; it never repairs
    // permissions or registers a service. Production Program does not call it.
    internal static InstalledAuthorityComposition Installed(Func<RuntimeAuthorityGate> oldGate,Func<CancellationToken,Task> quiesce,
        IChildControlFactory channels,Func<IAuthorityRuntimeLifecycle> admittedRuntime,IAuthorityRuntimeUpdatePreparation existingRuntimePreparation,
        IAuthorityRuntimeRecoveryObservation existingRuntimeObservation,Func<DateTimeOffset> clock)
    {
        var anchor=InstalledAuthorityReader.Read();if(anchor.ProbeSha256 is null)throw new IOException("AUTHORITY_PROBE_UNCONFIGURED");
        var authority=(SecurityIdentifier)new NTAccount("NT SERVICE",InstalledAuthorityReader.AuthorityService).Translate(typeof(SecurityIdentifier));
        var runtime=(SecurityIdentifier)new NTAccount(ServiceDefinition.Account).Translate(typeof(SecurityIdentifier));
        var root=Path.GetDirectoryName(WindowsInstallationReader.DescriptorPath)!;
        var probe=InstalledUpdaterLayout.ProbeExecutable;
        var roles=new InstalledFileRoles(authority,runtime);if(anchor.FullSystemPolicyDigest is not null)roles=roles.WithRecoveryHelpers();
        using(var image=WindowsFileBoundary.OpenInstalledRoleRead(probe,roles,InstalledFileRole.Anchor))
        {using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);var buffer=new byte[1024*1024];long offset=0;int count;while((count=image.Read(offset,buffer))>0){hash.AppendData(buffer,0,count);offset+=count;}if(Convert.ToHexStringLower(hash.GetHashAndReset())!=anchor.ProbeSha256)throw new IOException("AUTHORITY_PROBE_IDENTITY");}
        var baselineFiles=InstalledStateFiles.Installed(root,roles,probe,anchor.ProbeSha256);
        var binaryFiles=InstalledStateFiles.Installed(InstalledUpdaterLayout.BinaryRoot,roles,probe,anchor.ProbeSha256);
        var selectedProbe=InstalledOperationalProbe.Require(baselineFiles,binaryFiles,anchor.InstallationId,anchor.FullSystemPolicyDigest,probe,anchor.ProbeSha256);
        var files=InstalledStateFiles.Installed(root,roles,selectedProbe.Executable,selectedProbe.Sha256);
        return new(files,anchor,runtime.Value,oldGate,quiesce,channels,admittedRuntime,clock,
            existingRuntimePreparation.PrepareUpdateAsync,existingRuntimePreparation.CancelUpdateAsync,
            (catalog,id)=>InstalledUpdateTransaction.Begin(files,anchor,catalog,id,existingRuntimeObservation.AcquireRecoveryQuiescence(),clock));
    }
}

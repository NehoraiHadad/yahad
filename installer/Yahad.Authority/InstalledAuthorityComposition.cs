using System.Security.Principal;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed class InstalledAuthorityComposition
{
    internal InstalledStateFiles Files{get;}internal InstalledAuthorityAnchor Anchor{get;}internal AuthorityEndpointRouter Router{get;}
    private InstalledAuthorityComposition(InstalledStateFiles files,InstalledAuthorityAnchor anchor,string runtimeSid,Func<RuntimeAuthorityGate> oldGate,
        Func<CancellationToken,Task> quiesce,IChildControlFactory channels,Func<InstallationBinding,IServiceStartAuthorizer> runtime,Func<DateTimeOffset> clock)
    {
        Files=files;Anchor=anchor;files.ValidateRoot();
        var ledger=new OwnerApprovalLedger(files,anchor.InstallationId,anchor.OwnerSid,clock);var intake=new InstalledIntakeSelection(files,anchor,clock);
        _=intake.Mode(); // Never silently turn an unknown/existing automatic policy into notify.
        Router=new(files,anchor,runtimeSid,intake,ledger,selection=>
        {
            var gate=oldGate();var writer=new ProtectedStateTransactionWriter(files,anchor.InstallationId,c=>{c.ThrowIfCancellationRequested();gate.RequireQuiescence();return Task.CompletedTask;},ledger,channels,clock);
            return new(runtimeSid,new PrivateOperationCatalog(selection),ledger,writer,gate,new RuntimeHealthCoordinator(runtime));
        },quiesce);
    }
    // Test-only entry: a real private root plus synthetic service-role metadata.
    // This is not reachable from Program, protocol frames, environment or CLI.
    internal static InstalledAuthorityComposition PrivateFixture(string root,string anchorDigest,string runtimeSid,string probe,string probeDigest,
        Action<string,InstalledFileRole> roleObservation,Func<RuntimeAuthorityGate> oldGate,Func<CancellationToken,Task> quiesce,IChildControlFactory channels,
        Func<InstallationBinding,IServiceStartAuthorizer> runtime,Func<DateTimeOffset> clock)
    {
        var files=InstalledStateFiles.Private(root,probe,probeDigest,roleObservation);var bytes=files.Read(files.PathOf("installation.json"),4096);
        if(InstalledActivationResolver.Hash(bytes)!=anchorDigest)throw new IOException("AUTHORITY_COMPOSITION_ANCHOR");var anchor=InstalledAuthorityReader.Parse(bytes);
        return new(files,anchor,runtimeSid,oldGate,quiesce,channels,runtime,clock);
    }
    // Complete installed construction. Call only after the normal-host action is
    // approved: this verifies actual service token/file roles; it never repairs
    // permissions or registers a service. Production Program does not call it.
    internal static InstalledAuthorityComposition Installed(Func<RuntimeAuthorityGate> oldGate,Func<CancellationToken,Task> quiesce,
        IChildControlFactory channels,Func<InstallationBinding,IServiceStartAuthorizer> admittedRuntime,Func<DateTimeOffset> clock)
    {
        var anchor=InstalledAuthorityReader.Read();if(anchor.ProbeSha256 is null)throw new IOException("AUTHORITY_PROBE_UNCONFIGURED");
        var authority=(SecurityIdentifier)new NTAccount("NT SERVICE",InstalledAuthorityReader.AuthorityService).Translate(typeof(SecurityIdentifier));
        var runtime=(SecurityIdentifier)new NTAccount(ServiceDefinition.Account).Translate(typeof(SecurityIdentifier));
        var root=Path.GetDirectoryName(WindowsInstallationReader.DescriptorPath)!;
        var probe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),ServiceDefinition.Name,"bootstrap","probe","node","node.exe");
        using(var image=WindowsFileBoundary.OpenInstalledRoleRead(probe,new(authority,runtime),InstalledFileRole.Anchor))
        {using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);var buffer=new byte[1024*1024];long offset=0;int count;while((count=image.Read(offset,buffer))>0){hash.AppendData(buffer,0,count);offset+=count;}if(Convert.ToHexStringLower(hash.GetHashAndReset())!=anchor.ProbeSha256)throw new IOException("AUTHORITY_PROBE_IDENTITY");}
        return new(InstalledStateFiles.Installed(root,new(authority,runtime),probe,anchor.ProbeSha256),anchor,runtime.Value,oldGate,quiesce,channels,admittedRuntime,clock);
    }
}

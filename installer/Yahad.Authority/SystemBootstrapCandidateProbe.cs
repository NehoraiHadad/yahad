using Yahad.Install;
namespace Yahad.Authority;

internal sealed class SystemCandidateProbeOwnershipException:IOException
{
    private readonly JobProcess process;private readonly IReadOnlyList<IDisposable> resources;
    private readonly Action? cleared;
    internal SystemCandidateProbeOwnershipException(JobProcess process,IReadOnlyList<IDisposable> resources,Exception failure,Action? cleared=null)
        :base("SYSTEM_CANDIDATE_PROBE_CLEANUP_UNCONFIRMED",failure){this.process=process;this.resources=resources;this.cleared=cleared;NativeCleanupOwnershipGate.Retain(this,CleanupCoreAsync);}
    internal Task RetryCleanupAsync(CancellationToken cancellation)=>NativeCleanupOwnershipGate.RetryAsync(this,cancellation);
    private async Task CleanupCoreAsync(CancellationToken cancellation)
    {
        await process.TerminateAndWaitForTreeEmptyAsync(cancellation);_=await process.WaitAsync(cancellation);await process.WaitForTreeEmptyAsync(cancellation);
        process.Dispose();foreach(var resource in resources)resource.Dispose();cleared?.Invoke();
    }
}

// Executes only the fixed self-description command of the already source-bound
// staged helper, in an actual bounded owned job. Exit and whole-tree cleanup are
// observed independently. It does not create a service or admit an application.
internal static class SystemBootstrapCandidateProbe
{
    internal static async Task VerifyAsync(InstalledStateFiles state,InstalledStateFiles binaries,VerifiedSystemOperation operation,
        FullSystemUpdatePolicy policy,CancellationToken cancellation)
    {
        NativeCleanupOwnershipGate.RequireClear();policy.RequireCurrent(operation);var helper=operation.Components.Single(c=>c.Kind==SystemComponentKind.Helper);
        var root=binaries.PathOf(FixedSystemComponents.SlotsDirectory+"/"+operation.ManifestDigest+"/"+helper.SlotRelativeDirectory);
        var pins=new List<IDisposable>();JobProcess? process=null;var retained=false;
        try
        {
            foreach(var file in helper.Files)
            {
                FixedSystemComponents.Member(file.RelativePath);var path=Path.Combine(root,file.RelativePath.Replace('/',Path.DirectorySeparatorChar));
                using(var read=binaries.Open(path)){if(read.Length!=file.Size)throw new IOException("SYSTEM_CANDIDATE_PROBE_CLOSURE");}
                pins.Add(SystemBootstrapHelperNative.PinImage(path,file.Sha256));
            }
            var temporary=state.PathOf(".authority/candidate-probes");if(!Directory.Exists(temporary))state.CreateDirectory(temporary);
            temporary=Path.Combine(temporary,operation.OperationId);if(!Directory.Exists(temporary))state.CreateDirectory(temporary);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(30));
            policy.RequireCurrent(operation);
            try{process=JobProcess.Start(Path.Combine(root,helper.EntryPoint),["--describe"],temporary,LaunchEnvironment.Clean(temporary));}
            catch(JobStartQuiescenceUnconfirmedException owned){process=owned.OwnedProcess;throw;}
            if(await process.WaitAsync(deadline.Token)!=0)throw new SystemBootstrapCandidateFailure("SYSTEM_CANDIDATE_HELPER_PROBE_FAILED");
            await process.WaitForTreeEmptyAsync(deadline.Token);policy.RequireCurrent(operation);
        }
        catch(Exception failure)
        {
            if(process is not null)
            {
                using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try{await process.TerminateAndWaitForTreeEmptyAsync(cleanup.Token);_=await process.WaitAsync(cleanup.Token);await process.WaitForTreeEmptyAsync(cleanup.Token);}
                catch(Exception uncertain){retained=true;throw new SystemCandidateProbeOwnershipException(process,pins,new AggregateException(failure,uncertain));}
            }
            throw;
        }
        finally{if(!retained){process?.Dispose();foreach(var pin in pins)pin.Dispose();}}
    }
}

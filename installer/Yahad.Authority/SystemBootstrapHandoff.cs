using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal enum SystemBootstrapPhase {Prepared,CustodyConfirmed,ComponentIntent,ComponentPublished,AuthorityStartIntent,AuthorityHealthy,RuntimeVerified,AwaitingHelperExit,HelperIntent,HelperPublished,Complete,RollbackIntent,RolledBack,RecoveryRequired,RepairIntent,RepairPublished}
internal sealed record SystemBootstrapFileState(string RelativePath,long Size,string Sha256);
internal sealed record SystemBootstrapComponentState(SystemComponentKind Kind,string EntryPoint,string EntrySha256,SystemBootstrapFileState[] Files);
internal sealed record SystemBootstrapSnapshot(int Format,string InstallationId,long Epoch,string Version,string ManifestDigest,string? OperationalProbeDigest,SystemBootstrapComponentState[] Components,string? PolicyDigest=null,string? SourceCommit=null);
internal sealed record SystemBootstrapRecord(int Format,int Sequence,string PreviousDigest,string InstallationId,string OperationId,string ManifestDigest,string GrantId,string GrantBindingDigest,
    long ExpectedEpoch,SystemBootstrapPhase Phase,SystemComponentKind? Component,SystemBootstrapSnapshot Before);
internal sealed class SystemBootstrapCandidateFailure(string reason):IOException(reason);

// Implementations retain actual process/job/data/operation owners. Completion of
// a stop request or a caller Boolean is never a custody or health observation.
internal interface ISystemBootstrapKernel
{
    Task<SystemBootstrapSnapshot> PrepareAsync(VerifiedSystemOperation operation,CancellationToken cancellation);
    Task VerifyHelperCandidateAsync(VerifiedSystemOperation operation,CancellationToken cancellation);
    Task RequireCustodyAsync(VerifiedSystemOperation operation,CancellationToken cancellation);
    Task RequireDataUnchangedAsync(VerifiedSystemOperation operation,CancellationToken cancellation);
    Task PublishComponentAsync(VerifiedSystemOperation operation,SystemBootstrapSnapshot before,SystemComponentKind kind,CancellationToken cancellation);
    Task StartAndObserveAuthorityAsync(VerifiedSystemOperation operation,bool previous,CancellationToken cancellation);
    Task ObserveRuntimeBootstrapAsync(VerifiedSystemOperation operation,CancellationToken cancellation);
    Task WaitForOldHelperExitAsync(VerifiedSystemOperation operation,CancellationToken cancellation);
    Task PublishSelectionAsync(VerifiedSystemOperation operation,CancellationToken cancellation);
    Task RestorePreviousAsync(VerifiedSystemOperation operation,SystemBootstrapSnapshot before,CancellationToken cancellation);
    void RecordCandidateFailure(VerifiedSystemOperation operation);
    void RequireNotQuarantined(VerifiedSystemOperation operation);
}

// Uses the same native update-serial lease as InstalledUpdateJournal. A full
// transaction may supply a duplicate of its already-retained exclusive lease.
internal sealed class SystemBootstrapJournal:IDisposable
{
    private readonly InstalledStateFiles files;private readonly VerifiedSystemOperation operation;private readonly WindowsFileBoundary.DataLease lease;
    private readonly string root;private readonly List<SystemBootstrapRecord> records=[];private string digest="";
    internal SystemBootstrapRecord? Current=>records.LastOrDefault();
    internal bool HasPhase(SystemBootstrapPhase phase)=>records.Any(r=>r.Phase==phase);
    internal SystemBootstrapJournal(InstalledStateFiles files,VerifiedSystemOperation operation,WindowsFileBoundary.DataLease? serialLease=null)
    {
        this.files=files;this.operation=operation;
        foreach(var part in new[]{".authority",".authority/update-serial",".authority/bootstrap-handoffs"})
        {var path=files.PathOf(part);if(!Directory.Exists(path))files.CreateDirectory(path);using var pin=files.PinDirectory(path);}
        lease=serialLease?.DuplicateForDirectory(files.PathOf(".authority/update-serial"))??files.Lease(files.PathOf(".authority/update-serial"));
        root=files.PathOf(".authority/bootstrap-handoffs/"+operation.OperationId);
        try
        {
                var others=Directory.EnumerateDirectories(files.PathOf(".authority/bootstrap-handoffs")).Where(path=>!path.Equals(root,StringComparison.OrdinalIgnoreCase)).Take(1001).ToArray();
                if(others.Length>1000)throw new IOException("SYSTEM_HANDOFF_CATALOG_BOUND");
                foreach(var directory in others)
                {
                    InstalledActivationResolver.Id(Path.GetFileName(directory));
                    string[] entries;using(var pin=files.PinDirectory(directory))entries=Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal).Take(257).ToArray();
                    if(entries.Length==0)
                    {
                        // No published intent could have authorized effects.
                        // Preserve the empty namespace rather than blocking a new
                        // grant forever or pretending that a transaction finished.
                        var residue=files.PathOf(".authority/bootstrap-handoff-residue");if(!Directory.Exists(residue))files.CreateDirectory(residue);
                        if(Directory.EnumerateFileSystemEntries(residue).Take(1001).Count()>=1000)throw new IOException("SYSTEM_HANDOFF_RESIDUE_BOUND");
                        if(!MoveFileExW(@"\\?\"+directory,@"\\?\"+Path.Combine(residue,"empty-"+Path.GetFileName(directory)+"-"+Guid.NewGuid().ToString("N")),8))throw new IOException("SYSTEM_HANDOFF_RESIDUE_PUBLICATION");continue;
                    }
                    if(entries.Length is <1 or >256||entries.Any(p=>Directory.Exists(p)||Path.GetFileName(p).Contains(".partial",StringComparison.Ordinal)))throw new IOException("SYSTEM_HANDOFF_OTHER_UNFINISHED");
                    RequireTerminalChain(directory,entries);
                }
            if(Directory.Exists(root))Load();else files.CreateDirectory(root);
        }
        catch{lease.Dispose();throw;}
    }
    private void RequireTerminalChain(string directory,string[] entries)
    {
        var last=InspectEntries(files,directory,entries,operation.InstallationId,Path.GetFileName(directory));
        if(last.Phase is not(SystemBootstrapPhase.Complete or SystemBootstrapPhase.RolledBack))throw new IOException("SYSTEM_HANDOFF_OTHER_UNFINISHED");
    }
    // Readonly evidence inspection, never a grant. Historical terminal source
    // receipts/slots need not remain cached merely to prove journal completion.
    internal static SystemBootstrapRecord? Inspect(InstalledStateFiles files,string installation,string operationId)
    {
        InstalledActivationResolver.Id(installation);InstalledActivationResolver.Id(operationId);var directory=files.PathOf(".authority/bootstrap-handoffs/"+operationId);
        try{if((File.GetAttributes(directory)&FileAttributes.Directory)==0)throw new IOException("SYSTEM_HANDOFF_JOURNAL_KIND");}catch(FileNotFoundException){return null;}catch(DirectoryNotFoundException){return null;}
        using var pinned=files.PinDirectory(directory);var entries=Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal).Take(257).ToArray();
        if(entries.Length==0)return null;return InspectEntries(files,directory,entries,installation,operationId);
    }
    private static SystemBootstrapRecord InspectEntries(InstalledStateFiles files,string directory,string[] entries,string installation,string operationId)
    {
        if(entries.Length is <1 or >256||entries.Any(p=>Directory.Exists(p)||Path.GetFileName(p).Contains(".partial",StringComparison.Ordinal)))throw new IOException("SYSTEM_HANDOFF_INSPECTION_UNCERTAIN");
        var prior=new List<SystemBootstrapRecord>();var previous="";
        foreach(var path in entries)
        {
            if(Path.GetFileName(path)!=$"{prior.Count+1:D5}.json")throw new IOException("SYSTEM_HANDOFF_OTHER_SEQUENCE");
            var bytes=files.Read(path,8*1024*1024);var value=NativeJson.Read<SystemBootstrapRecord>(bytes);
            InstalledActivationResolver.Id(value.OperationId);InstalledActivationResolver.Id(value.GrantId);
            InstalledActivationResolver.Digest(value.ManifestDigest);InstalledActivationResolver.Digest(value.GrantBindingDigest);
            if(value.Format!=1||value.Sequence!=prior.Count+1||value.PreviousDigest!=previous||value.InstallationId!=installation||value.OperationId!=operationId||operationId!=Path.GetFileName(directory)||!Enum.IsDefined(value.Phase)||value.Before.Format!=1||value.Before.InstallationId!=installation||value.Before.Epoch!=value.ExpectedEpoch||value.ExpectedEpoch<1)throw new IOException("SYSTEM_HANDOFF_OTHER_BINDING");
            if(prior.FirstOrDefault() is {} first&&(value.ManifestDigest!=first.ManifestDigest||value.GrantId!=first.GrantId||value.GrantBindingDigest!=first.GrantBindingDigest||value.ExpectedEpoch!=first.ExpectedEpoch||JsonSerializer.Serialize(value.Before)!=JsonSerializer.Serialize(first.Before)))throw new IOException("SYSTEM_HANDOFF_OTHER_SNAPSHOT");
            RequireTransition(prior,value);prior.Add(value);previous=Hash(bytes);
        }
        return prior.Last();
    }
    private void Load()
    {
        using var pin=files.PinDirectory(root);var paths=Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal).Take(257).ToArray();
        if(paths.Length>256||paths.Any(Directory.Exists))throw new IOException("SYSTEM_HANDOFF_JOURNAL_UNCERTAIN");
        foreach(var path in paths.Where(p=>!Path.GetFileName(p).Contains(".partial",StringComparison.Ordinal)))
        {
            if(Path.GetFileName(path)!=$"{records.Count+1:D5}.json")throw new IOException("SYSTEM_HANDOFF_SEQUENCE");
            var bytes=files.Read(path,8*1024*1024);var record=NativeJson.Read<SystemBootstrapRecord>(bytes);Validate(record);records.Add(record);digest=Hash(bytes);
        }
        var partials=paths.Where(p=>Path.GetFileName(p).Contains(".partial",StringComparison.Ordinal)).ToArray();
        if(partials.Length>1)throw new IOException("SYSTEM_HANDOFF_JOURNAL_UNCERTAIN");
        foreach(var path in partials)
        {
            var prefix=$"{records.Count+1:D5}.json.partial-";var name=Path.GetFileName(path);
            if(!name.StartsWith(prefix,StringComparison.Ordinal)||!Guid.TryParseExact(name[prefix.Length..],"N",out var id)||id.ToString("N")!=name[prefix.Length..])throw new IOException("SYSTEM_HANDOFF_JOURNAL_UNCERTAIN");
            // Publication precedes all effects for a step. An unpublished next
            // record cannot authorize effects; retain its bytes as residue and
            // reconcile native observations from the last published intent.
            using(var pinned=files.Open(path)){_=pinned.Length;}
            var parent=files.PathOf(".authority/bootstrap-handoff-residue");if(!Directory.Exists(parent))files.CreateDirectory(parent);
            parent=Path.Combine(parent,operation.OperationId);if(!Directory.Exists(parent))files.CreateDirectory(parent);
            if(Directory.EnumerateFiles(parent).Take(33).Count()>=32)throw new IOException("SYSTEM_HANDOFF_RESIDUE_BOUND");
            if(!MoveFileExW(@"\\?\"+path,@"\\?\"+Path.Combine(parent,name+".retained-"+Guid.NewGuid().ToString("N")),8))throw new IOException("SYSTEM_HANDOFF_RESIDUE_PUBLICATION");
        }
    }
    private void Validate(SystemBootstrapRecord value)
    {
        if(value.Format!=1||value.Sequence!=records.Count+1||value.PreviousDigest!=digest||value.InstallationId!=operation.InstallationId||value.OperationId!=operation.OperationId||
            value.ManifestDigest!=operation.ManifestDigest||value.GrantId!=operation.GrantId||value.GrantBindingDigest!=operation.GrantBindingDigest||value.ExpectedEpoch!=operation.ExpectedBootstrapEpoch||
            !Enum.IsDefined(value.Phase)||value.Component is {} kind&&!Enum.IsDefined(kind))throw new IOException("SYSTEM_HANDOFF_BINDING");
        if(records.Count==0&&value.Phase!=SystemBootstrapPhase.Prepared||records.Count>0&&JsonSerializer.Serialize(value.Before)!=JsonSerializer.Serialize(records[0].Before))throw new IOException("SYSTEM_HANDOFF_SNAPSHOT_CHANGED");
        if(value.Before.Format!=1||value.Before.InstallationId!=operation.InstallationId||value.Before.Epoch!=operation.ExpectedBootstrapEpoch||value.Before.Version!=operation.BootstrapFromVersion)throw new IOException("SYSTEM_HANDOFF_PREVIOUS_BINDING");
        RequireTransition(records,value);
    }
    private static void RequireTransition(IReadOnlyList<SystemBootstrapRecord> records,SystemBootstrapRecord value)
    {
        if(value.Component is {} component&&!Enum.IsDefined(component))throw new IOException("SYSTEM_HANDOFF_COMPONENT");
        if(records.Count==0&&value.Phase!=SystemBootstrapPhase.Prepared)throw new IOException("SYSTEM_HANDOFF_INITIAL_PHASE");
        if(records.LastOrDefault()?.Phase is SystemBootstrapPhase.Complete or SystemBootstrapPhase.RolledBack)throw new IOException("SYSTEM_HANDOFF_ALREADY_TERMINAL");
        bool HasPhase(SystemBootstrapPhase phase)=>records.Any(r=>r.Phase==phase);
        if(records.LastOrDefault() is {} last)
        {
            var valid=value.Phase switch
            {
                SystemBootstrapPhase.RecoveryRequired=>true,
                SystemBootstrapPhase.RepairIntent=>true,
                SystemBootstrapPhase.RepairPublished=>last.Phase==SystemBootstrapPhase.RepairIntent,
                SystemBootstrapPhase.RollbackIntent=>true,
                SystemBootstrapPhase.RolledBack=>last.Phase==SystemBootstrapPhase.RollbackIntent,
                SystemBootstrapPhase.CustodyConfirmed=>last.Phase==SystemBootstrapPhase.Prepared,
                SystemBootstrapPhase.ComponentIntent=>last.Phase==SystemBootstrapPhase.CustodyConfirmed&&value.Component==SystemComponentKind.RuntimeHost||last.Phase==SystemBootstrapPhase.ComponentPublished&&(last.Component,value.Component) is (SystemComponentKind.RuntimeHost,SystemComponentKind.Probe) or (SystemComponentKind.Probe,SystemComponentKind.Authority),
                SystemBootstrapPhase.ComponentPublished=>last.Phase==SystemBootstrapPhase.ComponentIntent&&last.Component==value.Component,
                SystemBootstrapPhase.AuthorityStartIntent=>last.Phase==SystemBootstrapPhase.ComponentPublished&&last.Component==SystemComponentKind.Authority,
                SystemBootstrapPhase.AuthorityHealthy=>last.Phase==SystemBootstrapPhase.AuthorityStartIntent,
                SystemBootstrapPhase.RuntimeVerified=>last.Phase==SystemBootstrapPhase.AuthorityHealthy,
                SystemBootstrapPhase.AwaitingHelperExit=>last.Phase==SystemBootstrapPhase.RuntimeVerified,
                SystemBootstrapPhase.HelperIntent=>last.Phase is SystemBootstrapPhase.AwaitingHelperExit or SystemBootstrapPhase.HelperIntent||last.Phase==SystemBootstrapPhase.RecoveryRequired&&HasPhase(SystemBootstrapPhase.AwaitingHelperExit)&&!HasPhase(SystemBootstrapPhase.RollbackIntent),
                SystemBootstrapPhase.HelperPublished=>last.Phase==SystemBootstrapPhase.HelperIntent,
                SystemBootstrapPhase.Complete=>last.Phase==SystemBootstrapPhase.HelperPublished||last.Phase==SystemBootstrapPhase.RecoveryRequired&&HasPhase(SystemBootstrapPhase.HelperPublished),
                _=>false
            };
            if(!valid)throw new IOException("SYSTEM_HANDOFF_TRANSITION");
        }
    }
    internal void Append(SystemBootstrapPhase phase,SystemBootstrapSnapshot before,SystemComponentKind? component=null,Action<string>? fault=null)
    {
        if(records.Count>=256)throw new IOException("SYSTEM_HANDOFF_JOURNAL_BOUND");
        var frozen=NativeJson.Read<SystemBootstrapSnapshot>(JsonSerializer.SerializeToUtf8Bytes(before));
        var value=new SystemBootstrapRecord(1,records.Count+1,digest,operation.InstallationId,operation.OperationId,operation.ManifestDigest,operation.GrantId,operation.GrantBindingDigest,
            operation.ExpectedBootstrapEpoch,phase,component,frozen);Validate(value);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value);if(bytes.Length>8*1024*1024)throw new IOException("SYSTEM_HANDOFF_JOURNAL_BOUND");
        var final=Path.Combine(root,$"{value.Sequence:D5}.json");var temporary=final+".partial-"+Guid.NewGuid().ToString("N");
        using(var output=files.Create(temporary)){output.Write(bytes);fault?.Invoke("before-journal-flush");output.Flush(true);}
        if(!MoveFileExW(@"\\?\"+temporary,@"\\?\"+final,8))throw new IOException("SYSTEM_HANDOFF_JOURNAL_PUBLICATION");
        records.Add(value);digest=Hash(bytes);fault?.Invoke("journal-"+phase);
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Dispose()=>lease.Dispose();
    [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]private static extern bool MoveFileExW(string from,string to,uint flags);
}

// The bounded helper executes RunAsync. The NEW healthy Authority executes
// FinishHelperAsync only after that old helper actually exits. Neither method
// launches the app or migrates/changes a database.
internal sealed class SystemBootstrapHandoff(ISystemBootstrapKernel kernel,Func<VerifiedSystemOperation,CancellationToken,Task> requireCurrent)
{
    internal async Task RunAsync(VerifiedSystemOperation operation,SystemBootstrapJournal journal,CancellationToken cancellation,Action<string>? fault=null)
    {
        await PrepareAsync(operation,journal,cancellation,fault);
        await ContinuePreparedAsync(operation,journal,cancellation,fault:fault);
    }
    // The old Authority may durably prepare and verify the family-data backup
    // before transferring actual owners to the helper. Preparation publishes no
    // binaries and never attempts to stop its own service.
    internal async Task<SystemBootstrapSnapshot> PrepareAsync(VerifiedSystemOperation operation,SystemBootstrapJournal journal,CancellationToken cancellation,Action<string>? fault=null)
    {
        if(journal.Current is not null)throw new IOException("SYSTEM_HANDOFF_RECONCILIATION_REQUIRED");
        await requireCurrent(operation,cancellation);kernel.RequireNotQuarantined(operation);
        var before=await kernel.PrepareAsync(operation,cancellation);journal.Append(SystemBootstrapPhase.Prepared,before,fault:fault);
        return before;
    }
    internal async Task ContinuePreparedAsync(VerifiedSystemOperation operation,SystemBootstrapJournal journal,CancellationToken cancellation,
        Func<VerifiedSystemOperation,CancellationToken,Task>? beforeDisruption=null,Action<string>? fault=null)
    {
        if(journal.Current?.Phase!=SystemBootstrapPhase.Prepared)throw new IOException("SYSTEM_HANDOFF_NOT_PREPARED");
        var before=journal.Current.Before;
        var disrupted=false;var authorityAttempted=false;var helperAttempted=false;
        try
        {
            await requireCurrent(operation,cancellation);kernel.RequireNotQuarantined(operation);
            if(beforeDisruption is not null)await beforeDisruption(operation,cancellation);
            await requireCurrent(operation,cancellation);
            helperAttempted=true;await kernel.VerifyHelperCandidateAsync(operation,cancellation);
            await kernel.RequireCustodyAsync(operation,cancellation);await kernel.RequireDataUnchangedAsync(operation,cancellation);
            journal.Append(SystemBootstrapPhase.CustodyConfirmed,before,fault:fault);disrupted=true;
            foreach(var kind in new[]{SystemComponentKind.RuntimeHost,SystemComponentKind.Probe,SystemComponentKind.Authority})
            {
                await requireCurrent(operation,cancellation);journal.Append(SystemBootstrapPhase.ComponentIntent,before,kind,fault);
                await kernel.PublishComponentAsync(operation,before,kind,cancellation);journal.Append(SystemBootstrapPhase.ComponentPublished,before,kind,fault);
            }
            await requireCurrent(operation,cancellation);await kernel.RequireDataUnchangedAsync(operation,cancellation);
            journal.Append(SystemBootstrapPhase.AuthorityStartIntent,before,fault:fault);authorityAttempted=true;
            await kernel.StartAndObserveAuthorityAsync(operation,false,cancellation);journal.Append(SystemBootstrapPhase.AuthorityHealthy,before,fault:fault);
            await kernel.ObserveRuntimeBootstrapAsync(operation,cancellation);journal.Append(SystemBootstrapPhase.RuntimeVerified,before,fault:fault);
            await kernel.RequireDataUnchangedAsync(operation,cancellation);journal.Append(SystemBootstrapPhase.AwaitingHelperExit,before,fault:fault);
        }
        catch(Exception error)
        {
            if((authorityAttempted||helperAttempted)&&error is SystemBootstrapCandidateFailure)kernel.RecordCandidateFailure(operation);
            if(disrupted)await RollbackAsync(operation,journal,before,fault);throw;
        }
    }
    internal async Task FinishHelperAsync(VerifiedSystemOperation operation,SystemBootstrapJournal journal,CancellationToken cancellation,Action<string>? fault=null)
    {
        var state=journal.Current??throw new IOException("SYSTEM_HANDOFF_NOT_PREPARED");
        if(state.Phase is not(SystemBootstrapPhase.AwaitingHelperExit or SystemBootstrapPhase.HelperIntent or SystemBootstrapPhase.HelperPublished)&&!(state.Phase==SystemBootstrapPhase.RecoveryRequired&&journal.HasPhase(SystemBootstrapPhase.AwaitingHelperExit)&&!journal.HasPhase(SystemBootstrapPhase.RollbackIntent)))throw new IOException("SYSTEM_HELPER_FINALIZATION_PHASE");
        try
        {
            await requireCurrent(operation,cancellation);await kernel.WaitForOldHelperExitAsync(operation,cancellation);
            // Health is freshly observed after restart too; a prior journal label
            // is not an admission/health observation for this process generation.
            await kernel.StartAndObserveAuthorityAsync(operation,false,cancellation);await kernel.ObserveRuntimeBootstrapAsync(operation,cancellation);
            await kernel.RequireDataUnchangedAsync(operation,cancellation);
            if(!journal.HasPhase(SystemBootstrapPhase.HelperPublished))
            {
                await requireCurrent(operation,cancellation);
                journal.Append(SystemBootstrapPhase.HelperIntent,state.Before,SystemComponentKind.Helper,fault);
                await requireCurrent(operation,cancellation);
                await kernel.PublishComponentAsync(operation,state.Before,SystemComponentKind.Helper,cancellation);
                journal.Append(SystemBootstrapPhase.HelperPublished,state.Before,SystemComponentKind.Helper,fault);
            }
            await requireCurrent(operation,cancellation);await kernel.PublishSelectionAsync(operation,cancellation);
            journal.Append(SystemBootstrapPhase.Complete,state.Before,fault:fault);
        }
        catch{journal.Append(SystemBootstrapPhase.RecoveryRequired,state.Before,fault:fault);throw;}
    }
    internal async Task ReconcileAsync(VerifiedSystemOperation operation,SystemBootstrapJournal journal,CancellationToken cancellation,Action<string>? fault=null)
    {
        var state=journal.Current??throw new IOException("SYSTEM_HANDOFF_NOT_PREPARED");
        if(state.Phase is SystemBootstrapPhase.Complete or SystemBootstrapPhase.RolledBack)return;
        if(state.Phase is SystemBootstrapPhase.AwaitingHelperExit or SystemBootstrapPhase.HelperIntent or SystemBootstrapPhase.HelperPublished||state.Phase==SystemBootstrapPhase.RecoveryRequired&&journal.HasPhase(SystemBootstrapPhase.AwaitingHelperExit)&&!journal.HasPhase(SystemBootstrapPhase.RollbackIntent))
        {
            try{await requireCurrent(operation,cancellation);}catch(UnauthorizedAccessException){await RollbackAsync(operation,journal,state.Before,fault);return;}
            await FinishHelperAsync(operation,journal,cancellation,fault);return;
        }
        await RollbackAsync(operation,journal,state.Before,fault);
    }
    internal async Task ReconcileRollbackOnlyAsync(VerifiedSystemOperation operation,SystemBootstrapJournal journal,CancellationToken cancellation,Action<string>? fault=null)
    {
        cancellation.ThrowIfCancellationRequested();var state=journal.Current??throw new IOException("SYSTEM_HANDOFF_NOT_PREPARED");
        if(state.Phase is SystemBootstrapPhase.Complete or SystemBootstrapPhase.RolledBack)return;
        await RollbackAsync(operation,journal,state.Before,fault);
    }
    private async Task RollbackAsync(VerifiedSystemOperation operation,SystemBootstrapJournal journal,SystemBootstrapSnapshot before,Action<string>? fault)
    {
        using var cleanup=new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            journal.Append(SystemBootstrapPhase.RollbackIntent,before,fault:fault);
            await kernel.RequireCustodyAsync(operation,cleanup.Token);await kernel.RequireDataUnchangedAsync(operation,cleanup.Token);
            await kernel.RestorePreviousAsync(operation,before,cleanup.Token);await kernel.StartAndObserveAuthorityAsync(operation,true,cleanup.Token);
            await kernel.RequireDataUnchangedAsync(operation,cleanup.Token);journal.Append(SystemBootstrapPhase.RolledBack,before,fault:fault);
        }
        catch{journal.Append(SystemBootstrapPhase.RecoveryRequired,before,fault:fault);throw;}
    }
}

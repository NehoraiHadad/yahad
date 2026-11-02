using System.Security.Cryptography;
using System.Text.Json.Serialization;
namespace Yahad.Install;

internal sealed record OfflineMigrationPlan(int Format,string InstallationId,string OperationId,string SourceDataId,
    string CloneOperationId,string NewDataId,string BackupId,string BackupReceiptDigest,int SourceSchema,int TargetSchema,
    string PayloadDigest,string EntryDigest,string MasterKeyDigest,string PolicyDigest,string PinDigest,string? SystemOperationId=null)
{
    internal const string Entry="app/src/native-migrate-v1.ts";
    internal void Validate()
    {
        foreach(var value in new[]{InstallationId,OperationId,SourceDataId,CloneOperationId,NewDataId,BackupId})
            if(!Guid.TryParseExact(value,"N",out var id)||id.ToString("N")!=value)throw new IOException("MIGRATION_ID");
        foreach(var value in new[]{BackupReceiptDigest,PayloadDigest,EntryDigest,MasterKeyDigest,PolicyDigest,PinDigest})Digest(value);
        if(SystemOperationId is { } system&&(!Guid.TryParseExact(system,"N",out var parent)||parent.ToString("N")!=system))throw new IOException("MIGRATION_SYSTEM_OPERATION_ID");
        if(Format!=1||SourceDataId==NewDataId||SourceSchema is <1 or >100000||TargetSchema<=SourceSchema||TargetSchema>100000)throw new IOException("MIGRATION_PLAN_SCOPE");
    }
    internal static void Digest(string value)
    {if(value is null||!System.Text.RegularExpressions.Regex.IsMatch(value,"^[a-f0-9]{64}$"))throw new IOException("MIGRATION_DIGEST");}
}

// Constructed by the full-policy authority mapper after verifying the payload
// and NEW clone. Never deserialize this trusted launch spec from Owner/app IPC.
// Native runtime independently pins images and observes the actual lowbox token.
internal sealed record VerifiedMigrationSpec(OfflineMigrationPlan Plan,string NodePath,string NodeDigest,string EntryPath,
    string PayloadRoot,string CloneRoot,string DataDirectory,string RequestFile,string ResultFile,string TemporaryDirectory,
    string ExpectedRuntimeSid,string ExpectedAppContainerSid,string Nonce,string SourceDataDirectory,string ControlRoot,
    string ExpectedAuthoritySid)
{
    internal void Validate()
    {
        Plan.Validate();OfflineMigrationPlan.Digest(NodeDigest);
        if(!Guid.TryParseExact(Nonce,"N",out var nonce)||nonce.ToString("N")!=Nonce)throw new IOException("MIGRATION_NONCE");
        foreach(var path in new[]{NodePath,EntryPath,PayloadRoot,CloneRoot,DataDirectory,RequestFile,ResultFile,TemporaryDirectory,SourceDataDirectory,ControlRoot})
            if(!Path.IsPathFullyQualified(path)||!Same(WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(path)),path))throw new IOException("MIGRATION_ABSOLUTE_PATH");
        if(!Same(NodePath,Path.Combine(PayloadRoot,"runtime","node","node.exe"))||!Same(EntryPath,Path.Combine(PayloadRoot,"app","src","native-migrate-v1.ts"))||
            !Same(DataDirectory,Path.Combine(CloneRoot,"data"))||Same(DataDirectory,SourceDataDirectory)||!Within(CloneRoot,ControlRoot)||!Within(SourceDataDirectory,ControlRoot)||
            !Within(PayloadRoot,ControlRoot)||!Within(TemporaryDirectory,ControlRoot)||!Within(RequestFile,ControlRoot)||!Within(ResultFile,ControlRoot)||
            Same(RequestFile,ResultFile)||Within(RequestFile,DataDirectory)||Within(ResultFile,DataDirectory))throw new IOException("MIGRATION_PATH_SCOPE");
        var expectedClone=Path.Combine(ControlRoot,"datasets",Plan.SourceDataId,"restore-"+Plan.CloneOperationId,"completed");
        if(!Same(CloneRoot,expectedClone))throw new IOException("MIGRATION_CLONE_BINDING");
        var workspace=Path.Combine(ControlRoot,"migration-staging",Plan.OperationId);
        if(!Same(PayloadRoot,Path.Combine(workspace,"payload"))||!Same(RequestFile,Path.Combine(workspace,"work","request.json"))||
            !Same(ResultFile,Path.Combine(workspace,"work","result.json"))||!Same(TemporaryDirectory,Path.Combine(workspace,"temp")))throw new IOException("MIGRATION_WORKSPACE_BINDING");
        var sourceRoot=Path.Combine(ControlRoot,"datasets",Plan.SourceDataId);
        if(!Same(SourceDataDirectory,Path.Combine(sourceRoot,"data")))
        {
            var relative=Path.GetRelativePath(sourceRoot,SourceDataDirectory).Replace('\\','/').Split('/');
            if(relative.Length!=3||!relative[0].StartsWith("restore-",StringComparison.Ordinal)||relative[1]!="completed"||relative[2]!="data"||
                !Guid.TryParseExact(relative[0][8..],"N",out var sourceRestore)||sourceRestore.ToString("N")!=relative[0][8..])throw new IOException("MIGRATION_SOURCE_BINDING");
        }
        if(!ExpectedRuntimeSid.StartsWith("S-1-5-80-",StringComparison.Ordinal)||!ExpectedAuthoritySid.StartsWith("S-1-5-80-",StringComparison.Ordinal)||ExpectedRuntimeSid==ExpectedAuthoritySid||
            !ExpectedAppContainerSid.StartsWith("S-1-15-2-",StringComparison.Ordinal)||ExpectedAppContainerSid=="S-1-15-2-1")throw new IOException("MIGRATION_ACTOR_SCOPE");
    }
    internal static bool Same(string left,string right)=>string.Equals(left,right,StringComparison.OrdinalIgnoreCase);
    internal static bool Within(string child,string parent)=>child.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
}

internal sealed record MigrationSandboxObservation(int ProcessId,long Created,string RuntimeSid,string AppContainerSid,
    string[] CapabilitySids,bool LoopbackExempt,string EntryDigest,string NodeDigest);
internal interface IMigrationChildSession:IContainedAppSession
{MigrationSandboxObservation Observe();}
internal interface IMigrationProcessFactory
{Task<IMigrationChildSession> StartAsync(VerifiedMigrationSpec spec,CancellationToken cancellation);}
internal sealed record MigrationChildResult([property:JsonPropertyName("format")]int Format,[property:JsonPropertyName("operationId")]string OperationId,
    [property:JsonPropertyName("nonce")]string Nonce,[property:JsonPropertyName("sourceSchema")]int SourceSchema,
    [property:JsonPropertyName("targetSchema")]int TargetSchema,[property:JsonPropertyName("payloadDigest")]string PayloadDigest);
internal sealed record MigrationChildRequest([property:JsonPropertyName("format")]int Format,[property:JsonPropertyName("installationId")]string InstallationId,
    [property:JsonPropertyName("operationId")]string OperationId,[property:JsonPropertyName("sourceDataId")]string SourceDataId,
    [property:JsonPropertyName("cloneOperationId")]string CloneOperationId,[property:JsonPropertyName("newDataId")]string NewDataId,
    [property:JsonPropertyName("sourceSchema")]int SourceSchema,[property:JsonPropertyName("targetSchema")]int TargetSchema,
    [property:JsonPropertyName("payloadDigest")]string PayloadDigest,[property:JsonPropertyName("entryDigest")]string EntryDigest,
    [property:JsonPropertyName("dataDirectory")]string DataDirectory,[property:JsonPropertyName("resultFile")]string ResultFile,[property:JsonPropertyName("nonce")]string Nonce)
{
    internal static MigrationChildRequest From(VerifiedMigrationSpec spec)=>new(1,spec.Plan.InstallationId,spec.Plan.OperationId,spec.Plan.SourceDataId,
        spec.Plan.CloneOperationId,spec.Plan.NewDataId,spec.Plan.SourceSchema,spec.Plan.TargetSchema,spec.Plan.PayloadDigest,spec.Plan.EntryDigest,spec.DataDirectory,spec.ResultFile,spec.Nonce);
}
// Stored only inside a protected activation/operation. Data paths are derived
// from these identities; no serialized absolute path or executable is admitted.
internal sealed record InstalledMigration(OfflineMigrationPlan Plan,string Nonce,string ReceiptDigest,string NodeDigest,
    string RuntimeSid,string AppContainerSid,string? SourceRestoreOperationId=null);
// Pre-execution launch intent is deliberately a different DTO from completed,
// authenticated migration evidence. There is no fabricated receipt or success.
internal sealed record MigrationLaunchDescriptor(OfflineMigrationPlan Plan,string Nonce,string NodeDigest,string RuntimeSid,
    string AppContainerSid,string? SourceRestoreOperationId=null);

// Failure transfers the exact owner, not a guessed PID or a quiescent Boolean.
// Callers retain this instance (and their source/clone leases) until retry has
// observed root exit AND the entire actual owned tree empty.
internal sealed class MigrationOwnershipException:IOException
{
    private readonly IMigrationChildSession owned;private readonly List<object> owners=[];private readonly Func<CancellationToken,Task> normalize;
    internal IMigrationChildSession OwnedSession=>owned;
    internal MigrationOwnershipException(IMigrationChildSession owned,Exception original,Exception cleanup,Func<CancellationToken,Task> normalize):base("MIGRATION_QUIESCENCE_UNCONFIRMED",new AggregateException(original,cleanup)){this.owned=owned;this.normalize=normalize;}
    internal void KeepAlive(object owner)=>owners.Add(owner);
    internal async Task RetryCleanupAsync(CancellationToken cancellation)
    {await owned.TerminateAndWaitForTreeEmptyAsync(cancellation);await owned.WaitForRootExitAsync(cancellation);await owned.WaitForTreeEmptyAsync(cancellation);await normalize(cancellation);await owned.DisposeAsync();owners.Clear();}
}

internal sealed class NativeOfflineMigration
{
    private IMigrationChildSession? child;private bool complete;
    internal async Task<VerifiedMigration> RunAsync(InstalledStateFiles files,NativeRecoveryFixture source,VerifiedMigrationSpec spec,
        WindowsFileBoundary.DataLease cloneLease,IMigrationProcessFactory factory,Func<CancellationToken,Task> prepareSandbox,
        Func<CancellationToken,Task> normalizeNewScope,CancellationToken cancellation=default)
    {
        if(child is not null||complete)throw new IOException("MIGRATION_EXECUTION_REPLAY");spec.Validate();
        using var leaseProof=cloneLease.DuplicateForDirectory(spec.DataDirectory);
        NativeMigrationAdmission.RequireBaseline(files,spec);
        var sandboxIssued=false;
        try
        {
            sandboxIssued=true;
            await prepareSandbox(cancellation);
            // Factory returns even failed-created sessions. Store before Observe
            // so a token/assignment/resume error cannot erase ownership.
            child=await factory.StartAsync(spec,cancellation);
            var observation=child.Observe();RequireObserved(spec,observation);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromMinutes(5));
            if(await child.WaitForRootExitAsync(deadline.Token)!=0)throw new IOException("MIGRATION_CHILD_FAILED");
            await child.WaitForTreeEmptyAsync(deadline.Token);
            await normalizeNewScope(deadline.Token);
            var verified=await NativeMigrationAdmission.VerifyAndPublishAsync(files,source,spec,observation,deadline.Token);
            await child.DisposeAsync();child=null;complete=true;return verified;
        }
        catch(Exception original)
        {
            if(child is null){if(sandboxIssued)await normalizeNewScope(CancellationToken.None);throw;}
            try
            {
                using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await child.TerminateAndWaitForTreeEmptyAsync(cleanup.Token);await child.WaitForRootExitAsync(cleanup.Token);await child.WaitForTreeEmptyAsync(cleanup.Token);await normalizeNewScope(cleanup.Token);await child.DisposeAsync();child=null;
            }
            catch(Exception failure){var pending=new MigrationOwnershipException(child!,original,failure,normalizeNewScope);pending.KeepAlive(this);pending.KeepAlive(source);pending.KeepAlive(cloneLease);throw pending;}
            throw;
        }
    }
    internal static void RequireObserved(VerifiedMigrationSpec spec,MigrationSandboxObservation observed)
    {
        if(observed.ProcessId<1||observed.Created<=0||observed.RuntimeSid!=spec.ExpectedRuntimeSid||observed.AppContainerSid!=spec.ExpectedAppContainerSid||
            observed.CapabilitySids is null||observed.CapabilitySids.Length!=0||observed.LoopbackExempt||observed.EntryDigest!=spec.Plan.EntryDigest||observed.NodeDigest!=spec.NodeDigest)
            throw new IOException("MIGRATION_SANDBOX_OBSERVATION");
    }
}

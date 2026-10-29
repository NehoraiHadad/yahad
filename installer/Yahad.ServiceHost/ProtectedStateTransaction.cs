using System.Runtime.InteropServices;
using System.Text.Json;
using Yahad.Install;

namespace Yahad.Service;

internal sealed record ProtectedOperation(int Format,string Kind,string InstallationId,long ExpectedEpoch,string ExpectedActivationDigest,
    string ReleaseDigest,string DataId,int MinimumSchema,int MaximumSchema,string MasterKeyDigest,string PolicyDigest,string PinDigest,
    InstalledWeb Web,InstalledRecovery? Recovery,long ExpiresUnix,string Nonce);
// Returned by an authenticated boundary, never deserialized from request fields.
internal sealed record VerifiedOperationOwner(string Subject,string InstallationId,string OperationDigest,string Scope,long ApprovedUnix,long ValidUntilUnix);
internal interface IOwnerOperationAuthentication
{Task<VerifiedOperationOwner> AuthenticateAsync(string operationDigest,string requiredScope,CancellationToken cancellation);}
internal sealed class UnconfiguredOwnerOperationAuthentication:IOwnerOperationAuthentication
{public Task<VerifiedOperationOwner> AuthenticateAsync(string digest,string scope,CancellationToken cancellation)=>throw new UnauthorizedAccessException("OWNER_CHANNEL_UNCONFIGURED");}
internal sealed record ProtectedPending(int Format,ProtectedOperation Operation,VerifiedOperationOwner Owner,Dictionary<string,string> Before,Dictionary<string,string> After);
internal sealed record ProtectedNonce(int Format,string InstallationId,string Nonce,string OperationDigest,long Epoch);
internal sealed record ProtectedCommit(int Format,string PendingDigest,string ActivationDigest,long Epoch);

// Concrete writer restricted to an owned private synthetic root. It does not
// provision a Windows principal, change ACLs, generate credentials or select a
// production issuer. The installed entry cannot configure it via JSON or CLI.
internal sealed class ProtectedStateTransactionWriter
{
    private static readonly string[] Records=["owner-intent.json","web.json","activation.json","epoch.json","bootstrap.json"];
    private readonly string root,installation;private readonly JobProcess appJob;private readonly IOwnerOperationAuthentication authentication;
    private readonly Func<DateTimeOffset> clock;private readonly IChildControlFactory channels;
    internal ProtectedStateTransactionWriter(string syntheticRoot,string installationId,JobProcess retainedAppJob,
        IOwnerOperationAuthentication authentication,IChildControlFactory channels,Func<DateTimeOffset> clock)
    {root=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(syntheticRoot));SafeDirectory.ValidateSession(root);InstalledActivationResolver.Id(installationId);installation=installationId;appJob=retainedAppJob;this.authentication=authentication;this.clock=clock;this.channels=channels;}
    internal async Task<InstallationBinding> CommitAsync(ProtectedOperation operation,Action<string>? fault=null,CancellationToken cancellation=default)
    {
        SafeDirectory.ValidateSession(root);var authority=Authority();
        try
        {
            if(PendingArtifacts())throw new IOException("STATE_RECONCILIATION_REQUIRED");
            var owner=await Authorize(operation,false,cancellation);var nonce=NoncePath(operation.Nonce);
            if(Path.Exists(nonce))throw new IOException("STATE_NONCE_REPLAY");
            var before=ReadSnapshot();ValidateBefore(operation,before);var after=Build(operation,owner);
            await Publish(new(1,operation,owner,before,after),false,authority,fault,cancellation);authority=null;
            return Binding(after);
        }
        catch(ProbeOwnershipException owned){if(authority is not null){owned.Retain(authority);authority=null;}throw;}
        finally{authority?.Dispose();}
    }
    internal async Task<InstallationBinding> ResumeAsync(ProtectedOperation operation,Action<string>? fault=null,CancellationToken cancellation=default)
    {
        SafeDirectory.ValidateSession(root);var authority=Authority();
        try
        {
            InstalledActivationResolver.Id(operation.Nonce);
            var pendingPath=Path.Combine(root,"pending.json");var partial=pendingPath+".partial-"+operation.Nonce;
            var promoted=!Path.Exists(pendingPath);var bytes=Read(promoted?partial:pendingPath,128*1024);var pending=NativeJson.Read<ProtectedPending>(bytes);
            if(pending.Format!=1||Hash(JsonSerializer.SerializeToUtf8Bytes(pending.Operation))!=Hash(JsonSerializer.SerializeToUtf8Bytes(operation)))throw new IOException("STATE_PENDING_BINDING");
            var verified=await Authorize(operation,true,cancellation);
            if(verified.Subject!=pending.Owner.Subject||verified.ApprovedUnix!=pending.Owner.ApprovedUnix||pending.Owner.InstallationId!=installation||
                pending.Owner.OperationDigest!=Hash(JsonSerializer.SerializeToUtf8Bytes(operation))||pending.Owner.Scope!=operation.Kind)throw new IOException("STATE_PENDING_OWNER");
            ValidateBefore(operation,pending.Before);
            if(!Same(pending.After,Build(operation,pending.Owner)))throw new IOException("STATE_PENDING_CONTENT");
            ValidateVisible(pending);await appJob.WaitForTreeEmptyAsync(cancellation);
            if(promoted&&!MoveFileExW(@"\\?\"+partial,@"\\?\"+pendingPath,8))throw new IOException("STATE_PENDING_PROMOTION");
            await Publish(pending,true,authority,fault,cancellation,verified);authority=null;return Binding(pending.After);
        }
        catch(ProbeOwnershipException owned){if(authority is not null){owned.Retain(authority);authority=null;}throw;}
        finally{authority?.Dispose();}
    }
    private async Task Publish(ProtectedPending pending,bool resume,IDisposable authority,Action<string>? fault,CancellationToken cancellation,VerifiedOperationOwner? reauthenticated=null)
    {
        var op=pending.Operation;var bytes=JsonSerializer.SerializeToUtf8Bytes(pending);var pendingDigest=Hash(bytes);
        var receipt=Path.Combine(root,".authority","commit-"+op.Nonce+".json");
        var alreadyCommitted=resume&&Path.Exists(receipt);
        if(alreadyCommitted)
        {
            var existing=NativeJson.Read<ProtectedCommit>(Read(receipt,4096));
            if(existing!=new ProtectedCommit(1,pendingDigest,Binding(pending.After).ActivationReceiptDigest,op.ExpectedEpoch+1))throw new IOException("STATE_COMMIT_RECEIPT");
            ValidateVisible(pending,true);
        }
        await appJob.WaitForTreeEmptyAsync(cancellation);
        var candidate=Build(op,pending.Owner,true);var binding=Binding(candidate);
        using var verified=await new InstalledActivationResolver(root,new CandidateFiles(root,candidate),channels,clock)
            .InspectCommitCandidateAsync(binding,resume?pendingDigest:null,cancellation);
        await appJob.WaitForTreeEmptyAsync(cancellation);ValidateVisible(pending);CheckWindow(op,reauthenticated??pending.Owner,resume);
        if(alreadyCommitted){ValidateVisible(pending,true);File.Delete(Path.Combine(root,"pending.json"));authority.Dispose();return;}
        if(!resume){Atomic("pending.json",bytes,op.Nonce,false,fault);fault?.Invoke("pending-published");}
        var nonce=NoncePath(op.Nonce);var claim=new ProtectedNonce(1,installation,op.Nonce,Hash(JsonSerializer.SerializeToUtf8Bytes(op)),op.ExpectedEpoch+1);
        if(Path.Exists(nonce))
        {if(!resume||NativeJson.Read<ProtectedNonce>(Read(nonce,4096))!=claim)throw new IOException("STATE_NONCE_REPLAY");}
        else WriteNew(nonce,JsonSerializer.SerializeToUtf8Bytes(claim));
        fault?.Invoke("nonce-reserved");
        foreach(var name in Records)
        {
            cancellation.ThrowIfCancellationRequested();await appJob.WaitForTreeEmptyAsync(cancellation);ValidateVisible(pending);CheckWindow(op,reauthenticated??pending.Owner,resume);
            Atomic(name,Convert.FromBase64String(pending.After[name]),op.Nonce,true,fault);fault?.Invoke("record-"+name);
        }
        var proof=JsonSerializer.SerializeToUtf8Bytes(new ProtectedCommit(1,pendingDigest,Binding(pending.After).ActivationReceiptDigest,op.ExpectedEpoch+1));
        if(Path.Exists(receipt)){if(!Read(receipt,4096).SequenceEqual(proof))throw new IOException("STATE_COMMIT_RECEIPT");}else WriteNew(receipt,proof);
        fault?.Invoke("commit-receipt");ValidateVisible(pending,true);File.Delete(Path.Combine(root,"pending.json"));fault?.Invoke("pending-cleared");authority.Dispose();
    }
    private async Task<VerifiedOperationOwner> Authorize(ProtectedOperation op,bool resume,CancellationToken cancellation)
    {
        if(op.Format!=1||op.InstallationId!=installation||op.ExpectedEpoch<0||op.ExpectedEpoch==long.MaxValue||op.Kind is not("install" or "activate" or "recover")||
            (op.Kind=="install")!=(op.ExpectedEpoch==0)||(op.Kind=="recover")!=(op.Recovery is not null)||op.Web.InstallationId!=installation)throw new IOException("STATE_OPERATION_BINDING");
        InstalledActivationResolver.Id(op.Nonce);InstalledActivationResolver.Id(op.DataId);
        foreach(var digest in new[]{op.ReleaseDigest,op.MasterKeyDigest,op.PolicyDigest,op.PinDigest})InstalledActivationResolver.Digest(digest);
        if(op.ExpectedEpoch==0){if(op.ExpectedActivationDigest!="")throw new IOException("STATE_INSTALL_ANCHOR");}else InstalledActivationResolver.Digest(op.ExpectedActivationDigest);
        var scope=resume?"reconcile":op.Kind;var digestOfOperation=Hash(JsonSerializer.SerializeToUtf8Bytes(op));
        var owner=await authentication.AuthenticateAsync(digestOfOperation,scope,cancellation);
        if(string.IsNullOrWhiteSpace(owner.Subject)||owner.Subject.Length>128||owner.InstallationId!=installation||owner.OperationDigest!=digestOfOperation||owner.Scope!=scope)throw new UnauthorizedAccessException("STATE_OWNER_SCOPE");
        CheckWindow(op,owner,resume);return owner;
    }
    private void CheckWindow(ProtectedOperation op,VerifiedOperationOwner owner,bool resume)
    {var now=clock().ToUnixTimeSeconds();if(owner.ApprovedUnix<=0||owner.ApprovedUnix>now||owner.ValidUntilUnix<=now||(!resume&&(op.ExpiresUnix<=now||op.ExpiresUnix>now+600||owner.ValidUntilUnix<op.ExpiresUnix)))throw new UnauthorizedAccessException("STATE_AUTHORIZATION_EXPIRED");}
    private Dictionary<string,string> Build(ProtectedOperation op,VerifiedOperationOwner owner,bool inspection=false)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);string Put<T>(string name,T value){var b=JsonSerializer.SerializeToUtf8Bytes(value);result.Add(name,Convert.ToBase64String(b));return Hash(b);}
        var epoch=op.ExpectedEpoch+1;
        var ownerDigest=Put("owner-intent.json",new InstalledOwnerIntent(1,installation,epoch,op.Nonce,op.ReleaseDigest,op.DataId,op.Recovery is null?"activate":"recover",owner.Subject,owner.ApprovedUnix,null));
        var web=Put("web.json",op.Web);
        var head=Put("activation.json",new InstalledActivation(1,installation,epoch,op.ReleaseDigest,op.DataId,op.MinimumSchema,op.MaximumSchema,op.MasterKeyDigest,op.PolicyDigest,op.PinDigest,ownerDigest,web,inspection&&op.Recovery is not null?"recovered":"committed",op.Recovery));
        Put("epoch.json",new InstalledEpoch(1,installation,epoch,head));
        Put("bootstrap.json",new{schema=1,service=ServiceDefinition.Name,installationId=installation,activationReceiptDigest=head});return result;
    }
    private void ValidateBefore(ProtectedOperation op,Dictionary<string,string> before)
    {
        if(op.ExpectedEpoch==0){if(before.Count!=0||Directory.EnumerateFiles(Path.Combine(root,".authority"),"commit-*.json").Any())throw new IOException("STATE_CAS");return;}
        if(before.Count!=5||!Records.All(before.ContainsKey))throw new IOException("STATE_BEFORE_MEMBERS");
        byte[] Get(string name)=>Convert.FromBase64String(before[name]);
        var head=NativeJson.Read<InstalledActivation>(Get("activation.json"));var epoch=NativeJson.Read<InstalledEpoch>(Get("epoch.json"));var bootstrap=WindowsInstallationReader.Parse(Get("bootstrap.json"));
        if(Hash(Get("activation.json"))!=op.ExpectedActivationDigest||head.InstallationId!=installation||head.Epoch!=op.ExpectedEpoch||epoch!=new InstalledEpoch(1,installation,op.ExpectedEpoch,op.ExpectedActivationDigest)||
            bootstrap!=new InstallationBinding(installation,op.ExpectedActivationDigest)||Hash(Get("owner-intent.json"))!=head.OwnerIntentDigest||Hash(Get("web.json"))!=head.WebDigest)throw new IOException("STATE_CAS");
    }
    private Dictionary<string,string> ReadSnapshot()
    {var snapshot=new Dictionary<string,string>(StringComparer.Ordinal);foreach(var name in Records){var path=Path.Combine(root,name);if(Path.Exists(path))snapshot.Add(name,Convert.ToBase64String(Read(path,16384)));}return snapshot;}
    private void ValidateVisible(ProtectedPending pending,bool allAfter=false)
    {
        foreach(var name in Records)
        {
            var path=Path.Combine(root,name);if(!Path.Exists(path)){if(allAfter||pending.Before.ContainsKey(name))throw new IOException("STATE_VISIBLE_BINDING");continue;}
            var actual=Convert.ToBase64String(Read(path,16384));
            if(actual!=pending.After[name]&&(allAfter||!pending.Before.TryGetValue(name,out var old)||actual!=old))throw new IOException("STATE_VISIBLE_BINDING");
        }
    }
    private IDisposable Authority()
    {Directory.CreateDirectory(Path.Combine(root,".authority"));return WindowsFileBoundary.AcquireDataLease(Path.Combine(root,".authority"));}
    private bool PendingArtifacts()=>Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).Any(n=>n!.Equals("pending.json",StringComparison.OrdinalIgnoreCase)||n.Contains(".partial-",StringComparison.OrdinalIgnoreCase));
    private string NoncePath(string nonce)=>Path.Combine(root,".authority","nonce-"+nonce+".json");
    private static byte[] Read(string path,int limit){using var file=WindowsFileBoundary.OpenPrivateRead(path);return file.Small(limit);}
    private static string Hash(byte[] value)=>InstalledActivationResolver.Hash(value);
    private static bool Same(Dictionary<string,string> a,Dictionary<string,string> b)=>a.Count==b.Count&&a.All(p=>b.TryGetValue(p.Key,out var value)&&value==p.Value);
    private InstallationBinding Binding(Dictionary<string,string> records)=>new(installation,Hash(Convert.FromBase64String(records["activation.json"])));
    private static void WriteNew(string path,byte[] bytes){using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);file.Write(bytes);file.Flush(true);}
    private void Atomic(string name,byte[] bytes,string nonce,bool replace,Action<string>? fault)
    {
        var destination=Path.Combine(root,name);var partial=destination+".partial-"+nonce;
        if(Path.Exists(partial))
        {
            using(var check=WindowsFileBoundary.OpenPrivateRead(partial)){_ =check.Length;}
            var quarantine=Path.Combine(root,".authority","quarantine-"+nonce+"-"+name+"-"+Guid.NewGuid().ToString("N"));
            if(!MoveFileExW(@"\\?\"+partial,@"\\?\"+quarantine,8))throw new IOException("STATE_PARTIAL_QUARANTINE");
        }
        using(var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
        {output.Write(bytes);fault?.Invoke("partial-"+name);output.Flush(true);}
        if(!MoveFileExW(@"\\?\"+partial,@"\\?\"+destination,replace?9u:8u))throw new IOException("STATE_PUBLICATION");
    }
    private sealed class CandidateFiles(string root,Dictionary<string,string> records):IInstalledControlFiles
    {
        public byte[] Read(string path,int maximum,ICollection<IDisposable> retained)
        {if(Path.GetDirectoryName(path)!=root||!records.TryGetValue(Path.GetFileName(path),out var value))throw new IOException("STATE_CANDIDATE_PATH");var bytes=Convert.FromBase64String(value);if(bytes.Length>maximum)throw new IOException("STATE_CANDIDATE_BOUND");return bytes;}
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string source,string destination,uint flags);
}

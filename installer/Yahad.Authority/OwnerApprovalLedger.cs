using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record ApprovalChallenge(int Format,string Id,string Digest,string Scope,long ExpiresUnix,bool Reconcile,string? DelegationId);
internal sealed record OwnerApproval(int Format,string Digest,string Subject,string Scope,long ApprovedUnix,long ValidUntilUnix,string? DelegationId,bool ExplicitReconciliation=false);
internal sealed record RoutineDelegation(int Format,string Id,string InstallationId,string Subject,string DataId,string PolicyDigest,string PinDigest,string MasterKeyDigest,string Channel,
    string Origin,InstalledRecovery? Recovery,string MinimumVersion,string MaximumVersion,int Schema,long ExpiresUnix,int MaximumOperations,long MaximumPayloadBytes);
internal sealed record RoutineDelegationStatus(string State,long? ExpiresUnix,int? RemainingOperations);
internal sealed class RoutineApprovalRequiredException(string reason):UnauthorizedAccessException(reason);
internal sealed class OwnerApprovalLedger:IOwnerOperationAuthentication
{
    private readonly InstalledStateFiles files;private readonly string root,stateRoot,installation,ownerSid;private readonly Func<DateTimeOffset> clock;
    internal OwnerApprovalLedger(string privateRoot,string installationId,string ownerSid,Func<DateTimeOffset> clock)
        :this(InstalledStateFiles.Private(privateRoot),installationId,ownerSid,clock){}
    internal OwnerApprovalLedger(InstalledStateFiles files,string installationId,string ownerSid,Func<DateTimeOffset> clock)
    {
        this.files=files;files.ValidateRoot();InstalledActivationResolver.Id(installationId);_=new System.Security.Principal.SecurityIdentifier(ownerSid);
        stateRoot=files.Root;root=files.PathOf("owner-ledger");files.EnsureDirectory(root);installation=installationId;this.ownerSid=ownerSid;this.clock=clock;
    }
    private long Now=>clock().ToUnixTimeSeconds();
    private IDisposable Lease()
    {
        // A status/mode request can overlap the background writer's short
        // approval recheck. Serialize across reconstructed ledgers/processes,
        // retaining the existing native exclusive lease and a bounded wait.
        var wait=System.Diagnostics.Stopwatch.StartNew();
        while(true)
        {
            try{return files.Lease(root);}
            catch(System.ComponentModel.Win32Exception error)when(error.NativeErrorCode is 32 or 33)
            {if(wait.Elapsed>=TimeSpan.FromSeconds(2))throw new IOException("OWNER_LEDGER_BUSY",error);Thread.Sleep(10);}
        }
    }
    internal ApprovalChallenge Challenge(ProtectedOperation operation,bool reconcile=false)
    {
        Operation(operation);var digest=AuthorityProtocol.Digest(operation);using var held=Lease();
        if(Directory.EnumerateFiles(root,"challenge-*.json").Take(1001).Count()>1000)throw new IOException("OWNER_CHALLENGE_BOUND");
        if(File.Exists(PathOf("revoked-operation-"+digest)))throw new UnauthorizedAccessException("OWNER_OPERATION_REVOKED");
        var challenge=new ApprovalChallenge(1,Guid.NewGuid().ToString("N"),digest,operation.Kind,Math.Min(operation.ExpiresUnix,Now+600),reconcile,null);
        if(reconcile){_ =Read<OwnerApproval>("approval-"+digest);challenge=challenge with{ExpiresUnix=Now+600};}
        if(challenge.ExpiresUnix<=Now)throw new UnauthorizedAccessException("OWNER_CHALLENGE_EXPIRED");Write("challenge-"+challenge.Id,challenge);return challenge;
    }
    internal ApprovalChallenge DelegationChallenge(RoutineDelegation delegation)
    {
        Validate(delegation);using var held=Lease();RequireDelegationAnchor(delegation);var c=new ApprovalChallenge(1,Guid.NewGuid().ToString("N"),AuthorityProtocol.Digest(delegation),"delegate",Now+600,false,delegation.Id);Write("challenge-"+c.Id,c);return c;
    }
    internal ApprovalChallenge SystemChallenge(string digest,string scope)
    {
        InstalledActivationResolver.Digest(digest);if(scope is not("delegate-system" or "activate-system" or "reconcile-system"))throw new IOException("OWNER_SYSTEM_SCOPE");using var held=Lease();
        if(Directory.EnumerateFiles(root,"challenge-*.json").Take(1001).Count()>1000)throw new IOException("OWNER_CHALLENGE_BOUND");
        var challenge=new ApprovalChallenge(1,Guid.NewGuid().ToString("N"),digest,scope,Now+600,false,null);Write("challenge-"+challenge.Id,challenge);return challenge;
    }
    internal void ApproveSystem(AuthorityPeer peer,OwnerAnswer answer,string scope,string digest)
    {if(scope is not("delegate-system" or "activate-system" or "reconcile-system")||answer.OperationDigest!=digest)throw new UnauthorizedAccessException("OWNER_SYSTEM_BINDING");Approve(peer,answer,null,scope);}
    internal void Approve(AuthorityPeer peer,OwnerAnswer answer,RoutineDelegation? delegation=null,string? expectedSystemScope=null)
    {
        if(answer.Format!=1)throw new IOException("OWNER_ANSWER_FORMAT");InstalledActivationResolver.Id(answer.Challenge);InstalledActivationResolver.Digest(answer.OperationDigest);
        RequireOwner(peer);using var held=Lease();var c=Read<ApprovalChallenge>("challenge-"+answer.Challenge);
        if(c.Format!=1||c.Id!=answer.Challenge||c.Digest!=answer.OperationDigest||c.ExpiresUnix<=Now||File.Exists(PathOf("used-"+c.Id)))throw new UnauthorizedAccessException("OWNER_CHALLENGE_BINDING");
        if((c.Scope is "delegate-system" or "activate-system" or "reconcile-system")?expectedSystemScope!=c.Scope:expectedSystemScope is not null)throw new UnauthorizedAccessException("OWNER_SYSTEM_BINDING");
        // Consume before any approval publication. A crash may require a fresh challenge, never implicit approval.
        Write("used-"+c.Id,new{c.Id,c.Digest,Denied=!answer.Approve});if(!answer.Approve)return;
        if(c.Scope=="delegate")
        {
            if(delegation is null||c.DelegationId!=delegation.Id||c.Digest!=AuthorityProtocol.Digest(delegation))throw new UnauthorizedAccessException("OWNER_DELEGATION_BINDING");
            Validate(delegation);RequireDelegationAnchor(delegation);Write("delegation-"+delegation.Id,delegation);return;
        }
        if(delegation is not null)throw new IOException("OWNER_UNEXPECTED_DELEGATION");
        var approval=new OwnerApproval(1,c.Digest,ownerSid,c.Scope,Now,c.ExpiresUnix,null);
        if(c.Reconcile)
        {
            var old=Read<OwnerApproval>("approval-"+c.Digest);
            if(old.Subject!=ownerSid||old.Digest!=c.Digest||old.Scope!=c.Scope)throw new IOException("OWNER_RECONCILE_BINDING");
            // Append-only renewal; original approval identity/time is retained by the transaction consumer.
            Write("reconcile-"+c.Digest+"-"+c.Id,approval with{ApprovedUnix=old.ApprovedUnix,ExplicitReconciliation=true});
        }
        else Write("approval-"+c.Digest,approval);
    }
    internal void Delegate(RoutineDelegation delegation,AuthorityPeer peer,OwnerAnswer answer)=>Approve(peer,answer,delegation);
    internal void AuthorizeRoutine(ProtectedOperation operation,OperationFacts facts,string delegationId,bool reconcile=false,bool reserve=true)
    {
        Operation(operation);InstalledActivationResolver.Id(delegationId);using var held=Lease();var d=ReadDelegation(delegationId);Validate(d,false);
        if(d.ExpiresUnix<=Now)throw new RoutineApprovalRequiredException("OWNER_DELEGATION_EXPIRED");
        if(File.Exists(PathOf("revoked-delegation-"+d.Id)))throw new RoutineApprovalRequiredException("OWNER_DELEGATION_REVOKED");
        if(operation.Kind!="activate"||operation.Recovery!=d.Recovery||operation.DataId!=d.DataId||operation.MinimumSchema!=d.Schema||operation.MaximumSchema!=d.Schema||
            operation.PolicyDigest!=d.PolicyDigest||operation.PinDigest!=d.PinDigest||operation.MasterKeyDigest!=d.MasterKeyDigest||facts.IntakePolicyDigest!=d.PolicyDigest||facts.IntakePinDigest!=d.PinDigest||
            facts.Channel!=d.Channel||facts.Origin!=d.Origin||operation.Web.Origin!=d.Origin||facts.PayloadBytes is <1||facts.PayloadBytes>d.MaximumPayloadBytes||
            Version(facts.TargetVersion)<=Version(facts.CurrentVersion)||Version(facts.TargetVersion)<Version(d.MinimumVersion)||Version(facts.TargetVersion)>Version(d.MaximumVersion))throw new RoutineApprovalRequiredException("OWNER_DELEGATION_SCOPE");
        if(!reconcile&&operation.ExpiresUnix<=Now)throw new RoutineApprovalRequiredException("OWNER_OPERATION_EXPIRED");
        var digest=AuthorityProtocol.Digest(operation);var existing=PathOf("approval-"+digest);
        if(File.Exists(existing))
        {
            var prior=Read<OwnerApproval>("approval-"+digest);if(prior.DelegationId!=d.Id||prior.Digest!=digest||prior.Subject!=ownerSid)throw new IOException("OWNER_APPROVAL_CONFLICT");
            if(reconcile)
            {
                if(File.Exists(PathOf("revoked-operation-"+digest)))throw new UnauthorizedAccessException("OWNER_OPERATION_REVOKED");
                Write("reconcile-"+digest+"-"+Guid.NewGuid().ToString("N"),prior with{ValidUntilUnix=Math.Min(Now+600,d.ExpiresUnix)});return;
            }
            RequireCurrent(prior,digest);return;
        }
        if(reconcile)throw new IOException("OWNER_ORIGINAL_APPROVAL_REQUIRED");
        var claims=Directory.EnumerateFiles(root,"usage-"+d.Id+"-*.json").ToArray();
        if(claims.Length>=d.MaximumOperations)throw new RoutineApprovalRequiredException("OWNER_DELEGATION_BUDGET");
        if(!reserve)return; // Admission-only check; idle refusal cannot spend budget.
        // Burn budget durably before granting. A partial/corrupt record never restores budget.
        var usage="usage-"+d.Id+"-"+digest;if(!File.Exists(PathOf(usage)))Write(usage,new{delegation=d.Id,operation=digest});
        Write("approval-"+digest,new OwnerApproval(1,digest,ownerSid,"activate",Now,Math.Min(d.ExpiresUnix,operation.ExpiresUnix),d.Id));
    }
    public Task<VerifiedOperationOwner> AuthenticateAsync(string digest,string scope,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();InstalledActivationResolver.Digest(digest);using var held=Lease();var approval=Effective(digest,scope=="reconcile");RequireCurrent(approval,digest);
        if(scope!=approval.Scope&&scope!="reconcile")throw new UnauthorizedAccessException("OWNER_APPROVAL_SCOPE");
        return Task.FromResult(new VerifiedOperationOwner(approval.Subject,installation,digest,scope,approval.ApprovedUnix,approval.ValidUntilUnix));
    }
    public void RequireCurrent(VerifiedOperationOwner owner)
    {
        using var held=Lease();var a=Effective(owner.OperationDigest,owner.Scope=="reconcile");RequireCurrent(a,owner.OperationDigest);
        if(owner.Subject!=a.Subject||owner.InstallationId!=installation||owner.ApprovedUnix!=a.ApprovedUnix||owner.ValidUntilUnix!=a.ValidUntilUnix||owner.Scope!=a.Scope&&owner.Scope!="reconcile")throw new UnauthorizedAccessException("OWNER_APPROVAL_CHANGED");
    }
    internal void Revoke(AuthorityPeer peer,string id,bool delegation)
    {RequireOwner(peer);if(delegation)InstalledActivationResolver.Id(id);else InstalledActivationResolver.Digest(id);using var held=Lease();Write((delegation?"revoked-delegation-":"revoked-operation-")+id,new{Id=id,Subject=ownerSid,RevokedUnix=Now});}
    // The authenticated runtime may contract its existing automatic grant when
    // selecting Notify. This route cannot create, extend or renew any approval.
    internal void DisableRoutine(string id)
    {
        InstalledActivationResolver.Id(id);using var held=Lease();var d=ReadDelegation(id);Validate(d,false);
        if(!File.Exists(PathOf("revoked-delegation-"+id)))Write("revoked-delegation-"+id,new{Id=id,Subject=ownerSid,RevokedUnix=Now});
    }
    internal RoutineDelegationStatus RoutineStatus(string id)
    {
        InstalledActivationResolver.Id(id);
        try
        {
            using var held=Lease();var d=ReadDelegation(id);Validate(d,false);
            var used=Directory.EnumerateFiles(root,"usage-"+id+"-*.json").Take(101).Count();
            var remaining=Math.Max(0,d.MaximumOperations-used);
            return new(File.Exists(PathOf("revoked-delegation-"+id))?"revoked":d.ExpiresUnix<=Now?"expired":remaining==0?"exhausted":"active",d.ExpiresUnix,remaining);
        }
        catch(IOException error)when(error.Message=="OWNER_LEDGER_BUSY"){return new("busy",null,null);}
    }
    private OwnerApproval Effective(string digest,bool reconcile)
    {
        var original=Read<OwnerApproval>("approval-"+digest);
        if(!reconcile)return original;

        var names=Directory.EnumerateFiles(root,"reconcile-"+digest+"-*.json").ToArray();
        if(names.Length==0&&original.DelegationId is not null)return original;
        if(names.Length is <1 or >100)throw new UnauthorizedAccessException("OWNER_RECONCILE_REQUIRED");
        var renewed=names.Select(p=>Read<OwnerApproval>(Path.GetFileNameWithoutExtension(p))).OrderByDescending(x=>x.ValidUntilUnix).First();
        if(renewed.Subject!=original.Subject||renewed.ApprovedUnix!=original.ApprovedUnix||renewed.Scope!=original.Scope||
            (renewed.ExplicitReconciliation?renewed.DelegationId is not null:renewed.DelegationId!=original.DelegationId))throw new IOException("OWNER_RECONCILE_BINDING");return renewed;
    }
    private void RequireCurrent(OwnerApproval a,string digest)
    {
        if(a.Format!=1||a.Digest!=digest||a.Subject!=ownerSid||a.ValidUntilUnix<=Now||a.ApprovedUnix>Now||File.Exists(PathOf("revoked-operation-"+digest)))throw new UnauthorizedAccessException("OWNER_APPROVAL_REVOKED_OR_EXPIRED");
        if(a.DelegationId is { } id){var d=ReadDelegation(id);Validate(d);if(File.Exists(PathOf("revoked-delegation-"+id)))throw new UnauthorizedAccessException("OWNER_DELEGATION_REVOKED");}
    }
    private void RequireDelegationAnchor(RoutineDelegation d)
    {
        byte[] ReadState(string name){using var file=files.Open(Path.Combine(stateRoot,name));return file.Small(16384);}
        if(Directory.EnumerateFileSystemEntries(stateRoot).Select(Path.GetFileName).Any(n=>n=="pending.json"||n!.Contains(".partial-")))throw new IOException("OWNER_DELEGATION_PENDING_STATE");
        var bytes=ReadState("activation.json");var head=NativeJson.Read<InstalledActivation>(bytes);var anchor=WindowsInstallationReader.Parse(ReadState("bootstrap.json"));var epoch=NativeJson.Read<InstalledEpoch>(ReadState("epoch.json"));
        var digest=InstalledActivationResolver.Hash(bytes);var webBytes=ReadState("web.json");var web=NativeJson.Read<InstalledWeb>(webBytes);
        if(head.InstallationId!=installation||anchor!=new InstallationBinding(installation,digest)||epoch!=new InstalledEpoch(1,installation,head.Epoch,digest)||
            head.Phase!="committed"||head.DataId!=d.DataId||head.Recovery!=d.Recovery||head.MasterKeyDigest!=d.MasterKeyDigest||head.PolicyDigest!=d.PolicyDigest||head.PinDigest!=d.PinDigest||
            head.MinimumSchema!=d.Schema||head.MaximumSchema!=d.Schema||head.WebDigest!=InstalledActivationResolver.Hash(webBytes)||web.Origin!=d.Origin)throw new UnauthorizedAccessException("OWNER_DELEGATION_ANCHOR");
    }
    private void Validate(RoutineDelegation d,bool current=true)
    {
        InstalledActivationResolver.Id(d.Id);InstalledActivationResolver.Id(d.DataId);InstalledActivationResolver.Digest(d.PolicyDigest);InstalledActivationResolver.Digest(d.PinDigest);InstalledActivationResolver.Digest(d.MasterKeyDigest);
        if(d.Format!=1||d.InstallationId!=installation||d.Subject!=ownerSid||current&&d.ExpiresUnix<=Now||d.ExpiresUnix>Now+90*86400||d.MaximumOperations is <1 or >100||d.MaximumPayloadBytes is <1 or >2147483648||d.Schema<1||
            d.Channel is not("stable" or "preview")||!Uri.TryCreate(d.Origin,UriKind.Absolute,out var origin)||origin.Scheme!="https"||origin.GetLeftPart(UriPartial.Authority)!=d.Origin||Version(d.MinimumVersion)>Version(d.MaximumVersion))throw new UnauthorizedAccessException("OWNER_DELEGATION_LIMITS");
    }
    private static Version Version(string value)
    {if(!System.Text.RegularExpressions.Regex.IsMatch(value,"^[0-9]{1,5}\\.[0-9]{1,5}\\.[0-9]{1,5}$"))throw new IOException("OWNER_VERSION");return System.Version.Parse(value);}
    private void Operation(ProtectedOperation op){if(op.InstallationId!=installation||op.Kind is not("install" or "activate" or "recover"))throw new IOException("OWNER_OPERATION_BINDING");}
    private void RequireOwner(AuthorityPeer peer){if(peer.Sid!=ownerSid||peer.ProcessId<1||peer.Created<=0||peer.Session<=0||!peer.Interactive)throw new UnauthorizedAccessException("OWNER_PEER");}
    private string PathOf(string name)=>Path.Combine(root,name+".json");
    private T Read<T>(string name){using var f=files.Open(PathOf(name));return NativeJson.Read<T>(f.Small(32768));}
    private RoutineDelegation ReadDelegation(string id)
    {InstalledActivationResolver.Id(id);var delegation=Read<RoutineDelegation>("delegation-"+id);if(delegation.Id!=id)throw new IOException("OWNER_DELEGATION_BINDING");return delegation;}
    private void Write<T>(string name,T value)
    {
        if(Directory.EnumerateFileSystemEntries(root).Take(10001).Count()>10000)throw new IOException("OWNER_LEDGER_BOUND");
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value);if(bytes.Length>32768)throw new IOException("OWNER_RECORD_BOUND");
        // FileShare.None + WriteThrough + Flush before readers can open; partial/corrupt files fail closed.
        using var output=files.Create(PathOf(name));output.Write(bytes);output.Flush(true);
    }
}

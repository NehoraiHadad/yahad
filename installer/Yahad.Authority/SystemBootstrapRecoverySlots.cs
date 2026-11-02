using System.Text.Json;
using Yahad.Install;
namespace Yahad.Authority;

internal enum RecoveryHelperSlotPhase {Verified,Replacing}
internal sealed record RecoveryHelperClosure(string ManifestDigest,string Version,string EntrySha256,SystemBootstrapFileState[] Files);
internal sealed record RecoveryHelperSlotState(RecoveryHelperSlot Slot,RecoveryHelperSlotPhase Phase,RecoveryHelperClosure Closure,RecoveryHelperClosure? Prior=null);
internal sealed record RecoveryHelperSlotsSelection(int Format,string InstallationId,string PolicyDigest,long Epoch,RecoveryHelperSlot Active,
    RecoveryHelperSlotState[] Slots,string? PendingOperationId=null,string? PendingManifestDigest=null,string? GrantId=null,string? GrantBindingDigest=null);
internal sealed record RecoveryHelperHealth(RecoveryHelperSlot Slot,int ProcessId,long Created,string EntrySha256,int Protocol,string Nonce);
internal interface ISystemBootstrapHelperPublication
{
    Task RequireReadyAsync(VerifiedSystemOperation operation,SystemBootstrapSnapshot before,CancellationToken cancellation);
    Task PublishAsync(VerifiedSystemOperation operation,SystemBootstrapSnapshot before,CancellationToken cancellation);
    Task RestoreAsync(VerifiedSystemOperation operation,SystemBootstrapSnapshot before,CancellationToken cancellation);
}
internal interface IRecoveryHelperSlotKernel
{
    bool IsExecuting(RecoveryHelperSlot slot);
    void RequireProvisioned();
    Task RequireClosureAsync(RecoveryHelperSlot slot,RecoveryHelperClosure closure,CancellationToken cancellation);
    Task PublishAsync(VerifiedSystemOperation operation,RecoveryHelperSlot slot,RecoveryHelperClosure wanted,IReadOnlyList<RecoveryHelperClosure> prior,bool rollback,CancellationToken cancellation);
    Task<RecoveryHelperHealth> ObserveHealthyAsync(VerifiedSystemOperation operation,RecoveryHelperSlot slot,RecoveryHelperClosure closure,string nonce,CancellationToken cancellation);
}

// A subtransaction of the existing HelperIntent, using its operation and SAME
// exclusive update-serial owner. This has no source check, schedule or consent.
// One complete verified helper remains untouched throughout every publication.
internal sealed class SystemBootstrapRecoverySlots(InstalledStateFiles files,IRecoveryHelperSlotKernel kernel,Action<VerifiedSystemOperation> requireCurrent,WindowsFileBoundary.DataLease? serialLease=null):ISystemBootstrapHelperPublication
{
    internal static RecoveryHelperSlotsSelection Read(InstalledStateFiles files,string installation,string policy)
    {
        var value=NativeJson.Read<RecoveryHelperSlotsSelection>(files.Read(files.PathOf("bootstrap-helper-selection.json"),8*1024*1024));
        if(value.Format!=1||value.InstallationId!=installation||value.PolicyDigest!=policy||value.Epoch<1||!Enum.IsDefined(value.Active)||value.Slots is null||value.Slots.Length!=2||value.Slots.Select(s=>s.Slot).Distinct().Count()!=2)throw new IOException("HELPER_SLOTS_SELECTION");
        foreach(var slot in value.Slots)
        {
            if(!Enum.IsDefined(slot.Slot)||!Enum.IsDefined(slot.Phase))throw new IOException("HELPER_SLOT_STATE");ValidateClosure(slot.Closure);
            if(slot.Prior is{} prior)ValidateClosure(prior);if(slot.Phase==RecoveryHelperSlotPhase.Replacing&&slot.Prior is null)throw new IOException("HELPER_SLOT_PRIOR_REQUIRED");
        }
        if(value.Slots.Single(s=>s.Slot==value.Active).Phase!=RecoveryHelperSlotPhase.Verified)throw new IOException("HELPER_SLOT_ACTIVE_UNVERIFIED");
        if(value.PendingOperationId is not null){Yahad.Service.InstalledActivationResolver.Id(value.PendingOperationId);FixedSystemComponents.Digest(value.PendingManifestDigest!);Yahad.Service.InstalledActivationResolver.Id(value.GrantId!);FixedSystemComponents.Digest(value.GrantBindingDigest!);}
        else if(value.PendingManifestDigest is not null||value.GrantId is not null||value.GrantBindingDigest is not null||value.Slots.Any(s=>s.Phase!=RecoveryHelperSlotPhase.Verified))throw new IOException("HELPER_SLOT_PENDING_BINDING");
        return value;
    }
    private static void ValidateClosure(RecoveryHelperClosure value)
    {
        FixedSystemComponents.Digest(value.ManifestDigest);
        _=new SystemComponentDescriptor(SystemComponentKind.Helper,FixedSystemComponents.SlotPrefix(SystemComponentKind.Helper),FixedSystemComponents.EntryPoint(SystemComponentKind.Helper),value.EntrySha256,value.Files.Select(f=>new SystemComponentFile(f.RelativePath,f.Size,f.Sha256)));
        if(!System.Version.TryParse(value.Version,out var version)||version.ToString()!=value.Version||version.Build<0||version.Revision>=0)throw new IOException("HELPER_SLOT_VERSION");
    }
    public async Task RequireReadyAsync(VerifiedSystemOperation operation,SystemBootstrapSnapshot before,CancellationToken cancellation)
    {using var owned=HoldSerial();await RequireReadyCoreAsync(operation,cancellation);}
    private async Task RequireReadyCoreAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
    {
        kernel.RequireProvisioned();var state=Read(files,operation.InstallationId,operation.PolicyDigest);BindPending(operation,state);
        await kernel.RequireClosureAsync(state.Active,state.Slots.Single(s=>s.Slot==state.Active).Closure,cancellation);
        if(state.PendingOperationId is null)
            foreach(var slot in state.Slots)await kernel.RequireClosureAsync(slot.Slot,slot.Closure,cancellation);
    }
    public async Task PublishAsync(VerifiedSystemOperation operation,SystemBootstrapSnapshot before,CancellationToken cancellation)
    {
        using var owned=HoldSerial();requireCurrent(operation);await RequireReadyCoreAsync(operation,cancellation);var state=Read(files,operation.InstallationId,operation.PolicyDigest);
        var wanted=Candidate(operation);
        // Resume from actual protected selection + closed inventory. The old
        // slot is never overwritten merely because a prior step wrote a label.
        for(var step=0;step<2;step++)
        {
            var active=state.Slots.Single(s=>s.Slot==state.Active);
            await kernel.RequireClosureAsync(active.Slot,active.Closure,cancellation);
            var target=state.Slots.Single(s=>s.Slot!=state.Active);
            if(Same(active.Closure,wanted)&&target.Phase==RecoveryHelperSlotPhase.Verified&&Same(target.Closure,wanted))break;
            state=await PublishOne(operation,state,target,wanted,false,cancellation);
            requireCurrent(operation);
            // A switch occurs ONLY after actual generation-bound health and
            // cleanup. The newly selected complete slot stays untouched next.
            state=state with{Active=target.Slot,Epoch=checked(state.Epoch+1)};Write(state);
        }
        foreach(var slot in state.Slots){if(slot.Phase!=RecoveryHelperSlotPhase.Verified||!Same(slot.Closure,wanted))throw new IOException("HELPER_SLOTS_NOT_COMPLETE");await kernel.RequireClosureAsync(slot.Slot,wanted,cancellation);}
        requireCurrent(operation);Write(state with{PendingOperationId=null,PendingManifestDigest=null,GrantId=null,GrantBindingDigest=null});ArchivePriorBlocks();
    }
    public async Task RestoreAsync(VerifiedSystemOperation operation,SystemBootstrapSnapshot before,CancellationToken cancellation)
    {
        using var owned=HoldSerial();
        kernel.RequireProvisioned();var state=Read(files,operation.InstallationId,operation.PolicyDigest);BindPending(operation,state);
        var component=before.Components.Single(c=>c.Kind==SystemComponentKind.Helper);var wanted=new RecoveryHelperClosure(before.ManifestDigest,before.Version,component.EntrySha256,component.Files);ValidateClosure(wanted);
        // A running helper is never overwritten. Restore and authenticate the
        // other OS-addressable slot first; the running slot remains a complete
        // source-authorized fallback and is refreshed after its actual exit.
        var target=state.Slots.FirstOrDefault(s=>s.Phase==RecoveryHelperSlotPhase.Verified&&Same(s.Closure,wanted));
        if(target is null)
        {
            target=state.Slots.FirstOrDefault(s=>!kernel.IsExecuting(s.Slot))??throw new IOException("HELPER_ROLLBACK_NO_WRITABLE_SLOT");
            var survivor=state.Slots.Single(s=>s.Slot!=target.Slot);await RequireKnownClosure(survivor,cancellation);
            state=await PublishOne(operation,state,target,wanted,true,cancellation);target=state.Slots.Single(s=>s.Slot==target.Slot);
        }
        await kernel.RequireClosureAsync(target.Slot,wanted,cancellation);state=state with{Active=target.Slot,Epoch=checked(state.Epoch+1)};Write(state);
        var other=state.Slots.Single(s=>s.Slot!=target.Slot);
        if(!kernel.IsExecuting(other.Slot))state=await PublishOne(operation,state,other,wanted,true,cancellation);
        else
        {
            var complete=await RequireKnownClosure(other,cancellation);state=Replace(state,other with{Phase=RecoveryHelperSlotPhase.Verified,Closure=complete,Prior=null});Write(state);
        }
        Write(state with{PendingOperationId=null,PendingManifestDigest=null,GrantId=null,GrantBindingDigest=null});
    }
    private async Task<RecoveryHelperSlotsSelection> PublishOne(VerifiedSystemOperation operation,RecoveryHelperSlotsSelection state,RecoveryHelperSlotState target,RecoveryHelperClosure wanted,bool rollback,CancellationToken cancellation)
    {
        if(kernel.IsExecuting(target.Slot))throw new IOException("HELPER_SLOT_STILL_EXECUTING");
        var survivor=state.Slots.Single(s=>s.Slot!=target.Slot);await RequireKnownClosure(survivor,cancellation);
        if(state.Active==target.Slot)
        {
            var complete=await RequireKnownClosure(survivor,cancellation);var survivorNonce=Guid.NewGuid().ToString("N");
            var survivorHealth=await kernel.ObserveHealthyAsync(operation,survivor.Slot,complete,survivorNonce,cancellation);RequireHealth(survivor.Slot,complete,survivorNonce,survivorHealth);
            state=Replace(state,survivor with{Phase=RecoveryHelperSlotPhase.Verified,Closure=complete,Prior=null}) with{Active=survivor.Slot,Epoch=checked(state.Epoch+1)};Write(state);
        }
        var known=target.Prior is{} prior?new[]{target.Closure,prior}:new[]{target.Closure};
        if(!rollback)requireCurrent(operation);
        var pending=target with{Phase=RecoveryHelperSlotPhase.Replacing,Prior=target.Prior??target.Closure,Closure=wanted};
        state=Replace(state,pending) with{PendingOperationId=operation.OperationId,PendingManifestDigest=operation.ManifestDigest,GrantId=operation.GrantId,GrantBindingDigest=operation.GrantBindingDigest};Write(state);
        await kernel.PublishAsync(operation,target.Slot,wanted,known,rollback,cancellation);await kernel.RequireClosureAsync(target.Slot,wanted,cancellation);
        var nonce=Guid.NewGuid().ToString("N");var health=await kernel.ObserveHealthyAsync(operation,target.Slot,wanted,nonce,cancellation);
        RequireHealth(target.Slot,wanted,nonce,health);
        if(!rollback)requireCurrent(operation);await kernel.RequireClosureAsync(target.Slot,wanted,cancellation);
        state=Replace(state,pending with{Phase=RecoveryHelperSlotPhase.Verified,Prior=null});Write(state);return state;
    }
    private async Task<RecoveryHelperClosure> RequireKnownClosure(RecoveryHelperSlotState slot,CancellationToken cancellation)
    {
        try{await kernel.RequireClosureAsync(slot.Slot,slot.Closure,cancellation);return slot.Closure;}
        catch(IOException)when(slot.Prior is not null){await kernel.RequireClosureAsync(slot.Slot,slot.Prior,cancellation);return slot.Prior;}
    }
    private static RecoveryHelperSlotsSelection Replace(RecoveryHelperSlotsSelection value,RecoveryHelperSlotState slot)=>value with{Slots=value.Slots.Select(s=>s.Slot==slot.Slot?slot:s).ToArray()};
    private static void BindPending(VerifiedSystemOperation op,RecoveryHelperSlotsSelection state)
    {if(state.PendingOperationId is not null&&(state.PendingOperationId!=op.OperationId||state.PendingManifestDigest!=op.ManifestDigest||state.GrantId!=op.GrantId||state.GrantBindingDigest!=op.GrantBindingDigest))throw new IOException("HELPER_SLOT_OTHER_OPERATION");}
    private static RecoveryHelperClosure Candidate(VerifiedSystemOperation operation)
    {var c=operation.Components.Single(c=>c.Kind==SystemComponentKind.Helper);return new(operation.ManifestDigest,operation.ToVersion,c.EntrySha256,c.Files.Select(f=>new SystemBootstrapFileState(f.RelativePath,f.Size,f.Sha256)).ToArray());}
    private static bool Same(RecoveryHelperClosure a,RecoveryHelperClosure b)=>JsonSerializer.Serialize(a)==JsonSerializer.Serialize(b);
    private WindowsFileBoundary.DataLease HoldSerial()
    {
        var path=files.PathOf(".authority/update-serial");
        if(serialLease is not null)return serialLease.DuplicateForDirectory(path);
        if(files.Roles is not null)throw new IOException("HELPER_SLOTS_ACTUAL_SERIAL_OWNER_REQUIRED");
        if(!Directory.Exists(path))files.CreateDirectory(path);return files.Lease(path);
    }
    private static void RequireHealth(RecoveryHelperSlot slot,RecoveryHelperClosure wanted,string nonce,RecoveryHelperHealth health)
    {if(health.Slot!=slot||health.ProcessId<1||health.Created<=0||health.EntrySha256!=wanted.EntrySha256||health.Protocol!=1||health.Nonce!=nonce)throw new UnauthorizedAccessException("HELPER_SLOT_HEALTH_BINDING");}
    private void Write(RecoveryHelperSlotsSelection value)
    {
        var path=files.PathOf("bootstrap-helper-selection.json");var temporary=path+".partial-"+Guid.NewGuid().ToString("N");
        using(var output=files.Create(temporary)){output.Write(JsonSerializer.SerializeToUtf8Bytes(value));output.Flush(true);}SystemBootstrapHelperNative.Publish(temporary,path);
    }
    private void ArchivePriorBlocks()
    {
        foreach(var slot in Enum.GetValues<RecoveryHelperSlot>())
        {
            var path=files.PathOf(".authority/bootstrap-recovery-blocked-"+slot+".json");
            try{using var pin=files.Open(path);_=pin.Length;}catch(FileNotFoundException){continue;}catch(System.ComponentModel.Win32Exception error)when(error.NativeErrorCode is 2 or 3){continue;}
            var history=files.PathOf(".authority/bootstrap-recovery-blocked-history");if(!Directory.Exists(history))files.CreateDirectory(history);
            // Fresh authenticated helper closures are materially new evidence;
            // retain old blocked bytes rather than deleting/ignoring them.
            SystemBootstrapHelperNative.Publish(path,Path.Combine(history,slot+"-"+Guid.NewGuid().ToString("N")+".json"));
        }
    }
}

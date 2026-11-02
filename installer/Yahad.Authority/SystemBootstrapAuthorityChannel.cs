using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record SystemBootstrapChallenge(int Protocol,string OperationId,string ManifestDigest,string GrantId,string PolicyDigest,string Nonce,
    int HelperPid,long HelperCreated,string HelperImageDigest,int AuthorityPid,long AuthorityCreated,string AuthorityImageDigest,bool Previous,bool RepairOnly=false);
internal sealed record SystemBootstrapChannelMarker(int Protocol,string OperationId,string ManifestDigest,string Nonce,int HelperPid,long HelperCreated,int AuthorityPid,long AuthorityCreated,string Phase);

// Files below .authority are Authority-only, protected and pinned through the
// existing InstalledStateFiles boundary. There is no application or Owner route.
// Every message binds BOTH actual native process generations and a fresh nonce.
internal sealed class SystemBootstrapAuthorityChannel:IDisposable
{
    private readonly InstalledStateFiles files,binaries;private readonly FullSystemUpdatePolicy policy;private readonly SystemBootstrapSnapshot before;
    private SystemBootstrapChallenge? challenge;private SafeProcessHandle? authority;private bool runtimeRequested,custodySent;
    internal SystemBootstrapAuthorityChannel(InstalledStateFiles files,InstalledStateFiles binaries,FullSystemUpdatePolicy policy,SystemBootstrapSnapshot before)
    {this.files=files;this.binaries=binaries;this.policy=policy;this.before=before;}
    internal Task ObserveAuthorityAsync(VerifiedSystemOperation operation,bool previous,CancellationToken cancellation)=>ObserveAuthorityCoreAsync(operation,previous,false,cancellation);
    internal Task ObservePreviousForRepairAsync(VerifiedSystemOperation operation,CancellationToken cancellation)=>ObserveAuthorityCoreAsync(operation,true,true,cancellation);
    private async Task ObserveAuthorityCoreAsync(VerifiedSystemOperation operation,bool previous,bool repairOnly,CancellationToken cancellation)
    {
        if(!previous)policy.RequireCurrent(operation);using var service=WindowsFixedRuntimeService.AuthorityRead();
        WindowsFixedRuntimeService.WindowsPinnedServiceProcess? server=null;
        while(server is null)
        {
            cancellation.ThrowIfCancellationRequested();var status=service.Status();
            if(status.Pid!=0&&status.State is 2 or 4)server=service.Pin(status.Pid,0x00101040);else await Task.Delay(20,cancellation);
        }
        using(server)
        {
            var descriptor=previous?before.Components.Single(c=>c.Kind==SystemComponentKind.Authority):Candidate(operation,SystemComponentKind.Authority);
            VerifyLiveAuthority(binaries,descriptor,cancellation);server.RequireCurrent();
            using var self=SystemBootstrapHelperNative.OpenSelf();var helper=NativeCustodyTransfer.RequireTarget(self);SystemBootstrapHelperNative.RequireHelperImage(self);
            var helperDigest=HashImage(SystemBootstrapHelperNative.HelperImage);
            if(helperDigest!=before.Components.Single(c=>c.Kind==SystemComponentKind.Helper).EntrySha256&&helperDigest!=operation.Components.Single(c=>c.Kind==SystemComponentKind.Helper).EntrySha256)throw new IOException("SYSTEM_CHANNEL_HELPER_SOURCE");
            using(var helperImage=SystemBootstrapHelperNative.PinImage(SystemBootstrapHelperNative.HelperImage,helperDigest)){}
            var value=new SystemBootstrapChallenge(1,operation.OperationId,operation.ManifestDigest,operation.GrantId,operation.PolicyDigest,Guid.NewGuid().ToString("N"),
                helper.Pid,helper.Created,helperDigest,(int)server.Pid,server.Created,descriptor.EntrySha256,previous,repairOnly);
            if(authority is not null)authority.Dispose();authority=OpenProcess(0x00101040,false,server.Pid);if(authority.IsInvalid)throw new Win32Exception();
            Write(files,PathFor(files,operation.OperationId,"active.json"),value);challenge=value;runtimeRequested=false;custodySent=false;
            var reply=await ReadAsync<SystemBootstrapChannelMarker>(files,PathFor(files,operation.OperationId,value.Nonce+"-health.json"),cancellation,()=>RequirePeer(value,authority,true));
            RequireMarker(value,reply,"health");RequirePeer(value,authority,true);if(!previous)policy.RequireCurrent(operation);
        }
    }
    internal async Task<RetainedRuntimeQuiescence> ObserveRuntimeAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
    {
        var value=Current(operation);RequirePeer(value,authority!,true);if(!value.RepairOnly)policy.RequireCurrent(operation);
        if(runtimeRequested)throw new IOException("SYSTEM_CHANNEL_RUNTIME_REPLAY");runtimeRequested=true;
        Write(files,PathFor(files,operation.OperationId,value.Nonce+"-runtime-request.json"),Marker(value,"runtime"));
        var transfer=await ReadAsync<NativeRuntimeTransfer>(files,PathFor(files,operation.OperationId,value.Nonce+"-runtime.json"),cancellation,()=>RequirePeer(value,authority!,true));
        if(transfer.TargetPid!=value.HelperPid||transfer.TargetCreated!=value.HelperCreated)throw new UnauthorizedAccessException("SYSTEM_CHANNEL_RUNTIME_RECEIVER");
        var actual=RetainedRuntimeQuiescence.AdoptTransferred(transfer);
        try{await actual.RequireAsync(cancellation);RequirePeer(value,authority!,true);if(!value.RepairOnly)policy.RequireCurrent(operation);return actual;}catch{actual.Dispose();throw;}
    }
    internal async Task TransferAsync(SystemBootstrapHelperCustody custody,VerifiedSystemOperation operation,CancellationToken cancellation)
    {
        var value=Current(operation);RequirePeer(value,authority!,true);policy.RequireCurrent(operation);
        if(custodySent||!runtimeRequested)throw new IOException("SYSTEM_CHANNEL_CUSTODY_REPLAY");custodySent=true;
        var transfer=custody.DuplicateToAuthority(operation,authority!,value.Nonce);
        Write(files,PathFor(files,operation.OperationId,value.Nonce+"-custody.json"),transfer);
        var ack=await ReadAsync<SystemBootstrapChannelMarker>(files,PathFor(files,operation.OperationId,value.Nonce+"-ack.json"),cancellation,()=>RequirePeer(value,authority!,true));
        RequireMarker(value,ack,"adopted");RequirePeer(value,authority!,true);
    }
    private SystemBootstrapChallenge Current(VerifiedSystemOperation operation)
    {var value=challenge??throw new IOException("SYSTEM_CHANNEL_NO_AUTHORITY");if(value.OperationId!=operation.OperationId||value.ManifestDigest!=operation.ManifestDigest)throw new IOException("SYSTEM_CHANNEL_OPERATION_CHANGED");return value;}

    // Called by the new SCM Authority BEFORE ordinary app startup. Source and
    // closure checks precede the health marker; bootstrap health launches no app.
    internal static async Task<SystemBootstrapAuthorityReceiver> ReceiveInstalledAsync(InstalledStateFiles files,InstalledStateFiles binaries,
        FullSystemUpdatePolicy policy,VerifiedSystemOperation operation,SystemBootstrapSnapshot before,CancellationToken cancellation)
    {
        using var self=SystemBootstrapHelperNative.OpenSelf();var receiver=NativeCustodyTransfer.RequireTarget(self);SystemBootstrapHelperNative.RequireAuthorityImage(self);
        if(!WindowsAuthorityServiceIdentity.IsCurrentServer((uint)receiver.Pid))throw new UnauthorizedAccessException("SYSTEM_CHANNEL_AUTHORITY_SCM");
        SystemBootstrapChallenge value;SafeProcessHandle? helper=null;
        while(true)
        {
            value=await ReadAsync<SystemBootstrapChallenge>(files,PathFor(files,operation.OperationId,"active.json"),cancellation);
            if(value.AuthorityPid==receiver.Pid&&value.AuthorityCreated==receiver.Created)
            {
                RequireChallenge(operation,value);helper=OpenCurrentHelper(value);
                if(helper is not null)break; // A dead/reused helper challenge grants no ACK or handles.
            }
            await Task.Delay(20,cancellation); // Old generations never receive a new marker or handles.
        }
        var liveHelper=helper??throw new IOException("SYSTEM_CHANNEL_LIVE_HELPER_REQUIRED");
        try
        {
            RequireChallenge(operation,value);if(!value.Previous)policy.RequireCurrent(operation);
            if(value.HelperImageDigest!=before.Components.Single(c=>c.Kind==SystemComponentKind.Helper).EntrySha256&&value.HelperImageDigest!=operation.Components.Single(c=>c.Kind==SystemComponentKind.Helper).EntrySha256)throw new UnauthorizedAccessException("SYSTEM_CHANNEL_HELPER_SOURCE");
            var descriptor=value.Previous?before.Components.Single(c=>c.Kind==SystemComponentKind.Authority):Candidate(operation,SystemComponentKind.Authority);
            if(descriptor.EntrySha256!=value.AuthorityImageDigest)throw new UnauthorizedAccessException("SYSTEM_CHANNEL_AUTHORITY_DIGEST");
            VerifyLiveAuthority(binaries,descriptor,cancellation);
            RequirePeer(value,liveHelper,false);using(var image=SystemBootstrapHelperNative.PinImage(SystemBootstrapHelperNative.Image(liveHelper),value.HelperImageDigest)){}
            using(var claim=files.Create(PathFor(files,operation.OperationId,value.Nonce+"-receiver-claim.json"))){claim.Write(JsonSerializer.SerializeToUtf8Bytes(Marker(value,"claimed")));claim.Flush(true);}
            Write(files,PathFor(files,operation.OperationId,value.Nonce+"-health.json"),Marker(value,"health"));
            return new(files,policy,operation,value,liveHelper);
        }
        catch{liveHelper.Dispose();throw;}
    }
    private static SafeProcessHandle? OpenCurrentHelper(SystemBootstrapChallenge value)
    {
        var process=OpenProcess(0x00101040,false,(uint)value.HelperPid);
        if(process.IsInvalid){var error=Marshal.GetLastWin32Error();process.Dispose();if(error==87)return null;throw new Win32Exception(error);}
        try
        {
            var exited=SystemBootstrapHelperNative.Exited(process);var created=exited?0:NativeCustodyTransfer.Created(process);
            if(IsStaleHelperObservation(value.HelperPid,value.HelperCreated,SystemBootstrapHelperNative.ProcessId(process),created,exited)){process.Dispose();return null;}
            RequirePeer(value,process,false);return process;
        }
        catch{process.Dispose();throw;}
    }
    // Negative-only filter; returning false never authenticates a peer. The
    // native caller still checks actual account, fixed image, source and scope.
    internal static bool IsStaleHelperObservation(int expectedPid,long expectedCreated,int observedPid,long observedCreated,bool exited)
        =>exited||observedPid!=expectedPid||observedCreated!=expectedCreated;
    internal static async Task<RetainedRuntimeQuiescence> ObserveLocalRuntimeAsync(InstalledStateFiles files,CancellationToken cancellation)
    {
        var binding=WindowsInstallationReader.Parse(files.Read(files.PathOf("bootstrap.json"),1024));
        var bytes=files.Read(files.PathOf("activation.json"),16384);var activation=NativeJson.Read<InstalledActivation>(bytes);
        if(activation.InstallationId!=binding.InstallationId||activation.Phase!="committed"||InstalledActivationResolver.Hash(bytes)!=binding.ActivationReceiptDigest)throw new IOException("SYSTEM_CHANNEL_RUNTIME_CONTROL_HEAD");
        return await RuntimeBootstrapObservationOwner.Installed().ObserveAsync(binding,new(activation.ReleaseDigest),cancellation);
    }
    internal static Task ObserveLocalAuthorityAsync(InstalledStateFiles binaries,FullSystemUpdatePolicy policy,VerifiedSystemOperation operation,SystemBootstrapSnapshot before,bool previous,CancellationToken cancellation)
    {
        using var self=SystemBootstrapHelperNative.OpenSelf();var actual=NativeCustodyTransfer.RequireTarget(self);SystemBootstrapHelperNative.RequireAuthorityImage(self);
        if(!WindowsAuthorityServiceIdentity.IsCurrentServer((uint)actual.Pid))throw new UnauthorizedAccessException("SYSTEM_CHANNEL_LOCAL_SCM");
        if(!previous)policy.RequireCurrent(operation);VerifyLiveAuthority(binaries,previous?before.Components.Single(c=>c.Kind==SystemComponentKind.Authority):Candidate(operation,SystemComponentKind.Authority),cancellation);
        return Task.CompletedTask;
    }
    private static SystemBootstrapComponentState Candidate(VerifiedSystemOperation op,SystemComponentKind kind)
    {var c=op.Components.Single(c=>c.Kind==kind);return new(c.Kind,c.EntryPoint,c.EntrySha256,c.Files.Select(f=>new SystemBootstrapFileState(f.RelativePath,f.Size,f.Sha256)).ToArray());}
    private static string HashImage(string path){using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);return Convert.ToHexStringLower(SHA256.HashData(input));}
    public void Dispose(){authority?.Dispose();authority=null;}
    private static void VerifyLiveAuthority(InstalledStateFiles binaries,SystemBootstrapComponentState component,CancellationToken cancellation)
    {
        var root=Path.Combine(binaries.Root,FixedSystemComponents.ActiveDirectory(SystemComponentKind.Authority)!);
        var expected=new HashSet<string>(component.Files.Select(f=>f.RelativePath),StringComparer.OrdinalIgnoreCase);
        foreach(var file in component.Files)
        {
            cancellation.ThrowIfCancellationRequested();FixedSystemComponents.Member(file.RelativePath);var path=Path.Combine(root,file.RelativePath.Replace('/',Path.DirectorySeparatorChar));
            using(var pinned=binaries.Open(path)){if(pinned.Length!=file.Size)throw new IOException("SYSTEM_CHANNEL_AUTHORITY_CLOSURE");}
            using var input=SystemBootstrapHelperNative.PinImage(path,file.Sha256);
        }
        void Walk(string directory,int depth)
        {
            if(depth>64)throw new IOException("SYSTEM_CHANNEL_AUTHORITY_DEPTH");
            foreach(var path in Directory.EnumerateFileSystemEntries(directory))
            {var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("SYSTEM_CHANNEL_AUTHORITY_REPARSE");if((attributes&FileAttributes.Directory)!=0)Walk(path,depth+1);else if(!expected.Remove(Path.GetRelativePath(root,path).Replace('\\','/')))throw new IOException("SYSTEM_CHANNEL_AUTHORITY_EXTRA");}
        }
        Walk(root,0);if(expected.Count!=0)throw new IOException("SYSTEM_CHANNEL_AUTHORITY_MISSING");
    }
    internal static void RequireChallenge(VerifiedSystemOperation operation,SystemBootstrapChallenge value)
    {
        InstalledActivationResolver.Id(value.Nonce);FixedSystemComponents.Digest(value.AuthorityImageDigest);FixedSystemComponents.Digest(value.HelperImageDigest);
        if(value.Protocol!=1||value.OperationId!=operation.OperationId||value.ManifestDigest!=operation.ManifestDigest||value.GrantId!=operation.GrantId||value.PolicyDigest!=operation.PolicyDigest||value.HelperPid<1||value.AuthorityPid<1||value.HelperPid==value.AuthorityPid||value.HelperCreated<=0||value.AuthorityCreated<=0||value.RepairOnly&&!value.Previous)throw new UnauthorizedAccessException("SYSTEM_CHANNEL_CHALLENGE_BINDING");
    }
    internal static void RequirePeer(SystemBootstrapChallenge value,SafeProcessHandle process,bool authority)
    {
        if(NativeCustodyTransfer.RequireTarget(process)!=(authority?value.AuthorityPid:value.HelperPid,authority?value.AuthorityCreated:value.HelperCreated))throw new UnauthorizedAccessException("SYSTEM_CHANNEL_PEER_GENERATION");
        if(authority){SystemBootstrapHelperNative.RequireAuthorityImage(process);if(!WindowsAuthorityServiceIdentity.IsCurrentServer((uint)value.AuthorityPid))throw new UnauthorizedAccessException("SYSTEM_CHANNEL_SCM_CHANGED");}
        else SystemBootstrapHelperNative.RequireHelperImage(process);
    }
    internal static SystemBootstrapChannelMarker Marker(SystemBootstrapChallenge value,string phase)=>new(1,value.OperationId,value.ManifestDigest,value.Nonce,value.HelperPid,value.HelperCreated,value.AuthorityPid,value.AuthorityCreated,phase);
    internal static void RequireMarker(SystemBootstrapChallenge value,SystemBootstrapChannelMarker marker,string phase)
    {if(marker!=Marker(value,phase))throw new UnauthorizedAccessException("SYSTEM_CHANNEL_MARKER_BINDING");}
    internal static string PathFor(InstalledStateFiles files,string op,string name)
    {
        InstalledActivationResolver.Id(op);FixedSystemComponents.Member(name);var parent=files.PathOf(".authority/bootstrap-channel");if(!Directory.Exists(parent))files.CreateDirectory(parent);
        parent=Path.Combine(parent,op);if(!Directory.Exists(parent))files.CreateDirectory(parent);using var pin=files.PinDirectory(parent);return Path.Combine(parent,name);
    }
    internal static void Write<T>(InstalledStateFiles files,string path,T value)
    {
        var temporary=path+".partial-"+Guid.NewGuid().ToString("N");using(var output=files.Create(temporary)){output.Write(JsonSerializer.SerializeToUtf8Bytes(value));output.Flush(true);}
        SystemBootstrapHelperNative.Publish(temporary,path);
    }
    internal static async Task<T> ReadAsync<T>(InstalledStateFiles files,string path,CancellationToken cancellation,Action? requirePeer=null)
    {
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();requirePeer?.Invoke();
            try{return NativeJson.Read<T>(files.Read(path,65536));}
            catch(FileNotFoundException){}catch(Win32Exception error)when(error.NativeErrorCode is 2 or 3){}
            await Task.Delay(20,cancellation);
        }
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint rights,bool inherit,uint pid);
}

internal sealed class SystemBootstrapAuthorityReceiver(InstalledStateFiles files,FullSystemUpdatePolicy policy,VerifiedSystemOperation operation,
    SystemBootstrapChallenge challenge,SafeProcessHandle helper):IDisposable
{
    private bool received;
    internal bool Previous=>challenge.Previous;
    internal bool RepairOnly=>challenge.RepairOnly;
    internal async Task ServeRepairRuntimeAsync(CancellationToken cancellation)
    {
        if(!RepairOnly||received)throw new IOException("SYSTEM_CHANNEL_NOT_REPAIR_OBSERVATION");received=true;
        var request=await SystemBootstrapAuthorityChannel.ReadAsync<SystemBootstrapChannelMarker>(files,SystemBootstrapAuthorityChannel.PathFor(files,operation.OperationId,challenge.Nonce+"-runtime-request.json"),cancellation,()=>SystemBootstrapAuthorityChannel.RequirePeer(challenge,helper,false));
        SystemBootstrapAuthorityChannel.RequireMarker(challenge,request,"runtime");var reminted=policy.RemintBootstrapRecovery(operation.OperationId);
        if(reminted.ManifestDigest!=operation.ManifestDigest||reminted.GrantBindingDigest!=operation.GrantBindingDigest)throw new IOException("SYSTEM_CHANNEL_REPAIR_ADMISSION_CHANGED");
        using var proof=await SystemBootstrapAuthorityChannel.ObserveLocalRuntimeAsync(files,cancellation);
        SystemBootstrapAuthorityChannel.RequirePeer(challenge,helper,false);
        SystemBootstrapAuthorityChannel.Write(files,SystemBootstrapAuthorityChannel.PathFor(files,operation.OperationId,challenge.Nonce+"-runtime.json"),proof.DuplicateNativeHandlesForProcess(helper));
        // Caller stays in the controlled SCM recovery branch until stop; this
        // observation neither adopts family data nor admits normal application.
    }
    internal async Task WaitPreviousRollbackAsync(CancellationToken cancellation)
    {
        if(!Previous)throw new IOException("SYSTEM_CHANNEL_NOT_ROLLBACK");
        await SystemBootstrapHelperNative.WaitExitAsync(helper,cancellation);
        using var journal=new SystemBootstrapJournal(files,operation);
        if(journal.Current?.Phase!=SystemBootstrapPhase.RolledBack)throw new IOException("SYSTEM_CHANNEL_ROLLBACK_NOT_TERMINAL");
    }
    internal async Task<SystemBootstrapAuthorityCustody> AdoptAsync(string actualDataDirectory,CancellationToken cancellation)
    {
        if(received||Previous)throw new IOException("SYSTEM_CHANNEL_RECEIVE_REPLAY");received=true;
        var runtimeRequest=await SystemBootstrapAuthorityChannel.ReadAsync<SystemBootstrapChannelMarker>(files,SystemBootstrapAuthorityChannel.PathFor(files,operation.OperationId,challenge.Nonce+"-runtime-request.json"),cancellation,()=>SystemBootstrapAuthorityChannel.RequirePeer(challenge,helper,false));
        SystemBootstrapAuthorityChannel.RequireMarker(challenge,runtimeRequest,"runtime");policy.RequireCurrent(operation);
        using(var proof=await SystemBootstrapAuthorityChannel.ObserveLocalRuntimeAsync(files,cancellation))
        {var transfer=proof.DuplicateNativeHandlesForProcess(helper);SystemBootstrapAuthorityChannel.Write(files,SystemBootstrapAuthorityChannel.PathFor(files,operation.OperationId,challenge.Nonce+"-runtime.json"),transfer);}
        var capsule=await SystemBootstrapAuthorityChannel.ReadAsync<SystemBootstrapAuthorityTransfer>(files,SystemBootstrapAuthorityChannel.PathFor(files,operation.OperationId,challenge.Nonce+"-custody.json"),cancellation,()=>SystemBootstrapAuthorityChannel.RequirePeer(challenge,helper,false));
        if(capsule.OperationId!=operation.OperationId||capsule.ManifestDigest!=operation.ManifestDigest||capsule.Nonce!=challenge.Nonce||capsule.TargetPid!=challenge.AuthorityPid||capsule.TargetCreated!=challenge.AuthorityCreated||capsule.HelperCreated!=challenge.HelperCreated)throw new UnauthorizedAccessException("SYSTEM_CHANNEL_CUSTODY_BINDING");
        policy.RequireCurrent(operation);var owned=SystemBootstrapAuthorityCustody.Adopt(actualDataDirectory,capsule);
        try{await owned.Runtime.RequireAsync(cancellation);if(SystemBootstrapHelperNative.ProcessId(owned.Helper)!=challenge.HelperPid)throw new IOException("SYSTEM_CHANNEL_HELPER_PID");SystemBootstrapAuthorityChannel.RequirePeer(challenge,helper,false);SystemBootstrapAuthorityChannel.Write(files,SystemBootstrapAuthorityChannel.PathFor(files,operation.OperationId,challenge.Nonce+"-ack.json"),SystemBootstrapAuthorityChannel.Marker(challenge,"adopted"));return owned;}
        catch{owned.Dispose();throw;}
    }
    public void Dispose()=>helper.Dispose();
}

internal sealed class SystemBootstrapAuthorityCustody:IDisposable
{
    internal RetainedRuntimeQuiescence Runtime{get;}
    internal WindowsFileBoundary.DataLease Data{get;}
    internal WindowsFileBoundary.DataLease Serial{get;}
    internal SafeProcessHandle Helper{get;}
    internal SafeProcessHandle? OldAuthority{get;}
    internal RetainedRuntimeQuiescence HelperQuiescence{get;}
    private SystemBootstrapAuthorityCustody(RetainedRuntimeQuiescence runtime,WindowsFileBoundary.DataLease data,WindowsFileBoundary.DataLease serial,SafeProcessHandle helper,SafeProcessHandle? old,RetainedRuntimeQuiescence helperTree)
    {Runtime=runtime;Data=data;Serial=serial;Helper=helper;OldAuthority=old;HelperQuiescence=helperTree;}
    internal static SystemBootstrapAuthorityCustody Adopt(string dataDirectory,SystemBootstrapAuthorityTransfer value)
    {
        NativeCustodyTransfer.RequireReceiver(value.TargetPid,value.TargetCreated);
        var handles=value.Data.Ancestors.Concat(value.Serial.Ancestors).Concat([value.Data.Gate,value.Serial.Gate,value.Runtime.Host,value.Runtime.Job,value.Helper,value.HelperJob]).Concat(value.OldAuthority==0?Array.Empty<ulong>():[value.OldAuthority]).ToArray();
        if(handles.Distinct().Count()!=handles.Length||handles.Any(x=>x==0||x>long.MaxValue))throw new IOException("SYSTEM_CHANNEL_HANDLE_ALIAS");
        RetainedRuntimeQuiescence? runtime=null,helperTree=null;WindowsFileBoundary.DataLease? data=null,serial=null;SafeProcessHandle? helper=null,old=null;
        try
        {
            helper=new(NativeCustodyTransfer.Pointer(value.Helper),true);SystemBootstrapHelperNative.RequireHelperImage(helper);
            if(NativeCustodyTransfer.Created(helper)!=value.HelperCreated)throw new UnauthorizedAccessException("SYSTEM_CHANNEL_HELPER_GENERATION");
            using(var helperJob=new SafeFileHandle(NativeCustodyTransfer.Pointer(value.HelperJob),true)){SystemBootstrapHelperNative.RequireOutsideJob(helperJob);helperTree=RetainedRuntimeQuiescence.FromRetainedHostAndJob(helper,helperJob,SystemBootstrapHelperNative.ProcessId(helper));}
            if(value.OldAuthority!=0){old=new(NativeCustodyTransfer.Pointer(value.OldAuthority),true);if(NativeCustodyTransfer.Created(old)!=value.OldAuthorityCreated)throw new IOException("SYSTEM_CHANNEL_OLD_GENERATION");}
            runtime=RetainedRuntimeQuiescence.AdoptTransferred(value.Runtime);data=WindowsFileBoundary.DataLease.AdoptTransferred(dataDirectory,value.Data);
            serial=WindowsFileBoundary.DataLease.AdoptTransferred(filesDirectory(),value.Serial);return new(runtime,data,serial,helper,old,helperTree);
        }
        catch{serial?.Dispose();data?.Dispose();runtime?.Dispose();helperTree?.Dispose();helper?.Dispose();old?.Dispose();throw;}
        static string filesDirectory()=>Path.Combine(InstalledUpdaterLayout.StateRoot,".authority","update-serial");
    }
    public void Dispose(){Serial.Dispose();Data.Dispose();Runtime.Dispose();HelperQuiescence.Dispose();Helper.Dispose();OldAuthority?.Dispose();}
}

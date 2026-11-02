using System.ComponentModel;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

// Provisioning contract only: initial installer creates BOTH independent AUTO
// one-shot services with fixed A/B images and their own virtual accounts.
// Neither depends on primary Authority starting. No service/task/ACL API here.
internal static class SystemBootstrapRecoveryBootApplication
{
    internal static int RunInstalled(string[] args,Func<WindowsFileBoundary.DataLease?,FullSystemHelperComposition> factory)
    {
        if(args is not["--system-recovery-service",var name]||!Enum.TryParse<RecoveryHelperSlot>(name,false,out var slot)||!Enum.IsDefined(slot)||slot.ToString()!=name)throw new IOException("HELPER_BOOT_ARGUMENTS");
        return Run(new NativeScmApi(),slot,factory,error=>ServiceStartupDiagnostic.TryRecord(slot==RecoveryHelperSlot.A?StartupDiagnosticRole.RecoveryA:StartupDiagnosticRole.RecoveryB,error));
    }
    internal static int Run(IScmApi api,RecoveryHelperSlot slot,Func<WindowsFileBoundary.DataLease?,FullSystemHelperComposition> factory,Action<Exception>? reportFailure=null)
    {
        var completed=false;var name=BootstrapRecoveryLayout.ServiceName(slot);
        api.Dispatch(name,arguments=>
        {
            using var stop=new CancellationTokenSource(TimeSpan.FromMinutes(5));
            api.Register(name,control=>{if(control is ServiceDefinition.Stop or ServiceDefinition.Shutdown){stop.Cancel();return 0;}return control==ServiceDefinition.Interrogate?0u:120u;});
            api.Publish(new(ServiceState.StartPending,0,1,300000,0,0));
            InstalledStateFiles? failureFiles=null;string? failureOperation=null;
            try
            {
                if(arguments.Length!=1||arguments[0]!=name)throw new IOException("HELPER_BOOT_SCM_ARGUMENTS");
                var anchor=InstalledAuthorityReader.Read();using var self=SystemBootstrapHelperNative.OpenSelf();var actual=NativeCustodyTransfer.RequireTarget(self);
                RecoveryHelperScmIdentity.Require(slot,actual.Pid);
                if(!SystemBootstrapHelperNative.Image(self).Equals(BootstrapRecoveryLayout.Image(slot),StringComparison.OrdinalIgnoreCase))throw new IOException("HELPER_BOOT_SELF_IMAGE");
                if(anchor.FullSystemPolicyDigest is null)throw new IOException("HELPER_BOOT_FULL_CONSENT_ANCHOR_REQUIRED");
                InstalledProcessObjectAccess.PublishAuthorityCustodyAccess(SystemBootstrapHelperNative.Roles());
                NativeCleanupOwnershipGate.RetryRetainedAsync(stop.Token).GetAwaiter().GetResult();NativeCleanupOwnershipGate.RequireClear();
                var roles=SystemBootstrapHelperNative.Roles();var files=InstalledStateFiles.Installed(InstalledUpdaterLayout.StateRoot,roles,"","");
                failureFiles=files;var binaries=InstalledStateFiles.Installed(InstalledUpdaterLayout.BinaryRoot,roles,"","");
                var intent=LiveLaunchIntent(files,slot);
                if(intent is not null)
                {
                    failureOperation=intent.OperationId;
                    using var composition=factory(null);var operation=composition.Hooks.Policy.RemintBootstrapRecovery(intent.OperationId);
                    if(operation.ManifestDigest!=intent.ManifestDigest)throw new IOException("HELPER_LAUNCH_INTENT_OPERATION");
                    RequireOwnClosure(files,binaries,anchor.InstallationId,operation.PolicyDigest,slot,stop.Token);
                    // RUNNING describes an actual authenticated waiting helper,
                    // not candidate/bootstrap health or permission to touch data.
                    api.Publish(new(ServiceState.Running,1|4,0,0,0,0));
                    WaitCapsuleAsync(files,intent,actual.Pid,actual.Created,stop.Token).GetAwaiter().GetResult();
                    completed=SystemBootstrapHelperApplication.RunInstalledAsync(["--system-handoff"],composition.Hooks,stop.Token).GetAwaiter().GetResult()==0;return;
                }
                var active=files.PathOf(".authority/bootstrap-helper-active.json");
                try{_=File.GetAttributes(active);}catch(FileNotFoundException){completed=true;return;}
                var original=NativeJson.Read<SystemBootstrapHelperCapsule>(files.Read(active,65536));SystemBootstrapHelperApplication.RequireCapsule(original);failureOperation=original.OperationId;
                var blockedPath=files.PathOf(".authority/bootstrap-recovery-blocked-"+slot+".json");
                if(Present(files,blockedPath))
                {
                    var blocked=NativeJson.Read<JsonElement>(files.Read(blockedPath,8192));
                    if(!blocked.TryGetProperty("OperationId",out var blockedId)||blockedId.ValueKind!=JsonValueKind.String||blockedId.GetString()==original.OperationId){completed=true;return;}
                }
                WindowsFileBoundary.DataLease serial;
                try{serial=files.Lease(files.PathOf(".authority/update-serial"));}
                catch(Win32Exception busy)when(busy.NativeErrorCode==32)
                {
                    if(!ObserveBusyOwner(files))throw new IOException("HELPER_BOOT_SERIAL_OWNER_UNCONFIRMED",busy);
                    completed=true;return; // One actual owner observation; no wait, retry or boot loop.
                }
                using(serial)
                using(var composition=factory(serial))
                {
                    var capsule=NativeJson.Read<SystemBootstrapHelperCapsule>(files.Read(active,65536));SystemBootstrapHelperApplication.RequireCapsule(capsule);
                    if(!TryColdAttempt(files,anchor.InstallationId,slot,capsule)){completed=true;return;}
                    var operation=composition.Hooks.Policy.RemintBootstrapRecovery(capsule.OperationId);
                    RequireOwnClosure(files,binaries,anchor.InstallationId,operation.PolicyDigest,slot,stop.Token);
                    using(var journal=new SystemBootstrapJournal(files,operation,serial))
                    {
                        if(journal.Current?.Phase is SystemBootstrapPhase.Complete or SystemBootstrapPhase.RolledBack){completed=true;return;}
                    }
                    try{_=SystemBootstrapHelperApplication.RequireGoEvidence(files,capsule);}catch(IOException refused)when(refused.Message=="SYSTEM_HELPER_GO_REQUIRED"){completed=true;return;}
                    api.Publish(new(ServiceState.Running,1|4,0,0,0,0));
                    // Existing original Go and admission are revalidated by the
                    // SAME helper entry; stale numeric handles are not adopted.
                    completed=SystemBootstrapHelperApplication.RunInstalledAsync(["--system-handoff"],composition.Hooks,stop.Token).GetAwaiter().GetResult()==0;
                }
            }
            catch(Exception error)
            {
                try{reportFailure?.Invoke(error);}catch{}
                // Bound cross-failure wakeups. A normal live launch with a new
                // authenticated intent remains possible; failed cold recovery
                // cannot turn the two fixed SCM actions into an endless loop.
                if(failureFiles is not null)
                {
                    try
                    {
                        var blocked=failureFiles.PathOf(".authority/bootstrap-recovery-blocked-"+slot+".json");
                        if(!Present(failureFiles,blocked)){using var output=failureFiles.Create(blocked);output.Write(JsonSerializer.SerializeToUtf8Bytes(new{Format=1,Slot=slot,OperationId=failureOperation,Reason="recovery_failed"}));output.Flush(true);}
                    }
                    catch{} // No boundary repair or success claim on denial.
                }
                completed=false;
            }
            finally{api.Publish(completed?new(ServiceState.Stopped,0,0,0,0,0):new(ServiceState.Stopped,0,0,0,1066,(uint)ServiceFailure.StartFailed));}
        });
        return completed?0:1;
    }
    private static void RequireOwnClosure(InstalledStateFiles files,InstalledStateFiles binaries,string installation,string policy,RecoveryHelperSlot slot,CancellationToken cancellation)
    {
        var selected=SystemBootstrapRecoverySlots.Read(files,installation,policy).Slots.Single(s=>s.Slot==slot);
        try{RequireClosure(binaries,slot,selected.Closure,cancellation);}catch(IOException)when(selected.Prior is not null){RequireClosure(binaries,slot,selected.Prior,cancellation);}
    }
    private static bool Present(InstalledStateFiles files,string path)
    {try{using var pinned=files.Open(path);_=pinned.Length;return true;}catch(FileNotFoundException){return false;}catch(Win32Exception error)when(error.NativeErrorCode is 2 or 3){return false;}}
    private static bool TryColdAttempt(InstalledStateFiles files,string installation,RecoveryHelperSlot slot,SystemBootstrapHelperCapsule capsule)
    {
        var path=files.PathOf(".authority/bootstrap-recovery-attempt-"+capsule.OperationId+"-"+slot+".json");if(Present(files,path))return false;
        using var output=files.Create(path);output.Write(JsonSerializer.SerializeToUtf8Bytes(new{Format=1,InstallationId=installation,capsule.OperationId,capsule.ManifestDigest,Slot=slot}));output.Flush(true);return true;
    }
    private static bool ObserveBusyOwner(InstalledStateFiles files)
    {
        bool Observe(NativeDataLeaseTransfer witness)
        {
            try{using var actual=NativeCustodyTransfer.ObservePeerSerialOwner(witness);return true;}
            catch(Exception error)when(error is IOException or Win32Exception or UnauthorizedAccessException){return false;}
        }
        var intentPath=files.PathOf(".authority/bootstrap-helper-launch.json");
        if(Present(files,intentPath))
        {
            var intent=NativeJson.Read<RecoveryHelperLaunchIntent>(files.Read(intentPath,65536));ValidateIntent(intent);
            if(intent.SerialWitness is{} witness&&witness.TargetPid==intent.ParentPid&&witness.TargetCreated==intent.ParentCreated&&WindowsAuthorityServiceIdentity.IsCurrentServer((uint)intent.ParentPid)&&Observe(witness))return true;
        }
        var active=files.PathOf(".authority/bootstrap-helper-active.json");if(!Present(files,active))return false;
        var capsule=NativeJson.Read<SystemBootstrapHelperCapsule>(files.Read(active,65536));SystemBootstrapHelperApplication.RequireCapsule(capsule);
        if(Observe(capsule.Serial))return true;
        var challengePath=files.PathOf(".authority/bootstrap-channel/"+capsule.OperationId+"/active.json");if(!Present(files,challengePath))return false;
        var challenge=NativeJson.Read<SystemBootstrapChallenge>(files.Read(challengePath,65536));InstalledActivationResolver.Id(challenge.Nonce);
        if(challenge.OperationId!=capsule.OperationId||challenge.ManifestDigest!=capsule.ManifestDigest||!WindowsAuthorityServiceIdentity.IsCurrentServer((uint)challenge.AuthorityPid))return false;
        var path=files.PathOf(".authority/bootstrap-channel/"+capsule.OperationId+"/"+challenge.Nonce+"-custody.json");if(!Present(files,path))return false;
        var transfer=NativeJson.Read<SystemBootstrapAuthorityTransfer>(files.Read(path,65536));
        return transfer.OperationId==capsule.OperationId&&transfer.ManifestDigest==capsule.ManifestDigest&&transfer.Nonce==challenge.Nonce&&transfer.TargetPid==challenge.AuthorityPid&&transfer.TargetCreated==challenge.AuthorityCreated&&Observe(transfer.Serial);
    }
    private static RecoveryHelperLaunchIntent? LiveLaunchIntent(InstalledStateFiles files,RecoveryHelperSlot slot)
    {
        var path=files.PathOf(".authority/bootstrap-helper-launch.json");if(!Present(files,path))return null;
        var value=NativeJson.Read<RecoveryHelperLaunchIntent>(files.Read(path,8192));ValidateIntent(value);
        // Once Go was published this intent is consumed. A peer after helper
        // death must reconcile from fresh custody, even if old Authority has
        // not yet exited; it must not wait for a capsule for a dead PID again.
        var go=SystemBootstrapHelperApplication.MarkerPath(files,value.OperationId,"go",value.Nonce);
        if(Present(files,go))
        {
            var marker=NativeJson.Read<SystemBootstrapHelperMarker>(files.Read(go,4096));
            if(marker.OperationId==value.OperationId&&marker.ManifestDigest==value.ManifestDigest&&marker.Nonce==value.Nonce)return null;
        }
        if(value.Slot!=slot)return null;using var parent=OpenProcess(0x00101000,false,value.ParentPid);
        if(parent.IsInvalid){var error=Marshal.GetLastWin32Error();if(error==87)return null;throw new Win32Exception(error);}
        if(NativeCustodyTransfer.Created(parent)!=value.ParentCreated)return null;
        NativeCustodyTransfer.RequireTarget(parent);SystemBootstrapHelperNative.RequireAuthorityImage(parent);
        return WindowsAuthorityServiceIdentity.IsCurrentServer((uint)value.ParentPid)?value:null;
    }
    internal static void ValidateIntent(RecoveryHelperLaunchIntent value)
    {
        InstalledActivationResolver.Id(value.OperationId);InstalledActivationResolver.Id(value.Nonce);FixedSystemComponents.Digest(value.ManifestDigest);
        if(value.Format!=1||!Enum.IsDefined(value.Slot)||value.ParentPid<1||value.ParentCreated<=0)throw new IOException("HELPER_LAUNCH_INTENT_BINDING");
    }
    private static async Task WaitCapsuleAsync(InstalledStateFiles files,RecoveryHelperLaunchIntent intent,int pid,long created,CancellationToken cancellation)
    {
        using var parent=OpenProcess(0x00101000,false,intent.ParentPid);if(parent.IsInvalid)throw new Win32Exception();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while(true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if(NativeCustodyTransfer.RequireTarget(parent)!=(intent.ParentPid,intent.ParentCreated)||!WindowsAuthorityServiceIdentity.IsCurrentServer((uint)intent.ParentPid))throw new IOException("HELPER_LAUNCH_PARENT_CHANGED");
            var path=files.PathOf(".authority/bootstrap-helper-active.json");
            if(Present(files,path))
            {
                var value=NativeJson.Read<SystemBootstrapHelperCapsule>(files.Read(path,65536));
                if(value.OperationId==intent.OperationId&&value.ManifestDigest==intent.ManifestDigest&&value.Nonce==intent.Nonce&&value.HelperPid==pid&&value.HelperCreated==created)
                {if(value.RecoveryOnly!=intent.RecoveryOnly)throw new IOException("HELPER_LAUNCH_MODE_BINDING");SystemBootstrapHelperApplication.RequireCapsule(value);return;}
            }
            await Task.Delay(20,deadline.Token);
        }
    }
    internal static void RequireClosure(InstalledStateFiles binaries,RecoveryHelperSlot slot,RecoveryHelperClosure closure,CancellationToken cancellation)
    {
        var root=BootstrapRecoveryLayout.Directory(slot);var expected=new HashSet<string>(closure.Files.Select(f=>f.RelativePath),StringComparer.OrdinalIgnoreCase);
        foreach(var file in closure.Files)
        {
            cancellation.ThrowIfCancellationRequested();FixedSystemComponents.Member(file.RelativePath);using var input=binaries.Open(Path.Combine(root,file.RelativePath.Replace('/',Path.DirectorySeparatorChar)));
            if(input.Length!=file.Size)throw new IOException("HELPER_BOOT_CLOSURE_SIZE");using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[65536];long offset=0;int read;
            while((read=input.Read(offset,buffer))>0){cancellation.ThrowIfCancellationRequested();hash.AppendData(buffer.AsSpan(0,read));offset+=read;}
            if(Convert.ToHexStringLower(hash.GetHashAndReset())!=file.Sha256)throw new IOException("HELPER_BOOT_CLOSURE_HASH");
        }
        void Walk(string directory,int depth)
        {
            cancellation.ThrowIfCancellationRequested();if(depth>64)throw new IOException("HELPER_BOOT_CLOSURE_DEPTH");using var held=binaries.PinDirectory(directory);
            foreach(var path in Directory.EnumerateFileSystemEntries(directory))
            {if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("HELPER_BOOT_REPARSE");if(Directory.Exists(path))Walk(path,depth+1);else if(!expected.Remove(Path.GetRelativePath(root,path).Replace('\\','/')))throw new IOException("HELPER_BOOT_CLOSURE_EXTRA");}
        }
        Walk(root,0);if(expected.Count!=0)throw new IOException("HELPER_BOOT_CLOSURE_MISSING");
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
}

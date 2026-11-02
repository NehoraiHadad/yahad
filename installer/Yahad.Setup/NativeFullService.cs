using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
namespace Yahad.Setup;

// Installer-only SCM writer. Every name, image, argument and account comes from
// the compiled contract; no CLI, plan JSON or runtime peer can choose them.
internal sealed class NativeFullService:IDisposable
{
    private readonly FullServiceContract contract;private readonly Handle manager,service;
    private NativeFullService(FullServiceContract contract,Handle manager,Handle service){this.contract=contract;this.manager=manager;this.service=service;}
    internal static bool Exists(FullServiceKind kind)
    {
        var contract=FullServiceContract.Describe(kind);using var manager=new Handle(OpenSCManagerW(null,null,1));if(manager.IsInvalid)throw new Win32Exception();
        using var service=new Handle(OpenServiceW(manager,contract.Name,4));if(!service.IsInvalid)return true;
        var error=Marshal.GetLastPInvokeError();if(error==1060)return false;if(error==1072)return true;throw new Win32Exception(error);
    }
    internal static NativeFullService Create(FullServiceKind kind)
    {
        NativeInstallFiles.RequireAdministrator();var contract=FullServiceContract.Describe(kind);
        var manager=new Handle(OpenSCManagerW(null,null,3));if(manager.IsInvalid){manager.Dispose();throw new Win32Exception();}
        var service=new Handle(CreateServiceW(manager,contract.Name,contract.Name,0xf01ff,16,3,1,contract.Command,null,IntPtr.Zero,null,contract.Account,null));
        if(service.IsInvalid){service.Dispose();manager.Dispose();throw new Win32Exception();}
        var value=new NativeFullService(contract,manager,service);
        try
        {
            // This handle is the result of a successful CREATE, never adoption
            // of an existing name. Give only the administrative group ownership
            // before any narrow service ACL is published to other principals.
            var owner=new RawSecurityDescriptor("O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)");var descriptor=new byte[owner.BinaryLength];owner.GetBinaryForm(descriptor,0);
            if(!SetServiceObjectSecurity(service,5,descriptor))throw new Win32Exception();
            value.Change(5,1u);var names=Marshal.StringToHGlobalUni("SeChangeNotifyPrivilege\0\0");
            try{value.Change(6,new Privileges{Names=names});}finally{Marshal.FreeHGlobal(names);}
            value.RequireOwned();value.RequireAdministrativeWriters();var status=value.Status();RequireProvisioningState(3,status.State,status.Pid,status.Win32,status.Specific);return value;
        }
        catch{value.Dispose();throw;} // The durable installer intent owns any created service; no guessed rollback.
    }
    internal static NativeFullService Open(FullServiceKind kind)
    {
        NativeInstallFiles.RequireAdministrator();var contract=FullServiceContract.Describe(kind);var manager=new Handle(OpenSCManagerW(null,null,1));
        if(manager.IsInvalid){manager.Dispose();throw new Win32Exception();}var service=new Handle(OpenServiceW(manager,contract.Name,0xf01ff));
        if(service.IsInvalid){service.Dispose();manager.Dispose();throw new Win32Exception();}var value=new NativeFullService(contract,manager,service);
        try{value.RequireOwned();return value;}catch{value.Dispose();throw;}
    }
    internal static async Task<NativeFullService?> OpenForRemovalAsync(FullServiceKind kind,CancellationToken cancellation)
    {
        var until=Stopwatch.StartNew();
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();if(!Exists(kind))return null;
            try{return Open(kind);}
            catch(Win32Exception error)when(error.NativeErrorCode==1072)
            {if(until.Elapsed>=TimeSpan.FromSeconds(15))throw new IOException("UNINSTALL_SERVICE_REMOVAL_PENDING",error);await Task.Delay(100,cancellation);}
            catch(Win32Exception error)when(error.NativeErrorCode==1060){return null;}
        }
    }
    internal static string Sid(FullServiceKind kind)=>((SecurityIdentifier)new NTAccount(FullServiceContract.Describe(kind).Account).Translate(typeof(SecurityIdentifier))).Value;
    internal void RequireOwned()
    {
        RequireConfigurationIdentity();
        RequireRestrictedIdentity();
    }
    private uint RequireConfigurationIdentity()
    {
        QueryServiceConfigW(service,IntPtr.Zero,0,out var count);if(count<(uint)Marshal.SizeOf<Configuration>()||count>16384)throw new IOException("UPDATE_SERVICE_CONFIG_BOUND");var memory=Marshal.AllocHGlobal((int)count);
        try
        {
            if(!QueryServiceConfigW(service,memory,count,out _))throw new Win32Exception();var config=Marshal.PtrToStructure<Configuration>(memory);
            if(config.Type!=16||Marshal.PtrToStringUni(config.Image)!=contract.Command||!string.Equals(Marshal.PtrToStringUni(config.Account),contract.Account,StringComparison.OrdinalIgnoreCase))throw new IOException("UPDATE_SERVICE_NOT_OWNED");
            return config.Start;
        }
        finally{Marshal.FreeHGlobal(memory);}
    }
    private void RequireRestrictedIdentity()
    {
        if(ReadConfig2(5,(p,_)=>Marshal.ReadInt32(p))!=1)throw new IOException("UPDATE_SERVICE_SID_TYPE");
        _=ReadConfig2(6,(p,n)=>
        {
            if(n<IntPtr.Size)throw new IOException("UPDATE_SERVICE_PRIVILEGES");
            var text=Marshal.ReadIntPtr(p);
            var privileges=ReadString(text,p,n);
            if(privileges!="SeChangeNotifyPrivilege")throw new IOException("UPDATE_SERVICE_PRIVILEGES");
            var terminator=text+checked((privileges.Length+1)*2);
            if(terminator.ToInt64()<p.ToInt64()||terminator.ToInt64()+2>p.ToInt64()+n||Marshal.ReadInt16(terminator)!=0)throw new IOException("UPDATE_SERVICE_PRIVILEGES");return 0;
        });
    }
    internal static NativeFullService OpenForProvisioning(FullServiceKind kind,string owner,string? authority)
    {
        NativeInstallFiles.RequireAdministrator();var contract=FullServiceContract.Describe(kind);var manager=new Handle(OpenSCManagerW(null,null,1));
        if(manager.IsInvalid){manager.Dispose();throw new Win32Exception();}var handle=new Handle(OpenServiceW(manager,contract.Name,0xf01ff));
        if(handle.IsInvalid){handle.Dispose();manager.Dispose();throw new Win32Exception();}var value=new NativeFullService(contract,manager,handle);
        try
        {
            // Only the authenticated installer's exact-source preparing intent
            // enters this path. Ordinary Open remains strict. An interruption
            // between CreateService and SID/privilege configuration can resume
            // only before that service has EVER started.
            var status=value.Status();RequireProvisioningState(value.RequireConfigurationIdentity(),status.State,status.Pid,status.Win32,status.Specific);
            if(authority is not null)
            {try{value.RequireProtection(owner,authority);}catch(IOException){value.RequireAdministrativeWriters();}}
            else value.RequireAdministrativeWriters();
            value.Change(5,1u);var names=Marshal.StringToHGlobalUni("SeChangeNotifyPrivilege\0\0");
            try{value.Change(6,new Privileges{Names=names});}finally{Marshal.FreeHGlobal(names);}
            value.SetFailureActions(null);value.Change(4,0);value.RequireOwned();return value;
        }
        catch{value.Dispose();throw;}
    }
    internal static void RequireProvisioningState(uint start,uint state,uint pid,uint win32,uint specific)
    {if(start!=3||state!=1||pid!=0||win32!=1077||specific!=0)throw new IOException("UPDATE_SERVICE_PROVISIONING_UNCONFIRMED");}
    internal void RequirePreparingCancellation()
    {
        // Read only: unlike OpenForProvisioning this never changes identity,
        // privileges, startup or failure actions to make a service eligible.
        var status=Status();RequireProvisioningState(RequireConfigurationIdentity(),status.State,status.Pid,status.Win32,status.Specific);RequireRestrictedIdentity();
        QueryServiceObjectSecurity(service,5,IntPtr.Zero,0,out var size);if(size is <20 or >65536)throw new IOException("UPDATE_SERVICE_DACL_BOUND");
        var memory=Marshal.AllocHGlobal((int)size);
        try
        {
            if(!QueryServiceObjectSecurity(service,5,memory,size,out _))throw new Win32Exception();var bytes=new byte[size];Marshal.Copy(memory,bytes,0,bytes.Length);
            WindowsFileBoundary.ValidatePreparingInstallAcl(new(bytes,0),false);
        }
        finally{Marshal.FreeHGlobal(memory);}
    }
    internal void RequireProvisionedPreparingCancellation(string originalOwner,string authoritySid)
    {
        var status=Status();RequireProvisioningState(RequireConfigurationIdentity(),status.State,status.Pid,status.Win32,status.Specific);
        RequireRestrictedIdentity();RequireProtection(originalOwner,authoritySid);
    }
    private void RequireAdministrativeWriters()
    {
        QueryServiceObjectSecurity(service,5,IntPtr.Zero,0,out var bytes);if(bytes is <20 or >65536)throw new IOException("UPDATE_SERVICE_DACL_BOUND");var memory=Marshal.AllocHGlobal((int)bytes);
        try
        {
            if(!QueryServiceObjectSecurity(service,5,memory,bytes,out _))throw new Win32Exception();var raw=new byte[bytes];Marshal.Copy(memory,raw,0,raw.Length);
            RequireAdministrativeWriters(new RawSecurityDescriptor(raw,0));
        }
        finally{Marshal.FreeHGlobal(memory);}
    }
    internal static void RequireAdministrativeWriters(RawSecurityDescriptor acl)
    {
        RequireAdministrativeOwner(acl);if(acl.DiscretionaryAcl is null)throw new IOException("UPDATE_SERVICE_DACL");
        foreach(GenericAce raw in acl.DiscretionaryAcl)
        {
            if(raw is not CommonAce ace||ace.IsCallback)throw new IOException("UPDATE_SERVICE_DACL");
            if(ace.AceQualifier==AceQualifier.AccessDenied)continue;
            if(ace.AceQualifier!=AceQualifier.AccessAllowed)throw new IOException("UPDATE_SERVICE_DACL");
            if(ace.SecurityIdentifier.Value is "S-1-5-18" or "S-1-5-32-544")continue;
            // Generic/service read only: query config/status/dependents,
            // interrogate and READ_CONTROL. No start/stop, config or ownership.
            if((ace.AccessMask&~unchecked((int)0x8002008d))!=0)throw new IOException("UPDATE_SERVICE_DACL");
        }
    }
    internal static void RequireAdministrativeOwner(RawSecurityDescriptor acl)
    {if(acl.Owner?.Value is not("S-1-5-18" or "S-1-5-32-544"))throw new IOException("UPDATE_SERVICE_OWNER");}
    internal static string ServiceDescriptor(string originalOwner,string authoritySid,uint authorityRights,string runtimeSid)
    {
        if(!new SecurityIdentifier(originalOwner).IsAccountSid()||!originalOwner.StartsWith("S-1-5-21-",StringComparison.Ordinal)||!authoritySid.StartsWith("S-1-5-80-",StringComparison.Ordinal)||authorityRights!=0x35)throw new IOException("UPDATE_SERVICE_GRANT");
        var roles=new InstalledFileRoles(new SecurityIdentifier(authoritySid),new SecurityIdentifier(runtimeSid));
        if(authoritySid==runtimeSid||UpdaterServicePrincipals.IsRecovery(roles.Runtime)||!runtimeSid.StartsWith("S-1-5-80-",StringComparison.Ordinal))throw new IOException("UPDATE_SERVICE_GRANT");
        // 0x35 = query-config/status + start/stop. No change-config, delete,
        // write-DACL/owner, token duplication or machine-wide privilege.
        return $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00000005;;;{originalOwner})(A;;0x00000005;;;{runtimeSid})"+
            string.Concat(UpdaterServicePrincipals.All(new SecurityIdentifier(authoritySid)).Select(sid=>$"(A;;0x{authorityRights:x};;;{sid.Value})"));
    }
    internal void Protect(string originalOwner,string authoritySid)
    {
        NativeInstallFiles.RequireAdministrator();RequireOwned();var acl=new RawSecurityDescriptor(ServiceDescriptor(originalOwner,authoritySid,contract.AuthorityRights,Sid(FullServiceKind.Runtime)));var bytes=new byte[acl.BinaryLength];acl.GetBinaryForm(bytes,0);
        if(!SetServiceObjectSecurity(service,4,bytes))throw new Win32Exception();
        RequireProtection(originalOwner,authoritySid);
    }
    internal void RequireProtection(string originalOwner,string authoritySid)
    {
        QueryServiceObjectSecurity(service,5,IntPtr.Zero,0,out var size);if(size is <20 or >65536)throw new IOException("UPDATE_SERVICE_DACL_BOUND");var memory=Marshal.AllocHGlobal((int)size);
        try
        {
            if(!QueryServiceObjectSecurity(service,5,memory,size,out _))throw new Win32Exception();var bytes=new byte[size];Marshal.Copy(memory,bytes,0,bytes.Length);
            var acl=new RawSecurityDescriptor(bytes,0);RequireAdministrativeOwner(acl);if(acl.DiscretionaryAcl is null||acl.DiscretionaryAcl.Count!=7)throw new IOException("UPDATE_SERVICE_DACL");
            var wanted=new Dictionary<string,int>(StringComparer.Ordinal){{"S-1-5-18",0xf01ff},{"S-1-5-32-544",0xf01ff},{originalOwner,5},{Sid(FullServiceKind.Runtime),5}};
            foreach(var sid in UpdaterServicePrincipals.All(new SecurityIdentifier(authoritySid)))wanted.Add(sid.Value,0x35);
            foreach(GenericAce raw in acl.DiscretionaryAcl)
            {
                if(raw is not CommonAce ace||ace.IsCallback||ace.AceQualifier!=AceQualifier.AccessAllowed||ace.AceFlags!=AceFlags.None||!wanted.Remove(ace.SecurityIdentifier.Value,out var expected))throw new IOException("UPDATE_SERVICE_DACL");
                var observed=ace.AccessMask==0x10000000?0xf01ff:ace.AccessMask;if(observed!=expected)throw new IOException("UPDATE_SERVICE_DACL");
            }
        }
        finally{Marshal.FreeHGlobal(memory);}
    }
    private T ReadConfig2<T>(uint kind,Func<IntPtr,uint,T> read)
    {
        QueryServiceConfig2W(service,kind,IntPtr.Zero,0,out var bytes);if(bytes is <4 or >65536)throw new IOException("UPDATE_SERVICE_CONFIG_BOUND");var memory=Marshal.AllocHGlobal((int)bytes);
        try{if(!QueryServiceConfig2W(service,kind,memory,bytes,out _))throw new Win32Exception();return read(memory,bytes);}finally{Marshal.FreeHGlobal(memory);}
    }
    private static string ReadString(IntPtr text,IntPtr memory,uint bytes)
    {
        if(text==IntPtr.Zero)return "";var start=text.ToInt64();var lower=memory.ToInt64();var end=lower+bytes;
        if(start<lower||start>=end||(start-lower)%2!=0)throw new IOException("UPDATE_SERVICE_CONFIG_POINTER");var result=new System.Text.StringBuilder();
        for(var offset=0;start+offset+2<=end&&offset<65536;offset+=2){var c=(char)Marshal.ReadInt16(text,offset);if(c=='\0')return result.ToString();result.Append(c);}throw new IOException("UPDATE_SERVICE_CONFIG_STRING");
    }
    private void Change<T>(uint kind,T value)where T:struct
    {var memory=Marshal.AllocHGlobal(Marshal.SizeOf<T>());try{Marshal.StructureToPtr(value,memory,false);if(!ChangeServiceConfig2W(service,kind,memory))throw new Win32Exception();}finally{Marshal.FreeHGlobal(memory);}}
    internal void EnableStartup()
    {
        RequireOwned();if(!ChangeServiceConfigW(service,0xffffffff,contract.StartType,0xffffffff,null,null,IntPtr.Zero,null,null,null,null))throw new Win32Exception();
        if(contract.Kind==FullServiceKind.Authority)Change(3,1);
        var nonCrash=contract.FailureActionsOnNonCrash?1u:0u;
        SetFailureActions(contract.FailureCommand);Change(4,nonCrash);
        if(ReadConfig2(4,(p,_)=>Marshal.ReadInt32(p))!=nonCrash)throw new IOException("UPDATE_SERVICE_FAILURE_FLAGS");
        RequireStartType(contract.StartType);
    }
    internal void FenceForRemoval()
    {
        RequireOwned();if(!ChangeServiceConfigW(service,0xffffffff,4,0xffffffff,null,null,IntPtr.Zero,null,null,null,null))throw new Win32Exception();
        SetFailureActions(null);Change(4,0);
        RequireStartType(4);if(ReadConfig2(4,(p,_)=>Marshal.ReadInt32(p))!=0)throw new IOException("UPDATE_SERVICE_FAILURE_FLAGS");
    }
    private void RequireStartType(uint expected)
    {
        QueryServiceConfigW(service,IntPtr.Zero,0,out var bytes);if(bytes<(uint)Marshal.SizeOf<Configuration>()||bytes>16384)throw new IOException("UPDATE_SERVICE_CONFIG_BOUND");var memory=Marshal.AllocHGlobal((int)bytes);
        try{if(!QueryServiceConfigW(service,memory,bytes,out _))throw new Win32Exception();if(Marshal.PtrToStructure<Configuration>(memory).Start!=expected)throw new IOException("UPDATE_SERVICE_START_TYPE");}finally{Marshal.FreeHGlobal(memory);}
    }
    internal static FailureAction[] FailurePlan(FullServiceKind kind,bool configured)
    {
        if(!Enum.IsDefined(kind))throw new IOException("UPDATE_SERVICE_KIND");
        return !configured?[]:kind is FullServiceKind.RecoveryA or FullServiceKind.RecoveryB?[new(){Kind=3,Delay=1000},new(){Kind=0,Delay=0}]:[new(){Kind=3,Delay=1000}];
    }
    internal static void RequireFailurePlan(FullServiceKind kind,bool configured,uint reset,IReadOnlyList<FailureAction> actual)
    {
        var expected=FailurePlan(kind,configured);
        if(reset!=(configured?86400u:0u)||actual.Count!=expected.Length||actual.Where((value,index)=>value.Kind!=expected[index].Kind||value.Delay!=expected[index].Delay).Any())throw new IOException("UPDATE_SERVICE_FAILURE_ACTIONS");
    }
    private void SetFailureActions(string? command)
    {
        // NULL means "leave unchanged" in SERVICE_FAILURE_ACTIONS. Use an
        // empty command and a non-NULL zero-count array to REMOVE old actions.
        // A handled A/B refusal is not a crash. Even an unreported native
        // crash gets one peer launch then NONE, rather than repeating the last
        // RUN_COMMAND forever. The existing24hour failure-count reset remains.
        var plan=FailurePlan(contract.Kind,command is not null);var stride=Marshal.SizeOf<FailureAction>();
        var text=Marshal.StringToHGlobalUni(command??"");var action=Marshal.AllocHGlobal(stride*Math.Max(1,plan.Length));
        try
        {
            if(plan.Length==0)Marshal.StructureToPtr(new FailureAction(),action,false);
            for(var i=0;i<plan.Length;i++)Marshal.StructureToPtr(plan[i],action+i*stride,false);
            Change(2,new FailureActions{Reset=command is null?0u:86400u,Command=text,Count=(uint)plan.Length,Actions=action});
            VerifyFailureActions(command);
        }
        finally{if(text!=IntPtr.Zero)Marshal.FreeHGlobal(text);if(action!=IntPtr.Zero)Marshal.FreeHGlobal(action);}
    }
    private void VerifyFailureActions(string? command)
    {
        var plan=FailurePlan(contract.Kind,command is not null);var stride=Marshal.SizeOf<FailureAction>();
        _=ReadConfig2(2,(p,n)=>
            {
                if(n<(uint)Marshal.SizeOf<FailureActions>())throw new IOException("UPDATE_SERVICE_FAILURE_ACTIONS");
                var observed=Marshal.PtrToStructure<FailureActions>(p);if(ReadString(observed.Command,p,n)!=(command??"")||observed.Count!=plan.Length)throw new IOException("UPDATE_SERVICE_FAILURE_ACTIONS");
                var actual=new List<FailureAction>();
                if(plan.Length>0)
                {
                    var offset=observed.Actions.ToInt64()-p.ToInt64();if(offset<0||offset>n-stride*plan.Length)throw new IOException("UPDATE_SERVICE_FAILURE_ACTIONS");
                    for(var i=0;i<plan.Length;i++)actual.Add(Marshal.PtrToStructure<FailureAction>(observed.Actions+i*stride));
                }
                RequireFailurePlan(contract.Kind,command is not null,observed.Reset,actual);
                return 0;
            });
    }
    internal async Task StartAsync(CancellationToken cancellation)
    {
        RequireOwned();if(!StartServiceW(service,0,IntPtr.Zero)&&Marshal.GetLastPInvokeError()!=1056)throw new Win32Exception();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromMinutes(6));
        while(true){deadline.Token.ThrowIfCancellationRequested();var state=Status();if(state.State==4&&state.Pid!=0)return;if(state.State==1)throw new IOException("UPDATE_SERVICE_START_FAILED");await Task.Delay(250,deadline.Token);}
    }
    internal static bool OneShotCompleted(FullServiceKind kind,bool started,uint state,uint pid,uint win32,uint specific)
    {
        if(kind is not(FullServiceKind.RecoveryA or FullServiceKind.RecoveryB)||!started)throw new IOException("UPDATE_SERVICE_START_FAILED");
        if(state==1){if(pid!=0||win32!=0||specific!=0)throw new IOException("UPDATE_SERVICE_START_FAILED");return true;}
        if(state is not(2 or 3 or 4)||state==4&&pid==0)throw new IOException("UPDATE_SERVICE_START_FAILED");
        return false;
    }
    internal async Task StartOneShotAsync(string originalOwner,string authoritySid,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(contract.Kind is not(FullServiceKind.RecoveryA or FullServiceKind.RecoveryB))throw new IOException("UPDATE_SERVICE_START_FAILED");
        RequireOwned();RequireProtection(originalOwner,authoritySid);
        // Already-running is a collision, not evidence that this request ran.
        cancellation.ThrowIfCancellationRequested();if(!StartServiceW(service,0,IntPtr.Zero))throw new Win32Exception();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromMinutes(6));
        Process? observed=null;
        try
        {
            while(true)
            {
                deadline.Token.ThrowIfCancellationRequested();RequireOwned();RequireProtection(originalOwner,authoritySid);var state=Status();
                if(OneShotCompleted(contract.Kind,true,state.State,state.Pid,state.Win32,state.Specific))
                {
                    if(observed is not null){await observed.WaitForExitAsync(deadline.Token);if(observed.ExitCode!=0)throw new IOException("UPDATE_SERVICE_START_FAILED");}
                    RequireOwned();RequireProtection(originalOwner,authoritySid);var final=Status();
                    if(!OneShotCompleted(contract.Kind,true,final.State,final.Pid,final.Win32,final.Specific))throw new IOException("UPDATE_SERVICE_GENERATION_CHANGED");
                    return;
                }
                // Windows documents a valid process ID for RUNNING, not for
                // START_PENDING or STOP_PENDING. Never pin an unrelated PID.
                if(state.State==4&&state.Pid!=0)
                {
                    if(observed is not null&&state.Pid!=observed.Id)throw new IOException("UPDATE_SERVICE_GENERATION_CHANGED");
                    if(observed is null)
                    {try{observed=Process.GetProcessById((int)state.Pid);_=observed.SafeHandle;_=observed.StartTime;}catch(Exception error)when(error is ArgumentException or InvalidOperationException){observed?.Dispose();observed=null;}}
                    else if(observed.HasExited)
                    {try{using var current=Process.GetProcessById((int)state.Pid);if(current.StartTime!=observed.StartTime)throw new IOException("UPDATE_SERVICE_GENERATION_CHANGED");}catch(Exception error)when(error is ArgumentException or InvalidOperationException){}}
                }
                await Task.Delay(250,deadline.Token);
            }
        }
        finally{observed?.Dispose();}
    }
    internal (int Pid,long Created) RunningIdentity()
    {
        RequireOwned();var before=Status();if(before.State!=4||before.Pid==0)throw new IOException("UPDATE_SERVICE_NOT_RUNNING");
        using var process=Process.GetProcessById((int)before.Pid);_=process.SafeHandle;var created=process.StartTime.ToFileTimeUtc();
        var after=Status();if(process.HasExited||after.Pid!=before.Pid||after.State!=4)throw new IOException("UPDATE_SERVICE_GENERATION_CHANGED");return((int)before.Pid,created);
    }
    internal async Task StopAsync(CancellationToken cancellation)
    {
        RequireOwned();using var process=await StopSettledAsync(cancellation);await WaitForExitAsync(process,cancellation);
        var stopped=Status();if(stopped.State!=1||stopped.Pid!=0||stopped.Specific!=0||stopped.Win32 is not(0 or 1077))throw new IOException("UPDATE_SERVICE_QUIESCENCE_UNCONFIRMED");
    }
    // The SCM refuses STOP (1052/1061) unless the current state accepts it, as
    // with a START_PENDING service that does not. STOP is sent only when
    // accepted; otherwise the service is waited on until it settles, then sent.
    internal static bool SendsStop(uint state,uint accepted,bool requested)=>!requested&&state is not(1 or 3)&&(accepted&1)!=0;
    // SCM pending protocol: a state is live while its checkpoint advances within
    // its own wait hint. A settled state gets the fixed stop bound.
    internal static bool Stalled(uint waitHint,TimeSpan unchanged)=>unchanged>TimeSpan.FromMilliseconds(Math.Max(waitHint,60000));
    private bool stopRequested;
    internal void RequestStop()
    {
        RequireOwned();var state=Status();if(!SendsStop(state.State,state.Accepted,stopRequested))return;
        if(ControlService(service,1,out _)){stopRequested=true;return;}
        // The state moved between query and control. The settle loop re-reads it.
        var error=Marshal.GetLastPInvokeError();if(error is not(1052 or 1061 or 1062))throw new Win32Exception(error);
    }
    private async Task<Process?> StopSettledAsync(CancellationToken cancellation)
    {
        Process? process=null;
        try
        {
            var observed=Status();var unchanged=Stopwatch.StartNew();
            while(true)
            {
                var state=Status();
                if(state.State==1){stopRequested=false;return process;}
                // Windows documents a valid process ID only for RUNNING.
                if(state.State==4&&state.Pid!=0)
                {
                    if(process is null)
                    {
                        process=Process.GetProcessById((int)state.Pid);_=process.SafeHandle;
                        var again=Status();if(again.State!=4||again.Pid!=state.Pid)throw new IOException("UPDATE_SERVICE_GENERATION_CHANGED");
                    }
                    else if(process.Id!=state.Pid)throw new IOException("UPDATE_SERVICE_GENERATION_CHANGED");
                }
                RequestStop();
                if(state.State!=observed.State||state.Checkpoint!=observed.Checkpoint){observed=state;unchanged.Restart();}
                else if(Stalled(state.WaitHint,unchanged.Elapsed))throw new IOException("UPDATE_SERVICE_STOP_STALLED");
                await Task.Delay(100,cancellation);
            }
        }
        catch{process?.Dispose();throw;}
    }
    private static async Task WaitForExitAsync(Process? process,CancellationToken cancellation)
    {
        if(process is null)return;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(deadline.Token);
    }
    internal static void RequireRemovalStopped(uint state,uint pid)
    {if(state!=1||pid!=0)throw new IOException("UPDATE_SERVICE_QUIESCENCE_UNCONFIRMED");}
    internal void RequireFencedStoppedForRemoval()
    {RequireRemovalFence();var observed=Status();RequireRemovalStopped(observed.State,observed.Pid);}
    private void RequireRemovalFence()
    {
        RequireOwned();RequireStartType(4);
        if(ReadConfig2(4,(p,_)=>Marshal.ReadInt32(p))!=0)throw new IOException("UPDATE_SERVICE_FAILURE_FLAGS");
        VerifyFailureActions(null);
    }
    // Observation only, NOT a writer-quiescence grant. The full uninstaller
    // retains serial+data leases and rechecks durable custody before deletion.
    internal async Task StopForRemovalAsync(CancellationToken cancellation)
    {
        RequireRemovalFence();using var process=await StopSettledAsync(cancellation);await WaitForExitAsync(process,cancellation);
        RequireFencedStoppedForRemoval();
    }
    internal void Delete()
    {RequireOwned();var state=Status();if(state.State!=1||state.Pid!=0)throw new IOException("UPDATE_SERVICE_QUIESCENCE_UNCONFIRMED");if(!DeleteService(service))throw new Win32Exception();}
    private StatusProcess Status(){if(!QueryServiceStatusEx(service,0,out var value,(uint)Marshal.SizeOf<StatusProcess>(),out _))throw new Win32Exception();return value;}
    public void Dispose(){service.Dispose();manager.Dispose();}
    [StructLayout(LayoutKind.Sequential)]private struct Privileges{public IntPtr Names;}
    [StructLayout(LayoutKind.Sequential)]private struct Configuration{public uint Type,Start,Error;public IntPtr Image,Group;public uint Tag;public IntPtr Dependencies,Account,Display;}
    [StructLayout(LayoutKind.Sequential)]private struct StatusProcess{public uint Type,State,Accepted,Win32,Specific,Checkpoint,WaitHint,Pid,Flags;}
    [StructLayout(LayoutKind.Sequential)]private struct StatusBasic{public uint Type,State,Accepted,Win32,Specific,Checkpoint,WaitHint;}
    [StructLayout(LayoutKind.Sequential)]private struct FailureActions{public uint Reset;public IntPtr RebootMessage,Command;public uint Count;public IntPtr Actions;}
    [StructLayout(LayoutKind.Sequential)]internal struct FailureAction{public uint Kind,Delay;}
    private sealed class Handle:SafeHandleZeroOrMinusOneIsInvalid{internal Handle(IntPtr value):base(true){SetHandle(value);}protected override bool ReleaseHandle()=>CloseServiceHandle(handle);}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenSCManagerW(string? machine,string? database,uint rights);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenServiceW(Handle manager,string name,uint rights);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateServiceW(Handle manager,string name,string display,uint rights,uint type,uint start,uint error,string image,string? group,IntPtr tag,string? dependencies,string account,string? password);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryServiceConfigW(Handle service,IntPtr memory,uint bytes,out uint needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryServiceConfig2W(Handle service,uint kind,IntPtr memory,uint bytes,out uint needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool ChangeServiceConfigW(Handle service,uint type,uint start,uint error,string? image,string? group,IntPtr tag,string? dependencies,string? account,string? password,string? display);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool ChangeServiceConfig2W(Handle service,uint kind,IntPtr value);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool SetServiceObjectSecurity(Handle service,uint information,byte[] descriptor);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool QueryServiceObjectSecurity(Handle service,uint information,IntPtr descriptor,uint bytes,out uint needed);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool QueryServiceStatusEx(Handle service,uint level,out StatusProcess status,uint bytes,out uint needed);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool StartServiceW(Handle service,uint argc,IntPtr argv);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool ControlService(Handle service,uint control,out StatusBasic status);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool DeleteService(Handle service);
    [DllImport("advapi32.dll")]private static extern bool CloseServiceHandle(IntPtr service);
}

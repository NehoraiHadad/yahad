using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace Yahad.Install;

internal enum InstalledOwnObject {Process,Token}
internal sealed record InstalledOwnProcessIdentity(int CurrentPid,int ObjectPid,long Created,long TokenId,string UserSid,
    string RuntimeSid,string AuthoritySid,string? LogonSid,bool ServiceLogon,bool Administrator,bool AppContainer,bool ThreadToken,string[] Privileges);
internal interface IInstalledOwnObjectSession:IDisposable
{
    InstalledOwnProcessIdentity ObserveIdentity();
    RawSecurityDescriptor Read(InstalledOwnObject kind);
    void WriteDacl(InstalledOwnObject kind,RawSecurityDescriptor descriptor);
}
internal interface IInstalledProcessObjectKernel
{IInstalledOwnObjectSession OpenCurrent(bool publishToken);}

// Publish access on this process and its CURRENT primary-token object only.
// Roles come from the already validated immutable installed anchor. No API
// accepts another PID, handle, account, access mask or arbitrary service name.
internal static class InstalledProcessObjectAccess
{
    internal const int RuntimeControlProcess=0x00101101,PeerReadProcess=0x00101000,PeerReadToken=8,AuthorityCustodyProcess=0x00101141;
    private const int OtherProcessRead=0x00121400,OtherTokenRead=0x00020008;
    private enum Role {Runtime,Authority,Client,AuthorityCustody}
    private static readonly object serial=new();
    internal static void PublishRuntimePeerAccess(string actualRuntimeSid,string authoritySid,bool full=false)
        =>Publish(Role.Runtime,actualRuntimeSid,authoritySid,null,new WindowsInstalledProcessObjectKernel(),full);
    internal static void PublishAuthorityPeerAccess(string actualAuthoritySid,string runtimeSid,string originalOwnerSid)
        =>PublishAuthorityPeerAccess(actualAuthoritySid,runtimeSid,originalOwnerSid,new WindowsInstalledProcessObjectKernel());
    internal static void PublishClientPeerAccess(string actualCurrentSid,string authoritySid)
        =>PublishClientPeerAccess(actualCurrentSid,authoritySid,new WindowsInstalledProcessObjectKernel());
    // Only the immutable full profile enables the two fixed recovery actors.
    // Publish exact handle-transfer/job rights on this own process/token;
    // never grant these to Runtime, Owner or a caller-selected service.
    internal static void PublishAuthorityCustodyAccess(InstalledFileRoles roles)
    {
        using var current=WindowsIdentity.GetCurrent();var actor=current.User??throw new IOException("OWN_OBJECT_CURRENT_IDENTITY");
        if(!roles.IsUpdater(actor))throw new IOException("OWN_OBJECT_FIXED_ROLE");
        UpdaterServiceIdentity.Require(UpdaterServiceIdentity.FromUser(actor,roles.Authority),Environment.ProcessId);
        Publish(Role.AuthorityCustody,actor.Value,roles.Authority.Value,null,new WindowsInstalledProcessObjectKernel(),roles.HasRecoveryHelpers);
    }
    internal static void PublishAuthorityCustodyAccess(string actualAuthoritySid)
        =>PublishAuthorityCustodyAccess(actualAuthoritySid,new WindowsInstalledProcessObjectKernel());
    internal static void PublishRuntimePeerAccess(string actualRuntimeSid,string authoritySid,IInstalledProcessObjectKernel kernel)
        =>Publish(Role.Runtime,actualRuntimeSid,authoritySid,null,kernel);
    internal static void PublishAuthorityPeerAccess(string actualAuthoritySid,string runtimeSid,string originalOwnerSid,IInstalledProcessObjectKernel kernel)
        =>Publish(Role.Authority,actualAuthoritySid,runtimeSid,originalOwnerSid,kernel);
    internal static void PublishClientPeerAccess(string actualCurrentSid,string authoritySid,IInstalledProcessObjectKernel kernel)
        =>Publish(Role.Client,actualCurrentSid,authoritySid,null,kernel);
    internal static void PublishAuthorityCustodyAccess(string actualAuthoritySid,IInstalledProcessObjectKernel kernel)
        =>Publish(Role.AuthorityCustody,actualAuthoritySid,actualAuthoritySid,null,kernel);
    internal static void PublishFullCustodyAccess(string actor,string authority,IInstalledProcessObjectKernel kernel)
        =>Publish(Role.AuthorityCustody,actor,authority,null,kernel,true);
    internal static void PublishFullRuntimeAccess(string actor,string authority,IInstalledProcessObjectKernel kernel)
        =>Publish(Role.Runtime,actor,authority,null,kernel,true);
    private static void Publish(Role role,string actor,string peer,string? owner,IInstalledProcessObjectKernel kernel,bool full=false)
    {
        Sid(actor);Sid(peer);if(owner is not null){var sid=Sid(owner);if(!owner.StartsWith("S-1-5-21-",StringComparison.Ordinal)||!sid.IsAccountSid())throw new IOException("OWN_OBJECT_OWNER_ACCOUNT");}
        lock(serial)
        {
            using var session=kernel.OpenCurrent(role!=Role.Client);var identity=session.ObserveIdentity();RequireIdentity(identity,role,actor,peer,full);
            var mask=role switch{Role.Runtime=>RuntimeControlProcess,Role.AuthorityCustody=>AuthorityCustodyProcess,_=>PeerReadProcess};
            var peers=new Dictionary<string,int>(StringComparer.Ordinal){{peer,mask}};
            if(full&&role is Role.Runtime or Role.AuthorityCustody)
                foreach(var sid in UpdaterServicePrincipals.All(Sid(peer)))peers[sid.Value]=mask;
            if(full&&role==Role.AuthorityCustody)peers[identity.RuntimeSid]=PeerReadProcess;
            if(owner is not null)peers.Add(owner,PeerReadProcess);
            var tokenPeers=role==Role.Client?new Dictionary<string,int>(StringComparer.Ordinal):peers.ToDictionary(x=>x.Key,_=>PeerReadToken,StringComparer.Ordinal);
            var processBefore=session.Read(InstalledOwnObject.Process);var tokenBefore=session.Read(InstalledOwnObject.Token);
            var processAfter=AddPeerAces(processBefore,identity,peers,InstalledOwnObject.Process);
            var tokenAfter=AddPeerAces(tokenBefore,identity,tokenPeers,InstalledOwnObject.Token);
            RequireSame(identity,session.ObserveIdentity(),role,actor,peer,full);
            Apply(InstalledOwnObject.Process,processBefore,processAfter);
            if(role!=Role.Client)Apply(InstalledOwnObject.Token,tokenBefore,tokenAfter);
            RequireSame(identity,session.ObserveIdentity(),role,actor,peer,full);
            void Apply(InstalledOwnObject kind,RawSecurityDescriptor before,RawSecurityDescriptor after)
            {
                // Refuse concurrent changes rather than restoring an obsolete
                // snapshot over another legitimate owner action.
                if(!SameSecurity(before,session.Read(kind)))throw new IOException("OWN_OBJECT_SECURITY_CHANGED");
                RequireSame(identity,session.ObserveIdentity(),role,actor,peer,full);
                if(!SameDacl(before,after))session.WriteDacl(kind,after);
                if(!SameSecurity(after,session.Read(kind)))throw new IOException("OWN_OBJECT_PUBLICATION_UNCONFIRMED");
            }
        }
    }
    private static SecurityIdentifier Sid(string value)
    {
        try{var sid=new SecurityIdentifier(value);if(sid.Value!=value)throw new IOException("OWN_OBJECT_SID");return sid;}
        catch(ArgumentException error){throw new IOException("OWN_OBJECT_SID",error);}
    }
    private static void RequireIdentity(InstalledOwnProcessIdentity value,Role role,string actor,string peer,bool full)
    {
        Sid(value.RuntimeSid);Sid(value.AuthoritySid);
        if(value.CurrentPid<1||value.ObjectPid!=value.CurrentPid||value.Created<=0||value.TokenId==0||value.UserSid!=actor||value.ThreadToken||value.AppContainer||
            !value.RuntimeSid.StartsWith("S-1-5-80-",StringComparison.Ordinal)||!value.AuthoritySid.StartsWith("S-1-5-80-",StringComparison.Ordinal)||value.RuntimeSid==value.AuthoritySid)
            throw new IOException("OWN_OBJECT_CURRENT_IDENTITY");
        if(value.LogonSid is { } logon&&(!System.Text.RegularExpressions.Regex.IsMatch(logon,"^S-1-5-5-[0-9]+-[0-9]+$")||Sid(logon).Value!=logon))throw new IOException("OWN_OBJECT_LOGON_IDENTITY");
        if(role==Role.Runtime&&(actor!=value.RuntimeSid||peer!=value.AuthoritySid)||role==Role.Authority&&(actor!=value.AuthoritySid||peer!=value.RuntimeSid)||role==Role.Client&&peer!=value.AuthoritySid||
            role==Role.AuthorityCustody&&(peer!=value.AuthoritySid||actor!=value.AuthoritySid&&!(full&&UpdaterServicePrincipals.IsRecovery(Sid(actor)))))
            throw new IOException("OWN_OBJECT_FIXED_ROLE");
        if(role!=Role.Client&&(!value.ServiceLogon||value.Administrator||value.Privileges is null||value.Privileges.Length!=1||value.Privileges[0]!="SeChangeNotifyPrivilege"))
            throw new IOException("OWN_OBJECT_MINIMAL_SERVICE_TOKEN");
    }
    private static void RequireSame(InstalledOwnProcessIdentity before,InstalledOwnProcessIdentity after,Role role,string actor,string peer,bool full)
    {
        RequireIdentity(after,role,actor,peer,full);
        if(before.CurrentPid!=after.CurrentPid||before.ObjectPid!=after.ObjectPid||before.Created!=after.Created||before.TokenId!=after.TokenId||before.UserSid!=after.UserSid||
            before.LogonSid!=after.LogonSid||before.RuntimeSid!=after.RuntimeSid||before.AuthoritySid!=after.AuthoritySid)throw new IOException("OWN_OBJECT_GENERATION_CHANGED");
    }
    private static RawSecurityDescriptor AddPeerAces(RawSecurityDescriptor source,InstalledOwnProcessIdentity identity,IReadOnlyDictionary<string,int> peers,InstalledOwnObject kind)
    {
        if(source.Owner is null||source.DiscretionaryAcl is null||(source.ControlFlags&ControlFlags.DiscretionaryAclPresent)==0)throw new IOException("OWN_OBJECT_NULL_DACL");
        bool Trusted(string sid)=>sid==identity.UserSid||sid==identity.LogonSid||sid is "S-1-5-18" or "S-1-5-32-544";
        if(!Trusted(source.Owner.Value))throw new IOException("OWN_OBJECT_SECURITY_OWNER");
        var required=peers.Values.Aggregate(0,(a,b)=>a|b);var read=kind==InstalledOwnObject.Process?OtherProcessRead:OtherTokenRead;
        foreach(GenericAce raw in source.DiscretionaryAcl)
        {
            if(raw is not CommonAce ace||ace.IsCallback||ace.AceQualifier is not(AceQualifier.AccessAllowed or AceQualifier.AccessDenied))throw new IOException("OWN_OBJECT_UNSUPPORTED_ACE");
            if(ace.AceQualifier==AceQualifier.AccessDenied)
            {
                if((ace.AceFlags&AceFlags.InheritOnly)==0&&(ace.AccessMask&required)!=0)throw new IOException("OWN_OBJECT_REQUIRED_ACCESS_DENIED");continue;
            }
            var sid=ace.SecurityIdentifier.Value;if(sid=="S-1-1-0"&&ace.AccessMask!=0)throw new IOException("OWN_OBJECT_WORLD_DACL");
            if(Trusted(sid))continue;
            var allowed=read|(peers.TryGetValue(sid,out var mask)?mask:0);
            if((ace.AccessMask&~allowed)!=0)throw new IOException("OWN_OBJECT_UNRELATED_WRITE_ACCESS");
        }
        var copy=Copy(source);var acl=copy.DiscretionaryAcl!;
        foreach(var peer in peers)
        {
            var granted=0;foreach(GenericAce raw in acl)if(raw is CommonAce ace&&ace.AceQualifier==AceQualifier.AccessAllowed&&ace.SecurityIdentifier.Value==peer.Key&&(ace.AceFlags&AceFlags.InheritOnly)==0)granted|=ace.AccessMask;
            if((granted&peer.Value)==peer.Value)continue;
            acl.InsertAce(acl.Count,new CommonAce(AceFlags.None,AceQualifier.AccessAllowed,peer.Value,Sid(peer.Key),false,null));
        }
        return copy;
    }
    private static RawSecurityDescriptor Copy(RawSecurityDescriptor source)
    {var bytes=new byte[source.BinaryLength];source.GetBinaryForm(bytes,0);return new(bytes,0);}
    private static bool SameDacl(RawSecurityDescriptor first,RawSecurityDescriptor second)
    {
        if(first.DiscretionaryAcl is null||second.DiscretionaryAcl is null)return first.DiscretionaryAcl==second.DiscretionaryAcl;
        var a=new byte[first.DiscretionaryAcl.BinaryLength];var b=new byte[second.DiscretionaryAcl.BinaryLength];first.DiscretionaryAcl.GetBinaryForm(a,0);second.DiscretionaryAcl.GetBinaryForm(b,0);return a.AsSpan().SequenceEqual(b);
    }
    private static bool SameSecurity(RawSecurityDescriptor first,RawSecurityDescriptor second)
    {
        const ControlFlags inheritance=ControlFlags.DiscretionaryAclProtected|ControlFlags.DiscretionaryAclAutoInherited|ControlFlags.DiscretionaryAclAutoInheritRequired|ControlFlags.DiscretionaryAclPresent;
        return first.Owner==second.Owner&&first.Group==second.Group&&(first.ControlFlags&inheritance)==(second.ControlFlags&inheritance)&&SameDacl(first,second);
    }
}

internal sealed class WindowsInstalledProcessObjectKernel:IInstalledProcessObjectKernel
{
    public IInstalledOwnObjectSession OpenCurrent(bool publishToken)=>new Session(publishToken);
    private sealed class Session:IInstalledOwnObjectSession
    {
        private readonly SafeProcessHandle process;private readonly SafeAccessTokenHandle token;private readonly bool publishToken;
        private readonly uint pid;private readonly long created,tokenId;private bool disposed;
        internal Session(bool publishToken)
        {
            this.publishToken=publishToken;RequireNoThreadToken();pid=GetCurrentProcessId();
            process=OpenProcess(0x00161000,false,pid);if(process.IsInvalid){process.Dispose();throw new IOException("OWN_OBJECT_OPEN_PROCESS",new Win32Exception());}
            try
            {
                if(GetProcessId(process)!=pid||!GetProcessTimes(process,out created,out _,out _,out _))throw new IOException("OWN_OBJECT_PROCESS_BINDING",new Win32Exception());
                if(!OpenProcessToken(process,publishToken?0x60008u:0x20008u,out token))throw new IOException("OWN_OBJECT_OPEN_TOKEN",new Win32Exception());
                try{tokenId=TokenId(token);}catch{token.Dispose();throw;}
            }
            catch{process.Dispose();throw;}
        }
        public InstalledOwnProcessIdentity ObserveIdentity()
        {
            ObjectDisposedException.ThrowIf(disposed,this);RequireNoThreadToken();
            if(GetCurrentProcessId()!=pid||GetProcessId(process)!=pid||!GetProcessTimes(process,out var actual,out _,out _,out _)||actual!=created)throw new IOException("OWN_OBJECT_PROCESS_CHANGED");
            if(!OpenProcessToken(process,8,out var current))throw new IOException("OWN_OBJECT_CURRENT_TOKEN",new Win32Exception());
            using(current)
            {
                if(TokenId(current)!=tokenId)throw new IOException("OWN_OBJECT_TOKEN_CHANGED");
                using var identity=new WindowsIdentity(current.DangerousGetHandle());var groups=Groups(current);var app=Information(current,29);
                bool container;try{container=Marshal.ReadInt32(app)!=0;}finally{Marshal.FreeHGlobal(app);}
                var runtime=(SecurityIdentifier)new NTAccount("NT SERVICE",InstalledUpdaterLayout.RuntimeServiceName).Translate(typeof(SecurityIdentifier));
                var authority=(SecurityIdentifier)new NTAccount("NT SERVICE",InstalledUpdaterLayout.AuthorityServiceName).Translate(typeof(SecurityIdentifier));
                return new(checked((int)pid),checked((int)GetProcessId(process)),created,tokenId,identity.User?.Value??"",runtime.Value,authority.Value,groups.Logon,
                    groups.Service,groups.Administrator,container,false,Privileges(current));
            }
        }
        public RawSecurityDescriptor Read(InstalledOwnObject kind)
        {
            var handle=Handle(kind);_ =GetKernelObjectSecurity(handle,7,null,0,out var count);
            if(count is <20 or >65536)throw new IOException("OWN_OBJECT_SECURITY_BOUND");var bytes=new byte[count];
            if(!GetKernelObjectSecurity(handle,7,bytes,count,out _))throw new IOException("OWN_OBJECT_READ_SECURITY",new Win32Exception());return new(bytes,0);
        }
        public void WriteDacl(InstalledOwnObject kind,RawSecurityDescriptor descriptor)
        {
            if(kind==InstalledOwnObject.Token&&!publishToken)throw new IOException("OWN_OBJECT_CLIENT_TOKEN_WRITE_FORBIDDEN");
            var bytes=new byte[descriptor.BinaryLength];descriptor.GetBinaryForm(bytes,0);
            // DACL_SECURITY_INFORMATION only: leave owner/group/SACL/labels and
            // existing noncontainer protection/inheritance policy untouched.
            if(!SetKernelObjectSecurity(Handle(kind),4,bytes))throw new IOException("OWN_OBJECT_WRITE_SECURITY",new Win32Exception());
        }
        private SafeHandle Handle(InstalledOwnObject kind)
        {ObjectDisposedException.ThrowIf(disposed,this);return kind switch{InstalledOwnObject.Process=>process,InstalledOwnObject.Token=>token,_=>throw new IOException("OWN_OBJECT_KIND")};}
        public void Dispose(){if(disposed)return;disposed=true;token.Dispose();process.Dispose();}
    }
    private static void RequireNoThreadToken()
    {
        if(OpenThreadToken(GetCurrentThread(),8,true,out var token)){token.Dispose();throw new IOException("OWN_OBJECT_THREAD_IDENTITY");}
        var error=Marshal.GetLastWin32Error();token?.Dispose();if(error!=1008)throw new IOException("OWN_OBJECT_THREAD_QUERY",new Win32Exception(error));
    }
    private static long TokenId(SafeAccessTokenHandle token)
    {var memory=Information(token,10,out var bytes);try{if(bytes<8)throw new IOException("OWN_OBJECT_TOKEN_STATISTICS_BOUND");return Marshal.ReadInt64(memory);}finally{Marshal.FreeHGlobal(memory);}}
    // IsInRole duplicates primary tokens internally. This observation handle is
    // deliberately TOKEN_QUERY only; inspect its existing enabled group instead
    // of requesting duplicate/impersonation rights just to reject administrators.
    internal static bool EnabledAdministrator(string sid,uint attributes)
        =>sid=="S-1-5-32-544"&&(attributes&0x14)==4;
    private static (string? Logon,bool Service,bool Administrator) Groups(SafeAccessTokenHandle token)
    {
        var memory=Information(token,2,out var bytes);
        try
        {
            var count=Marshal.ReadInt32(memory);var offset=IntPtr.Size==8?8:4;var stride=Marshal.SizeOf<SidAttributes>();
            if(count is <0 or >2048||offset+(long)count*stride>bytes)throw new IOException("OWN_OBJECT_GROUP_BOUND");string? logon=null;var service=false;var administrator=false;
            for(var i=0;i<count;i++)
            {
                var value=Marshal.PtrToStructure<SidAttributes>(memory+offset+i*stride);var sid=new SecurityIdentifier(value.Sid).Value;
                if((value.Attributes&0xc0000000)==0xc0000000){if(logon is not null)throw new IOException("OWN_OBJECT_LOGON_COUNT");logon=sid;}
                if(sid=="S-1-5-6"&&(value.Attributes&4)!=0)service=true;
                if(EnabledAdministrator(sid,value.Attributes))administrator=true;
            }
            return(logon,service,administrator);
        }
        finally{Marshal.FreeHGlobal(memory);}
    }
    private static string[] Privileges(SafeAccessTokenHandle token)
    {
        var memory=Information(token,3,out var bytes);
        try
        {
            var count=Marshal.ReadInt32(memory);var stride=Marshal.SizeOf<LuidAttributes>();if(count is <0 or >128||4L+count*stride>bytes)throw new IOException("OWN_OBJECT_PRIVILEGE_BOUND");var result=new string[count];
            for(var i=0;i<count;i++){var value=Marshal.PtrToStructure<LuidAttributes>(memory+4+i*stride);var name=new StringBuilder(256);var length=256u;
                if(!LookupPrivilegeNameW(null,ref value.Luid,name,ref length))throw new IOException("OWN_OBJECT_PRIVILEGE_QUERY",new Win32Exception());result[i]=name.ToString();}
            return result;
        }
        finally{Marshal.FreeHGlobal(memory);}
    }
    private static IntPtr Information(SafeAccessTokenHandle token,int kind)
        =>Information(token,kind,out _);
    private static IntPtr Information(SafeAccessTokenHandle token,int kind,out uint size)
    {
        _ =GetTokenInformation(token,kind,IntPtr.Zero,0,out size);if(size is <4 or >65536)throw new IOException("OWN_OBJECT_TOKEN_BOUND");var buffer=Marshal.AllocHGlobal((int)size);
        try{if(!GetTokenInformation(token,kind,buffer,size,out _))throw new IOException("OWN_OBJECT_TOKEN_QUERY",new Win32Exception());return buffer;}catch{Marshal.FreeHGlobal(buffer);throw;}
    }
    [StructLayout(LayoutKind.Sequential)]private struct SidAttributes{public IntPtr Sid;public uint Attributes;}
    [StructLayout(LayoutKind.Sequential)]private struct Luid{public uint Low;public int High;}
    [StructLayout(LayoutKind.Sequential)]private struct LuidAttributes{public Luid Luid;public uint Attributes;}
    [DllImport("kernel32.dll")]private static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exit,out long kernel,out long user);
    [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool OpenProcessToken(SafeProcessHandle process,uint rights,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool OpenThreadToken(IntPtr thread,uint access,[MarshalAs(UnmanagedType.Bool)]bool asSelf,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetTokenInformation(SafeAccessTokenHandle token,int kind,IntPtr memory,uint size,out uint returned);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool LookupPrivilegeNameW(string? system,ref Luid luid,StringBuilder name,ref uint length);
    [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetKernelObjectSecurity(SafeHandle handle,uint information,[Out]byte[]? descriptor,uint length,out uint needed);
    [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetKernelObjectSecurity(SafeHandle handle,uint information,byte[] descriptor);
}

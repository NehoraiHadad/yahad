using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
namespace Yahad.Service;

internal sealed record MigrationProfileRuntime(ServiceTokenObservation Token,string ProcessSid,string ExpectedRuntimeSid,bool ThreadTokenPresent);
internal sealed record MigrationProfileCreation(int HResult,string? Sid);
internal sealed record MigrationProfileUserScope(string UserSid,string ProfileRoot,string ContainerRoot,bool ProfileExists,bool ContainerExists,bool ReparseFree);
internal interface IInstalledMigrationProfileKernel
{
    MigrationProfileRuntime ObserveRuntime();
    string DeriveSid(string fixedName);
    bool LoopbackExempt(string sid);
    MigrationProfileCreation Create(string fixedName,IReadOnlyList<string> capabilities);
    MigrationProfileUserScope ObserveCurrentUserScope(string sid);
}

// Installed composition calls this only AFTER reading and authenticating the
// immutable full-policy pin. The digest is never an Owner/app IPC permission.
// Program remains disabled; no native profile API is called by the fake seam.
internal static class InstalledMigrationProfile
{
    internal const int AlreadyExists=unchecked((int)0x800700b7),AccessDenied=unchecked((int)0x80070005);
    internal static string EnsureForCurrentRuntime(string installationId,string immutableFullPolicyDigest)
        =>EnsureForCurrentRuntime(installationId,immutableFullPolicyDigest,new WindowsInstalledMigrationProfileKernel());
    // Internal test seam: no CLI, JSON, environment or service arguments select it.
    internal static string EnsureForCurrentRuntime(string installationId,string immutableFullPolicyDigest,IInstalledMigrationProfileKernel kernel)
    {
        string name;
        try{name=NativeMigrationSandbox.ProfileName(installationId);}catch(Exception error)when(error is IOException or ArgumentException){throw new IOException("MIGRATION_PROFILE_INSTALLATION",error);}
        if(immutableFullPolicyDigest is null||!System.Text.RegularExpressions.Regex.IsMatch(immutableFullPolicyDigest,"^[a-f0-9]{64}$"))throw new IOException("MIGRATION_PROFILE_FULL_PIN");
        var runtime=RequireRuntime(kernel.ObserveRuntime());var sid=kernel.DeriveSid(name);RequireSid(sid);
        if(kernel.LoopbackExempt(sid))throw new IOException("MIGRATION_PROFILE_LOOPBACK_EXEMPT");
        RequireSameRuntime(runtime,kernel.ObserveRuntime());
        // CreateAppContainerProfile is per CURRENT user. No impersonation,
        // alternate token, LocalSystem fallback, profile loading or ACL repair.
        var created=kernel.Create(name,Array.Empty<string>());
        if(created.HResult is not(0 or AlreadyExists))
            throw new IOException(created.HResult==AccessDenied?"MIGRATION_PROFILE_CREATE_DENIED":"MIGRATION_PROFILE_CREATE_FAILED",Marshal.GetExceptionForHR(created.HResult));
        if(created.HResult==0&&created.Sid is null||created.Sid is not null&&created.Sid!=sid)throw new IOException("MIGRATION_PROFILE_SID_BINDING");
        // Derivation alone does NOT establish that a profile exists. Even an
        // ERROR_ALREADY_EXISTS result requires current-user folder evidence.
        var profile=kernel.ObserveCurrentUserScope(sid);RequireUserScope(profile,runtime.ExpectedRuntimeSid);
        RequireSameRuntime(runtime,kernel.ObserveRuntime());
        if(kernel.DeriveSid(name)!=sid)throw new IOException("MIGRATION_PROFILE_SID_CHANGED");
        if(kernel.LoopbackExempt(sid))throw new IOException("MIGRATION_PROFILE_LOOPBACK_EXEMPT");
        return sid;
    }
    private static MigrationProfileRuntime RequireRuntime(MigrationProfileRuntime value)
    {
        if(value.ThreadTokenPresent)throw new IOException("MIGRATION_PROFILE_THREAD_IDENTITY");
        try{ServiceIdentityPolicy.Require(value.Token,value.ExpectedRuntimeSid);}
        catch(ServiceStartDenied error){throw new IOException("MIGRATION_PROFILE_RUNTIME_IDENTITY",error);}
        if(value.ProcessSid!=value.ExpectedRuntimeSid)throw new IOException("MIGRATION_PROFILE_RUNTIME_IDENTITY");return value;
    }
    private static void RequireSameRuntime(MigrationProfileRuntime expected,MigrationProfileRuntime observed)
    {
        RequireRuntime(observed);if(observed.ExpectedRuntimeSid!=expected.ExpectedRuntimeSid||observed.ProcessSid!=expected.ProcessSid)
            throw new IOException("MIGRATION_PROFILE_RUNTIME_CHANGED");
    }
    private static void RequireSid(string sid)
    {
        if(sid is null||!sid.StartsWith("S-1-15-2-",StringComparison.Ordinal)||sid=="S-1-15-2-1")throw new IOException("MIGRATION_PROFILE_SID");
        try{_ =new SecurityIdentifier(sid);}catch(ArgumentException error){throw new IOException("MIGRATION_PROFILE_SID",error);}
    }
    private static void RequireUserScope(MigrationProfileUserScope value,string runtime)
    {
        if(value.UserSid!=runtime||!value.ProfileExists||!value.ContainerExists||!value.ReparseFree)throw new IOException("MIGRATION_PROFILE_USER_SCOPE");
        try
        {
            var profile=Canonical(value.ProfileRoot);var container=Canonical(value.ContainerRoot);var portable=profile.Replace('\\','/');
            if(!VerifiedMigrationSpec.Within(container,profile)||profile==Path.GetPathRoot(profile)||
                portable.EndsWith("/System32/config/systemprofile",StringComparison.OrdinalIgnoreCase)||portable.EndsWith("/SysWOW64/config/systemprofile",StringComparison.OrdinalIgnoreCase)||
                portable.EndsWith("/ServiceProfiles/LocalService",StringComparison.OrdinalIgnoreCase)||portable.EndsWith("/ServiceProfiles/NetworkService",StringComparison.OrdinalIgnoreCase)||
                portable.EndsWith("/Users/Default",StringComparison.OrdinalIgnoreCase)||portable.EndsWith("/Users/Public",StringComparison.OrdinalIgnoreCase))throw new IOException("MIGRATION_PROFILE_USER_SCOPE");
        }
        catch(Exception error)when(error is IOException or ArgumentException or NotSupportedException){throw new IOException("MIGRATION_PROFILE_USER_SCOPE",error);}
    }
    private static string Canonical(string path)
    {
        if(path is null||path.Length is <3 or >32767||!Path.IsPathFullyQualified(path))throw new IOException("MIGRATION_PROFILE_PATH");
        return Path.TrimEndingDirectorySeparator(WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(path)));
    }
}

internal sealed class WindowsInstalledMigrationProfileKernel:IInstalledMigrationProfileKernel
{
    public MigrationProfileRuntime ObserveRuntime()
    {
        var expected=(SecurityIdentifier)new NTAccount(ServiceDefinition.Account).Translate(typeof(SecurityIdentifier));
        if(OpenThreadToken(GetCurrentThread(),8,true,out var thread))
        {thread.Dispose();throw new IOException("MIGRATION_PROFILE_THREAD_IDENTITY");}
        var error=Marshal.GetLastWin32Error();thread?.Dispose();if(error!=1008)throw new IOException("MIGRATION_PROFILE_THREAD_QUERY",new Win32Exception(error));
        using var token=ProcessToken();using var identity=new WindowsIdentity(token.DangerousGetHandle());
        return new(new WindowsServiceTokenReader().Read(),identity.User?.Value??"",expected.Value,false);
    }
    public string DeriveSid(string fixedName)
    {
        var result=DeriveAppContainerSidFromAppContainerName(fixedName,out var sid);
        try{if(result!=0||sid==IntPtr.Zero)throw new IOException("MIGRATION_PROFILE_DERIVE",Marshal.GetExceptionForHR(result));return new SecurityIdentifier(sid).Value;}
        finally{if(sid!=IntPtr.Zero)_=FreeSid(sid);}
    }
    public MigrationProfileCreation Create(string fixedName,IReadOnlyList<string> capabilities)
    {
        if(capabilities.Count!=0)throw new IOException("MIGRATION_PROFILE_CAPABILITIES");
        var result=CreateAppContainerProfile(fixedName,"Yahad offline data migration","Restricted offline migration for the installed Yahad runtime",IntPtr.Zero,0,out var sid);
        try{return new(result,sid==IntPtr.Zero||result is not(0 or InstalledMigrationProfile.AlreadyExists)?null:new SecurityIdentifier(sid).Value);}
        finally{if(sid!=IntPtr.Zero)_=FreeSid(sid);}
    }
    public MigrationProfileUserScope ObserveCurrentUserScope(string sid)
    {
        using var token=ProcessToken();using var identity=new WindowsIdentity(token.DangerousGetHandle());
        var length=0u;_ =GetUserProfileDirectoryW(token,null,ref length);if(length is <2 or >32768)throw new IOException("MIGRATION_PROFILE_USER_LOOKUP");
        var profile=new StringBuilder((int)length);if(!GetUserProfileDirectoryW(token,profile,ref length))throw new IOException("MIGRATION_PROFILE_USER_LOOKUP",new Win32Exception());
        var result=GetAppContainerFolderPath(sid,out var memory);
        try
        {
            if(result!=0||memory==IntPtr.Zero)throw new IOException("MIGRATION_PROFILE_FOLDER_LOOKUP",Marshal.GetExceptionForHR(result));
            var folder=Marshal.PtrToStringUni(memory)??throw new IOException("MIGRATION_PROFILE_FOLDER_LOOKUP");
            var userExists=DirectoryEvidence(profile.ToString());var containerExists=DirectoryEvidence(folder);
            return new(identity.User?.Value??"",profile.ToString(),folder,userExists,containerExists,true);
        }
        finally{if(memory!=IntPtr.Zero)Marshal.FreeCoTaskMem(memory);}
    }
    private static bool DirectoryEvidence(string path)
    {
        try
        {
            var value=File.GetAttributes(path);if((value&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=FileAttributes.Directory)throw new IOException("MIGRATION_PROFILE_FOLDER_KIND");
            SafeDirectory.NoReparseAncestors(path);return true;
        }
        catch(FileNotFoundException){return false;}
        catch(DirectoryNotFoundException){return false;}
    }
    public bool LoopbackExempt(string sid)
    {
        var error=NetworkIsolationGetAppContainerConfig(out var count,out var entries);if(error!=0)throw new IOException("MIGRATION_PROFILE_NETWORK_INSPECTION",new Win32Exception((int)error));
        try
        {
            if(count>65536||count>0&&entries==IntPtr.Zero)throw new IOException("MIGRATION_PROFILE_NETWORK_BOUND");var stride=Marshal.SizeOf<SidAttributes>();
            for(var i=0u;i<count;i++)if(new SecurityIdentifier(Marshal.PtrToStructure<SidAttributes>(entries+checked((int)i*stride)).Sid).Value==sid)return true;return false;
        }
        finally
        {
            if(entries!=IntPtr.Zero)
            {
                var heap=GetProcessHeap();if(count<=65536){var stride=Marshal.SizeOf<SidAttributes>();for(var i=0u;i<count;i++){var item=Marshal.PtrToStructure<SidAttributes>(entries+checked((int)i*stride));if(item.Sid!=IntPtr.Zero)_=HeapFree(heap,0,item.Sid);}}
                _=HeapFree(heap,0,entries);
            }
        }
    }
    private static SafeAccessTokenHandle ProcessToken()
    {if(!OpenProcessToken(GetCurrentProcess(),8,out var token))throw new IOException("MIGRATION_PROFILE_PROCESS_TOKEN",new Win32Exception());return token;}
    [StructLayout(LayoutKind.Sequential)]private struct SidAttributes{public IntPtr Sid;public uint Attributes;}
    [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentThread();
    [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool OpenProcessToken(IntPtr process,uint access,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool OpenThreadToken(IntPtr thread,uint access,[MarshalAs(UnmanagedType.Bool)]bool openAsSelf,out SafeAccessTokenHandle token);
    [DllImport("userenv.dll",CharSet=CharSet.Unicode)]private static extern int DeriveAppContainerSidFromAppContainerName(string name,out IntPtr sid);
    [DllImport("userenv.dll",CharSet=CharSet.Unicode)]private static extern int CreateAppContainerProfile(string name,string display,string description,IntPtr capabilities,uint count,out IntPtr sid);
    [DllImport("advapi32.dll")]private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("userenv.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetUserProfileDirectoryW(SafeAccessTokenHandle token,StringBuilder? path,ref uint length);
    [DllImport("userenv.dll",CharSet=CharSet.Unicode)]private static extern int GetAppContainerFolderPath(string sid,out IntPtr path);
    [DllImport("firewallapi.dll")]private static extern uint NetworkIsolationGetAppContainerConfig(out uint count,out IntPtr entries);
    [DllImport("kernel32.dll")]private static extern IntPtr GetProcessHeap();
    [DllImport("kernel32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool HeapFree(IntPtr heap,uint flags,IntPtr memory);
}

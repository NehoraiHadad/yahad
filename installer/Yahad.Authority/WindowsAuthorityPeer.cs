using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
namespace Yahad.Authority;

internal interface IAuthorityPeerApi
{
    int ClientPid(SafePipeHandle pipe);IDisposable PinProcess(int pid);long Created(int pid);
    bool Identify(SafePipeHandle pipe);AuthorityPeer ThreadIdentity(int pid,long created);bool Revert();
}
internal sealed class WindowsAuthorityPeer(IAuthorityPeerApi api,Action fatalRevert)
{
    internal static WindowsAuthorityPeer Native()=>new(new NativeAuthorityPeerApi(),()=>Environment.FailFast("AUTHORITY_REVERT_UNCONFIRMED"));
    // Synchronous by design: never await while a client identification token is active.
    internal AuthorityPeer Read(SafePipeHandle connectedPipe)
    {
        var pid=api.ClientPid(connectedPipe);using var pin=api.PinProcess(pid);var created=api.Created(pid);
        if(pid<1||created<=0||!api.Identify(connectedPipe))throw new UnauthorizedAccessException("AUTHORITY_IDENTIFICATION_FAILED");
        AuthorityPeer result;
        try{result=api.ThreadIdentity(pid,created);}
        finally{if(!api.Revert()){fatalRevert();throw new IOException("AUTHORITY_REVERT_UNCONFIRMED");}}
        if(api.ClientPid(connectedPipe)!=pid||api.Created(pid)!=created)throw new UnauthorizedAccessException("AUTHORITY_PEER_CHANGED");return result;
    }
}
// Compiled native adapter; current entry never instantiates it or opens a pipe.
internal sealed class NativeAuthorityPeerApi:IAuthorityPeerApi
{
    public int ClientPid(SafePipeHandle pipe){if(!GetNamedPipeClientProcessId(pipe,out var pid)||pid>int.MaxValue)throw new Win32Exception();return (int)pid;}
    public IDisposable PinProcess(int pid){var h=OpenProcess(0x1000|0x100000,false,pid);if(h.IsInvalid){h.Dispose();throw new Win32Exception();}return h;}
    public long Created(int pid)
    {using var h=OpenProcess(0x1000,false,pid);if(h.IsInvalid||!GetProcessTimes(h,out var created,out _,out _,out _))throw new Win32Exception();return created;}
    public bool Identify(SafePipeHandle pipe)=>ImpersonateNamedPipeClient(pipe);
    public AuthorityPeer ThreadIdentity(int pid,long created)
    {
        using var identity=WindowsIdentity.GetCurrent(true)??throw new UnauthorizedAccessException("AUTHORITY_NO_IDENTIFICATION");
        if(identity.ImpersonationLevel!=TokenImpersonationLevel.Identification)throw new UnauthorizedAccessException("AUTHORITY_SQOS");
        if(!GetTokenInformation(identity.AccessToken,12,out var session,4,out _)||session>int.MaxValue)throw new Win32Exception();
        return new(identity.User?.Value??throw new UnauthorizedAccessException(),pid,created,(int)session,
            identity.Groups?.Any(s=>s.Value is "S-1-5-4" or "S-1-5-14")==true);
    }
    public bool Revert()=>RevertToSelf();
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint rights,bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exit,out long kernel,out long user);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool ImpersonateNamedPipeClient(SafePipeHandle pipe);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool RevertToSelf();
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool GetTokenInformation(SafeAccessTokenHandle token,int kind,out uint value,uint length,out uint returned);
}

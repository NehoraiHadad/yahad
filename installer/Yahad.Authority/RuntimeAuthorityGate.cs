using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record RuntimeGeneration(string InstallationId,string ReleaseDigest,string Nonce,int ProcessId,long Created);
internal interface IRuntimeGateKernel
{
    RuntimeGeneration ObserveHost();void AssignWaitingHost(RuntimeGeneration generation);
    bool InOwnedJob(RuntimeGeneration generation);bool HostExited(RuntimeGeneration generation);bool OwnedTreeEmpty();
}
internal sealed class RuntimeAuthorityGate(IRuntimeGateKernel kernel)
{
    private RuntimeGeneration? generation;private bool admitted;
    internal void Admit(RuntimeGeneration expected)
    {
        InstalledActivationResolver.Id(expected.InstallationId);InstalledActivationResolver.Id(expected.Nonce);InstalledActivationResolver.Digest(expected.ReleaseDigest);
        if(expected.ProcessId<1||expected.Created<=0)throw new IOException("RUNTIME_GENERATION_BINDING");
        if(generation is not null)throw new IOException("RUNTIME_GENERATION_REPLAY");generation=expected;
        if(kernel.ObserveHost()!=expected)throw new IOException("RUNTIME_HOST_BINDING");
        kernel.AssignWaitingHost(expected);
        if(kernel.ObserveHost()!=expected||!kernel.InOwnedJob(expected))throw new IOException("RUNTIME_JOB_UNCONFIRMED");admitted=true;
    }
    internal void RequireLaunch(RuntimeGeneration expected)
    {if(!admitted||generation!=expected||kernel.ObserveHost()!=expected||!kernel.InOwnedJob(expected)||kernel.HostExited(expected))throw new UnauthorizedAccessException("RUNTIME_LAUNCH_GATE");}
    internal void RequireQuiescence()
    {if(generation is null||!kernel.HostExited(generation)||!kernel.OwnedTreeEmpty())throw new IOException("RUNTIME_QUIESCENCE_UNCONFIRMED");}
}
// Uses an authority-owned retained job and host handle. It neither creates a
// service nor accepts a client-supplied job handle. Construction is not enabled.
internal sealed class NativeRuntimeGateKernel(SafeFileHandle ownedJob,SafeProcessHandle retainedHost,Func<RuntimeGeneration> observed):IRuntimeGateKernel
{
    public RuntimeGeneration ObserveHost()=>observed();
    public void AssignWaitingHost(RuntimeGeneration generation)
    {if(retainedHost.IsInvalid||ownedJob.IsInvalid||!AssignProcessToJobObject(ownedJob,retainedHost))throw new Win32Exception();}
    public bool InOwnedJob(RuntimeGeneration generation)
    {if(!IsProcessInJob(retainedHost,ownedJob,out var inside))throw new Win32Exception();return inside;}
    public bool HostExited(RuntimeGeneration generation)=>WaitForSingleObject(retainedHost,0)==0;
    public bool OwnedTreeEmpty()
    {
        var bytes=Marshal.AllocHGlobal(48);
        try{if(!QueryInformationJobObject(ownedJob,1,bytes,48,out _))throw new Win32Exception();return Marshal.ReadInt32(bytes,36)==0;}
        finally{Marshal.FreeHGlobal(bytes);}
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,SafeProcessHandle process);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool IsProcessInJob(SafeProcessHandle process,SafeFileHandle job,out bool result);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeProcessHandle process,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool QueryInformationJobObject(SafeFileHandle job,int kind,IntPtr data,uint length,out uint returned);
}

internal sealed class GatedRuntimeStartAuthorizer(IServiceStartAuthorizer installed,RuntimeAuthorityGate gate,RuntimeGeneration generation):IServiceStartAuthorizer,IObservedAdmission
{
    public TimeSpan AdmissionBudget=>installed is IObservedAdmission observed?observed.AdmissionBudget:TimeSpan.FromSeconds(25);
    public event Action? ObservedWork{add{if(installed is IObservedAdmission o)o.ObservedWork+=value;}remove{if(installed is IObservedAdmission o)o.ObservedWork-=value;}}
    public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation){cancellation.ThrowIfCancellationRequested();gate.RequireLaunch(generation);return installed.AuthorizeAsync(cancellation);}
}

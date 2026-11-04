using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace Yahad.Install;

internal enum StartupDiagnosticRole { Runtime, Updater }
// One startup stage transition: a fixed stage name, elapsed time since the
// service began starting, and a fixed failure token latched at that stage.
internal sealed record StartupStep(string Stage,long ElapsedMilliseconds,string? Failure);
// The contained app root ended before it reported readiness. Its exit code is
// the only fact the host owns about why; the app's own output stays private.
internal sealed class ChildExitedException(int exitCode):IOException("SERVICE_CHILD_EXITED")
{internal int ExitCode=>exitCode;}
// Fixed local Application event only. No source-registration/registry writes,
// exception text, paths, arguments, tokens or data. Logging cannot replace the
// original failure when the event log is unavailable.
internal static class ServiceStartupDiagnostic
{
    internal static string Encode(StartupDiagnosticRole role,Exception error,IReadOnlyList<StartupStep>? steps=null)
    {
        var cause=error;int? native=null,exit=null;
        for(var depth=0;depth<8;depth++)
        {
            if(cause is ChildExitedException exited)exit??=exited.ExitCode;
            if(cause is Win32Exception win32){native=win32.NativeErrorCode;break;}if(cause.InnerException is not{} inner)break;cause=inner;
        }
        return JsonSerializer.Serialize(new{Format=1,Role=role.ToString(),SourceCommit=ApplicationRelease.SourceCommit,ProcessId=Environment.ProcessId,
            Code=KnownCode(error.Message),HResult=error.HResult,NativeError=native,ExitCode=exit,Sites=Sites(error),
            Steps=steps?.TakeLast(16).Select(step=>new{step.Stage,Ms=step.ElapsedMilliseconds,step.Failure}).ToArray()});
    }
    // Compiled Yahad method names and IL offsets locate a refusal that has no
    // allowlisted code; with the exact build they map to one source line.
    internal static string[] Sites(Exception error)=>new StackTrace(error,false).GetFrames()
        .Where(frame=>frame.GetMethod()?.DeclaringType?.Assembly.GetName().Name?.StartsWith("Yahad.",StringComparison.Ordinal)==true)
        .Select(frame=>frame.GetMethod()!.DeclaringType!.FullName+"."+frame.GetMethod()!.Name+"+IL_"+frame.GetILOffset().ToString("X4"))
        .Where(site=>site.Length<=512).Take(4).ToArray();
    internal static string? KnownCode(string value)=>value switch
    {
        "NATIVE_REPARSE_KIND_OR_HARDLINK" or "NATIVE_LOCAL_PATH"=>value,
        "SERVICE_CHILD_EXITED" or "SERVICE_CHILD_EOF" or "SERVICE_CHILD_PEER" or "SERVICE_CHILD_READY"=>value,
        "INSTALLED_LAUNCH_BOUND" or "INSTALLED_LAUNCH_FORMAT" or "INSTALLED_LAUNCH_PORT" or "INSTALLED_LAUNCH_ORIGIN" or "INSTALLED_LAUNCH_IDENTIFIER"=>value,
        _=>null
    };
    internal static bool TryRecord(StartupDiagnosticRole role,Exception error,IReadOnlyList<StartupStep>? steps=null)
    {try{return Write(Encode(role,error,steps),1001);}catch{return false;}}
    // The repair gave up on an upgrade; an administrator has to act. Versions and
    // the upgrade identifier only.
    internal static bool TryRecordUpgrade(string id,string from,string to,int attempts)
    {try{return Write(JsonSerializer.Serialize(new{Format=1,Upgrade=id,From=from,To=to,Attempts=attempts}),3003);}catch{return false;}}
    private static bool Write(string value,uint id)
    {
        // RegisterEventSource retrieves a handle; an unregistered fixed source
        // falls back to Application. It does not create an event-source key.
        // https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-registereventsourcew
        var handle=RegisterEventSourceW(null,"Yahad");if(handle==IntPtr.Zero)return false;
        try{return ReportEventW(handle,1,0,id,IntPtr.Zero,1,0,[value],IntPtr.Zero);}
        finally{_=DeregisterEventSource(handle);}
    }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr RegisterEventSourceW(string? server,string source);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool ReportEventW(IntPtr handle,ushort type,ushort category,uint id,IntPtr sid,ushort count,uint size,[MarshalAs(UnmanagedType.LPArray,ArraySubType=UnmanagedType.LPWStr)]string[] values,IntPtr data);
    [DllImport("advapi32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool DeregisterEventSource(IntPtr handle);
}

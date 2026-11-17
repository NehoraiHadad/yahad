using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
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
// The events' fields, in this order and spelled exactly so (default options).
internal sealed record StartupDiagnosticStep(string Stage,long Ms,string? Failure);
internal sealed record StartupDiagnostic(int Format,string Role,string SourceCommit,int ProcessId,string? Code,int HResult,int? NativeError,int? ExitCode,
    string[] Sites,StartupDiagnosticStep[]? Steps);
internal sealed record UpgradeDiagnostic(int Format,string Upgrade,string From,string To,int Attempts);
internal sealed record UpdateDiagnostic(int Format,string Operation,string Error,string Code,bool Permanent,string? From,string? Version,string SourceCommit,string[] Sites);
[JsonSerializable(typeof(StartupDiagnostic))][JsonSerializable(typeof(UpgradeDiagnostic))][JsonSerializable(typeof(UpdateDiagnostic))]
internal sealed partial class DiagnosticJsonContext:JsonSerializerContext;
// The Application events of source "Yahad", one JSON string each:
//   1001 error    a service (Runtime or Updater) could not start or stopped on a failure
//   2001 warning  an update check or install failed (error: rollback or internal)
//   3003 error    the repair gave up on an interrupted upgrade
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
        return JsonSerializer.Serialize(new StartupDiagnostic(1,role.ToString(),InfrastructureRelease.SourceCommit,Environment.ProcessId,
            KnownCode(error.Message),error.HResult,native,exit,Sites(error),
            steps?.TakeLast(16).Select(step=>new StartupDiagnosticStep(step.Stage,step.ElapsedMilliseconds,step.Failure)).ToArray()),DiagnosticJsonContext.Default.StartupDiagnostic);
    }
    // Compiled Yahad method names and code offsets locate a refusal that has no
    // allowlisted code; with the exact build they map to one source line. A JIT image
    // reports the IL offset; a NativeAOT image has no IL, so it reports the offset into
    // the method's native code. DiagnosticMethodInfo needs no reflection metadata.
    internal static string[] Sites(Exception error)=>new StackTrace(error,false).GetFrames()
        .Select(frame=>(Frame:frame,Method:DiagnosticMethodInfo.Create(frame)))
        .Where(site=>site.Method?.DeclaringAssemblyName.StartsWith("Yahad.",StringComparison.Ordinal)==true)
        .Select(site=>site.Method!.DeclaringTypeName+"."+site.Method.Name+Offset(site.Frame))
        .Where(site=>site.Length<=512).Take(4).ToArray();
    private static string Offset(StackFrame frame)
        =>frame.GetILOffset() is var il and not StackFrame.OFFSET_UNKNOWN?"+IL_"+il.ToString("X4"):"+NATIVE_"+frame.GetNativeOffset().ToString("X4");
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
    {try{return Write(JsonSerializer.Serialize(new UpgradeDiagnostic(1,id,from,to,attempts),DiagnosticJsonContext.Default.UpgradeDiagnostic),3003);}catch{return false;}}
    // What the updates page shows, kept for whoever reads the machine later: the fixed
    // operation and error word, the precise code (UpdaterRules.Code, message-free),
    // the versions, and for a check the Yahad sites that threw.
    internal static string EncodeUpdate(string operation,string error,string code,bool permanent,string? from,string? version,Exception? cause)
        =>JsonSerializer.Serialize(new UpdateDiagnostic(1,operation,error,code,permanent,from,version,InfrastructureRelease.SourceCommit,cause is null?[]:Sites(cause)),
            DiagnosticJsonContext.Default.UpdateDiagnostic);
    internal static bool TryRecordUpdate(string operation,string error,string code,bool permanent,string? from,string? version,Exception? cause)
    {try{return Write(EncodeUpdate(operation,error,code,permanent,from,version,cause),2001,error is "rollback" or "internal"?Error:Warning);}catch{return false;}}
    private const ushort Error=1,Warning=2;
    private static bool Write(string value,uint id,ushort type=Error)
    {
        // RegisterEventSource retrieves a handle; an unregistered fixed source
        // falls back to Application. It does not create an event-source key.
        // https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-registereventsourcew
        var handle=RegisterEventSourceW(null,"Yahad");if(handle==IntPtr.Zero)return false;
        try{return ReportEventW(handle,type,0,id,IntPtr.Zero,1,0,[value],IntPtr.Zero);}
        finally{_=DeregisterEventSource(handle);}
    }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr RegisterEventSourceW(string? server,string source);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool ReportEventW(IntPtr handle,ushort type,ushort category,uint id,IntPtr sid,ushort count,uint size,[MarshalAs(UnmanagedType.LPArray,ArraySubType=UnmanagedType.LPWStr)]string[] values,IntPtr data);
    [DllImport("advapi32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool DeregisterEventSource(IntPtr handle);
}

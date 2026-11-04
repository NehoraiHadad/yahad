using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Security.Principal;
using Yahad.Install;
using Yahad.Service;
using Yahad.Setup;

internal static class ServiceStartupFailureTests
{
    private static void Require(bool value){if(!value)throw new Exception("STARTUP_FAILURE_ASSERTION");}
    private static void Refuse(Action run){try{run();}catch(IOException){return;}throw new Exception("STARTUP_FAILURE_UNEXPECTED_ADMISSION");}
    internal static async Task RunAsync(Func<string,Func<Task>,Task> check)
    {
        foreach(var diagnosticUnavailable in new[]{false,true})
        foreach(var cause in new Exception[]{new IOException("RUNTIME_INSTALLATION_ANCHOR"),new ServiceStartDenied(ServiceFailure.IdentityDenied),new OperationCanceledException()})
        await check("Runtime failure observer preserves original refusal and STOPPED verdict "+cause.GetType().Name+" unavailable "+diagnosticUnavailable,async()=>
        {
            var reports=new List<ServiceReport>();Exception? seen=null;var calls=0;
            var controller=new ServiceController(new RefusedStart(cause),reportFailure:error=>
            {seen=error;calls++;if(diagnosticUnavailable)throw new IOException("DIAGNOSTIC_UNAVAILABLE");});
            var result=await controller.RunAsync(reports.Add);
            Require(ReferenceEquals(seen,cause)&&calls==1&&result.QuiescenceConfirmed);
            Require(result.Failure==(cause is ServiceStartDenied?ServiceFailure.IdentityDenied:ServiceFailure.StartFailed));
            Require(reports[^1].State==ServiceState.Stopped&&reports[^1].Win32ExitCode==1066&&reports.All(r=>r.State!=ServiceState.Running));
        });
        foreach(var cause in new Exception[]{new IOException("RUNTIME_INSTALLATION_ANCHOR"),new ServiceStartDenied(ServiceFailure.IdentityDenied),new OperationCanceledException()})
        await check("Runtime failure report carries the stage history and its latched verdict "+cause.GetType().Name,async()=>
        {
            StartupStep[]? steps=null;ServiceController? controller=null;
            controller=new ServiceController(new RefusedStart(cause),reportFailure:_=>steps=controller!.StartupSteps);await controller.RunAsync(_=>{});
            var verdict=cause switch{ServiceStartDenied=>"admission_denied",OperationCanceledException=>"startup_timeout",_=>"startup_failed"};
            Require(steps is [{Stage:"Authorizing",Failure:null},{Stage:"Authorizing"} last]&&last.Failure==verdict);
            using var json=JsonDocument.Parse(ServiceStartupDiagnostic.Encode(StartupDiagnosticRole.Runtime,cause,steps));
            Require(json.RootElement.GetProperty("Steps")[1].GetProperty("Failure").GetString()==verdict&&json.RootElement.GetProperty("ExitCode").ValueKind==JsonValueKind.Null);
        });
        await check("Runtime status transport retains startup cause and reports failed final status",async()=>
        {
            var cause=new IOException("RUNTIME_INSTALLATION_ANCHOR");var transport=new IOException("TEST_STATUS_TRANSPORT");var seen=new List<Exception>();
            var controller=new ServiceController(new RefusedStart(cause),reportFailure:seen.Add);
            var result=await controller.RunAsync(report=>{if(report.State==ServiceState.Stopped)throw transport;});
            Require(result.Failure==ServiceFailure.StatusTransport&&result.QuiescenceConfirmed&&seen.SequenceEqual(new Exception[]{cause,transport}));
        });
        await check("Runtime diagnostic permits only a fixed refusal code without private exception text",()=>
        {
            using var known=JsonDocument.Parse(ServiceStartupDiagnostic.Encode(StartupDiagnosticRole.Runtime,new IOException("SERVICE_CHILD_EOF",new Win32Exception(5))));
            Require(known.RootElement.GetProperty("Role").GetString()=="Runtime"&&known.RootElement.GetProperty("Code").GetString()=="SERVICE_CHILD_EOF"&&known.RootElement.GetProperty("NativeError").GetInt32()==5);
            var privateText="private-profile-path-and-token";var encoded=ServiceStartupDiagnostic.Encode(StartupDiagnosticRole.Runtime,new IOException(privateText));
            using var hidden=JsonDocument.Parse(encoded);Require(hidden.RootElement.GetProperty("Code").ValueKind==JsonValueKind.Null&&!encoded.Contains(privateText));return Task.CompletedTask;
        });
        await check("stop observation requires only stopped with zero PID, so a failed last run can still be repaired",()=>
        {
            NativeService.RequireStopped(1,0);
            foreach(var status in new[]{(1u,123u),(2u,0u),(3u,123u),(4u,0u),(4u,123u)})Refuse(()=>NativeService.RequireStopped(status.Item1,status.Item2));
            return Task.CompletedTask;
        });
        await check("a held lease leaves its gate inspectable while excluding every other reader and writer",()=>
        {
            var temporary=Path.GetFullPath(Path.GetTempPath());var root=PrivateSession.Create(temporary);WindowsFileBoundary.DataLease? lease=null;
            if(!Path.GetFullPath(root).StartsWith(temporary.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("PRIVATE_TEST_SCOPE");
            try
            {
                lease=WindowsFileBoundary.AcquireDataLease(root);var gate=Path.Combine(root,"process.lock");
                WindowsFileBoundary.RequireNoLink(gate,false);
                try{using var read=WindowsFileBoundary.OpenRead(gate);throw new Exception("EXCLUSIVE_GATE_REOPENED");}catch(Win32Exception error)when(error.NativeErrorCode==32){}
                try{using var writer=WindowsFileBoundary.AcquireDataLease(root);throw new Exception("SECOND_WRITER_ADMITTED");}catch(Win32Exception error)when(error.NativeErrorCode==32){}
                lease.Dispose();lease=null;
            }
            finally{lease?.Dispose();Directory.Delete(root,true);}
            return Task.CompletedTask;
        });
        await check("failure action readback refuses a missing, repeated or altered restart, reset or extra action",()=>
        {
            var plan=NativeService.FailurePlan(true);NativeService.RequireFailurePlan(true,86400,plan);
            Refuse(()=>NativeService.RequireFailurePlan(true,86400,[plan[0]]));
            Refuse(()=>NativeService.RequireFailurePlan(true,86400,[plan[0],plan[0],plan[0]]));
            Refuse(()=>NativeService.RequireFailurePlan(true,1,plan));
            Refuse(()=>NativeService.RequireFailurePlan(true,86400,[new(){Kind=3,Delay=5000},plan[1],plan[2]]));
            Refuse(()=>NativeService.RequireFailurePlan(true,86400,[..plan,new(){Kind=0,Delay=0}]));
            NativeService.RequireFailurePlan(false,0,[]);
            Refuse(()=>NativeService.RequireFailurePlan(false,86400,[]));return Task.CompletedTask;
        });
        await check("startup diagnostic excludes unknown exception text, paths and data",()=>
        {
            var encoded=ServiceStartupDiagnostic.Encode(StartupDiagnosticRole.Runtime,new IOException(@"C:\private-family\secret-token.json"));
            using var json=JsonDocument.Parse(encoded);Require(json.RootElement.GetProperty("Code").ValueKind==JsonValueKind.Null);
            Require(!encoded.Contains("private-family",StringComparison.Ordinal)&&!encoded.Contains("secret-token",StringComparison.Ordinal));
            Require(ServiceStartupDiagnostic.KnownCode("AUTHORITY_PRIVATE_FAMILY_SECRET")==null);
            Require(json.RootElement.GetProperty("Sites").GetArrayLength()<=3);return Task.CompletedTask;
        });
        await check("startup diagnostic retains exact public refusal and inner native number",()=>
        {
            var encoded=ServiceStartupDiagnostic.Encode(StartupDiagnosticRole.Runtime,new IOException("SERVICE_CHILD_PEER",new Win32Exception(5)));
            using var json=JsonDocument.Parse(encoded);var root=json.RootElement;Require(root.GetProperty("Code").GetString()=="SERVICE_CHILD_PEER"&&root.GetProperty("NativeError").GetInt32()==5&&root.GetProperty("Role").GetString()=="Runtime");
            Require(root.GetProperty("SourceCommit").GetString()==ApplicationRelease.SourceCommit);return Task.CompletedTask;
        });
        await check("unavailable diagnostics cannot mask startup failure or invoke another writer",()=>
        {
            var calls=0;Require(!ServiceStartupDiagnostic.TryRecord(StartupDiagnosticRole.Runtime,new IOException("INSTALLED_LAUNCH_CHANGED"),_=>{calls++;throw new UnauthorizedAccessException();}));Require(calls==1);
            Require(!ServiceStartupDiagnostic.TryRecord((StartupDiagnosticRole)99,new IOException(),_=>{calls++;return true;}));Require(calls==1);return Task.CompletedTask;
        });
    }
    private sealed class RefusedStart(Exception cause):IServiceStartAuthorizer
    {public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)=>Task.FromException<AuthorizedServiceStart>(cause);}
}

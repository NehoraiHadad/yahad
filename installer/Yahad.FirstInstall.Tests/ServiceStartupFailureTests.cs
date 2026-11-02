using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Security.Principal;
using Yahad.Authority;
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
        await check("Runtime diagnostic permits only fixed profile refusal without private exception text",()=>
        {
            using var known=JsonDocument.Parse(ServiceStartupDiagnostic.Encode(StartupDiagnosticRole.Runtime,new IOException("MIGRATION_PROFILE_CREATE_DENIED",new Win32Exception(5))));
            Require(known.RootElement.GetProperty("Role").GetString()=="Runtime"&&known.RootElement.GetProperty("Code").GetString()=="MIGRATION_PROFILE_CREATE_DENIED"&&known.RootElement.GetProperty("NativeError").GetInt32()==5);
            var privateText="private-profile-path-and-token";var encoded=ServiceStartupDiagnostic.Encode(StartupDiagnosticRole.Runtime,new IOException(privateText));
            using var hidden=JsonDocument.Parse(encoded);Require(hidden.RootElement.GetProperty("Code").ValueKind==JsonValueKind.Null&&!encoded.Contains(privateText));return Task.CompletedTask;
        });
        await check("handled helper refusal does not wake peer; primary failure behavior remains separate",()=>
        {
            Require(!FullServiceContract.Describe(FullServiceKind.Runtime).FailureActionsOnNonCrash);
            Require(FullServiceContract.Describe(FullServiceKind.Authority).FailureActionsOnNonCrash);
            foreach(var kind in new[]{FullServiceKind.RecoveryA,FullServiceKind.RecoveryB})Require(!FullServiceContract.Describe(kind).FailureActionsOnNonCrash);
            return Task.CompletedTask;
        });
        await check("removal observation requires stopped zero PID without pretending a successful service exit",()=>
        {
            NativeFullService.RequireRemovalStopped(1,0);
            foreach(var status in new[]{(1u,123u),(2u,0u),(3u,123u),(4u,0u),(4u,123u)})Refuse(()=>NativeFullService.RequireRemovalStopped(status.Item1,status.Item2));
            return Task.CompletedTask;
        });
        await check("retained lease validates only its exact gate metadata while excluding another writer",()=>
        {
            var temporary=Path.GetFullPath(Path.GetTempPath());var root=SafeDirectory.CreatePrivateSession(temporary);WindowsFileBoundary.DataLease? lease=null;
            if(!Path.GetFullPath(root).StartsWith(temporary.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("PRIVATE_TEST_SCOPE");
            try
            {
                lease=WindowsFileBoundary.AcquireDataLease(root);var gate=Path.Combine(root,"process.lock");var observed=false;
                Require(lease.TryValidateHeldGate(gate,acl=>{Require(acl.Owner==WindowsIdentity.GetCurrent().User);observed=true;}));Require(observed);
                try{using var read=WindowsFileBoundary.OpenPrivateRead(gate);throw new Exception("EXCLUSIVE_GATE_REOPENED");}catch(Win32Exception error)when(error.NativeErrorCode==32){}
                try{using var writer=WindowsFileBoundary.AcquireDataLease(root);throw new Exception("SECOND_WRITER_ADMITTED");}catch(Win32Exception error)when(error.NativeErrorCode==32){}
                var other=Path.Combine(root,"other");Directory.CreateDirectory(other);File.WriteAllBytes(Path.Combine(other,"process.lock"),[]);
                Require(!lease.TryValidateHeldGate(Path.Combine(other,"process.lock"),_=>throw new Exception("FOREIGN_GATE_TRUSTED")));
                Require(!lease.TryValidateHeldGate(Path.Combine(root,"other-file"),_=>throw new Exception("ARBITRARY_FILE_TRUSTED")));
                Refuse(()=>lease.TryValidateHeldGate(gate,_=>throw new IOException("ROLE_METADATA_REFUSED")));
                lease.Dispose();Refuse(()=>lease.TryValidateHeldGate(gate,_=>throw new Exception("CLOSED_GATE_TRUSTED")));lease=null;
            }
            finally{lease?.Dispose();SafeDirectory.NoReparseAncestors(root);Directory.Delete(root,true);}
            return Task.CompletedTask;
        });
        await check("unreported early helper crashes terminate mutual failover without a writable journal",()=>
        {
            var pending=new Queue<FullServiceKind>();pending.Enqueue(FullServiceKind.RecoveryA);var failures=new Dictionary<FullServiceKind,int>();var launches=0;
            while(pending.Count>0&&launches<20)
            {
                var kind=pending.Dequeue();launches++;failures.TryGetValue(kind,out var previous);failures[kind]=previous+1;
                var plan=NativeFullService.FailurePlan(kind,true);var action=plan[Math.Min(previous,plan.Length-1)];
                if(action.Kind==3)pending.Enqueue(kind==FullServiceKind.RecoveryA?FullServiceKind.RecoveryB:FullServiceKind.RecoveryA);
            }
            Require(launches==3&&pending.Count==0&&failures[FullServiceKind.RecoveryA]==2&&failures[FullServiceKind.RecoveryB]==1);
            return Task.CompletedTask;
        });
        await check("failure action readback refuses repeated last launch, altered reset, delay and extra actions",()=>
        {
            foreach(var kind in new[]{FullServiceKind.RecoveryA,FullServiceKind.RecoveryB})
            {
                var plan=NativeFullService.FailurePlan(kind,true);NativeFullService.RequireFailurePlan(kind,true,86400,plan);
                Refuse(()=>NativeFullService.RequireFailurePlan(kind,true,86400,[plan[0]]));
                Refuse(()=>NativeFullService.RequireFailurePlan(kind,true,86400,[plan[0],plan[0]]));
                Refuse(()=>NativeFullService.RequireFailurePlan(kind,true,1,plan));
                Refuse(()=>NativeFullService.RequireFailurePlan(kind,true,86400,[new(){Kind=3,Delay=0},plan[1]]));
                Refuse(()=>NativeFullService.RequireFailurePlan(kind,true,86400,[..plan,new(){Kind=0,Delay=0}]));
                NativeFullService.RequireFailurePlan(kind,false,0,[]);
                Refuse(()=>NativeFullService.RequireFailurePlan(kind,false,86400,[]));
            }
            NativeFullService.RequireFailurePlan(FullServiceKind.Authority,true,86400,[new(){Kind=3,Delay=1000}]);
            Refuse(()=>NativeFullService.FailurePlan((FullServiceKind)99,true));return Task.CompletedTask;
        });
        await check("startup diagnostic excludes unknown exception text, paths and data",()=>
        {
            var encoded=ServiceStartupDiagnostic.Encode(StartupDiagnosticRole.Authority,new IOException(@"C:\private-family\secret-token.json"));
            using var json=JsonDocument.Parse(encoded);Require(json.RootElement.GetProperty("Code").ValueKind==JsonValueKind.Null);
            Require(!encoded.Contains("private-family",StringComparison.Ordinal)&&!encoded.Contains("secret-token",StringComparison.Ordinal));
            Require(ServiceStartupDiagnostic.KnownCode("AUTHORITY_PRIVATE_FAMILY_SECRET")==null);
            Require(json.RootElement.GetProperty("Sites").GetArrayLength()<=3);return Task.CompletedTask;
        });
        await check("startup diagnostic retains exact public refusal and inner native number",()=>
        {
            var encoded=ServiceStartupDiagnostic.Encode(StartupDiagnosticRole.RecoveryB,new IOException("OWN_OBJECT_OPEN_TOKEN",new Win32Exception(5)));
            using var json=JsonDocument.Parse(encoded);var root=json.RootElement;Require(root.GetProperty("Code").GetString()=="OWN_OBJECT_OPEN_TOKEN"&&root.GetProperty("NativeError").GetInt32()==5&&root.GetProperty("Role").GetString()=="RecoveryB");
            Require(root.GetProperty("SourceCommit").GetString()==ApplicationRelease.SourceCommit);return Task.CompletedTask;
        });
        await check("unavailable diagnostics cannot mask startup failure or invoke another writer",()=>
        {
            var calls=0;Require(!ServiceStartupDiagnostic.TryRecord(StartupDiagnosticRole.Authority,new IOException("INSTALLED_ROLE_ACCESS"),_=>{calls++;throw new UnauthorizedAccessException();}));Require(calls==1);
            Require(!ServiceStartupDiagnostic.TryRecord((StartupDiagnosticRole)99,new IOException(),_=>{calls++;return true;}));Require(calls==1);return Task.CompletedTask;
        });
        await check("authority reports original startup failure before fail-closed process exit",()=>
        {
            var failure=new IOException("AUTHORITY_ANCHOR_BOUND");var api=new Scm{PublishFailure=failure};Exception? observed=null;var exits=new List<int>();
            Require(AuthorityServiceApplication.Run(api,exits.Add,error=>observed=error)==1);Require(ReferenceEquals(observed,failure)&&exits.SequenceEqual(new[]{1}));
            Require(api.Reports.All(status=>status.State is not(ServiceState.Running or ServiceState.Stopped)));return Task.CompletedTask;
        });
        await check("early recovery refusal reports failure even before installed files can be opened",()=>
        {
            var api=new Scm{WrongArguments=true};Exception? observed=null;var factoryCalled=false;
            Require(SystemBootstrapRecoveryBootApplication.Run(api,RecoveryHelperSlot.A,_=>{factoryCalled=true;throw new Exception();},error=>observed=error)==1);
            Require(!factoryCalled&&observed is IOException{Message:"HELPER_BOOT_SCM_ARGUMENTS"});
            Require(api.Reports.Last().State==ServiceState.Stopped&&api.Reports.Last().Win32ExitCode==1066);return Task.CompletedTask;
        });
    }
    private sealed class RefusedStart(Exception cause):IServiceStartAuthorizer
    {public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)=>Task.FromException<AuthorizedServiceStart>(cause);}
    private sealed class Scm:IScmApi
    {
        internal bool WrongArguments;internal Exception? PublishFailure;internal List<ServiceReport> Reports=[];
        public void Dispatch(string name,Action<string[]> main)=>main([WrongArguments?name+"-wrong":name]);
        public void Register(string name,Func<uint,uint> handler){}
        public void Publish(ServiceReport report){if(PublishFailure is{} failure)throw failure;Reports.Add(report);}
    }
}

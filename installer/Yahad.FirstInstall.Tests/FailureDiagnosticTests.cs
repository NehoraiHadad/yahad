using Yahad.Install;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Security.Principal;
using System.Security.AccessControl;
using Yahad.Setup;

internal static class FailureDiagnosticTests
{
    internal static async Task RunAsync(Func<string,Func<Task>,Task> check)
    {
        static void Require(bool value){if(!value)throw new IOException("FAILURE_DIAGNOSTIC_REGRESSION");}
        await check("fixed installer refusal codes survive without exposing arbitrary exception text",()=>
        {
            foreach(var code in new[]{"INSTALL_TARGET_CHANGED","INSTALL_SERVICE_GENERATION_CHANGED","INSTALL_SERVICE_STOP_STALLED","UNINSTALL_INCOMPLETE","UNINSTALL_QUIESCENCE_UNCONFIRMED","UNINSTALL_SERVICE_REAPPEARED"})Require(SetupOperation.Code(new IOException(code))==code);
            Require(SetupOperation.Code(new IOException(@"C:\private-family\connection.json"))=="INSTALL_FAILED");
            // A code no entry names is still never lost: only its words are generic.
            foreach(var retired in new[]{"INSTALL_STAGING_CHANGED","UPDATE_PREPARING_CANCEL_REFUSED","UPDATE_SERVICE_START_FAILED"})
                Require(SetupOperation.Code(new IOException(retired))==retired&&ReferenceEquals(SetupErrorCatalog.Resolve(retired),SetupErrorCatalog.Fallback));
            return Task.CompletedTask;
        });
        await check("every deliberate code survives whatever exception type carries it",()=>
        {
            foreach(var error in new Exception[]{new InvalidDataException("LAN_PUBLIC_CERTIFICATE_INVALID"),new InvalidOperationException("LIFECYCLE_STATE"),new ArgumentException("NUL_ARGUMENT"),
                new UnauthorizedAccessException("ADMIN_REQUIRED"),new JsonException("NATIVE_JSON_NULL"),new System.Net.Http.HttpRequestException("SERVER_NOT_READY"),new System.Security.Cryptography.CryptographicException("LAN_CA_CHANGED"),
                new System.Runtime.InteropServices.COMException("LAN_FIREWALL_UNAVAILABLE")})
                Require(SetupOperation.Code(new SetupOperationException(UninstallPhase.FullNetwork,error))==error.Message);
            // The hand-off is a control result: no exception can produce it.
            Require(SetupOperation.Code(new IOException(RepairHost.HandOff))=="INSTALL_FAILED");
            // Framework text is never a code, and the generic code keeps the type.
            foreach(var error in new Exception[]{new InvalidOperationException("Sequence contains no elements"),new System.Runtime.InteropServices.COMException("x",unchecked((int)0x800706BA)),new KeyNotFoundException("ok")})
            {
                var detail=SetupFailureDetail.From(error);
                Require(SetupOperation.Code(error)=="INSTALL_FAILED"&&detail.Exception==error.GetType().FullName);
            }
            return Task.CompletedTask;
        });
        await check("System.IO's Win32 HRESULTs name disk space, locked files and access",()=>
        {
            foreach(var (hresult,code) in new[]{(unchecked((int)0x80070070),"DISK_SPACE"),(unchecked((int)0x80070027),"DISK_SPACE"),(unchecked((int)0x80070020),"FILE_LOCKED"),(unchecked((int)0x80070021),"FILE_LOCKED"),(unchecked((int)0x80070005),"ACCESS_DENIED"),(unchecked((int)0x80070057),"INSTALL_FAILED")})
                Require(SetupOperation.Code(new SetupOperationException(UninstallPhase.DeleteBinaries,new IOException(@"C:\private\file",hresult)))==code);
            foreach(var (native,code) in new[]{(33,"FILE_LOCKED"),(39,"DISK_SPACE")})Require(SetupOperation.Code(new Win32Exception(native))==code);
            return Task.CompletedTask;
        });
        await check("a health probe that does not answer is SERVER_NOT_READY, the operation's deadline is INSTALL_TIMEOUT",async()=>
        {
            foreach(var failure in new Exception[]{new TaskCanceledException("probe",new TimeoutException()),new System.Net.Http.HttpRequestException("refused"),new IOException("NATIVE_DUPLICATE_FIELD")})
            {
                using var client=new System.Net.Http.HttpClient(new FailingHandler(failure));Exception? seen=null;
                try{await NativeSetupHost.AskAsync(client,"http://127.0.0.1:9/health/ready",e=>e.GetProperty("ok").GetBoolean(),CancellationToken.None);}catch(Exception error){seen=error;}
                Require(seen is IOException{Message:"SERVER_NOT_READY"}&&SetupOperation.Code(seen)=="SERVER_NOT_READY"&&SetupFailureDetail.From(seen).Exception!.Contains(failure.GetType().Name,StringComparison.Ordinal));
            }
            using(var cancelled=new CancellationTokenSource())
            {
                cancelled.Cancel();using var client=new System.Net.Http.HttpClient(new FailingHandler(new TaskCanceledException()));Exception? seen=null;
                try{await NativeSetupHost.AskAsync(client,"http://127.0.0.1:9/",e=>0,cancelled.Token);}catch(Exception error){seen=error;}
                Require(seen is OperationCanceledException&&SetupOperation.Code(seen)=="INSTALL_TIMEOUT");
            }
        });
        await check("every code the engine throws or returns resolves to a deliberate catalog entry",()=>
        {
            var codes=EngineCodes();Require(codes.Count>150);
            var missing=codes.Where(code=>!SetupErrorCatalog.IsCode(code)||ReferenceEquals(SetupErrorCatalog.Resolve(code),SetupErrorCatalog.Fallback)).ToArray();
            if(missing.Length!=0)throw new IOException("CATALOG_MISSING "+string.Join(",",missing));
            // The generic code and the codes Code() itself names are entries too.
            foreach(var code in new[]{"INSTALL_FAILED","ACCESS_DENIED","FILE_LOCKED","DISK_SPACE","INSTALL_TIMEOUT"})Require(!ReferenceEquals(SetupErrorCatalog.Resolve(code),SetupErrorCatalog.Fallback));
            // Every entry says what happened and what to do; an action it names can be taken.
            foreach(var code in codes)
            {
                var entry=SetupErrorCatalog.Resolve(code);
                Require(entry.What.Length>10&&entry.Next.Length>10&&(entry.Action is SetupErrorAction.None or SetupErrorAction.ContinueRemoval||SetupErrorCatalog.Target(entry.Action) is not null)&&(entry.Action==SetupErrorAction.None||SetupErrorCatalog.Label(entry.Action) is not null));
                Require(entry.Category!=SetupErrorCategory.Defect||entry.Action==SetupErrorAction.ReportProblem);
            }
            Require(SetupErrorCatalog.Next("DISK_SPACE",SetupErrorCatalog.Resolve("DISK_SPACE"),3L*1024*1024*1024+1).Contains("3.1 GB",StringComparison.Ordinal));
            return Task.CompletedTask;
        });
        await check("the failure record and copied details carry numbers and names, never messages or paths, and keep the last 20",()=>
        {
            const string secret=@"C:\Users\private-family\secret.json";
            var cause=new IOException(secret,unchecked((int)0x80070070));
            var failure=new SetupOperationException(UninstallPhase.FullPrepare,cause);
            var report=SetupFailureReport.Create(SetupOperation.Code(failure),SetupFailureDetail.From(failure),"install",new DateTimeOffset(2026,11,17,10,15,30,TimeSpan.Zero),"1.2.3");
            var json=System.Text.Encoding.UTF8.GetString(report.Json());var text=report.Text(@"C:\ProgramData\Yahad.FirstInstall.setup-logs\setup-x.json");
            Require(report is {Code:"DISK_SPACE",Category:"Environment",Phase:"FullPrepare",HResult:"0x80070070",Exception:"System.IO.IOException",AppVersion:"1.2.3",Utc:"2026-11-17T10:15:30Z"});
            Require(report.SetupVersion==InfrastructureRelease.Version&&!json.Contains("private-family",StringComparison.Ordinal)&&!text.Contains("private-family",StringComparison.Ordinal));
            Require(text.Contains("Yahad Setup: DISK_SPACE",StringComparison.Ordinal)&&text.Contains("Phase: FullPrepare",StringComparison.Ordinal)&&text.Contains("Log: C:\\ProgramData",StringComparison.Ordinal));
            var root=Path.Combine(PrivateSession.Create(Path.GetTempPath()),"setup-logs");
            for(var i=0;i<SetupFailureLog.Kept+5;i++)Require(SetupFailureLog.Write(report,root,path=>Directory.CreateDirectory(path)) is {} written&&File.Exists(written));
            File.WriteAllBytes(Path.Combine(root,"setup-x.json.partial-"+new string('a',32)),[1]);SetupFailureLog.Prune(root);
            var files=Directory.GetFiles(root);Require(files.Length==SetupFailureLog.Kept&&files.All(f=>Path.GetFileName(f).StartsWith("setup-",StringComparison.Ordinal)&&f.EndsWith(".json",StringComparison.Ordinal)));
            var kept=JsonDocument.Parse(File.ReadAllBytes(files[0])).RootElement;Require(kept.GetProperty("Code").GetString()=="DISK_SPACE"&&kept.GetProperty("Format").GetInt32()==1);
            // A root Setup cannot prepare leaves no record and no exception.
            Require(SetupFailureLog.Write(report,root,_=>throw new IOException("INSTALL_ROOT_OWNER")) is null);
            // Beside the state root, never inside it: a failed first install leaves nothing there.
            Require(!SetupOperation.Within(SetupFailureLog.Root,InstallLocations.State)&&Path.GetDirectoryName(SetupFailureLog.Root)==Path.GetDirectoryName(InstallLocations.State));
            return Task.CompletedTask;
        });
        await check("installation and removal get measured absolute bounds",()=>
        {
            Require(SetupOperation.OperationTimeout("install")==TimeSpan.FromMinutes(48));
            Require(SetupOperation.OperationTimeout("uninstall")==TimeSpan.FromMinutes(96));
            var refused=false;try{SetupOperation.OperationTimeout("unknown");}catch(IOException){refused=true;}Require(refused);
            return Task.CompletedTask;
        });
        await check("record creation replacement and failed-publication cleanup use only synthetic owned files",()=>
        {
            var root=PrivateSession.Create(Path.GetTempPath());
            var path=Path.Combine(root,"diagnostic.json");
            NativeInstallFiles.Publish(path,"first"u8.ToArray());
            NativeInstallFiles.Publish(path,"second"u8.ToArray());
            Require(File.ReadAllText(path)=="second");
            var collision=Path.Combine(root,"occupied");Directory.CreateDirectory(collision);
            var refused=false;try{NativeInstallFiles.Replace(collision,"refused"u8.ToArray());}catch(Win32Exception){refused=true;}
            Require(refused&&Directory.Exists(collision)&&Directory.GetFiles(root,"occupied.partial-*").Length==0);
            return Task.CompletedTask;
        });
        await check("native error codes survive operation context without changing established failure meanings",()=>
        {
            foreach(var (native,code) in new[]{(5,"ACCESS_DENIED"),(32,"FILE_LOCKED"),(112,"DISK_SPACE"),(87,"INSTALL_FAILED")})
            {
                var failure=new SetupOperationException(UninstallPhase.SaveRemovalState,new Win32Exception(native));
                Require(SetupOperation.Code(failure)==code);
                var detail=SetupFailureDetail.From(failure);
                Require(detail.Phase=="SaveRemovalState"&&detail.NativeError==native);
            }
            return Task.CompletedTask;
        });
        await check("removal choice ownership and cancellation refusals remain refusals through diagnostics",()=>
        {
            foreach(var (cause,code) in new (Exception,string)[]{
                (new IOException("UNINSTALL_MODE_CHANGED"),"UNINSTALL_MODE_CHANGED"),
                (new UnauthorizedAccessException("private owner"),"ACCESS_DENIED"),
                (new OperationCanceledException(),"INSTALL_TIMEOUT")})
                Require(SetupOperation.Code(new SetupOperationException(UninstallPhase.StopService,cause))==code);
            return Task.CompletedTask;
        });
        await check("setup result serializes bounded numeric diagnostics without private exception text",()=>
        {
            const string secret="C:\\private-family\\secret-connection.json";
            var failure=new SetupOperationException(UninstallPhase.WriteRemovalReceipt,new Win32Exception(87,secret));
            var result=new SetupResult(false,SetupOperation.Code(failure),DataPreserved:false,Failure:SetupFailureDetail.From(failure));
            var bytes=JsonSerializer.SerializeToUtf8Bytes(result);
            var text=System.Text.Encoding.UTF8.GetString(bytes);
            Require(!text.Contains("private-family",StringComparison.Ordinal)&&!text.Contains("secret-connection",StringComparison.Ordinal));
            var copy=JsonSerializer.Deserialize<SetupResult>(bytes)!;
            Require(copy is {Ok:false,DataPreserved:false,Failure:{Phase:"WriteRemovalReceipt",NativeError:87}});
            var copiedDetail=copy.Failure??throw new IOException("FAILURE_DETAIL_MISSING");
            Require(copiedDetail.Display().Contains("Windows 87",StringComparison.Ordinal));
            return Task.CompletedTask;
        });
        await check("unlisted refusal records compiled throw sites without its text",()=>
        {
            const string text=@"UNLISTED_FAMILY_TOKEN C:\hidden\connection.json";
            static void Refuse()=>throw new IOException(text);
            Exception? thrown=null;try{Refuse();}catch(IOException error){thrown=error;}
            var failure=new SetupOperationException(UninstallPhase.FullUninstall,thrown!);
            var detail=SetupFailureDetail.From(failure);
            Require(SetupOperation.Code(failure)=="INSTALL_FAILED"&&detail.Sites is {Length:>0} sites&&sites[0].Contains("Refuse",StringComparison.Ordinal)&&sites.All(site=>site.Contains("+IL_",StringComparison.Ordinal)));
            return Task.CompletedTask;
        });
        await check("non-native exceptions retain only stage and HRESULT",()=>
        {
            var failure=new SetupOperationException(UninstallPhase.RemoveService,new IOException("private detail"));
            var detail=SetupFailureDetail.From(failure);
            Require(detail.NativeError is null&&detail.Phase=="RemoveService"&&detail.Display().Contains("HRESULT",StringComparison.Ordinal));
            Require(!JsonSerializer.Serialize(detail).Contains("private detail",StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }
    private const string CodePattern="[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+";
    // Every failure code the engine can hand the window or a result file, read from
    // the sources: thrown codes, failed results, the code families composed from a
    // package prefix, the literal codes passed down for a helper to throw, and the
    // updater's exit table. Success codes and the repair hand-off are not failures.
    private static SortedSet<string> EngineCodes([System.Runtime.CompilerServices.CallerFilePath]string here="")
    {
        var installer=Path.GetDirectoryName(Path.GetDirectoryName(here))??throw new IOException("INSTALLER_ROOT_MISSING");
        var sources=new[]{"Yahad.Setup","Yahad.Install.Core"}.SelectMany(project=>Directory.EnumerateFiles(Path.Combine(installer,project),"*.cs",SearchOption.AllDirectories))
            .Where(file=>!file.Contains(Path.DirectorySeparatorChar+"obj"+Path.DirectorySeparatorChar,StringComparison.Ordinal)&&!file.Contains(Path.DirectorySeparatorChar+"bin"+Path.DirectorySeparatorChar,StringComparison.Ordinal)
                &&Path.GetFileName(file)!="SetupErrorCatalog.cs").ToArray();
        if(sources.Length<20)throw new IOException("INSTALLER_SOURCES_MISSING");
        var codes=new SortedSet<string>(StringComparer.Ordinal);
        void Add(System.Text.RegularExpressions.MatchCollection found){foreach(System.Text.RegularExpressions.Match m in found)codes.Add(m.Groups[1].Value);}
        foreach(var file in sources)
        {
            var text=File.ReadAllText(file);
            Add(System.Text.RegularExpressions.Regex.Matches(text,@"Exception\(\s*""("+CodePattern+@")"""));
            Add(System.Text.RegularExpressions.Regex.Matches(text,@"new\(\s*false\s*,\s*""("+CodePattern+@")"""));
            Add(System.Text.RegularExpressions.Regex.Matches(text,@"(?:InspectBinaries|RecordFormat\.Of)\([^;]*?""("+CodePattern+@")"""));
            foreach(System.Text.RegularExpressions.Match call in System.Text.RegularExpressions.Regex.Matches(text,@"\b(?:Result|Done|RollBackAsync)\([^;]*;"))
                Add(System.Text.RegularExpressions.Regex.Matches(call.Value,@"""("+CodePattern+@")"""));
            foreach(System.Text.RegularExpressions.Match suffix in System.Text.RegularExpressions.Regex.Matches(text,@"[Cc]odes\+""([A-Z0-9_]+)"""))
                foreach(var prefix in new[]{"PACKAGE_","APP_PACKAGE_"})codes.Add(prefix+suffix.Groups[1].Value);
            var exit=System.Text.RegularExpressions.Regex.Match(text,@"static int Exit\(string code\)=>code switch\s*\{(?<body>.*?)\};",System.Text.RegularExpressions.RegexOptions.Singleline);
            if(exit.Success)Add(System.Text.RegularExpressions.Regex.Matches(exit.Groups["body"].Value,@"""("+CodePattern+@")"""));
        }
        codes.RemoveWhere(code=>UpgradeRules.Exit(code)==0||code==RepairHost.HandOff);
        return codes;
    }
    private sealed class FailingHandler(Exception failure):System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken cancellation)
        {cancellation.ThrowIfCancellationRequested();return Task.FromException<System.Net.Http.HttpResponseMessage>(failure);}
    }
}

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
            foreach(var retired in new[]{"INSTALL_STAGING_CHANGED","UPDATE_PREPARING_CANCEL_REFUSED","UPDATE_SERVICE_START_FAILED"})Require(SetupOperation.Code(new IOException(retired))=="INSTALL_FAILED");
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
}

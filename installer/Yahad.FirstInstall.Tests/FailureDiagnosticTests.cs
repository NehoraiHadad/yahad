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
            foreach(var code in new[]{"INSTALL_TARGET_CHANGED","INSTALL_SERVICE_GENERATION_CHANGED","INSTALL_SERVICE_STOP_STALLED","INSTALL_NOT_PREPARED","INSTALL_NOT_COMMITTED","INSTALL_KEY_INVALID","UNINSTALL_REGISTRATION_CHANGED","UNINSTALL_INCOMPLETE","UNINSTALL_QUIESCENCE_UNCONFIRMED","UNINSTALL_SERVICE_REAPPEARED"})Require(SetupOperation.Code(new IOException(code))==code);
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
        await check("installer diagnostic ACL refuses widened, foreign, inherited and owner-held descriptors",()=>
        {
            var owner=new SecurityIdentifier("S-1-5-21-1-2-3-1001");
            var descriptor=NativeInstallFiles.Descriptor(ownerSid:owner.Value).Replace("OICI","");
            foreach(var changed in new[]{descriptor.Replace("0x1200a9","0x1200ab"),descriptor.Replace("0x1200a9","0x1600a9"),descriptor.Replace("0x1200a9","0x1a00a9"),descriptor.Replace(owner.Value,"S-1-5-21-1-2-3-1002"),descriptor.Replace("(A;;0x1200a9","(A;ID;0x1200a9"),descriptor.Replace("D:P","D:"),descriptor.Replace("O:S-1-5-32-544","O:"+owner.Value)})
            {
                var refused=false;try{Yahad.Install.WindowsFileBoundary.ValidateInstallerDiagnosticAcl(new RawSecurityDescriptor(changed),owner);}catch(IOException){refused=true;}Require(refused);
            }
            return Task.CompletedTask;
        });
        await check("metadata ACL changes on a synthetic owned file need no audit-policy privilege",()=>
        {
            var root=Yahad.Install.SafeDirectory.CreatePrivateSession(Path.GetTempPath());
            var path=Path.Combine(root,"synthetic-receipt.json");File.WriteAllText(path,"synthetic no private data");
            var sid=WindowsIdentity.GetCurrent().User!.Value;
            NativeInstallFiles.SetOwnedFile(path,$"O:{sid}D:P(A;;FA;;;{sid})(A;;FA;;;SY)(A;;FA;;;BA)");
            var acl=new FileInfo(path).GetAccessControl();
            Require(acl.AreAccessRulesProtected&&acl.GetOwner(typeof(SecurityIdentifier))?.Value==sid);
            return Task.CompletedTask;
        });
        await check("protected diagnostic creation replacement and failed-publication cleanup use only synthetic owned files",()=>
        {
            var root=Yahad.Install.SafeDirectory.CreatePrivateSession(Path.GetTempPath());
            var path=Path.Combine(root,"diagnostic.json");var sid=WindowsIdentity.GetCurrent().User!.Value;
            var descriptor=$"O:{sid}D:P(A;;FA;;;{sid})(A;;FA;;;SY)(A;;FA;;;BA)";
            NativeInstallFiles.WriteNew(path,"first"u8.ToArray(),descriptor);
            Require(new FileInfo(path).GetAccessControl().AreAccessRulesProtected);
            NativeInstallFiles.ReplaceOwned(path,"second"u8.ToArray(),descriptor);
            Require(File.ReadAllText(path)=="second");
            var collision=Path.Combine(root,"occupied");Directory.CreateDirectory(collision);
            var refused=false;try{NativeInstallFiles.ReplaceOwned(collision,"refused"u8.ToArray(),descriptor);}catch(Win32Exception){refused=true;}
            Require(refused&&Directory.Exists(collision)&&Directory.GetFiles(root,"occupied.partial-*").Length==0);
            return Task.CompletedTask;
        });
        await check("diagnostic read-only owner grant never widens installer metadata authority",()=>
        {
            var owner=new SecurityIdentifier("S-1-5-21-1-2-3-1001");
            var descriptor=UninstallDiagnostics.Descriptor(owner.Value);
            Yahad.Install.WindowsFileBoundary.ValidateInstallerDiagnosticAcl(new RawSecurityDescriptor(descriptor),owner);
            foreach(var changed in new[]{descriptor.Replace("0x120089","FA"),descriptor.Replace(owner.Value,"S-1-5-21-1-2-3-1002"),descriptor.Replace("D:P","D:"),descriptor.Replace("O:BA","O:"+owner.Value)})
            {
                var refused=false;try{Yahad.Install.WindowsFileBoundary.ValidateInstallerDiagnosticAcl(new RawSecurityDescriptor(changed),owner);}catch(IOException){refused=true;}Require(refused);
            }
            var controlRefused=false;try{Yahad.Install.WindowsFileBoundary.ValidateInstallerMetadataAcl(new RawSecurityDescriptor(descriptor));}catch(IOException){controlRefused=true;}Require(controlRefused);
            return Task.CompletedTask;
        });
        await check("full installer diagnostic writer uses the same bounded read-only policy as its reader",()=>
        {
            var owner=new SecurityIdentifier("S-1-5-21-1-2-3-1001");
            var descriptor=FullInstallDiagnostics.Descriptor(owner.Value);
            Require(descriptor==UninstallDiagnostics.Descriptor(owner.Value));
            Yahad.Install.WindowsFileBoundary.ValidateInstallerDiagnosticAcl(new RawSecurityDescriptor(descriptor),owner);
            Require(!descriptor.Contains("0x1200a9",StringComparison.Ordinal));
            return Task.CompletedTask;
        });
        await check("exact legacy full diagnostic owner read-execute grant is accepted without metadata authority",()=>
        {
            var owner=new SecurityIdentifier("S-1-5-21-1-2-3-1001");
            var descriptor=NativeInstallFiles.Descriptor(ownerSid:owner.Value).Replace("OICI","");
            Yahad.Install.WindowsFileBoundary.ValidateInstallerDiagnosticAcl(new RawSecurityDescriptor(descriptor),owner);
            var refused=false;try{Yahad.Install.WindowsFileBoundary.ValidateInstallerMetadataAcl(new RawSecurityDescriptor(descriptor));}catch(IOException){refused=true;}Require(refused);
            return Task.CompletedTask;
        });
        await check("legacy diagnostic compatibility still refuses writes other principals inheritance and invalid ownership",()=>
        {
            var owner=new SecurityIdentifier("S-1-5-21-1-2-3-1001");
            var descriptor=NativeInstallFiles.Descriptor(ownerSid:owner.Value).Replace("OICI","");
            foreach(var changed in new[]{descriptor.Replace("0x1200a9","0x1200ab"),descriptor.Replace("0x1200a9","0x1600a9"),descriptor.Replace("0x1200a9","0x1a00a9"),descriptor.Replace(owner.Value,"S-1-5-21-1-2-3-1002"),descriptor.Replace("(A;;0x1200a9","(A;ID;0x1200a9"),descriptor.Replace("D:P","D:"),descriptor.Replace("O:S-1-5-32-544","O:"+owner.Value)})
            {
                var refused=false;try{Yahad.Install.WindowsFileBoundary.ValidateInstallerDiagnosticAcl(new RawSecurityDescriptor(changed),owner);}catch(IOException){refused=true;}Require(refused);
            }
            return Task.CompletedTask;
        });
        await check("persisted diagnostic is sanitized bound to one installation and cannot authorize removal",()=>
        {
            var state=FullInstallerIntegrationTests.State("removing_all_data");
            var failure=new SetupOperationException(UninstallPhase.ProtectRemovalReceipt,new Win32Exception(87,"private exception text"));
            var record=UninstallDiagnosticRecord.Create(state,UninstallMode.DeleteData,failure);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(record);var text=System.Text.Encoding.UTF8.GetString(bytes);
            Require(bytes.Length<8192&&!text.Contains("private",StringComparison.Ordinal));
            var copy=JsonSerializer.Deserialize<UninstallDiagnosticRecord>(bytes)!;copy.RequireSameInstallation(state);
            var refused=false;try{copy.RequireSameInstallation(state with{InstallationId=Guid.NewGuid().ToString("N")});}catch(IOException){refused=true;}Require(refused);
            Require(copy.Mode=="DeleteData"&&copy.Phase=="ProtectRemovalReceipt"&&copy.NativeError==87);
            Require(InstallLocations.RemovalDiagnosticFile==InstallLocations.Binaries+".uninstall-diagnostic.json");
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
                Require(SetupOperation.Code(new SetupOperationException(UninstallPhase.ReadState,cause))==code);
            return Task.CompletedTask;
        });
        await check("setup result serializes bounded numeric diagnostics without private exception text",()=>
        {
            const string secret="C:\\private-family\\secret-connection.json";
            var failure=new SetupOperationException(UninstallPhase.ProtectRemovalReceipt,new Win32Exception(87,secret));
            var result=new SetupResult(false,SetupOperation.Code(failure),DataPreserved:false,Failure:SetupFailureDetail.From(failure));
            var bytes=JsonSerializer.SerializeToUtf8Bytes(result);
            var text=System.Text.Encoding.UTF8.GetString(bytes);
            Require(!text.Contains("private-family",StringComparison.Ordinal)&&!text.Contains("secret-connection",StringComparison.Ordinal));
            var copy=JsonSerializer.Deserialize<SetupResult>(bytes)!;
            Require(copy is {Ok:false,DataPreserved:false,Failure:{Phase:"ProtectRemovalReceipt",NativeError:87}});
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
            var encoded=System.Text.Encoding.UTF8.GetString(FullInstallDiagnostics.Encode(new string('a',32),failure,123));
            Require(encoded.Contains("Refuse",StringComparison.Ordinal)&&!encoded.Contains("UNLISTED",StringComparison.Ordinal)&&!encoded.Contains("hidden",StringComparison.Ordinal));
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

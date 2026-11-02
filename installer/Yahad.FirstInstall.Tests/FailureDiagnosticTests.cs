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
        await check("fixed installer recovery refusal codes survive without exposing arbitrary exception text",()=>
        {
            foreach(var code in new[]{"INSTALL_STAGING_CHANGED","UPDATE_SERVICE_START_FAILED","UPDATE_SERVICE_GENERATION_CHANGED","UPDATE_PREPARING_CANCEL_REFUSED","UPDATE_PREPARING_INVENTORY_CHANGED","UPDATE_PREPARING_SERVICE_CHANGED","UPDATE_PREPARING_BINARIES_PUBLISHED","UPDATE_PREPARING_ENROLLED","UPDATE_PREPARING_FILE_CHANGED","UPDATE_PREPARING_STATE_CHANGED","UPDATE_PREPARING_CANCEL_REQUIRES_DELETE"})Require(SetupElevation.Code(new IOException(code))==code);
            Require(SetupElevation.Code(new IOException("C:\\private-family\\connection.json"))=="INSTALL_FAILED");
            Require(SetupElevation.Code(new IOException("UPDATE_SERVICE_QUIESCENCE_UNCONFIRMED"))=="UPDATE_SERVICE_QUIESCENCE_UNCONFIRMED");
            return Task.CompletedTask;
        });
        await check("full installation and removal get measured absolute bounds; legacy and repair stay bounded",()=>
        {
            Require(SetupElevation.OperationTimeout("install",true)==TimeSpan.FromMinutes(48));
            Require(SetupElevation.OperationTimeout("uninstall",true)==TimeSpan.FromMinutes(96));
            foreach(var operation in new[]{"install","uninstall"})
                Require(SetupElevation.OperationTimeout(operation,false)==TimeSpan.FromMinutes(12));
            Require(SetupElevation.OperationTimeout("repair-uninstall-receipt",true)==TimeSpan.FromMinutes(12));
            Require(SetupElevation.OperationTimeout("repair-uninstall-receipt",false)==TimeSpan.FromMinutes(12));
            var refused=false;try{SetupElevation.OperationTimeout("unknown",true);}catch(IOException){refused=true;}Require(refused);
            return Task.CompletedTask;
        });
        await check("staging reuse requires the entire exact file and directory inventory",()=>
        {
            string[] files=["payload/app/a.js"],directories=["payload","payload/app"];
            InstallPackage.RequireStagingInventory(files,directories,files,directories);
            InstallPackage.RequireStagingInventory(files,directories,[],["payload"],false);
            var cleanupRefused=false;try{InstallPackage.RequireStagingInventory(files,directories,["payload/app/extra.js"],directories,false);}catch(IOException){cleanupRefused=true;}Require(cleanupRefused);
            foreach(var actual in new string[][]{[],["payload/app/extra.js"],["payload/app/a.js","payload/app/extra.js"],["payload/app/a.js","PAYLOAD/APP/A.JS"]})
            {
                var refused=false;try{InstallPackage.RequireStagingInventory(files,directories,actual,directories);}catch(IOException){refused=true;}Require(refused);
            }
            foreach(var actual in new string[][]{[],["payload"],["payload","payload/app","payload/extra"],["payload","PAYLOAD","payload/app"]})
            {
                var refused=false;try{InstallPackage.RequireStagingInventory(files,directories,files,actual);}catch(IOException){refused=true;}Require(refused);
            }
            return Task.CompletedTask;
        });
        await check("staging retry accepts only complete members or an exact source-verified prefix",()=>
        {
            byte[] source=[1,2,3,4,5];string Hash(byte[] value)=>Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(value));
            var record=new InstallMember("payload/app/a.js",source.Length,Hash(source));
            string Verify(byte[] existing,byte[] expected,CancellationToken cancellation=default)
            {
                using var input=new MemoryStream(expected);
                int Read(long offset,byte[] buffer){if(offset>=existing.Length)return 0;buffer[0]=existing[(int)offset];return 1;}
                return InstallPackage.VerifyExistingStageFile(record,existing.Length,Read,input,cancellation);
            }
            Require(Verify(source,source)==Hash(source));Require(Verify([1,2],source)==Hash([1,2]));Require(Verify([],source)==Hash([]));
            foreach(var existing in new byte[][]{[1,9],[1,2,3,4,5,6],[1,2,3,4,9]})
            {var refused=false;try{Verify(existing,source);}catch(IOException){refused=true;}Require(refused);}
            foreach(var changedSource in new byte[][]{[1,2,3],[1,2,3,4,9],[1,2,3,4,5,6]})
            {var refused=false;try{Verify([1,2],changedSource);}catch(IOException){refused=true;}Require(refused);}
            var canceled=false;try{Verify([1,2],source,new CancellationToken(true));}catch(OperationCanceledException){canceled=true;}Require(canceled);
            return Task.CompletedTask;
        });
        await check("receipt refusal reports only fixed check names and keeps refusal code across authenticated result",()=>
        {
            foreach(var gate in Enum.GetValues<Yahad.Install.ReceiptRecoveryCheck>())
            {
                var cause=new Yahad.Install.ReceiptRecoveryRefusal(gate);
                var error=new SetupOperationException(UninstallPhase.CompareRecoveryState,cause);
                var result=SetupElevation.FailureResult(error,UninstallMode.DeleteData,true);
                Require(!result.Ok&&result.DataPreserved&&result.Code=="UNINSTALL_RECEIPT_RECOVERY_REFUSED");
                var bytes=JsonSerializer.SerializeToUtf8Bytes(new SetupWire(1,"result",null,result));
                var copy=JsonSerializer.Deserialize<SetupWire>(bytes)!.Result!;
                Require(copy.Failure is{Phase:"CompareRecoveryState"}&&copy.Failure.RecoveryCheck==gate.ToString()&&copy.Failure.Display().Contains(gate.ToString(),StringComparison.Ordinal));
            }
            var ordinary=SetupFailureDetail.From(new IOException("C:\\private-family\\connection.json"));
            Require(ordinary.RecoveryCheck is null&&!JsonSerializer.Serialize(ordinary).Contains("private-family",StringComparison.Ordinal));
            var unknownRefused=false;try{_=new Yahad.Install.ReceiptRecoveryRefusal((Yahad.Install.ReceiptRecoveryCheck)int.MaxValue);}catch(ArgumentOutOfRangeException){unknownRefused=true;}Require(unknownRefused);
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
            NativeInstallFiles.SetOwnedDirectory(root,$"O:{sid}D:P(A;OICI;FA;;;{sid})(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
            Require(new DirectoryInfo(root).GetAccessControl().AreAccessRulesProtected);
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
            var state=new InstallerState(1,Guid.NewGuid().ToString("N"),"private-data","private-package","private-payload","private-source","private-owner",47123,"removing_all_data",[new("private-file",1,"private-hash")],"private-service","private-setup");
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
        await check("lost final progress cannot recreate diagnostic after successful receipt cleanup",()=>
        {
            var allowed=true;var removed=false;var writes=0;
            try{UninstallDiagnostics.Complete(ref allowed,()=>removed=true,()=>throw new IOException("synthetic lost result pipe"));}
            catch(IOException){if(allowed)writes++;}
            Require(removed&&!allowed&&writes==0);
            allowed=true;var notified=false;
            try{UninstallDiagnostics.Complete(ref allowed,()=>throw new IOException("synthetic receipt locked"),()=>notified=true);}
            catch(IOException){if(allowed)writes++;}
            Require(allowed&&!notified&&writes==1);
            return Task.CompletedTask;
        });
        await check("native error codes survive operation context without changing established failure meanings",()=>
        {
            foreach(var (native,code) in new[]{(5,"ACCESS_DENIED"),(32,"FILE_LOCKED"),(112,"DISK_SPACE"),(87,"INSTALL_FAILED")})
            {
                var failure=new SetupOperationException(UninstallPhase.SaveRemovalState,new Win32Exception(native));
                Require(SetupElevation.Code(failure)==code);
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
                Require(SetupElevation.Code(new SetupOperationException(UninstallPhase.ReadState,cause))==code);
            return Task.CompletedTask;
        });
        await check("authenticated result serializes bounded numeric diagnostics without private exception text",()=>
        {
            const string secret="C:\\private-family\\secret-connection.json";
            var failure=new SetupOperationException(UninstallPhase.ProtectRemovalReceipt,new Win32Exception(87,secret));
            var result=new SetupResult(false,SetupElevation.Code(failure),DataPreserved:false,Failure:SetupFailureDetail.From(failure));
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new SetupWire(1,"result",null,result));
            var text=System.Text.Encoding.UTF8.GetString(bytes);
            Require(!text.Contains("private-family",StringComparison.Ordinal)&&!text.Contains("secret-connection",StringComparison.Ordinal));
            var copy=JsonSerializer.Deserialize<SetupWire>(bytes)!;
            Require(copy.Result is {Ok:false,DataPreserved:false,Failure:{Phase:"ProtectRemovalReceipt",NativeError:87}});
            var copiedDetail=copy.Result?.Failure??throw new IOException("FAILURE_DETAIL_MISSING");
            Require(copiedDetail.Display().Contains("Windows 87",StringComparison.Ordinal));
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

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yahad.Install;
using Yahad.Setup;

internal static class ReceiptRecoveryTests
{
    private static void Require(bool value){if(!value)throw new IOException("RECEIPT_RECOVERY_REGRESSION");}
    private static void Refuse(Action action)
    {
        try{action();}catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception){return;}
        throw new IOException("RECEIPT_RECOVERY_ACCEPTED");
    }
    private static InstallerState Trusted()
    {
        var files=new[]{new InstallMember("host/Yahad.ServiceHost.exe",5,new string('d',64))}
            .Concat(Enumerable.Range(0,9).Select(i=>new InstallMember("payload/file"+i,1,new string('e',64)))).ToArray();
        return new(1,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),new string('a',64),new string('b',64),new string('c',40),"S-1-5-21-1-2-3-1001",47123,"committed",files,"S-1-5-80-1-2-3-4-5",new string('f',64));
    }
    private static OwnerProcess Owner(InstallerState state)=>new(state.OwnerSid,123,456,new string('0',64));
    private static ReceiptRecoveryEvidence Proof(InstallerState state)=>new(state.ServiceSid,true,new string('d',64),5,state.SetupSha256);
    private static string LegacyAcl=>"O:BAD:P(A;;0x1301bf;;;SY)(A;;0x1301bf;;;BA)(A;;0x1200a9;;;BU)(A;;FA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)(A;;0x1200a9;;;S-1-15-2-1)(A;;0x1200a9;;;S-1-15-2-2)";
    private static string RawLegacyAcl=>LegacyAcl+"(A;OICIIO;GA;;;CO)(A;OICIIO;GA;;;SY)(A;OICIIO;GA;;;BA)(A;OICIIO;GXGR;;;BU)(A;CIIO;GA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)(A;OICIIO;GXGR;;;S-1-15-2-1)(A;OICIIO;GXGR;;;S-1-15-2-2)";
    private static RawSecurityDescriptor RawFileAcl(string path)
    {
        using var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var status=GetSecurityInfo(held.SafeFileHandle,1,5,out _,out _,out _,out _,out var descriptor);if(status!=0)throw new System.ComponentModel.Win32Exception((int)status);
        try{var length=GetSecurityDescriptorLength(descriptor);Require(length>0&&length<=65536);var bytes=new byte[length];Marshal.Copy(descriptor,bytes,0,bytes.Length);return new RawSecurityDescriptor(bytes,0);}finally{LocalFree(descriptor);}
    }
    private static void RequireProtectedReceipt(string path,string owner,string service)
    {
        var acl=RawFileAcl(path);Require(acl.Owner!.Value==owner&&(acl.ControlFlags&ControlFlags.DiscretionaryAclProtected)!=0&&acl.DiscretionaryAcl!.Count==3);
        var expected=new HashSet<(string,int)>{("S-1-5-18",0x1f01ff),(owner,0x1f01ff),(service,0x1200a9)};
        foreach(GenericAce raw in acl.DiscretionaryAcl!)Require(raw is CommonAce ace&&!ace.IsCallback&&ace.AceQualifier==AceQualifier.AccessAllowed&&ace.AceFlags==AceFlags.None&&expected.Remove((ace.SecurityIdentifier.Value,ace.AccessMask)));
        Require(expected.Count==0);
    }
    internal static async Task RunAsync(Func<string,Func<Task>,Task> check)
    {
        await check("uninstall receipt is protected at creation and replacement under the legacy Modify parent",()=>
        {
            var root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());var parent=Path.Combine(root,"program-files-analogue");var sid=WindowsIdentity.GetCurrent().User!.Value;const string service="S-1-5-80-1-2-3-4-5";
            NativeInstallFiles.NewDirectory(parent,RawLegacyAcl.Replace("O:BA","O:"+sid).Replace(";;;BA)",";;;"+sid+")"));
            var path=Path.Combine(parent,"receipt.json");var descriptor=NativeInstallFiles.Descriptor(service).Replace("O:S-1-5-32-544","O:"+sid).Replace(";;;BA)",";;;"+sid+")");
            foreach(var text in new[]{"synthetic first intent","synthetic resumed intent"})
            {
                NativeInstallFiles.PublishProtectedFile(path,Encoding.UTF8.GetBytes(text),descriptor);
                RequireProtectedReceipt(path,sid,service); // Before any later ACL setter.
                NativeInstallFiles.SetOwnedFile(path,descriptor);
                RequireProtectedReceipt(path,sid,service);Require(File.ReadAllText(path)==text);
                Require(Directory.GetFiles(parent,"receipt.json.partial-*").Length==0);
            }
            return Task.CompletedTask;
        });
        await check("protected receipt publication refuses bad descriptors and preserves locked original before retry",()=>
        {
            var fixture=SafeDirectory.CreatePrivateSession(Path.GetTempPath());var root=Path.Combine(fixture,"program-files-analogue");var path=Path.Combine(root,"receipt.json");var sid=WindowsIdentity.GetCurrent().User!.Value;const string service="S-1-5-80-1-2-3-4-5";
            NativeInstallFiles.NewDirectory(root,RawLegacyAcl.Replace("O:BA","O:"+sid).Replace(";;;BA)",";;;"+sid+")"));
            var descriptor=NativeInstallFiles.Descriptor(service).Replace("O:S-1-5-32-544","O:"+sid).Replace(";;;BA)",";;;"+sid+")");
            NativeInstallFiles.PublishProtectedFile(path,"original"u8.ToArray(),descriptor);
            foreach(var invalid in new[]{descriptor.Replace("D:P","D:"),descriptor.Replace("O:"+sid,""),"O:"+sid})
                Refuse(()=>NativeInstallFiles.PublishProtectedFile(path,"invalid"u8.ToArray(),invalid));
            Require(File.ReadAllText(path)=="original");
            using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                Refuse(()=>NativeInstallFiles.PublishProtectedFile(path,"locked replacement"u8.ToArray(),descriptor));
                Require(File.ReadAllText(path)=="original"&&Directory.GetFiles(root,"receipt.json.partial-*").Length==0);
                RequireProtectedReceipt(path,sid,service);
            }
            NativeInstallFiles.PublishProtectedFile(path,"retry"u8.ToArray(),descriptor);
            Require(File.ReadAllText(path)=="retry");RequireProtectedReceipt(path,sid,service);
            Require(Directory.GetFiles(root,"receipt.json.partial-*").Length==0);return Task.CompletedTask;
        });
        await check("raw legacy receipt admits only the observed thirteen exact effective and propagation ACE tuples",()=>
        {
            WindowsFileBoundary.ValidateInterruptedReceiptAcl(new RawSecurityDescriptor(RawLegacyAcl));
            Refuse(()=>WindowsFileBoundary.ValidateInstallerMetadataAcl(new RawSecurityDescriptor(RawLegacyAcl)));
            foreach(var changed in new[]{RawLegacyAcl.Replace("O:BA","O:SY"),RawLegacyAcl.Replace("D:P","D:"),RawLegacyAcl.Replace("(A;OICIIO;GA;;;CO)",""),RawLegacyAcl+"(A;OICIIO;GA;;;CO)",RawLegacyAcl.Replace("GA;;;CO","GR;;;CO"),RawLegacyAcl.Replace("OICIIO;GA;;;SY","OICI;GA;;;SY"),RawLegacyAcl.Replace("OICIIO;GXGR;;;BU","OICIIO;GA;;;BU"),RawLegacyAcl.Replace("CIIO;GA;;;S-1-5-80-956","OICIIO;GA;;;S-1-5-80-956"),RawLegacyAcl.Replace("0x1301bf;;;SY","FA;;;SY"),RawLegacyAcl.Replace("GA;;;CO","GA;;;BA"),RawLegacyAcl.Replace("A;OICIIO;GA;;;CO","D;OICIIO;GA;;;CO"),RawLegacyAcl.Replace("OICIIO;GA;;;CO","OICIIOID;GA;;;CO")})
                Refuse(()=>WindowsFileBoundary.ValidateInterruptedReceiptAcl(new RawSecurityDescriptor(changed)));
            var callback=new RawSecurityDescriptor(RawLegacyAcl);callback.DiscretionaryAcl![6]=new CommonAce(AceFlags.ObjectInherit|AceFlags.ContainerInherit|AceFlags.InheritOnly,AceQualifier.AccessAllowed,0x10000000,new SecurityIdentifier("S-1-3-0"),true,Array.Empty<byte>());
            Refuse(()=>WindowsFileBoundary.ValidateInterruptedReceiptAcl(callback));return Task.CompletedTask;
        });
        await check("native receipt descriptor retains thirteen ACEs while FileSecurity presents six effective ACEs",()=>
        {
            var root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());var path=Path.Combine(root,"receipt.json");var sid=WindowsIdentity.GetCurrent().User!.Value;
            var analogue=RawLegacyAcl.Replace("O:BA","O:"+sid).Replace(";;;BA)",";;;"+sid+")");
            using(var created=NativeInstallFiles.CreateNewFile(path,analogue)){created.Write("synthetic no private data"u8);created.Flush(true);}
            using var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            var status=GetSecurityInfo(held.SafeFileHandle,1,5,out _,out _,out _,out _,out var descriptor);if(status!=0)throw new System.ComponentModel.Win32Exception((int)status);
            RawSecurityDescriptor raw;
            try{var length=GetSecurityDescriptorLength(descriptor);Require(length>0&&length<=65536);var bytes=new byte[length];Marshal.Copy(descriptor,bytes,0,bytes.Length);raw=new RawSecurityDescriptor(bytes,0);}finally{LocalFree(descriptor);}
            var managed=new RawSecurityDescriptor(new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm(),0);
            Require(raw.DiscretionaryAcl!.Count==13&&managed.DiscretionaryAcl!.Count==6&&raw.Owner!.Value==sid&&(raw.ControlFlags&ControlFlags.DiscretionaryAclProtected)!=0);
            var comparison=new RawSecurityDescriptor(raw.GetSddlForm(AccessControlSections.Owner|AccessControlSections.Access).Replace(sid,"S-1-5-32-544"));
            WindowsFileBoundary.ValidateInterruptedReceiptAcl(comparison);
            Refuse(()=>WindowsFileBoundary.ValidateInterruptedReceiptAcl(raw));
            Require(File.ReadAllText(path)=="synthetic no private data");return Task.CompletedTask;
        });
        await check("recovery diagnostics distinguish fixed ACL identity and digest refusals without authorizing a write",()=>
        {
            static void Expect(ReceiptRecoveryCheck expected,Action action)
            {
                try{action();}catch(ReceiptRecoveryRefusal refused){Require(refused.Check==expected&&refused.Message=="UNINSTALL_RECEIPT_RECOVERY_REFUSED");return;}
                throw new IOException("RECEIPT_DIAGNOSTIC_ACCEPTED");
            }
            var trusted=Trusted();var pending=trusted with{Phase="removing_all_data"};var writes=0;
            Expect(ReceiptRecoveryCheck.ReceiptOwnerOrDacl,()=>WindowsFileBoundary.ValidateInterruptedReceiptAcl(new RawSecurityDescriptor(LegacyAcl.Replace("O:BA","O:SY"))));
            Expect(ReceiptRecoveryCheck.ReceiptAce,()=>WindowsFileBoundary.ValidateInterruptedReceiptAcl(new RawSecurityDescriptor(LegacyAcl.Replace("0x1301bf;;;SY","FA;;;SY"))));
            Expect(ReceiptRecoveryCheck.StateAuthority,()=>UninstallReceiptRecovery.Protect(trusted,pending,Owner(trusted) with{Sid="S-1-5-21-9-9-9-1001"},Proof(trusted),()=>writes++,()=>pending,()=>{}));
            Expect(ReceiptRecoveryCheck.ReceiptIdentity,()=>UninstallReceiptRecovery.Protect(trusted,pending with{DataId=Guid.NewGuid().ToString("N")},Owner(trusted),Proof(trusted),()=>writes++,()=>pending,()=>{}));
            Expect(ReceiptRecoveryCheck.HostDigest,()=>UninstallReceiptRecovery.Protect(trusted,pending,Owner(trusted),Proof(trusted) with{HostBytes=6},()=>writes++,()=>pending,()=>{}));
            Expect(ReceiptRecoveryCheck.SetupDigest,()=>UninstallReceiptRecovery.Protect(trusted,pending,Owner(trusted),Proof(trusted) with{SetupSha256=new string('1',64)},()=>writes++,()=>pending,()=>{}));
            Require(writes==0);return Task.CompletedTask;
        });
        await check("repair-only DACL writer restores Modify without requesting ownership and preserves bytes on retry",()=>
        {
            var root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());var path=Path.Combine(root,"receipt.json");
            File.WriteAllText(path,"synthetic no private data");var sid=WindowsIdentity.GetCurrent().User!.Value;
            // Nonadmin owner analogue: exact observed six-ACE masks, with the
            // synthetic owner replacing BA. Real BA ownership is checked by
            // the separate legacy validator and privileged runtime preflight.
            var legacy=LegacyAcl.Replace("O:BA","O:"+sid).Replace("0x1301bf;;;BA","0x1301bf;;;"+sid);
            NativeInstallFiles.SetOwnedFile(path,legacy);
            var before=new FileInfo(path).GetAccessControl();var owner=before.GetOwner(typeof(SecurityIdentifier));var group=before.GetGroup(typeof(SecurityIdentifier));
            var original=File.ReadAllBytes(path);var final=$"O:BAD:P(A;;FA;;;SY)(A;;FA;;;{sid})(A;;0x1200a9;;;S-1-5-80-1-2-3-4-5)";
            // Preserve proof of the old failure, instead of dropping it from
            // the passing suite. No ACL should be changed by that refusal.
            Refuse(()=>NativeInstallFiles.SetOwnedFile(path,final.Replace("O:BA","O:"+sid)));
            Require(new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access)==before.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            // This owner analogue intentionally has the legacy broad readers,
            // so it is not accepted by the ordinary private-file validator.
            using var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            foreach(var attempt in new[]{0,1})
            {
                NativeInstallFiles.ProtectInterruptedReceiptDacl(path,final);
                var after=new FileInfo(path).GetAccessControl();
                Require(owner!.Equals(after.GetOwner(typeof(SecurityIdentifier)))&&group!.Equals(after.GetGroup(typeof(SecurityIdentifier)))&&after.AreAccessRulesProtected);
                // Windows retains the descriptive AutoInherited control bit
                // (D:PAI). Compare every DACL byte, with protection checked
                // above, rather than requiring its textual D:P spelling.
                var actualAcl=new RawSecurityDescriptor(after.GetSecurityDescriptorBinaryForm(),0).DiscretionaryAcl!;
                var expectedAcl=new RawSecurityDescriptor(final).DiscretionaryAcl!;
                var actualBytes=new byte[actualAcl.BinaryLength];actualAcl.GetBinaryForm(actualBytes,0);
                var expectedBytes=new byte[expectedAcl.BinaryLength];expectedAcl.GetBinaryForm(expectedBytes,0);
                Require(actualBytes.SequenceEqual(expectedBytes));
                held.Position=0;var bytes=new byte[original.Length];held.ReadExactly(bytes);
                Require(held.Length==original.Length&&bytes.SequenceEqual(original));
            }
            return Task.CompletedTask;
        });
        await check("receipt repair requires independent committed state and compares every field before writing",()=>
        {
            var trusted=Trusted();var pending=trusted with{Phase="removing_all_data",Files=[..trusted.Files]};var calls=new List<string>();
            UninstallReceiptRecovery.Protect(trusted,pending,Owner(trusted),Proof(trusted),()=>calls.Add("protect"),()=>{calls.Add("strict-read");return pending;},()=>calls.Add("unchanged"));
            Require(calls.SequenceEqual(new[]{"protect","strict-read","unchanged"}));return Task.CompletedTask;
        });
        await check("wrong missing partial or uncommitted protected state never invokes receipt ACL writer",()=>
        {
            var trusted=Trusted();var pending=trusted with{Phase="removing_all_data"};var writes=0;
            foreach(var phase in new[]{"preparing","reinstalling","uninstalled","removing_all_data","removing_keep_data","unknown"})
                Refuse(()=>UninstallReceiptRecovery.Protect(trusted with{Phase=phase},pending,Owner(trusted),Proof(trusted),()=>writes++,()=>pending,()=>{}));
            Refuse(()=>UninstallReceiptRecovery.Protect(null,pending,Owner(trusted),Proof(trusted),()=>writes++,()=>pending,()=>{}));
            Refuse(()=>UninstallReceiptRecovery.Protect(trusted,null,Owner(trusted),Proof(trusted),()=>writes++,()=>pending,()=>{}));
            Refuse(()=>UninstallReceiptRecovery.Protect(trusted,pending,Owner(trusted) with{Sid="S-1-5-21-9-9-9-1001"},Proof(trusted),()=>writes++,()=>pending,()=>{}));
            Require(writes==0);return Task.CompletedTask;
        });
        await check("altered unrelated receipt identity source hashes mode paths and members are not recovery authority",()=>
        {
            var trusted=Trusted();var pending=trusted with{Phase="removing_all_data"};var writes=0;
            var altered=new[]{pending with{InstallationId=Guid.NewGuid().ToString("N")},pending with{DataId=Guid.NewGuid().ToString("N")},pending with{SourceCommit=new string('1',40)},pending with{PackageDigest=new string('1',64)},pending with{PayloadDigest=new string('1',64)},pending with{SetupSha256=new string('1',64)},pending with{Port=47124},pending with{PayloadSchema=6},pending with{OwnerSid="S-1-5-21-9-9-9-1001"},pending with{ServiceSid="S-1-5-80-9-9-9-9-9"},pending with{Phase="removing_keep_data"},pending with{Files=[new("../../unrelated",1,new string('1',64)),..pending.Files.Skip(1)]},pending with{Files=[pending.Files[0] with{Sha256=new string('1',64)},..pending.Files.Skip(1)]},pending with{Files=null!}};
            foreach(var candidate in altered)Refuse(()=>UninstallReceiptRecovery.Protect(trusted,candidate,Owner(trusted),Proof(trusted),()=>writes++,()=>candidate,()=>{}));
            Require(writes==0);return Task.CompletedTask;
        });
        await check("unowned running or unclean service and changed installed binaries refuse before ACL write",()=>
        {
            var trusted=Trusted();var pending=trusted with{Phase="removing_all_data"};var proof=Proof(trusted);var writes=0;
            foreach(var bad in new[]{proof with{ServiceSid="S-1-5-80-9-9-9-9-9"},proof with{ServiceOwnedAndCleanStopped=false},proof with{HostSha256=new string('1',64)},proof with{HostBytes=6},proof with{SetupSha256=new string('1',64)}})
                Refuse(()=>UninstallReceiptRecovery.Protect(trusted,pending,Owner(trusted),bad,()=>writes++,()=>pending,()=>{}));
            Require(writes==0);return Task.CompletedTask;
        });
        await check("receipt repair interruptions and post-write changes never report successful validation",()=>
        {
            var trusted=Trusted();var pending=trusted with{Phase="removing_all_data"};var reads=0;var unchanged=0;
            Refuse(()=>UninstallReceiptRecovery.Protect(trusted,pending,Owner(trusted),Proof(trusted),()=>throw new IOException("synthetic ACL failure"),()=>{reads++;return pending;},()=>unchanged++));
            Require(reads==0&&unchanged==0);
            // A refused native write must not create a success state or
            // prevent a later independently validated retry.
            var retries=0;UninstallReceiptRecovery.Protect(trusted,pending,Owner(trusted),Proof(trusted),()=>retries++,()=>{reads++;return pending;},()=>unchanged++);
            Require(retries==1&&reads==1&&unchanged==1);unchanged=0;
            Refuse(()=>UninstallReceiptRecovery.Protect(trusted,pending,Owner(trusted),Proof(trusted),()=>{},()=>pending with{DataId=Guid.NewGuid().ToString("N")},()=>unchanged++));
            Require(unchanged==0);
            Refuse(()=>UninstallReceiptRecovery.Protect(trusted,pending,Owner(trusted),Proof(trusted),()=>{},()=>pending,()=>throw new IOException("synthetic content changed")));
            return Task.CompletedTask;
        });
        await check("legacy comparison accepts only the observed six-ACE Modify template while normal metadata refuses it",()=>
        {
            WindowsFileBoundary.ValidateInterruptedReceiptAcl(new RawSecurityDescriptor(LegacyAcl));
            Refuse(()=>WindowsFileBoundary.ValidateInstallerMetadataAcl(new RawSecurityDescriptor(LegacyAcl)));
            foreach(var bad in new[]{LegacyAcl.Replace("D:P","D:"),LegacyAcl.Replace("O:BA","O:SY"),LegacyAcl.Replace("0x1200a9;;;BU","FA;;;BU"),LegacyAcl.Replace("(A;;0x1301bf;;;SY)","(A;;0x1301bf;;;BA)"),LegacyAcl+"(A;;FR;;;WD)",LegacyAcl.Replace("(A;;0x1301bf;;;SY)","(A;ID;0x1301bf;;;SY)"),LegacyAcl.Replace("S-1-15-2-2","S-1-15-2-3"),LegacyAcl.Replace("0x1301bf","FA"),LegacyAcl.Replace("0x1301bf;;;SY","FA;;;SY"),LegacyAcl.Replace("0x1301bf;;;BA","FA;;;BA"),LegacyAcl.Replace("0x1301bf;;;SY","0x1301be;;;SY"),LegacyAcl.Replace("0x1301bf;;;BA","0x1701bf;;;BA"),LegacyAcl.Replace("(A;;0x1301bf;;;SY)","")})
                Refuse(()=>WindowsFileBoundary.ValidateInterruptedReceiptAcl(new RawSecurityDescriptor(bad)));
            var callback=new RawSecurityDescriptor(LegacyAcl);
            callback.DiscretionaryAcl![0]=new CommonAce(AceFlags.None,AceQualifier.AccessAllowed,0x1301bf,new SecurityIdentifier("S-1-5-18"),true,Array.Empty<byte>());
            Refuse(()=>WindowsFileBoundary.ValidateInterruptedReceiptAcl(callback));
            return Task.CompletedTask;
        });
        await check("installed-binary comparison never accepts writable service or owner grants",()=>
        {
            var service=new SecurityIdentifier("S-1-5-80-1-2-3-4-5");var owner=new SecurityIdentifier("S-1-5-21-1-2-3-1001");
            var acl=NativeInstallFiles.Descriptor(service.Value,ownerSid:owner.Value).Replace("OICI","");
            WindowsFileBoundary.ValidateInstallerBinaryAcl(new RawSecurityDescriptor(acl),service,owner);
            Refuse(()=>WindowsFileBoundary.ValidateInstallerBinaryAcl(new RawSecurityDescriptor(acl.Replace("0x1200a9","FA")),service,owner));
            Refuse(()=>WindowsFileBoundary.ValidateInstallerBinaryAcl(new RawSecurityDescriptor(acl+"(A;;FR;;;WD)"),service,owner));return Task.CompletedTask;
        });
        await check("repair result stays bound to original authenticated action without install LAN or keep-data substitution",()=>
        {
            Require(SetupElevation.ActionName(true,true,UninstallMode.DeleteData,null)=="repair-uninstall-receipt");
            Refuse(()=>SetupElevation.ActionName(false,true,UninstallMode.DeleteData,null));
            Refuse(()=>SetupElevation.ActionName(true,true,UninstallMode.KeepData,null));
            Refuse(()=>SetupElevation.ActionName(true,true,UninstallMode.DeleteData,new(LanSetupAction.Disable)));
            Require(SetupElevation.ActionName(true,false,UninstallMode.DeleteData,null)=="uninstall");return Task.CompletedTask;
        });
        await check("repair-only failure reports preserved data while full-removal failure remains uncertain",()=>
        {
            var failure=new System.ComponentModel.Win32Exception(5);
            Require(SetupElevation.FailureResult(failure,UninstallMode.DeleteData,true) is{Ok:false,DataPreserved:true,Code:"ACCESS_DENIED"});
            Require(SetupElevation.FailureResult(failure,UninstallMode.DeleteData,false) is{Ok:false,DataPreserved:false});
            return Task.CompletedTask;
        });
        await check("legacy receipt comparison rejects hardlinks before trusting any JSON or ACL",()=>
        {
            var root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());var file=Path.Combine(root,"receipt.json");File.WriteAllText(file,"synthetic");
            var alias=Path.Combine(root,"alias.json");if(!CreateHardLinkW(alias,file,IntPtr.Zero))throw new System.ComponentModel.Win32Exception();
            var rejected=false;try{using var lease=WindowsFileBoundary.OpenInterruptedReceiptForComparison(file);}catch(IOException e)when(e.Message=="NATIVE_REPARSE_KIND_OR_HARDLINK"){rejected=true;}
            Require(rejected&&File.Exists(alias));return Task.CompletedTask;
        });
        await check("legacy receipt comparison refuses a junction ancestor without reading its synthetic target",async()=>
        {
            var root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());var target=SafeDirectory.CreatePrivateSession(Path.GetTempPath());
            File.WriteAllText(Path.Combine(target,"receipt.json"),"synthetic");var junction=Path.Combine(root,"junction");
            var start=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{"/c","mklink","/J",junction,target})start.ArgumentList.Add(arg);
            using var child=Process.Start(start)??throw new IOException("JUNCTION_FIXTURE_NOT_STARTED");using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(30));await child.WaitForExitAsync(limit.Token);Require(child.ExitCode==0);
            var rejected=false;try{using var lease=WindowsFileBoundary.OpenInterruptedReceiptForComparison(Path.Combine(junction,"receipt.json"));}catch(IOException e)when(e.Message=="NATIVE_REPARSE_KIND_OR_HARDLINK"){rejected=true;}
            Require(rejected&&File.ReadAllText(Path.Combine(target,"receipt.json"))=="synthetic");
        });
        await check("read-only receipt lease stays held across ACL protection and unchanged-byte verification",()=>
        {
            var root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());var path=Path.Combine(root,"receipt.json");File.WriteAllText(path,"synthetic no private data");var sid=WindowsIdentity.GetCurrent().User!.Value;
            using var lease=WindowsFileBoundary.OpenPrivateRead(path);var digest=lease.Sha256(8192);
            NativeInstallFiles.ProtectInterruptedReceiptDacl(path,$"O:{sid}D:P(A;;FA;;;{sid})(A;;FA;;;SY)(A;;FA;;;BA)");
            Require(lease.Sha256(8192)==digest&&Encoding.UTF8.GetString(lease.Small(8192))=="synthetic no private data");return Task.CompletedTask;
        });
    }
    internal static Task RenderAsync(string output)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try
            {
                var app=new App();app.InitializeComponent();
                foreach(var size in new[]{new Size(800,700),new Size(760,620)})
                {
                    var window=new ReceiptRepairWindow("S-1-5-80-1-2-3-4-5");var root=(FrameworkElement)window.Content;
                    root.Width=size.Width;root.Height=size.Height;root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout();
                    var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file=File.Create(Path.Combine(output,"receipt-repair-"+size.Width+".png"));encoder.Save(file);
                    Require(root.FlowDirection==FlowDirection.RightToLeft||window.FlowDirection==FlowDirection.RightToLeft);
                }
                done.SetResult();
            }
            catch(Exception e){done.SetException(e);}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CreateHardLinkW(string link,string target,IntPtr security);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern uint GetSecurityInfo(Microsoft.Win32.SafeHandles.SafeFileHandle handle,uint type,uint flags,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr value);
}

using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Yahad.Install;

internal static class InstallAclTests
{
    private const string Service="S-1-5-80-1-2-3-4-5";
    private static void Require(bool value){if(!value)throw new Exception("INSTALL_ACL_REGRESSION");}
    private static void Refused(Action action,string code)
    {
        try{action();}catch(IOException error)when(error.Message==code){return;}
        throw new Exception("NOT_REFUSED:"+code);
    }
    private static void Junction(string link,string target)
    {
        using var process=Process.Start(new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,ArgumentList={"/c","mklink","/J",link,target}})!;
        process.WaitForExit();if(process.ExitCode!=0)throw new Exception("JUNCTION_NOT_CREATED");
    }
    private static int ServiceRights(string sddl)
    {
        var acl=new RawSecurityDescriptor(sddl);
        Require((acl.ControlFlags&ControlFlags.DiscretionaryAclProtected)!=0);
        var aces=acl.DiscretionaryAcl!.Cast<CommonAce>().ToArray();
        Require(aces.All(a=>a.AceQualifier==AceQualifier.AccessAllowed&&(a.AceFlags&(AceFlags.ObjectInherit|AceFlags.ContainerInherit))==(AceFlags.ObjectInherit|AceFlags.ContainerInherit)));
        // Only SYSTEM, Administrators and the one service: Users have no access at all.
        // An owner, when the ACL names one, gets exactly what the service gets.
        const string OwnerRights="S-1-3-4";
        Require(aces.Select(a=>a.SecurityIdentifier.Value).Where(sid=>sid!=OwnerRights).Order().SequenceEqual(new[]{"S-1-5-18","S-1-5-32-544",Service}.Order()));
        var rights=aces.Single(a=>a.SecurityIdentifier.Value==Service).AccessMask;
        Require(aces.Where(a=>a.SecurityIdentifier.Value==OwnerRights).All(a=>a.AccessMask==rights));
        return rights;
    }
    internal static async Task RunAsync(Func<string,Func<Task>,Task> check)
    {
        await check("the install ACL gives the service no way to change anything and the data ACL lets it modify",()=>
        {
            const int Write=0x2|0x4|0x10|0x100,Takeover=0x10000|0x40|0x40000|0x80000;
            var install=ServiceRights(NativeInstallFiles.InstallAcl(Service));Require((install&(Write|Takeover))==0&&(install&0x1)!=0);
            var data=ServiceRights(NativeInstallFiles.DataAcl(Service));Require((data&Write)==Write&&(data&0x10000)!=0&&(data&(0x40|0x40000|0x80000))==0);
            return Task.CompletedTask;
        });
        await check("directories and files made below a root inherit its ACL and carry none of their own",()=>
        {
            var parent=PrivateSession.Create(Path.GetTempPath());var root=Path.Combine(parent,"root");var user=WindowsIdentity.GetCurrent().User!.Value;
            NativeInstallFiles.NewDirectory(root,$"O:{user}D:P(A;OICI;FA;;;{user})(A;OICI;FA;;;SY)(A;OICI;0x1200a9;;;{Service})");
            var folder=Path.Combine(root,"folder");NativeInstallFiles.NewDirectory(folder);
            var record=Path.Combine(folder,"record.json");NativeInstallFiles.Publish(record,"first"u8.ToArray());NativeInstallFiles.Publish(record,"second"u8.ToArray());
            foreach(var security in new FileSystemSecurity[]{new DirectoryInfo(folder).GetAccessControl(),new FileInfo(record).GetAccessControl()})
            {
                Require(!security.AreAccessRulesProtected);
                var rules=security.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
                Require(rules.All(r=>r.IsInherited)&&rules.Any(r=>r.IdentityReference.Value==Service));
            }
            Directory.Delete(parent,true);return Task.CompletedTask;
        });
        await check("what the service creates in its data it can modify and delete but not re-permission",()=>
        {
            var parent=PrivateSession.Create(Path.GetTempPath());var root=Path.Combine(parent,"data");var user=WindowsIdentity.GetCurrent().User!.Value;
            NativeInstallFiles.NewDirectory(root,NativeInstallFiles.DataAcl(user).Replace("O:BA",$"O:{user}").Replace("(A;OICI;FA;;;BA)",""));
            var folder=Path.Combine(root,"folder");Directory.CreateDirectory(folder);var file=Path.Combine(folder,"file");File.WriteAllText(file,"first");File.AppendAllText(file,"second");
            foreach(var change in new Action[]{()=>{var security=new DirectoryInfo(folder).GetAccessControl();security.SetAccessRuleProtection(true,true);new DirectoryInfo(folder).SetAccessControl(security);},()=>{var security=new FileInfo(file).GetAccessControl();security.SetAccessRuleProtection(true,true);new FileInfo(file).SetAccessControl(security);}})
            {
                var refused=false;try{change();}catch(UnauthorizedAccessException){refused=true;}
                Require(refused);
            }
            File.Delete(file);Directory.Delete(folder);Directory.Delete(parent,true);return Task.CompletedTask;
        });
        await check("a state root a user could have squatted in ProgramData is refused and Setup's own is accepted",()=>
        {
            var parent=PrivateSession.Create(Path.GetTempPath());var user=WindowsIdentity.GetCurrent().User!;
            var squat=Path.Combine(parent,"squat");NativeInstallFiles.NewDirectory(squat,$"O:{user.Value}D:P(A;OICI;FA;;;{user.Value})");
            Refused(()=>WindowsFileBoundary.RequireAdministratorOwned(squat),"INSTALL_ROOT_OWNER");
            Require(WindowsFileBoundary.IsAdministrativeOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null))&&WindowsFileBoundary.IsAdministrativeOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)));
            Require(!WindowsFileBoundary.IsAdministrativeOwner(user)&&!WindowsFileBoundary.IsAdministrativeOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null))&&!WindowsFileBoundary.IsAdministrativeOwner(null));
            var linked=Path.Combine(parent,"linked");Junction(linked,squat);
            Refused(()=>WindowsFileBoundary.RequireAdministratorOwned(linked),"NATIVE_REPARSE_KIND_OR_HARDLINK");
            Directory.Delete(linked);Directory.Delete(parent,true);return Task.CompletedTask;
        });
        await check("a junction left in a tree the service can write stops inspection and is never followed by a delete",()=>
        {
            // Elevated Setup and SYSTEM delete through data the Runtime account can write.
            var root=PrivateSession.Create(Path.GetTempPath());var outside=PrivateSession.Create(Path.GetTempPath());var kept=Path.Combine(outside,"kept");File.WriteAllText(kept,"synthetic");
            var nested=Path.Combine(root,"nested");Directory.CreateDirectory(nested);File.WriteAllText(Path.Combine(nested,"member"),"synthetic");
            var link=Path.Combine(nested,"link");Junction(link,outside);
            Refused(()=>Yahad.Setup.RemovalTree.Inspect(root,WindowsFileBoundary.RequireNoLink),"UNINSTALL_LINK_PRESERVED");
            Refused(()=>WindowsFileBoundary.DeleteNoFollow(link,true),"NATIVE_REPARSE_KIND_OR_HARDLINK");
            Refused(()=>WindowsFileBoundary.DeleteNoFollow(kept.Replace(outside,link),false),"NATIVE_REPARSE_KIND_OR_HARDLINK");
            Refused(()=>{using var read=WindowsFileBoundary.OpenRead(kept.Replace(outside,link));},"NATIVE_REPARSE_KIND_OR_HARDLINK");
            Require(File.ReadAllText(kept)=="synthetic"&&File.Exists(Path.Combine(nested,"member")));
            Directory.Delete(link);Directory.Delete(root,true);Directory.Delete(outside,true);return Task.CompletedTask;
        });
    }
}

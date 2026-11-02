using System.Security.AccessControl;
using System.Security.Principal;
using Yahad.Install;

internal static class UpdaterServiceIdentityTests
{
    private static void Assert(bool value){if(!value)throw new IOException("UPDATER_IDENTITY_ASSERTION");}
    private static void Refuse(Action action){try{action();}catch(Exception e)when(e is IOException or UnauthorizedAccessException){return;}throw new IOException("UPDATER_IDENTITY_UNEXPECTED_ADMISSION");}
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        foreach(var actor in Enum.GetValues<UpdaterServiceActor>())
        await check("fixed updater identity accepts only own account, image, SCM generation "+actor,()=>
        {
            var account=@"NT SERVICE\"+UpdaterServiceIdentity.Name(actor);var command=UpdaterServiceIdentity.Command(actor);
            UpdaterServiceIdentity.RequireObserved(actor,account,command,16,2,4,77,77);
            UpdaterServiceIdentity.RequireObserved(actor,account.ToLowerInvariant(),command,16,2,2,77,77);
            UpdaterServiceIdentity.RequireObserved(actor,account,command,16,2,1,0,null,true);
            foreach(var other in Enum.GetValues<UpdaterServiceActor>().Where(x=>x!=actor))
            {
                Refuse(()=>UpdaterServiceIdentity.RequireObserved(actor,@"NT SERVICE\"+UpdaterServiceIdentity.Name(other),command,16,2,4,77,77));
                Refuse(()=>UpdaterServiceIdentity.RequireObserved(actor,account,UpdaterServiceIdentity.Command(other),16,2,4,77,77));
            }
            Refuse(()=>UpdaterServiceIdentity.RequireObserved(actor,account,command,32,2,4,77,77));
            Refuse(()=>UpdaterServiceIdentity.RequireObserved(actor,account,command,16,3,4,77,77));
            Refuse(()=>UpdaterServiceIdentity.RequireObserved(actor,account,command,16,2,4,78,77));
            Refuse(()=>UpdaterServiceIdentity.RequireObserved(actor,account,command,16,2,1,0,77));
            Refuse(()=>UpdaterServiceIdentity.RequireObserved(actor,account,command,16,2,4,77,null,true));
            return Task.CompletedTask;
        });
        await check("full roles admit two compiled helpers without widening app-only or Runtime",()=>
        {
            var authority=new SecurityIdentifier("S-1-5-80-6-7-8-9-10");var runtime=new SecurityIdentifier("S-1-5-80-1-2-3-4-5");
            var normal=new InstalledFileRoles(authority,runtime);var full=normal.WithRecoveryHelpers();
            foreach(var helper in new[]{UpdaterServicePrincipals.RecoveryA,UpdaterServicePrincipals.RecoveryB})
            {
                var allowed=new RawSecurityDescriptor($"O:{helper.Value}D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1301bf;;;{helper.Value})");
                full.Validate(allowed,InstalledFileRole.Control,false);Refuse(()=>normal.Validate(allowed,InstalledFileRole.Control,false));
                Refuse(()=>full.Validate(allowed,InstalledFileRole.Anchor,false));
                Refuse(()=>full.Validate(allowed,InstalledFileRole.Data,false));
                Refuse(()=>full.Validate(new RawSecurityDescriptor($"O:BAD:P(A;;FA;;;{helper.Value})"),InstalledFileRole.Control,false));
                Refuse(()=>new InstalledFileRoles(helper,runtime).WithRecoveryHelpers().Validate(allowed,InstalledFileRole.Control,false));
            }
            Assert(!normal.IsUpdater(UpdaterServicePrincipals.RecoveryA)&&full.UpdaterActors.Length==3&&!full.IsUpdater(runtime));
            Refuse(()=>UpdaterServiceIdentity.FromUser(runtime,authority));
            Refuse(()=>UpdaterServiceIdentity.FromUser(new("S-1-5-80-9-9-9-9-9"),authority));
            return Task.CompletedTask;
        });
        await check("restore staging alone permits DACL publication and exact final ACL detection preserves owner",()=>
        {
            var roles=new InstalledFileRoles(new("S-1-5-80-6-7-8-9-10"),new("S-1-5-80-1-2-3-4-5")).WithRecoveryHelpers();
            var scoped="O:"+UpdaterServicePrincipals.RecoveryA.Value+"D:P(A;;FA;;;SY)(A;;FA;;;BA)"+
                string.Concat(roles.UpdaterActors.Select(s=>$"(A;;0x1701bf;;;{s.Value})"));
            roles.Validate(new(scoped),InstalledFileRole.RecoveryStaging,false);
            foreach(var ordinary in Enum.GetValues<InstalledFileRole>().Where(r=>r!=InstalledFileRole.RecoveryStaging))
                Refuse(()=>roles.Validate(new(scoped),ordinary,false));
            Refuse(()=>roles.Validate(new(scoped.Replace("0x1701bf","0x1f01bf")),InstalledFileRole.RecoveryStaging,false));
            Refuse(()=>roles.Validate(new(scoped+$"(A;;0x1200a9;;;{roles.Runtime.Value})"),InstalledFileRole.RecoveryStaging,false));
            var final=scoped.Replace("0x1701bf","0x1200a9")+$"(A;;0x1301bf;;;{roles.Runtime.Value})";
            roles.Validate(new(final),InstalledFileRole.Data,false);
            Assert(!WindowsFileBoundary.SameRoleAccess(new(scoped),new(final)));
            Assert(WindowsFileBoundary.SameRoleAccess(new(final),new(final.Replace(UpdaterServicePrincipals.RecoveryA.Value+"D:",UpdaterServicePrincipals.RecoveryB.Value+"D:"))));
            Assert(!WindowsFileBoundary.SameRoleAccess(new(final+$"(A;;0x40000;;;{roles.Authority.Value})"),new(final)));
            Assert(!WindowsFileBoundary.SameRoleAccess(new(final.Replace("D:P","D:")),new(final)));
            return Task.CompletedTask;
        });
    }
}

using System.Security.AccessControl;
using System.Security.Principal;
namespace Yahad.Install;

public enum InstalledFileRole { Anchor, Control, Payload, Data, Lease, Recovery, RecoveryEvidence, MasterKey, RecoveryStaging }
// Roles must come from the immutable installed anchor, never request fields.
public sealed record InstalledFileRoles(SecurityIdentifier Authority,SecurityIdentifier Runtime)
{
    // Set only by full-profile composition after validating the immutable
    // full-system enrollment. This is not a serialized request capability.
    internal bool HasRecoveryHelpers { get; private init; }
    internal InstalledFileRoles WithRecoveryHelpers()=>this with{HasRecoveryHelpers=true};
    internal SecurityIdentifier[] UpdaterActors=>HasRecoveryHelpers?UpdaterServicePrincipals.All(Authority):[Authority];
    internal bool IsUpdater(SecurityIdentifier sid)=>sid==Authority||HasRecoveryHelpers&&UpdaterServicePrincipals.IsRecovery(sid);
    internal int UpdaterMask(InstalledFileRole role)=>role switch
    {
        InstalledFileRole.Anchor or InstalledFileRole.Data or InstalledFileRole.MasterKey=>0x1200a9,
        InstalledFileRole.RecoveryStaging when HasRecoveryHelpers=>0x1701bf, // Modify + WRITE_DAC; no WRITE_OWNER.
        InstalledFileRole.RecoveryStaging=>throw new IOException("RECOVERY_STAGING_ROLE"),
        _=>0x1301bf
    };
    internal static int RuntimeMask(InstalledFileRole role)=>role switch
    {InstalledFileRole.Data or InstalledFileRole.Lease=>0x1301bf,InstalledFileRole.Recovery or InstalledFileRole.RecoveryStaging=>0,_=>0x1200a9};
    public void Validate(RawSecurityDescriptor acl,InstalledFileRole role,bool directory=true)
    {
        var system=new SecurityIdentifier("S-1-5-18");var admins=new SecurityIdentifier("S-1-5-32-544");
        if(!Authority.Value.StartsWith("S-1-5-80-")||!Runtime.Value.StartsWith("S-1-5-80-")||Authority==Runtime||UpdaterServicePrincipals.IsRecovery(Authority)||UpdaterServicePrincipals.IsRecovery(Runtime)||
            acl.Owner is null||acl.DiscretionaryAcl is null||(acl.ControlFlags&ControlFlags.DiscretionaryAclPresent)==0||directory&&(acl.ControlFlags&ControlFlags.DiscretionaryAclProtected)==0)
            throw new IOException("INSTALLED_ROLE_BINDING");
        var administrative=acl.Owner==system||acl.Owner==admins;
        if(!administrative&&!(role!=InstalledFileRole.Anchor&&IsUpdater(acl.Owner))&&!(role is InstalledFileRole.Data or InstalledFileRole.Lease&&acl.Owner==Runtime))throw new IOException("INSTALLED_ROLE_OWNER");
        foreach(GenericAce raw in acl.DiscretionaryAcl)
        {
            if(raw is not CommonAce ace||ace.IsCallback)throw new IOException("INSTALLED_ROLE_ACE");
            if(ace.AceQualifier==AceQualifier.AccessDenied)continue;
            if(ace.AceQualifier!=AceQualifier.AccessAllowed)throw new IOException("INSTALLED_ROLE_ACE");
            if(ace.SecurityIdentifier==system||ace.SecurityIdentifier==admins)continue;
            int allowed=IsUpdater(ace.SecurityIdentifier)?UpdaterMask(role):ace.SecurityIdentifier==Runtime?RuntimeMask(role):0;
            if(allowed==0||(ace.AccessMask&~allowed)!=0)throw new IOException("INSTALLED_ROLE_ACCESS");
        }
    }
}

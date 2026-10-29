using System.Security.AccessControl;
using System.Security.Principal;
namespace Yahad.Install;

public enum InstalledFileRole { Anchor, Control, Payload, Data, Lease, Recovery, MasterKey }
// Roles must come from the immutable installed anchor, never request fields.
public sealed record InstalledFileRoles(SecurityIdentifier Authority,SecurityIdentifier Runtime)
{
    public void Validate(RawSecurityDescriptor acl,InstalledFileRole role)
    {
        var system=new SecurityIdentifier("S-1-5-18");var admins=new SecurityIdentifier("S-1-5-32-544");
        if(!Authority.Value.StartsWith("S-1-5-80-")||!Runtime.Value.StartsWith("S-1-5-80-")||Authority==Runtime||
            acl.Owner is null||acl.DiscretionaryAcl is null||(acl.ControlFlags&ControlFlags.DiscretionaryAclPresent)==0||(acl.ControlFlags&ControlFlags.DiscretionaryAclProtected)==0)
            throw new IOException("INSTALLED_ROLE_BINDING");
        var administrative=acl.Owner==system||acl.Owner==admins;
        if(!administrative&&!(role!=InstalledFileRole.Anchor&&acl.Owner==Authority)&&!(role is InstalledFileRole.Data or InstalledFileRole.Lease&&acl.Owner==Runtime))throw new IOException("INSTALLED_ROLE_OWNER");
        int read=(int)(FileSystemRights.ReadAndExecute|FileSystemRights.Synchronize|FileSystemRights.ReadPermissions);
        int modify=(int)(FileSystemRights.Modify|FileSystemRights.Synchronize);
        foreach(GenericAce raw in acl.DiscretionaryAcl)
        {
            if(raw is not CommonAce ace||ace.IsCallback)throw new IOException("INSTALLED_ROLE_ACE");
            if(ace.AceQualifier==AceQualifier.AccessDenied)continue;
            if(ace.AceQualifier!=AceQualifier.AccessAllowed)throw new IOException("INSTALLED_ROLE_ACE");
            if(ace.SecurityIdentifier==system||ace.SecurityIdentifier==admins)continue;
            int allowed=ace.SecurityIdentifier==Authority?role switch{InstalledFileRole.Anchor or InstalledFileRole.Data or InstalledFileRole.MasterKey=>read,_=>modify}:
                ace.SecurityIdentifier==Runtime?role switch{InstalledFileRole.Data or InstalledFileRole.Lease=>modify,InstalledFileRole.Recovery=>0,_=>read}:0;
            if(allowed==0||(ace.AccessMask&~allowed)!=0)throw new IOException("INSTALLED_ROLE_ACCESS");
        }
    }
}

using System.Security.Principal;

namespace Yahad.Install;

// Fixed product principals, not a group/list accepted from a request or file.
// Windows service SIDs depend on the service name, not a machine/user secret.
// Values verified with Windows sc.exe showsid for these two compiled names.
internal static class UpdaterServicePrincipals
{
    internal static readonly SecurityIdentifier RecoveryA = new("S-1-5-80-47898842-2152575715-4262255954-2619742302-882247756");
    internal static readonly SecurityIdentifier RecoveryB = new("S-1-5-80-179135091-1176045619-1781793314-11882229-2188683259");
    internal static SecurityIdentifier Recovery(RecoveryHelperSlot slot) => slot switch
    {
        RecoveryHelperSlot.A => RecoveryA,
        RecoveryHelperSlot.B => RecoveryB,
        _ => throw new IOException("HELPER_SLOT")
    };
    internal static bool IsRecovery(SecurityIdentifier sid) => sid == RecoveryA || sid == RecoveryB;
    internal static bool IsUpdater(SecurityIdentifier sid, SecurityIdentifier authority) => sid == authority || IsRecovery(sid);
    internal static SecurityIdentifier[] All(SecurityIdentifier authority)
    {
        if (IsRecovery(authority)) throw new IOException("INSTALLED_ROLE_BINDING");
        return [authority, RecoveryA, RecoveryB];
    }
}

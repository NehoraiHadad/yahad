namespace Yahad.Install;

internal enum RecoveryHelperSlot {A,B}
// Initial installer provisions both independent automatic one-shot entries.
// Runtime code has no creation/configuration API. A/B share the SAME Authority
// account/policy/source/journal; cross-registration virtual-account logon still
// requires native installer acceptance and never falls back to LocalSystem.
internal static class BootstrapRecoveryLayout
{
    internal static string ServiceName(RecoveryHelperSlot slot)=>slot switch
    {RecoveryHelperSlot.A=>InstalledUpdaterLayout.AuthorityServiceName+".RecoveryA",RecoveryHelperSlot.B=>InstalledUpdaterLayout.AuthorityServiceName+".RecoveryB",_=>throw new IOException("HELPER_SLOT")};
    internal static string Directory(RecoveryHelperSlot slot)=>Path.Combine(InstalledUpdaterLayout.BinaryRoot,"bootstrap",slot switch
    {RecoveryHelperSlot.A=>"handoff-a",RecoveryHelperSlot.B=>"handoff-b",_=>throw new IOException("HELPER_SLOT")});
    internal static string Image(RecoveryHelperSlot slot)=>Path.Combine(Directory(slot),"Yahad.Authority.exe");
    internal static bool IsHelperImage(string path)=>Enum.GetValues<RecoveryHelperSlot>().Any(slot=>Path.GetFullPath(path).Equals(Image(slot),StringComparison.OrdinalIgnoreCase))||
        Path.GetFullPath(path).Equals(Path.Combine(InstalledUpdaterLayout.BinaryRoot,"bootstrap","handoff","Yahad.Authority.exe"),StringComparison.OrdinalIgnoreCase);
}

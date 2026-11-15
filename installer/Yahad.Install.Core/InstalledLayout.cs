namespace Yahad.Install;

// Fixed machine locations: mutable state under ProgramData, administrator-owned
// binaries under Program Files.
internal static class InstalledLayout
{
    private const string Product="Yahad.FirstInstall";
    internal static string StateRoot=>Path.Combine(MachineFolders.Required(Environment.SpecialFolder.CommonApplicationData),Product);
    internal static string BinaryRoot=>Path.Combine(MachineFolders.Required(Environment.SpecialFolder.ProgramFiles),Product);
    internal static string VersionRoot(string version)=>Path.Combine(BinaryRoot,"versions",version);
    // The Updater's own folder, inside the state root's ACL: the Runtime can read it
    // but cannot write it, so it can never shape what the Updater stages or runs.
    internal static string UpdaterRoot=>Path.Combine(StateRoot,"updater");
    internal static string UpdaterImage(string version)=>Path.Combine(VersionRoot(version),"updater","Yahad.Updater.exe");
    internal static string UpdaterCommand(string version)=>"\""+UpdaterImage(version)+"\" --service";
    // Platform is infrastructure, beside the host and the Updater: the app tree
    // never carries it. The host hands this image to the app as YAHAD_PLATFORM_HELPER.
    internal static string PlatformImage(string version)=>Path.Combine(VersionRoot(version),"platform","Yahad.Platform.exe");
}

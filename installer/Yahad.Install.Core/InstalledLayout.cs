namespace Yahad.Install;

// Fixed machine locations: mutable state under ProgramData, administrator-owned
// binaries under Program Files.
internal static class InstalledLayout
{
    private const string Product="Yahad.FirstInstall";
    internal static string StateRoot=>Path.Combine(MachineFolders.Required(Environment.SpecialFolder.CommonApplicationData),Product);
    internal static string BinaryRoot=>Path.Combine(MachineFolders.Required(Environment.SpecialFolder.ProgramFiles),Product);
}

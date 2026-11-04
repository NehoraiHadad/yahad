namespace Yahad.Install;

// Machine known folders expand %SystemDrive%. Without it in the environment they
// resolve to "", which would silently turn every installed path into a relative one.
internal static class MachineFolders
{
    internal static string Required(Environment.SpecialFolder folder)
    {
        var path=Environment.GetFolderPath(folder);
        if(!Path.IsPathFullyQualified(path))throw new IOException("MACHINE_FOLDER_UNRESOLVED");
        return path;
    }
}

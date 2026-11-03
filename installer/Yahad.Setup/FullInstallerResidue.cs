using System.IO;
using System.Security.Principal;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Setup;
// A replaced installer record is written beside it as <name>.partial-<guid>
// and renamed over it. An interrupted replacement leaves only that partial.
internal static class FullInstallerResidue
{
    internal static readonly string[] Metadata=["installer-state.json","upgrade-state.json",InstalledLaunch.FileName,"lan-preparation.json","setup-capability.json","master-key"];
    internal static bool IsOwnedName(string name)
    {
        foreach(var target in Metadata)
        {
            var prefix=target+".partial-";if(!name.StartsWith(prefix,StringComparison.Ordinal))continue;
            var suffix=name[prefix.Length..];return Guid.TryParseExact(suffix,"N",out var id)&&id.ToString("N")==suffix;
        }
        return false;
    }
    // Deletes only owned partials of the records above, each ACL-checked as an
    // installation member. Any other ".partial-" name is left and refused.
    internal static void Clear(string directory,SecurityIdentifier application)
    {
        if(!Directory.Exists(directory))return;
        var found=Directory.EnumerateFiles(directory,"*.partial-*").Take(129).ToArray();if(found.Length>128)throw new IOException("INSTALL_TARGET_CHANGED");
        foreach(var path in found)
        {if(!IsOwnedName(Path.GetFileName(path)))throw new IOException("INSTALL_TARGET_CHANGED");WindowsFileBoundary.DeleteRemovalMember(path,false,application);}
    }
}

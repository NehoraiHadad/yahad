using System.IO;
using System.Runtime.InteropServices;
using Yahad.Install;
namespace Yahad.Setup;

// Runs only in the original unelevated UI, after authenticated install success.
// Never chooses an administrator's profile when another account approves UAC.
internal static class OwnerShortcuts
{
    internal static string Arguments(string mode,string installation)
    {
        if(mode is not("confirm" or "recover")||!Guid.TryParseExact(installation,"N",out var id)||id.ToString("N")!=installation)throw new IOException("OWNER_SHORTCUT_ARGUMENTS");
        return "--"+mode+" "+installation;
    }
    internal static bool TryCreate(string installation)
    {
        object? shell=null;
        try
        {
            var target=Path.Combine(InstalledUpdaterLayout.BinaryRoot,"bootstrap","owner","Yahad.Owner.exe");SafeDirectory.NoReparseAncestors(target);if(!File.Exists(target))return false;
            var folder=Environment.GetFolderPath(Environment.SpecialFolder.Programs);SafeDirectory.NoReparseAncestors(folder);
            var type=Type.GetTypeFromProgID("WScript.Shell")??throw new IOException("OWNER_SHORTCUT_UNAVAILABLE");shell=Activator.CreateInstance(type)??throw new IOException("OWNER_SHORTCUT_UNAVAILABLE");
            foreach(var mode in new[]{"confirm","recover"})
            {
                var path=Path.Combine(folder,"Yahad - "+mode+".lnk");SafeDirectory.NoReparseAncestors(path);object? link=null;
                try
                {
                    dynamic automation=shell;link=automation.CreateShortcut(path);dynamic shortcut=link;
                    var arguments=Arguments(mode,installation);
                    if(File.Exists(path)&&(!string.Equals((string)shortcut.TargetPath,target,StringComparison.OrdinalIgnoreCase)||(string)shortcut.Arguments!=arguments))return false;
                    shortcut.TargetPath=target;shortcut.Arguments=arguments;shortcut.WorkingDirectory=Path.GetDirectoryName(target);shortcut.Description=mode=="confirm"?"יחד — אישור עדכון":"יחד — שחזור ועדכון";shortcut.Save();
                }
                finally{if(link is not null)Marshal.FinalReleaseComObject(link);}
            }
            return true;
        }
        catch{return false;}
        finally{if(shell is not null)Marshal.FinalReleaseComObject(shell);}
    }
    internal static bool TryRemove(string installation)
    {
        object? shell=null;
        try
        {
            var target=Path.Combine(InstalledUpdaterLayout.BinaryRoot,"bootstrap","owner","Yahad.Owner.exe");
            var folder=Environment.GetFolderPath(Environment.SpecialFolder.Programs);SafeDirectory.NoReparseAncestors(folder);
            var type=Type.GetTypeFromProgID("WScript.Shell")??throw new IOException("OWNER_SHORTCUT_UNAVAILABLE");shell=Activator.CreateInstance(type)??throw new IOException("OWNER_SHORTCUT_UNAVAILABLE");
            var selected=new List<string>();
            foreach(var mode in new[]{"confirm","recover"})
            {
                var path=Path.Combine(folder,"Yahad - "+mode+".lnk");if(!File.Exists(path))continue;SafeDirectory.NoReparseAncestors(path);object? link=null;
                try
                {
                    dynamic automation=shell;link=automation.CreateShortcut(path);dynamic shortcut=link;
                    if(!string.Equals((string)shortcut.TargetPath,target,StringComparison.OrdinalIgnoreCase)||(string)shortcut.Arguments!=Arguments(mode,installation))return false;
                    selected.Add(path);
                }
                finally{if(link is not null)Marshal.FinalReleaseComObject(link);}
            }
            foreach(var path in selected){SafeDirectory.NoReparseAncestors(path);File.Delete(path);}return true;
        }
        catch{return false;}
        finally{if(shell is not null)Marshal.FinalReleaseComObject(shell);}
    }
}

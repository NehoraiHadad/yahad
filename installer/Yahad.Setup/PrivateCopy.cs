using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Yahad.Install;
namespace Yahad.Setup;

// Windows refuses to delete a running image but lets it be renamed. An engine whose
// action deletes the tree it runs from (removal, quiet or window; a repair that deletes
// its own infrastructure tree, 8.1) therefore hands the action to a private copy of
// itself and stays as that copy's faithful proxy: it waits, the copy alone writes the
// result, and the engine returns the copy's exact exit code.
//
// The copy is started from a fresh directory under %SystemRoot%\Temp whose protected
// ACL grants only SYSTEM and Administrators. An elevated administrator's own %TEMP% is
// writable, delete-child included, by that same user's unelevated processes, which
// could swap the image between copy and start. %SystemRoot%\Temp grants Users only
// traverse and create (no list, no delete-child), so nothing unelevated can rename or
// replace an entry an administrator made there. The state root is not used: removal
// with its data deletes it.
//
// While the engine waits, its image is still the tree's member. When the copy reaches
// that member it moves it (a same-volume rename, never a copy) into the private
// directory instead of deleting it. The engine cannot delete its own moved image, so
// that file and the directory are left to Windows to delete at the next restart.
internal static class PrivateCopy
{
    private const string Prefix="yahad-setup-",OriginName="origin",MovedName="origin-image";
    private const string Acl="O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
    internal static string Parent=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Temp");
    // Removal deletes the binary root and, with its data, the state root.
    internal static bool RemovalDeletes(string image)=>SetupOperation.Within(image,InstallLocations.Binaries)||SetupOperation.Within(image,InstallLocations.State);
    internal static bool IsPrivateDirectory(string directory,string parent)
    {
        var name=Path.GetFileName(directory);
        return string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)),Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)),StringComparison.OrdinalIgnoreCase)&&
            name.StartsWith(Prefix,StringComparison.Ordinal)&&Guid.TryParseExact(name[Prefix.Length..],"N",out var id)&&id.ToString("N")==name[Prefix.Length..];
    }
    // Runs this image's copy with the same arguments and returns its exit code once it
    // has exited. It throws only before the copy starts.
    internal static int Run(string[] arguments)
    {
        var image=Path.GetFullPath(Environment.ProcessPath??throw new IOException("SETUP_IMAGE"));
        var directory=Path.Combine(Parent,Prefix+Guid.NewGuid().ToString("N"));
        NativeInstallFiles.NewDirectory(directory,Acl);
        try
        {
            var copy=Path.Combine(directory,InstalledLayout.EngineName);File.Copy(image,copy,false);
            NativeInstallFiles.PublishNew(Path.Combine(directory,OriginName),Encoding.UTF8.GetBytes(image));
            var start=new ProcessStartInfo(copy){UseShellExecute=false};foreach(var argument in arguments)start.ArgumentList.Add(argument);
            using var child=Process.Start(start)??throw new IOException("SETUP_NOT_STARTED");
            child.WaitForExit();return child.ExitCode;
        }
        finally{Reclaim(directory);}
    }
    // The copy has exited: its image and the origin record go now; this engine's own
    // moved image, and so the directory, at the next restart.
    private static void Reclaim(string directory)
    {
        foreach(var file in Directory.EnumerateFiles(directory))
        {
            try{File.Delete(file);}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException){DeleteAtRestart(file);}
        }
        if(Directory.EnumerateFileSystemEntries(directory).Any())DeleteAtRestart(directory);else Directory.Delete(directory);
    }
    private static void DeleteAtRestart(string path)
    {if(!MoveFileExW(path,null,4))Console.Error.WriteLine("PRIVATE_COPY_LEFT "+path+" "+new Win32Exception().Message);}
    // The engine this process is a private copy of, when it is one.
    private static readonly Lazy<string?> Origin=new(()=>
    {
        var directory=Path.GetDirectoryName(Environment.ProcessPath);
        if(directory is null||!IsPrivateDirectory(directory,Parent))return null;
        var record=Path.Combine(directory,OriginName);
        return File.Exists(record)?Encoding.UTF8.GetString(NativeInstallFiles.ReadRecord(record,4096)):null;
    });
    internal static bool IsOrigin(string path,string? origin)=>origin is not null&&string.Equals(Path.GetFullPath(path),Path.GetFullPath(origin),StringComparison.OrdinalIgnoreCase);
    // Every installed-tree deletion goes through here: the waiting engine's image is
    // moved out of the tree, everything else is deleted without following a link.
    internal static void DeleteMember(string path,bool directory)
    {
        if(!directory&&IsOrigin(path,Origin.Value))
        {if(!MoveFileExW(path,Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!,MovedName),8))throw new Win32Exception();return;}
        WindowsFileBoundary.DeleteNoFollow(path,directory);
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool MoveFileExW(string existing,string? replacement,uint flags);
}

using System.ComponentModel;
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
// directory instead of deleting it. A single-file .NET image loads each assembly from
// its own path on first use, so once the image is moved the waiting engine can load
// nothing more (a FileNotFoundException, checked on a compressed single-file image).
// After the copy starts, the engine therefore runs no managed code that could load
// anything: it creates the copy with CreateProcessW, waits on its handle and leaves
// with ExitProcess, all through stubs bound by Marshal.Prelink before the start. The
// copy, whose own image is intact, schedules its directory for deletion at the next
// restart when its action ends (neither image can be deleted while it runs).
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
    // Runs this image's copy with the same command line and leaves with the copy's
    // exit code once it has exited. It returns only by throwing, and only before the
    // copy starts.
    internal static int Run()
    {
        var image=Path.GetFullPath(Environment.ProcessPath??throw new IOException("SETUP_IMAGE"));
        var directory=Path.Combine(Parent,Prefix+Guid.NewGuid().ToString("N"));
        NativeInstallFiles.NewDirectory(directory,Acl);
        var copy=Path.Combine(directory,InstalledLayout.EngineName);
        try
        {
            File.Copy(image,copy,false);
            NativeInstallFiles.PublishNew(Path.Combine(directory,OriginName),Encoding.UTF8.GetBytes(image));
            foreach(var name in new[]{nameof(CreateProcessW),nameof(WaitForSingleObject),nameof(GetExitCodeProcess),nameof(ExitProcess)})
                Marshal.Prelink(typeof(PrivateCopy).GetMethod(name,System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!);
        }
        catch{Directory.Delete(directory,true);throw;}
        var command=new StringBuilder("\""+copy+"\"").Append(Arguments(Marshal.PtrToStringUni(GetCommandLineW())!));
        var startup=new StartupInfo{Size=Marshal.SizeOf<StartupInfo>()};
        if(!CreateProcessW(copy,command,IntPtr.Zero,IntPtr.Zero,false,0,IntPtr.Zero,directory,ref startup,out var process))
        {var error=new Win32Exception();Directory.Delete(directory,true);throw error;}
        // From here on: native calls only (see the class comment).
        WaitForSingleObject(process.Process,0xFFFFFFFF);
        if(!GetExitCodeProcess(process.Process,out var exit))exit=1;
        ExitProcess(exit);
        return 1;
    }
    // This process's command line after its image name, unchanged, so the copy parses
    // exactly the arguments this engine was given (CommandLineToArgvW rules for argv0:
    // a quoted image runs to the next quote, an unquoted one to the first space or tab).
    internal static string Arguments(string commandLine)
    {
        int end;
        if(commandLine.StartsWith('"')){end=commandLine.IndexOf('"',1);end=end<0?commandLine.Length:end+1;}
        else{end=commandLine.IndexOfAny([' ','	']);if(end<0)end=commandLine.Length;}
        return commandLine[end..];
    }
    // Called by a private copy when its action has ended: its directory, with both
    // images in it, goes at the next restart.
    internal static void ScheduleOwnRemoval()
    {
        try
        {
            var directory=Path.GetDirectoryName(Environment.ProcessPath);
            if(directory is null||Origin.Value is null)return;
            foreach(var file in Directory.EnumerateFiles(directory))DeleteAtRestart(file);
            DeleteAtRestart(directory);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or Win32Exception){Console.Error.WriteLine("PRIVATE_COPY_LEFT "+error.Message);}
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
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct StartupInfo{public int Size;public IntPtr Reserved,Desktop,Title;public int X,Y,XSize,YSize,XCountChars,YCountChars,FillAttribute,Flags;public short ShowWindow,Reserved2;public IntPtr Reserved3,StdInput,StdOutput,StdError;}
    [StructLayout(LayoutKind.Sequential)]private struct ProcessInformation{public IntPtr Process,Thread;public int ProcessId,ThreadId;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool MoveFileExW(string existing,string? replacement,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool CreateProcessW(string application,StringBuilder commandLine,IntPtr processAttributes,IntPtr threadAttributes,bool inheritHandles,uint flags,IntPtr environment,string currentDirectory,ref StartupInfo startup,out ProcessInformation information);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetExitCodeProcess(IntPtr process,out int exitCode);
    [DllImport("kernel32.dll")]private static extern void ExitProcess(int exitCode);
    [DllImport("kernel32.dll")]private static extern IntPtr GetCommandLineW();
}

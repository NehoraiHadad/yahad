using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Yahad.Install;

/// <summary>Allowlisted child environment; never clones host environment variables.</summary>
public static class LaunchEnvironment
{
    public static Dictionary<string,string> Clean(string privateTemporaryDirectory)
    {
        if (!Path.IsPathFullyQualified(privateTemporaryDirectory) || !Directory.Exists(privateTemporaryDirectory))
            throw new IOException("PRIVATE_TEMP_REQUIRED");
        var windows = new StringBuilder(32768);
        var length = GetWindowsDirectoryW(windows,(uint)windows.Capacity);
        if (length == 0 || length >= windows.Capacity) throw new Win32Exception();
        // OS path comes from the Windows API, never a spoofed host SystemRoot.
        // SystemDrive is the Windows directory's drive; machine known folders
        // (ProgramData, Program Files) expand it and resolve to "" without it.
        var system = windows.ToString();
        if (system.Length < 3 || !char.IsAsciiLetter(system[0]) || system[1] != ':' || system[2] != '\\') throw new IOException("WINDOWS_DRIVE_REQUIRED");
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"]=system, ["WINDIR"]=system, ["SystemDrive"]=system[..2], ["PATH"]="",
            ["TEMP"]=privateTemporaryDirectory, ["TMP"]=privateTemporaryDirectory,
            ["APPDATA"]=privateTemporaryDirectory, ["LOCALAPPDATA"]=privateTemporaryDirectory
        };
    }

    internal static string Block(IReadOnlyDictionary<string,string> environment)
    {
        var sorted = new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in environment)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || item.Key.Contains('=') || item.Key.Contains('\0') ||
                item.Value is null || item.Value.Contains('\0') || !sorted.TryAdd(item.Key,item.Value))
                throw new ArgumentException("INVALID_CHILD_ENVIRONMENT");
        }
        var block = string.Join('\0',sorted.Select(p=>p.Key+"="+p.Value))+"\0\0";
        if (block.Length > 32767) throw new ArgumentException("CHILD_ENVIRONMENT_BOUNDS");
        return block;
    }

    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern uint GetWindowsDirectoryW(StringBuilder output,uint size);
}

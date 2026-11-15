using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Yahad.Install;

// One relative member name inside an installed tree or an archive: '/' separated,
// no empty, dot or dot-dot part, no trailing dot or space, no device name, no '\'
// or ':'. Prefix rules are the caller's.
internal static class MemberNames
{
    internal const int MaximumLength=240;
    internal static bool Valid(string name)
    {
        if(name.Length is <1 or >MaximumLength||name.Contains('\\')||name.Contains(':'))return false;
        foreach(var part in name.Split('/'))
            if(part is "" or "." or ".."||part.EndsWith('.')||part.EndsWith(' ')||Regex.IsMatch(part,"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\\..*)?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))return false;
        return true;
    }
}
internal enum TreeKind{Infrastructure,App}
internal sealed record TreeMember(string Path,long Size,string Sha256);
// infra-tree.json and app-tree.json (docs/WINDOWS-APP-PACKAGE-PLAN.md 5.3): the
// builder's member record of one tree. The installer never re-serialises it; the
// pointer and the installer record name the SHA-256 of its exact bytes. It lists
// every member of the tree except itself.
internal sealed record TreeRecord(int Format,string Version,string SourceCommit,TreeMember[] Files)
{
    internal const int CurrentFormat=1,MaximumBytes=16777216,MaximumMembers=50000;
    internal const long MaximumMemberBytes=536870912,MaximumTotalBytes=2147483648;
    internal static readonly string[] InfrastructurePrefixes=["host/","updater/","platform/","runtime/","legal/","setup/"];
    internal static readonly string[] AppPrefixes=["app/"];
    internal static string RecordName(TreeKind kind)=>kind==TreeKind.Infrastructure?InstalledLayout.InfraTreeName:InstalledLayout.AppTreeName;
    internal static bool InTree(TreeKind kind,string path)
        =>(kind==TreeKind.Infrastructure?InfrastructurePrefixes:AppPrefixes).Any(p=>path.StartsWith(p,StringComparison.Ordinal))&&
          !(kind==TreeKind.App&&path.StartsWith("app/native/",StringComparison.OrdinalIgnoreCase));
    internal static string Sha256(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static bool IsDigest(string? value)=>value is not null&&Regex.IsMatch(value,"\\A[a-f0-9]{64}\\z",RegexOptions.CultureInvariant);
    internal static bool IsCommit(string? value)=>value is not null&&Regex.IsMatch(value,"\\A[a-f0-9]{40}\\z",RegexOptions.CultureInvariant);
    // UTF-8 without BOM, strict JSON, sorted by ordinal path and unique ignoring case.
    // As setup_package.py's _closed_names: no member is also a directory of another,
    // and one directory is spelled one way, both ignoring case.
    internal static TreeRecord Parse(byte[] bytes,TreeKind kind)
    {
        if(bytes.Length is <1 or >MaximumBytes||bytes is [0xEF,0xBB,0xBF,..])throw new IOException("TREE_RECORD_INVALID");
        TreeRecord value;
        try{value=NativeJson.ReadCamel<TreeRecord>(bytes);}catch(JsonException){throw new IOException("TREE_RECORD_INVALID");}
        if(value.Format!=CurrentFormat||!ReleaseManifest.IsVersion(value.Version)||!IsCommit(value.SourceCommit)||value.Files is not {Length:>=1 and <=MaximumMembers})throw new IOException("TREE_RECORD_INVALID");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;string? previous=null;
        foreach(var member in value.Files)
        {
            if(member is null||!MemberNames.Valid(member.Path)||!InTree(kind,member.Path)||!names.Add(member.Path)||
                previous is not null&&string.CompareOrdinal(previous,member.Path)>=0||
                member.Size is <0 or >MaximumMemberBytes||(total+=member.Size)>MaximumTotalBytes||!IsDigest(member.Sha256))throw new IOException("TREE_RECORD_INVALID");
            previous=member.Path;
        }
        var directories=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var member in value.Files)
            for(var slash=member.Path.IndexOf('/');slash>0;slash=member.Path.IndexOf('/',slash+1))
            {var directory=member.Path[..slash];if(!directories.TryAdd(directory,directory)&&directories[directory]!=directory)throw new IOException("TREE_RECORD_INVALID");}
        if(value.Files.Any(f=>directories.ContainsKey(f.Path)))throw new IOException("TREE_RECORD_INVALID");
        return value;
    }
    // What the host checks before every start (5.3): the record's bytes are the ones
    // the pointer names, every recorded member is present with its recorded size, and
    // nothing else is in the tree. A link is never followed. Per-member hashes are
    // checked at extraction and by repair, not here.
    internal static TreeRecord VerifyInstalled(string root,TreeKind kind,string version,string treeSha256)
    {
        root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if(!Directory.Exists(root)||(File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new IOException("ACTIVE_POINTER_INVALID");
        var recordPath=Path.Combine(root,RecordName(kind));byte[] bytes;
        try{bytes=NativeInstallFiles.ReadRecord(recordPath,MaximumBytes);}catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){throw new IOException("ACTIVE_POINTER_INVALID");}
        if(Sha256(bytes)!=treeSha256)throw new IOException("ACTIVE_POINTER_INVALID");
        TreeRecord record;
        try{record=Parse(bytes,kind);}catch(IOException){throw new IOException("ACTIVE_POINTER_INVALID");}
        if(record.Version!=version)throw new IOException("ACTIVE_POINTER_INVALID");
        var expected=record.Files.ToDictionary(f=>f.Path,f=>f.Size,StringComparer.OrdinalIgnoreCase);
        var parents=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var name in expected.Keys){var slash=name.LastIndexOf('/');while(slash>0){parents.Add(name[..slash]);slash=name.LastIndexOf('/',slash-1);}}
        var seen=0;var count=0;
        void Walk(string directory,int depth)
        {
            if(depth>64)throw new IOException("ACTIVE_POINTER_INVALID");
            foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if(++count>MaximumMembers*2)throw new IOException("ACTIVE_POINTER_INVALID");
                var attributes=File.GetAttributes(entry);var relative=Path.GetRelativePath(root,entry).Replace('\\','/');
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("ACTIVE_POINTER_INVALID");
                if((attributes&FileAttributes.Directory)!=0){if(!parents.Contains(relative))throw new IOException("ACTIVE_POINTER_INVALID");Walk(entry,depth+1);continue;}
                if(depth==0&&string.Equals(relative,RecordName(kind),StringComparison.OrdinalIgnoreCase))continue;
                if(!expected.TryGetValue(relative,out var size)||new FileInfo(entry).Length!=size)throw new IOException("ACTIVE_POINTER_INVALID");
                seen++;
            }
        }
        Walk(root,0);
        if(seen!=expected.Count)throw new IOException("ACTIVE_POINTER_INVALID");
        return record;
    }
}

using System.IO;
using Yahad.Install;
namespace Yahad.Setup;
internal sealed record RemovalTree(string Root,string[] Files,string[] Directories)
{
    internal static RemovalTree Inspect(string root,Action<string,bool> validate)
    {
        root=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(root));var files=new List<string>();var directories=new List<string>();var count=0;
        void Walk(string path,int depth)
        {
            if(depth>128)throw new IOException("UNINSTALL_TREE_LIMIT");validate(path,true);
            foreach(var entry in Directory.EnumerateFileSystemEntries(path))
            {
                if(++count>200000)throw new IOException("UNINSTALL_TREE_LIMIT");
                var attributes=File.GetAttributes(entry);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("UNINSTALL_LINK_PRESERVED");
                if((attributes&FileAttributes.Directory)!=0){Walk(entry,depth+1);directories.Add(entry);}else{validate(entry,false);files.Add(entry);}
            }
        }
        Walk(root,0);return new(root,files.ToArray(),directories.OrderByDescending(p=>p.Length).ToArray());
    }
    internal void DeleteContents(Action<string,bool> delete,Action<int> progress,CancellationToken cancellation)
    {
        var all=Files.OrderBy(p=>Path.GetFileName(p)=="installer-state.json"?1:0).Select(p=>(Path:p,Directory:false)).Concat(Directories.Select(p=>(Path:p,Directory:true))).ToArray();var completed=0;
        foreach(var member in all){cancellation.ThrowIfCancellationRequested();delete(member.Path,member.Directory);if(++completed%100==0||completed==all.Length)progress(completed*100/Math.Max(all.Length,1));}
        if(Directory.EnumerateFileSystemEntries(Root).Any())throw new IOException("UNINSTALL_ROOT_NOT_EMPTY");
    }
}

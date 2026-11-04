using System.IO;
using System.Runtime.InteropServices;
using Yahad.Install;
using Yahad.Setup;
internal static class RemovalTests
{
    private static string Root()=>PrivateSession.Create(Path.GetTempPath());
    private static RemovalTree Plan(string root)=>RemovalTree.Inspect(root,WindowsFileBoundary.RequireNoLink);
    private static void Delete(string path,bool dir)=>WindowsFileBoundary.DeleteNoFollow(path,dir);
    private static void Require(bool value){if(!value)throw new Exception("REMOVAL_REGRESSION");}
    internal static void Complete()
    {
        var root=Root();var outside=Root();var backup=Path.Combine(outside,"external-backup");File.WriteAllText(backup,"synthetic external backup");
        var folder=Path.Combine(root,"datasets");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"app.sqlite"),"synthetic no family data");File.WriteAllText(Path.Combine(root,"master-key"),"synthetic not a key");
        Plan(root).DeleteContents(Delete,_=>{},default);
        Delete(root,true);Require(!Directory.Exists(root)&&File.ReadAllText(backup)=="synthetic external backup");
    }
    internal static void LockedAndHardlink()
    {
        var root=Root();var data=Path.Combine(root,"locked");File.WriteAllText(data,"synthetic");
        using(var locked=new FileStream(data,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {try{Plan(root).DeleteContents(Delete,_=>{},default);throw new Exception("locked file accepted");}catch(System.ComponentModel.Win32Exception){}Require(File.Exists(data));}
        var link=Path.Combine(root,"hardlink");if(!CreateHardLinkW(link,data,IntPtr.Zero))throw new System.ComponentModel.Win32Exception();
        try{Plan(root);throw new Exception("hardlink accepted");}catch(IOException){}Require(File.ReadAllText(data)=="synthetic");
        File.Delete(link);Plan(root).DeleteContents(Delete,_=>{},default);Delete(root,true);
    }
    internal static void Interrupted()
    {
        var root=Root();var controller=Root();var journal=Path.Combine(controller,"uninstall-state.json");File.WriteAllText(journal,UninstallChoice.Phase(UninstallMode.DeleteData));
        File.WriteAllText(Path.Combine(root,"first"),"synthetic");File.WriteAllText(Path.Combine(root,"second"),"synthetic");var count=0;
        var plan=Plan(root);try{plan.DeleteContents((path,dir)=>{if(++count==2)throw new IOException("synthetic disk failure");Delete(path,dir);},_=>{},default);throw new Exception("fault ignored");}catch(IOException){}
        Require(File.Exists(journal)&&Directory.Exists(root));Plan(root).DeleteContents(Delete,_=>{},default);
        Delete(root,true);Require(!Directory.Exists(root)&&File.Exists(journal));
        var cancelled=Root();using var stop=new CancellationTokenSource();stop.Cancel();
        try{Plan(cancelled).DeleteContents(Delete,_=>{},stop.Token);throw new Exception("cancel ignored");}catch(OperationCanceledException){}Require(File.Exists(Path.Combine(cancelled,".yahad-fixture")));
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CreateHardLinkW(string link,string target,IntPtr security);
}

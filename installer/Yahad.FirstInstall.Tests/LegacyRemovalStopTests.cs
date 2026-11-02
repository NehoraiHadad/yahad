using System.ComponentModel;
using System.IO;
using System.Security.Principal;
using Yahad.Install;
using Yahad.Setup;

internal static class LegacyRemovalStopTests
{
    private static void Require(bool value){if(!value)throw new IOException("LEGACY_REMOVAL_STOP_REGRESSION");}
    private static void Refuse(Action action)
    {try{action();}catch(Exception failure)when(failure is IOException or Win32Exception or UnauthorizedAccessException){return;}throw new Exception("UNEXPECTED_REMOVAL_ADMISSION");}
    internal static async Task RunAsync(Func<string,Func<Task>,Task> check)
    {
        await check("legacy removal accepts owned stopped status zero and no start attempt this boot (1077)",()=>
        {
            NativeServiceInstall.RequireRemovalStop(1,0,0,0);
            NativeServiceInstall.RequireRemovalStop(1,1077,0,0);
            return Task.CompletedTask;
        });
        await check("legacy removal refuses every active transition and nonzero reported process",()=>
        {
            foreach(var state in new uint[]{0,2,3,4,5,6,7,uint.MaxValue})
                foreach(var exit in new uint[]{0,1077})Refuse(()=>NativeServiceInstall.RequireRemovalStop(state,exit,0,0));
            foreach(var exit in new uint[]{0,1077})Refuse(()=>NativeServiceInstall.RequireRemovalStop(1,exit,0,123));
            return Task.CompletedTask;
        });
        await check("legacy removal keeps failed and uncertain shutdowns refused",()=>
        {
            foreach(var error in new uint[]{5,1066,1067,1069,1072,uint.MaxValue})Refuse(()=>NativeServiceInstall.RequireRemovalStop(1,error,0,0));
            foreach(var exit in new uint[]{0,1077})Refuse(()=>NativeServiceInstall.RequireRemovalStop(1,exit,1,0));
            return Task.CompletedTask;
        });
        await check("receipt repair retains its original strict clean-stop contract",()=>
        {
            NativeServiceInstall.RequireCleanStop(1,0,0);
            Refuse(()=>NativeServiceInstall.RequireCleanStop(1,1077,0));
            Refuse(()=>NativeServiceInstall.RequireCleanStop(4,0,0));
            Refuse(()=>NativeServiceInstall.RequireCleanStop(1,0,1));
            return Task.CompletedTask;
        });
        await check("removal service ownership still requires the exact type account and fixed image",()=>
        {
            var account="NT SERVICE\\"+InstallLocations.ServiceName;
            var image="\""+InstallLocations.ServiceImage+"\" --service";
            NativeServiceInstall.RequireOwnedConfiguration(16,account,image);
            NativeServiceInstall.RequireOwnedConfiguration(16,account.ToLowerInvariant(),image);
            foreach(var type in new uint[]{0,1,32,256})Refuse(()=>NativeServiceInstall.RequireOwnedConfiguration(type,account,image));
            foreach(var wrong in new string?[]{null,"LocalSystem","NT SERVICE\\OtherProduct",account+" "})Refuse(()=>NativeServiceInstall.RequireOwnedConfiguration(16,wrong,image));
            foreach(var wrong in new string?[]{null,image+" --other",image.Replace("--service","--uninstall"),"\"C:\\unrelated.exe\" --service"})Refuse(()=>NativeServiceInstall.RequireOwnedConfiguration(16,account,wrong));
            return Task.CompletedTask;
        });
        await check("only genuine absence skips removal directory and data lease checks",()=>
        {
            Require(RemovalTree.DirectoryPresent(()=>FileAttributes.Directory));
            Require(!RemovalTree.DirectoryPresent(()=>throw new FileNotFoundException()));
            Require(!RemovalTree.DirectoryPresent(()=>throw new DirectoryNotFoundException()));
            Refuse(()=>RemovalTree.DirectoryPresent(()=>throw new UnauthorizedAccessException()));
            Refuse(()=>RemovalTree.DirectoryPresent(()=>throw new IOException("synthetic I/O failure")));
            Refuse(()=>RemovalTree.DirectoryPresent(()=>throw new Win32Exception(5)));
            Refuse(()=>RemovalTree.DirectoryPresent(()=>FileAttributes.Normal));
            Refuse(()=>RemovalTree.DirectoryPresent(()=>FileAttributes.Directory|FileAttributes.ReparsePoint));
            return Task.CompletedTask;
        });
        await check("stopped 1077 cannot bypass an actual held private data lease",()=>
        {
            var root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());var data=Path.Combine(root,"data");Directory.CreateDirectory(data);
            var marker=Path.Combine(data,"synthetic-record");File.WriteAllText(marker,"synthetic original");var mutations=0;
            try
            {
                using(var held=WindowsFileBoundary.AcquireDataLease(data))
                {
                    Refuse(()=>{NativeServiceInstall.RequireRemovalStop(1,1077,0,0);using var competing=WindowsFileBoundary.AcquireDataLease(data);mutations++;});
                    Require(mutations==0&&File.ReadAllText(marker)=="synthetic original");
                }
                using(var acquired=WindowsFileBoundary.AcquireDataLease(data))
                {
                    // Same gate order as the product: status recheck while the
                    // real lease is held, before any durable removal intent.
                    Refuse(()=>{NativeServiceInstall.RequireRemovalStop(4,0,0,111);mutations++;});
                    Require(mutations==0);
                    NativeServiceInstall.RequireRemovalStop(1,1077,0,0);
                    var sid=WindowsIdentity.GetCurrent().User!;
                    RemovalTree.Inspect(data,(path,directory)=>WindowsFileBoundary.ValidateRemovalMember(path,directory,sid));
                    Require(File.ReadAllText(marker)=="synthetic original");
                }
            }
            finally
            {
                var temporary=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                Require(root.StartsWith(temporary,StringComparison.OrdinalIgnoreCase)&&root.Length>temporary.Length);
                SafeDirectory.NoReparseAncestors(root);Directory.Delete(root,true);
            }
            return Task.CompletedTask;
        });
        await check("stopped 1077 keeps locked and hardlinked tree refusal and interrupted choice recovery",()=>
        {
            NativeServiceInstall.RequireRemovalStop(1,1077,0,0);
            RemovalTests.LockedAndHardlink();RemovalTests.Interrupted();
            return Task.CompletedTask;
        });
    }
}

using System.ComponentModel;
using System.IO;
using System.Security.Principal;
using Yahad.Install;
using Yahad.Setup;
internal static class FullInstallerAtomicTests
{
    internal static void Run()
    {
        var root=PrivateSession.Create(Path.GetTempPath());var path=Path.Combine(root,"synthetic-removal.json");
        byte[] bytes=[1,2,3,4];
        try{NativeInstallFiles.PublishNew(path,bytes,()=>throw new IOException("SYNTHETIC_CUT_BEFORE_COMMIT"));throw new Exception("CUT_NOT_OBSERVED");}
        catch(IOException error)when(error.Message=="SYNTHETIC_CUT_BEFORE_COMMIT"){}
        var partials=Directory.GetFiles(root,"synthetic-removal.json.partial-*");
        if(File.Exists(path)||partials.Length!=1)throw new Exception("TORN_CANONICAL_RECEIPT");
        var partial=partials[0];using(var held=WindowsFileBoundary.OpenRead(partial))if(!held.Small(16).SequenceEqual(bytes))throw new Exception("UNFLUSHED_PRIVATE_PARTIAL");
        NativeInstallFiles.PublishNew(path,bytes);if(!File.ReadAllBytes(path).SequenceEqual(bytes))throw new Exception("ATOMIC_PUBLICATION_FAILED");
        try{NativeInstallFiles.PublishNew(path,[9,8,7]);throw new Exception("ATOMIC_REPLACEMENT_ALLOWED");}catch(Win32Exception){}
        if(!File.ReadAllBytes(path).SequenceEqual(bytes))throw new Exception("CANONICAL_RECEIPT_OVERWRITTEN");
        // Preserved installer copies nest a digest slot under an installation id
        // and run past MAX_PATH; the Win32 file layer must create and publish there.
        var deep=root;foreach(var component in new[]{new string('a',64),new string('b',64),new string('c',64),new string('d',64)}){deep=Path.Combine(deep,component);NativeInstallFiles.NewDirectory(deep);}
        var longPath=Path.Combine(deep,"System.Runtime.InteropServices.RuntimeInformation.dll");if(longPath.Length<=260)throw new Exception("LONG_PATH_NOT_EXERCISED");
        NativeInstallFiles.PublishNew(longPath,bytes);NativeInstallFiles.Replace(longPath,[5,6]);
        if(!File.ReadAllBytes(longPath).SequenceEqual(new byte[]{5,6}))throw new Exception("LONG_PATH_PUBLICATION_FAILED");
    }
}

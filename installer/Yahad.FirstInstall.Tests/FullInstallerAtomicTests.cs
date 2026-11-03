using System.ComponentModel;
using System.IO;
using System.Security.Principal;
using Yahad.Install;
using Yahad.Setup;
internal static class FullInstallerAtomicTests
{
    internal static void Run()
    {
        var root=SafeDirectory.CreatePrivateSession(Path.GetTempPath());var path=Path.Combine(root,"synthetic-removal.json");
        var sid=WindowsIdentity.GetCurrent().User!.Value;var descriptor=$"O:{sid}D:P(A;;FA;;;{sid})";byte[] bytes=[1,2,3,4];
        try{NativeInstallFiles.PublishNewProtectedFile(path,bytes,descriptor,()=>throw new IOException("SYNTHETIC_CUT_BEFORE_COMMIT"));throw new Exception("CUT_NOT_OBSERVED");}
        catch(IOException error)when(error.Message=="SYNTHETIC_CUT_BEFORE_COMMIT"){}
        var partials=Directory.GetFiles(root,"synthetic-removal.json.partial-*");
        if(File.Exists(path)||partials.Length!=1)throw new Exception("TORN_CANONICAL_RECEIPT");
        var partial=partials[0];using(var held=WindowsFileBoundary.OpenPrivateRead(partial))if(!held.Small(16).SequenceEqual(bytes))throw new Exception("UNFLUSHED_PRIVATE_PARTIAL");
        NativeInstallFiles.PublishNewProtectedFile(path,bytes,descriptor);if(!File.ReadAllBytes(path).SequenceEqual(bytes))throw new Exception("ATOMIC_PUBLICATION_FAILED");
        try{NativeInstallFiles.PublishNewProtectedFile(path,[9,8,7],descriptor);throw new Exception("ATOMIC_REPLACEMENT_ALLOWED");}catch(Win32Exception){}
        if(!File.ReadAllBytes(path).SequenceEqual(bytes))throw new Exception("CANONICAL_RECEIPT_OVERWRITTEN");
        // Preserved installer copies nest a digest slot under an installation id
        // and run past MAX_PATH; the Win32 file layer must create and publish there.
        var deep=root;foreach(var component in new[]{new string('a',64),new string('b',64),new string('c',64),new string('d',64)}){deep=Path.Combine(deep,component);NativeInstallFiles.NewDirectory(deep,descriptor);}
        var longPath=Path.Combine(deep,"System.Runtime.InteropServices.RuntimeInformation.dll");if(longPath.Length<=260)throw new Exception("LONG_PATH_NOT_EXERCISED");
        NativeInstallFiles.PublishNewProtectedFile(longPath,bytes,descriptor);NativeInstallFiles.ReplaceOwned(longPath,[5,6],descriptor);
        if(!File.ReadAllBytes(longPath).SequenceEqual(new byte[]{5,6}))throw new Exception("LONG_PATH_PUBLICATION_FAILED");
    }
}

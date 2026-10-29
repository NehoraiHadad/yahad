using System.IO;
using Yahad.Install;
namespace Yahad.Setup;
internal static class ShortcutCleanup
{
    internal static bool TryRemove(string? origin)
    {
        if(!Uri.TryCreate(origin,UriKind.Absolute,out var uri)||uri.Scheme!="http"||uri.Host!="127.0.0.1"||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!=""||uri.AbsolutePath!="/"||uri.Port is <1024 or >65535)return false;
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"Yahad.url");
        try
        {
            if(!File.Exists(path))return true;SafeDirectory.NoReparseAncestors(path);if(new FileInfo(path).Length>4096)return false;
            if(File.ReadAllText(path).Replace("\r\n","\n")!="[InternetShortcut]\nURL="+origin+"\n")return false;
            File.Delete(path);return true;
        }
        catch{return false;}
    }
}

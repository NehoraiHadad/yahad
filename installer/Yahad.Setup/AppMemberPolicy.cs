using System.IO;
using System.IO.Compression;
using Yahad.Install;
namespace Yahad.Setup;

// The app member policy (docs/WINDOWS-APP-PACKAGE-PLAN.md 5.2), one rule for app
// members whichever package carries them: the app archive (AppPackage) and the apps/
// namespace of a Setup's install package (InstallPackage), which a first install, a
// manual Setup and the Setup track all install. It is installer/windows/bundle.py's
// REQUIRED and refused_member: the reader refuses what the writer would. Names are
// relative to the app tree root; codes is the refusal prefix of the package read.
internal static class AppMemberPolicy
{
    internal static readonly string[] Required=["app/package.json","app/server/main.mjs","app/dist/index.html","app/deploy/lan-capabilities.json","app/deploy/lan-help.html",
        "app/legal/server-bundle.json","app/legal/ThirdPartyNotices-server.txt","app/legal/build-inputs.json","app/legal/SBOM.json","app/legal/Yahad-LICENSE.txt"];
    private static readonly string[] ForbiddenDirectories=["app/src/","app/node_modules/","app/native/"];
    private static readonly string[] ForbiddenExtensions=[".node",".exe",".dll",".ps1",".cmd",".bat"];
    private const string ForbiddenFile="app/package-lock.json";
    // An app member: in the app namespace and none of the refused source, dependency,
    // native or executable names, in any case.
    internal static void RequireName(string member,string codes)
    {
        if(ForbiddenDirectories.Any(d=>member.StartsWith(d,StringComparison.OrdinalIgnoreCase))||string.Equals(member,ForbiddenFile,StringComparison.OrdinalIgnoreCase)||
            ForbiddenExtensions.Any(e=>member.EndsWith(e,StringComparison.OrdinalIgnoreCase)))throw new IOException(codes+"FORBIDDEN");
        if(!TreeRecord.InTree(TreeKind.App,member))throw new IOException(codes+"PATH");
    }
    // Whatever its name, no app member is a PE image: the server is one self-contained
    // ESM file, so native code has no place in the app tree.
    internal static void RequireNotImage(ZipArchiveEntry entry,string codes)
    {
        var head=new byte[2];using var input=entry.Open();var read=0;int n;
        while(read<2&&(n=input.Read(head,read,2-read))>0)read+=n;
        if(read==2&&head[0]==(byte)'M'&&head[1]==(byte)'Z')throw new IOException(codes+"PE");
    }
}

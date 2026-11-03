using System.IO;
using System.Text.Json;
namespace Yahad.Setup;

// Unattended install/removal for administrators and machine acceptance. Same
// in-process operation, consent and result contract as the window; the command
// line is the explicit choice. The result file never carries the setup capability token.
// Only with --setup-link stdout does an install hand the invoking administrator
// the same one-time private link the window opens, on its standard output and
// nowhere else; without it a quiet install leaves first-family setup to the window.
internal static class QuietSetup
{
    internal sealed record Request(bool Uninstall,UninstallMode Mode,bool? AutomaticUpdates,string ResultPath,bool SetupLinkToStdout=false);
    internal static Request? Parse(string[] args)=>args switch
    {
        ["--quiet-install","--updates",var updates,"--result",var path,"--setup-link","stdout"]=>Install(updates,path,true),
        ["--quiet-install","--updates",var updates,"--result",var path]=>Install(updates,path,false),
        ["--quiet-uninstall","--data","keep","--result",var path]=>new(true,UninstallMode.KeepData,null,Full(path)),
        ["--quiet-uninstall","--data","delete","--result",var path]=>new(true,UninstallMode.DeleteData,null,Full(path)),
        _=>null
    };
    private static Request? Install(string updates,string path,bool link)=>updates switch
    {"notify"=>new(false,UninstallMode.KeepData,false,Full(path),link),"auto"=>new(false,UninstallMode.KeepData,true,Full(path),link),_=>null};
    private static string Full(string path)=>Path.IsPathFullyQualified(path)?path:throw new IOException("SETUP_ARGUMENTS");
    internal static UpdateSetupChoice? Consent(Request request,bool fullProfile,bool existing,DateTimeOffset now)
        =>request.Uninstall||!fullProfile||existing?null:new(2,true,request.AutomaticUpdates==true,now.ToUnixTimeSeconds());
    internal static async Task<int> RunAsync(Request request)
    {
        SetupResult result;var clock=System.Diagnostics.Stopwatch.StartNew();var phases=new List<object>();string? current=null;
        // Phase entry times only, so an operator or CI can see where time goes.
        void Progress(SetupProgress p){lock(phases){if(p.Phase==current)return;current=p.Phase;phases.Add(new{p.Phase,Seconds=Math.Round(clock.Elapsed.TotalSeconds,1)});}}
        try
        {
            var update=Consent(request,InstallPackage.EmbeddedFullProfile(),MainWindow.HasExistingUpdateInstallation(),DateTimeOffset.UtcNow);
            result=await SetupOperation.RunAsync(request.Uninstall,Progress,CancellationToken.None,request.Mode,null,update:update);
            if(result.Ok&&request.Uninstall)ShortcutCleanup.TryRemove(result.Url);
        }
        catch(Exception error){result=new(false,SetupOperation.Code(error),Failure:SetupFailureDetail.From(error));}
        File.WriteAllBytes(request.ResultPath,JsonSerializer.SerializeToUtf8Bytes(new{result.Ok,result.Code,result.Url,result.DataPreserved,result.Failure,result.FullProfileInstallationId,Phases=phases,Seconds=Math.Round(clock.Elapsed.TotalSeconds,1)}));
        if(request.SetupLinkToStdout&&result.Ok&&result.Url is not null&&result.SetupToken is not null)
        {Console.Out.WriteLine(result.Url+"#setup="+result.SetupToken);Console.Out.Flush();}
        return result.Ok?0:1;
    }
}

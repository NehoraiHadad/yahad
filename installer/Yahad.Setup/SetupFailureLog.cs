using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;

// The developer's record of one failure: the code, its catalog category, where it
// stopped, the numbers Windows gave, the exception's type, the compiled throw sites
// and this engine's identity. Like the result file it carries no exception message
// and no path, so nothing private is shown, copied or kept.
internal sealed record SetupFailureReport(int Format,string Code,string Category,string Operation,string? Phase,int? Win32,string HResult,string? Exception,
    string[]? Sites,string SetupVersion,string? SourceCommit,string? AppVersion,string Utc)
{
    internal static SetupFailureReport Create(string code,SetupFailureDetail? detail,string operation,DateTimeOffset now,string? appVersion)
    {
        string? commit;try{commit=InfrastructureRelease.CompiledSourceCommit(typeof(SetupFailureReport).Assembly);}catch(IOException){commit=null;}
        return new(1,code,SetupErrorCatalog.Resolve(code).Category.ToString(),operation,detail?.Phase,detail?.NativeError,
            detail is null?"":"0x"+detail.HResult.ToString("X8",CultureInfo.InvariantCulture),detail?.Exception,detail?.Sites,
            InfrastructureRelease.Version,commit,appVersion,now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",CultureInfo.InvariantCulture));
    }
    internal byte[] Json()=>JsonSerializer.SerializeToUtf8Bytes(this,new JsonSerializerOptions{WriteIndented=true});
    // The block "copy details" puts on the clipboard and the technical section shows.
    internal string Text(string? logPath)
    {
        var text=new StringBuilder();
        void Line(string name,string? value){if(!string.IsNullOrEmpty(value))text.Append(name).Append(": ").Append(value).Append('\n');}
        Line("Yahad Setup",Code);Line("Category",Category);Line("Operation",Operation);Line("Phase",Phase);
        Line("Windows error",Win32?.ToString(CultureInfo.InvariantCulture));Line("HRESULT",HResult);Line("Exception",Exception);
        Line("Setup",SetupVersion+(SourceCommit is null?"":" ("+SourceCommit[..Math.Min(12,SourceCommit.Length)]+")"));Line("App",AppVersion);
        Line("Time (UTC)",Utc);Line("Sites",Sites is {Length:>0}?string.Join(" | ",Sites):null);Line("Log",logPath);
        return text.ToString().TrimEnd('\n');
    }
}

// %ProgramData%\Yahad.FirstInstall.setup-logs: beside the state root, never in it.
// The state root's presence is what makes this machine installed
// (ExistingInstallation.Present) and a first install refuses one it did not create
// (INSTALL_COLLISION), so a failed first install must leave nothing there. Only
// administrators and SYSTEM can open it; the last Kept records are kept.
internal static class SetupFailureLog
{
    internal const int Kept=20;
    internal static string Root=>InstallLocations.State+".setup-logs";
    // Any user may create a folder in ProgramData: a folder Setup did not make is not
    // written to, and the failure is still shown and copied without a log file.
    private static void Prepare(string root)
    {
        if(!Directory.Exists(root))NativeInstallFiles.NewDirectory(root,NativeInstallFiles.InstallAcl());
        WindowsFileBoundary.RequireAdministratorOwned(root);
    }
    // The record's path, or null when it could not be written. Writing it never
    // replaces the failure being reported.
    internal static string? Write(SetupFailureReport report)=>Write(report,Root,Prepare);
    internal static string? Write(SetupFailureReport report,string root,Action<string> prepare)
    {
        try
        {
            prepare(root);
            var path=Path.Combine(root,"setup-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")[..8]+".json");
            NativeInstallFiles.PublishNew(path,report.Json());Prune(root);return path;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or Win32Exception){return null;}
    }
    // The names sort by time; everything but the newest Kept records goes, partials included.
    internal static void Prune(string root)
    {
        var keep=Directory.EnumerateFiles(root,"setup-*.json").Select(Path.GetFileName).OrderDescending(StringComparer.Ordinal).Take(Kept).ToHashSet(StringComparer.Ordinal);
        foreach(var file in Directory.EnumerateFiles(root))if(!keep.Contains(Path.GetFileName(file)))WindowsFileBoundary.DeleteNoFollow(file,false);
    }
    // A full removal deletes Setup's records with the rest of the family's data. A
    // folder Setup did not make is not Setup's to delete.
    internal static void Remove(CancellationToken cancellation)
    {
        var root=Root;if(!Directory.Exists(root))return;
        try{WindowsFileBoundary.RequireAdministratorOwned(root);}catch(IOException error)when(error.Message=="INSTALL_ROOT_OWNER"){return;}
        RemovalTree.Inspect(root,WindowsFileBoundary.RequireNoLink).DeleteContents(WindowsFileBoundary.DeleteNoFollow,_=>{},cancellation);
        WindowsFileBoundary.DeleteNoFollow(root,true);
    }
}

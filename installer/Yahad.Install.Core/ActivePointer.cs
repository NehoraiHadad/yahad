using System.Text.Json;
namespace Yahad.Install;

internal sealed record ActiveInfrastructure(string Version,int ContractMin,int ContractMax,string TreeSha256);
internal sealed record ActiveApp(string Version,int RequiresContract,string TreeSha256);
// %ProgramFiles%\Yahad.FirstInstall\active.json (docs/WINDOWS-APP-PACKAGE-PLAN.md 4.3):
// the one (infrastructure, app) pair every binary runs. Only the Setup engine writes
// it, atomically (a same-directory partial, then MoveFileEx with replace and write-
// through: NativeInstallFiles.Publish). It inherits the binary root's ACL, so its
// write boundary is SYSTEM and Administrators.
internal sealed record ActivePointer(int Format,ActiveInfrastructure Infrastructure,ActiveApp App)
{
    internal const int CurrentFormat=1,MaximumBytes=4096;
    internal void Require()
    {
        if(Format!=CurrentFormat||Infrastructure is null||App is null||!SemVer.IsVersion(Infrastructure.Version)||!SemVer.IsVersion(App.Version)||
            !InfrastructureRelease.IsContract(Infrastructure.ContractMin)||!InfrastructureRelease.IsContract(Infrastructure.ContractMax)||Infrastructure.ContractMin>Infrastructure.ContractMax||
            App.RequiresContract<Infrastructure.ContractMin||App.RequiresContract>Infrastructure.ContractMax||
            !TreeRecord.IsDigest(Infrastructure.TreeSha256)||!TreeRecord.IsDigest(App.TreeSha256))throw new IOException("ACTIVE_POINTER_INVALID");
    }
    internal byte[] Encode()
    {
        Require();var bytes=NativeJson.WriteCamel(this);
        if(bytes.Length>MaximumBytes)throw new IOException("ACTIVE_POINTER_INVALID");return bytes;
    }
    internal static ActivePointer Parse(byte[] bytes)
    {
        if(bytes.Length is <1 or >MaximumBytes)throw new IOException("ACTIVE_POINTER_INVALID");
        ActivePointer value;
        try{value=NativeJson.ReadCamel<ActivePointer>(bytes);}catch(Exception error)when(error is JsonException or IOException){throw new IOException("ACTIVE_POINTER_INVALID");}
        value.Require();return value;
    }
    internal static ActivePointer Read()
    {
        byte[] bytes;
        try{bytes=NativeInstallFiles.ReadRecord(InstalledLayout.ActivePointerFile,MaximumBytes);}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){throw new IOException("ACTIVE_POINTER_INVALID");}
        return Parse(bytes);
    }
    // A host, Updater or Platform image runs only the pair whose infrastructure it is:
    // the same version and exactly its own contract range.
    internal void RequireThisInfrastructure()
    {
        if(Infrastructure.Version!=InfrastructureRelease.Version||Infrastructure.ContractMin!=InfrastructureRelease.ContractMin||
            Infrastructure.ContractMax!=InfrastructureRelease.ContractMax||!InfrastructureRelease.Supports(App.RequiresContract))throw new IOException("ACTIVE_POINTER_INVALID");
    }
    // What the host checks at every start: the pointer, then both trees it names.
    internal static ActivePointer ReadVerified(bool app)
    {
        var pointer=Read();pointer.RequireThisInfrastructure();
        TreeRecord.VerifyInstalled(InstalledLayout.InfraRoot(pointer.Infrastructure.Version),TreeKind.Infrastructure,pointer.Infrastructure.Version,pointer.Infrastructure.TreeSha256);
        if(app)TreeRecord.VerifyInstalled(InstalledLayout.AppRoot(pointer.App.Version),TreeKind.App,pointer.App.Version,pointer.App.TreeSha256);
        return pointer;
    }
}

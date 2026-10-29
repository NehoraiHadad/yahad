using System.IO;
namespace Yahad.Setup;
internal enum UninstallMode { KeepData=1, DeleteData=2 }
internal sealed class UninstallChoice
{
    internal const string ConfirmationPhrase="מחיקה";
    internal UninstallMode Mode{get;private set;}=UninstallMode.KeepData;
    internal string Confirmation{get;set;}="";
    internal bool CanBegin=>Mode==UninstallMode.KeepData||Confirmation==ConfirmationPhrase;
    internal void Select(UninstallMode mode){Require(mode);if(Mode!=mode){Mode=mode;Confirmation="";}}
    internal UninstallMode Confirmed(){if(!CanBegin)throw new IOException("UNINSTALL_CONFIRMATION_REQUIRED");return Mode;}
    internal static void Require(UninstallMode mode){if(mode is not(UninstallMode.KeepData or UninstallMode.DeleteData))throw new IOException("UNINSTALL_MODE_INVALID");}
    internal static string Phase(UninstallMode mode){Require(mode);return mode==UninstallMode.DeleteData?"removing_all_data":"removing_keep_data";}
    internal static bool SameInstallation(InstallerState expected,InstallerState actual)=>
        expected with{Phase=actual.Phase,Files=actual.Files}==actual&&expected.Files.SequenceEqual(actual.Files);
    internal static void RequireSameIntent(string phase,UninstallMode mode)
    {Require(mode);if(phase.StartsWith("removing_",StringComparison.Ordinal)&&phase!=Phase(mode))throw new IOException("UNINSTALL_MODE_CHANGED");}
}

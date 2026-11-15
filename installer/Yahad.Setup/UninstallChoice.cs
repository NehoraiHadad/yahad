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
    // Records compare arrays by reference, and these two were read separately.
    internal static bool SameInstallation(InstallerState expected,InstallerState actual)=>
        expected with{Phase=actual.Phase,Active=actual.Active,Previous=actual.Previous}==actual&&SamePair(expected.Active,actual.Active)&&
        (expected.Previous is null||actual.Previous is null?expected.Previous==actual.Previous:SamePair(expected.Previous,actual.Previous));
    internal static bool SamePair(InstalledPair expected,InstalledPair actual)=>
        expected.Infrastructure with{Files=actual.Infrastructure.Files}==actual.Infrastructure&&expected.Infrastructure.Files.SequenceEqual(actual.Infrastructure.Files)&&
        expected.App with{Files=actual.App.Files}==actual.App&&expected.App.Files.SequenceEqual(actual.App.Files);
}

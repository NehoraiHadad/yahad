using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace Yahad.Install;

internal sealed record BootstrapHelperServiceContract(RecoveryHelperSlot Slot,string Name,string Account,string Executable,string Arguments,
    uint ServiceType,uint StartType,string FailureCommand,bool FailureActionsOnNonCrash);
// Declarative installer handoff only. OS-owned cmd/sc are fixed literal paths;
// no user command, URL, account or service name is interpolated. No registration,
// task, registry, ACL or SERVICE_CHANGE_CONFIG call is performed by this type.
internal static class BootstrapRecoveryProvisioningContract
{
    internal static RecoveryHelperSlot Peer(RecoveryHelperSlot slot)=>slot switch{RecoveryHelperSlot.A=>RecoveryHelperSlot.B,RecoveryHelperSlot.B=>RecoveryHelperSlot.A,_=>throw new IOException("HELPER_SLOT")};
    internal static BootstrapHelperServiceContract Describe(RecoveryHelperSlot slot)
    {
        var system=SystemDirectory();var account=@"NT SERVICE\"+BootstrapRecoveryLayout.ServiceName(slot);
        return new(slot,BootstrapRecoveryLayout.ServiceName(slot),account,BootstrapRecoveryLayout.Image(slot),"--system-recovery-service "+slot,16,2,
            Quote(Path.Combine(system,"sc.exe"))+" start "+BootstrapRecoveryLayout.ServiceName(Peer(slot)),false);
    }
    internal static string PrimaryFailureCommand()
    {
        var system=SystemDirectory();var sc=Quote(Path.Combine(system,"sc.exe"));
        return Quote(Path.Combine(system,"cmd.exe"))+" /D /Q /S /C \""+sc+" start "+BootstrapRecoveryLayout.ServiceName(RecoveryHelperSlot.A)+" || "+sc+" start "+BootstrapRecoveryLayout.ServiceName(RecoveryHelperSlot.B)+"\"";
    }
    private static string Quote(string value)=>"\""+value+"\"";
    private static string SystemDirectory()
    {var value=new StringBuilder(32768);var length=GetSystemDirectoryW(value,(uint)value.Capacity);if(length==0||length>=value.Capacity)throw new Win32Exception();return WindowsFileBoundary.CanonicalLocalPath(value.ToString());}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetSystemDirectoryW(StringBuilder path,uint length);
}

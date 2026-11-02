using System.ComponentModel;
using System.IO;

namespace Yahad.Setup;

// Only fixed operation names and numeric error identifiers cross the elevated
// boundary. Exception messages can contain private paths and must stay out.
internal enum UninstallPhase
{
    ReadState, InspectReceiptRecovery, InspectRecoveryService, InspectRecoveryHost, InspectRecoverySetup, InspectRecoveryReceipt, CompareRecoveryState, VerifyReceiptRecovery, ValidateBinaries, ValidateRegistration, StopService, AcquireDataLease,
    InspectData, WriteRemovalReceipt, ProtectRemovalReceipt, SaveRemovalState,
    RemoveLan, RemoveService, WaitForServiceRemoval, PreflightDataDeletion,
    DeleteData, DeleteDataRoot, DeleteBinaries, RemoveRegistration, SavePreservedState, DeleteDiagnostic, DeleteReceipt,
    FullPrepare, FullProvision, FullNetwork, FullStart, FullUninstall
}

internal sealed class SetupOperationException(UninstallPhase phase, Exception cause)
    : IOException("UNINSTALL_OPERATION_FAILED", cause)
{
    internal UninstallPhase Phase { get; } = phase;
    internal bool DiagnosticRecorded { get; set; }
}

internal sealed record SetupFailureDetail(string? Phase, int? NativeError, int HResult,bool DiagnosticRecorded=false,string? RecoveryCheck=null,string[]? Sites=null)
{
    internal static Exception Cause(Exception error) =>
        error is SetupOperationException { InnerException: { } cause } ? cause : error;

    internal static SetupFailureDetail From(Exception error)
    {
        var cause = Cause(error);
        return new(error is SetupOperationException operation ? operation.Phase.ToString() : null,
            cause is Win32Exception native ? native.NativeErrorCode : null, cause.HResult,
            error is SetupOperationException {DiagnosticRecorded:true},
            cause is Yahad.Install.ReceiptRecoveryRefusal refusal ? refusal.Check.ToString() : null,
            Yahad.Install.ServiceStartupDiagnostic.Sites(cause));
    }

    internal string Display() =>
        (RecoveryCheck is null ? "" : RecoveryCheck+" | ")+
        (Phase is null ? "" : " · " + Phase) +
        (NativeError is { } native ? " · Windows " + native : " · HRESULT " + HResult.ToString("X8"))+
        (DiagnosticRecorded ? " · פרטי התקלה נשמרו" : "");
}

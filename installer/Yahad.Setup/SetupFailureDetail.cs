using System.ComponentModel;
using System.IO;

namespace Yahad.Setup;

// Only fixed operation names and numeric error identifiers reach the window
// and the result file. Exception messages can contain private paths and must stay out.
internal enum UninstallPhase
{
    StopService, AcquireDataLease, InspectData, WriteRemovalReceipt, SaveRemovalState,
    RemoveLan, RemoveService, WaitForServiceRemoval,
    DeleteData, DeleteDataRoot, DeleteBinaries, RemoveRegistration, SavePreservedState, DeleteReceipt,
    FullPrepare, FullProvision, FullNetwork, FullStart, FullUninstall
}

internal sealed class SetupOperationException(UninstallPhase phase, Exception cause)
    : IOException("UNINSTALL_OPERATION_FAILED", cause)
{
    internal UninstallPhase Phase { get; } = phase;
}

internal sealed record SetupFailureDetail(string? Phase, int? NativeError, int HResult,string[]? Sites=null)
{
    internal static Exception Cause(Exception error) =>
        error is SetupOperationException { InnerException: { } cause } ? cause : error;

    internal static SetupFailureDetail From(Exception error)
    {
        var cause = Cause(error);
        return new(error is SetupOperationException operation ? operation.Phase.ToString() : null,
            cause is Win32Exception native ? native.NativeErrorCode : null, cause.HResult,
            Yahad.Install.ServiceStartupDiagnostic.Sites(cause));
    }

    internal string Display() =>
        (Phase is null ? "" : " · " + Phase) +
        (NativeError is { } native ? " · Windows " + native : " · HRESULT " + HResult.ToString("X8"));
}

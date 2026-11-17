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

// Exception is the cause's type name, never its message: beside a generic code it
// is what tells a developer which kind of failure nothing named.
internal sealed record SetupFailureDetail(string? Phase, int? NativeError, int HResult,string[]? Sites=null,string? Exception=null)
{
    internal static Exception Cause(Exception error) => error switch
    {
        SetupOperationException { InnerException: { } cause } => Cause(cause),
        AggregateException { InnerExceptions: [var first, ..] } => first,
        _ => error
    };
    // A cleanup that also failed is reported beside what caused it, not in its place.
    internal static AggregateException Besides(Exception cause, Exception incomplete) => new(cause, incomplete);

    internal static SetupFailureDetail From(Exception error)
    {
        var cause = Cause(error);
        return new(error is SetupOperationException operation ? operation.Phase.ToString() : null,
            cause is Win32Exception native ? native.NativeErrorCode : null, cause.HResult,
            Yahad.Install.ServiceStartupDiagnostic.Sites(cause), Types(cause));
    }
    // The cause and what it wraps (a SERVER_NOT_READY around the probe's timeout),
    // outermost first, by type name only.
    private static string Types(Exception cause)
    {
        var names = new List<string>();
        for (Exception? current = cause; current is not null && names.Count < 4; current = current.InnerException) names.Add(current.GetType().FullName ?? current.GetType().Name);
        return string.Join(" < ", names);
    }

    internal string Display() =>
        (Phase is null ? "" : " · " + Phase) +
        (NativeError is { } native ? " · Windows " + native : " · HRESULT " + HResult.ToString("X8"));
}

using Yahad.Install;
namespace Yahad.Authority;

// One SCM-owned A/B waiting helper is used for ordinary handoff, process failure
// and boot recovery. No direct-child fallback, alternate source or scheduler.
internal static class SystemBootstrapHelperLauncher
{
    internal static SystemBootstrapHelperSession Start(InstalledStateFiles files,InstalledStateFiles binaries,VerifiedSystemOperation operation,
        SystemBootstrapSnapshot observedBefore,RetainedRuntimeQuiescence runtime,WindowsFileBoundary.DataLease data,
        WindowsFileBoundary.DataLease serial,string dataDirectory,FullSystemUpdatePolicy policy)
        =>ScmRecoveryHelperLauncher.Start(files,binaries,operation,observedBefore,runtime,data,serial,dataDirectory,policy);
    internal static SystemBootstrapHelperSession StartRecovery(InstalledStateFiles files,InstalledStateFiles binaries,VerifiedSystemOperation operation,
        SystemBootstrapSnapshot observedBefore,RetainedRuntimeQuiescence runtime,WindowsFileBoundary.DataLease data,
        WindowsFileBoundary.DataLease serial,string dataDirectory,FullSystemUpdatePolicy policy)
        =>ScmRecoveryHelperLauncher.StartRecovery(files,binaries,operation,observedBefore,runtime,data,serial,dataDirectory,policy);
}

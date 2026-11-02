# Query-only primary-token startup failure

Native source `61ca980d182a144a95acc146f90c4ecf3d0ea51c` reached FullStart and failed with UPDATE_SERVICE_START_FAILED. Application/Yahad event 1001 at 02:59:42 and 02:59:44 Asia/Jerusalem identified Authority and RecoveryA: SecurityException HRESULT 0x8013150A in WindowsInstalledProcessObjectKernel.Session.ObserveIdentity. Runtime remained stopped/1077. This is not a successful installation.

ObserveIdentity opens the current primary token with TOKEN_QUERY (8), then called WindowsPrincipal.IsInRole. That method duplicates a primary token into an identification token and throws SecurityException when duplication fails. WindowsIdentity preserves the source handle's access rights when copying its handle. See official .NET sources:

- https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Security.Principal.Windows/src/System/Security/Principal/WindowsPrincipal.cs#L134-L158
- https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Security.Principal.Windows/src/System/Security/Principal/WindowsIdentity.cs#L232-L289
- https://learn.microsoft.com/windows/win32/api/securitybaseapi/nf-securitybaseapi-checktokenmembership

The new read-only own-process probe reproduced the same SecurityException and HRESULT before the fix. After the fix the identical probe succeeds. It reads the test process's own identity only, without changing DACLs or operating services.

The fix derives administrator rejection from the already queried TokenGroups: exact built-in Administrators SID, enabled and not deny-only. It keeps TOKEN_QUERY and all process generation, token ID, no-impersonation, AppContainer, privilege, service identity and DACL guards. It adds no rights or installed configuration changes. An enabled admin SID conservatively causes rejection even in an unusual restricted token; this is not a general replacement for Windows access checking.

Validation: candidate build succeeded with zero warnings/errors; own-process regression 1 passed; affected synthetic updater identity/process access suite 157 passed, zero failed, including seven group-attribute cases. Independent actual-diff security/minimality review found no actionable issue. No actual service acceptance or new installer success is claimed. Logs, exit codes and narrowly filtered native events are retained in `.local/token-query-fix` and `.local/delivery-61ca980`.

# Windows full-updater service identities and preparing-only cancellation

The owner reaffirmed automatic updates of both the application and the updater.
The existing fixed A/B recovery services remain; no legacy migration framework,
new scheduler, password-managed account or privileged account fallback is added.

## Observed failure

The protected sanitized diagnostic from the ASUS attempt recorded FullProvision,
Win32 1057. Runtime and Authority names were present (query denied to the normal
caller); A/B and LAN names were absent. Absence was not inferred from denial.
Creation order and the frozen contract showed the first A/B registration using
the other service's virtual account. Windows virtual accounts use the matching
service name. See [CreateServiceW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-createservicew)
and [1057](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1000-1299-).

## Focused correction

- A/B each use their corresponding virtual account. Compiled service-name SIDs
  are checked against Windows resolution before publishing full-role grants.
- Authority/A/B retain only service query/start/stop (0x35); Runtime and the
  original Owner have query-only (0x5). Administrative SCM ownership remains.
- Immutable full-profile consent enables exactly these three updater actors.
  App-only role validation remains strict. Owner RPC remains Primary-only.
- Custody requires actual fixed SCM PID/image/account and process creation time.
  A nonce-bound child probe is observed separately and cannot receive custody or
  pass the ordinary updater identity guard.
- Each process publishes only its own narrow process/token peer permissions.
  Runtime has no custody grant. There is no debug or token-duplication privilege.
- WRITE_DAC is limited to creation through the existing generated restore-staging
  view. Promotion preserves ownership and removes this right; exact already-final
  objects are skipped after interruption. Selected new migration workspaces keep
  their existing scoped DACL/owner normalization; source/live data stays excluded.

## Failed pre-enrollment installation

Ordinary package-digest retry, enrolled uninstall and Format4 remain unchanged.
The normal authenticated elevated DeleteData flow can cancel only protected
Format3 preparing state with no role binding, completed service provisioning,
policy/plan pin or preserved inventory. Binary root must be empty; data root may
contain only that unchanged state and one complete manifest-verified staging
directory. No dataset, key, activation, enrollment or extra file is admitted.
Runtime/Authority must match fixed configuration, minimal privileges,
administrative-only ownership/DACL and stopped demand status1077/PID0. A/B, LAN
and uninstall registration must be absent.1077 alone is not historical inactivity
proof; the independent no-publication/no-data inventory is required.

An immutable administrative Format5 cancellation intent is published before any
deletion. Retries allow missing planned entries, reject changed/new entries,
await bounded pending service deletion, verify hashes through no-follow/single-link
handles, delete only planned files and empty roots, and retain the receipt last.
Completion is asynchronous. Diagnostics disarm after receipt deletion and before
the final UI notification, so a disconnected window cannot recreate residue.

## Validation status

Focused synthetic identity/process/migration tests:149 passed,0 failed.
Final installer/cancellation run:48 passed,0 failed, including17 deletion cuts,
pending observation, lost final notification and asynchronous final verification.
Independent read-only review found the pending-deletion and final-notification
cases; fixes and regressions passed, and its final recheck found no remaining
issue in the reviewed scope. Builds had0 warnings and0 errors. Broader regression
and source-bound packaging results are recorded in the local delivery report.

No actual reinstall, service/account/ACL change, data deletion, update application,
LAN/firewall/trust change, provider connection or publication was performed while
developing this correction. Native positive acceptance of the corrected exact
package remains pending. Unsigned distribution and outstanding licensing evidence
retain their previously recorded status; synthetic tests do not close them.

## Standalone size follow-up

The existing verified EXE is576035434bytes; its embedded installZIP is432542213
bytes, leaving143493221bytes for Setup/runtime. Identical-content groups in that
ZIP account for217671986 repeated compressed bytes. That is an upper bound for
deduplication, not a claim that independent runtime/probe closures can be removed.

Only low-code packaging changes were made: remove Format2's duplicate host/
closure (36005915 compressed bytes in the old package), map its full-profile LAN
inventory from the existing bootstrap/servicehost namespace, retain Format1's
host contract, stream DEFLATE9 and reuse the same audited ZIP for both transports.
Single-file compression is enabled through the SDK; actual EXE-only launch,
startup and memory measurements remain a prerequisite before candidate use.
[Microsoft describes the startup/memory trade-off](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview#compress-assemblies-in-single-file-apps).

The old512MiB per-member removal cap also rejected the measured standalone EXE.
Only exact Yahad.Setup.exe now has a1GiB cap; other members retain512MiB. The
removal intent's8GiB aggregate cap remains. Focused installer tests now51pass,
including actual-size/boundary, Format1/2 ZIP shape and LAN namespace regressions;
the targeted Python full-system suite has19passes. Final native packaging metrics
are recorded separately, not extrapolated from these older artifact numbers.

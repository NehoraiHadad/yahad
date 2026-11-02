# Full Windows updater: isolated implementation handoff

Implementation source: `6041e15d85c4a4c7ce1f24bde10d6108e6801f91`.
Base: `060e241fe064e492dd8756a2172c0439bf288903`, including the installer
`887648e4ddf10015820a809369492672b117a0e5` adapter-identity fix. Historical
`50483e7` and the `13839ef` foundation remain in history. No Setup,
NativeSetupHost, Store, receipt producer, packaging or workflow source differs
from installer `887648e4` in this handoff.

The user required minimum routine interaction, including application, database
schema and updater self-updates after explained initial consent. The code now
implements that separate explicit `full-system-v1` profile within the existing
Authority/Owner/ServiceHost/Install.Core updater. It does not reinterpret or
extend the old 90-day/100-operation/same-schema grants.

## Implemented

- Protected full-profile Auto/Notify consent and admission, fixed GitHub stable
  intake, strict system/component manifests and existing scheduler/backoff.
  Notify cannot auto-apply; revocation remains accessible during downloads.
- Actual runtime/data/global ownership adapters, durable operation journals,
  verified backup before disruption, and SCM-owned A/B handoff helpers for
  replacing Authority, RuntimeHost, Owner, probe and helper closures.
- Runtime-account offline migration with a fixed entry and zero-network
  AppContainer contract, new-directory data copies, typed probation and one
  generation-bound effects release. Unknown outcomes do not authorize resend
  or restoring old data over possible later writes.
- Observed crash recovery, rollback/forward repair, bounded availability restart
  of already committed current data after revocation, retained cleanup retry,
  failed-release quarantine, atomic interrupted intake metadata and audit
  compaction that does not consume a lifetime 512-operation limit.
- Protected request/status completion, recovered last-update timestamps,
  progress UI and compiled native Owner confirmation/recovery entry points.

## Executed validation

| Check | Result | Limit |
|---|---:|---|
| Final exact-commit scoped C# suite | **587 passed, 0 failed** | Private fixtures; SCM/token/admission observations are synthetic |
| Existing shared production harness | **119 passed, 0 failed** | Same production source bytes before commit metadata changed; no real SCM |
| Relevant application tests | **75 passed, 0 failed, 2 skipped** | Two missing-helper failures passed a focused rerun after fixture preparation |
| Scoped/Core/ServiceHost/Authority, Platform and Owner builds | **0 warnings, 0 errors** | Production entry points remain disabled |
| TypeScript typecheck | **passed** | Node 24.22.2, TypeScript repository version |

The shared harness's earlier **105/14** result is preserved locally; its final
119/0 includes actual private Store workers and two successive legacy routine
launches with one bounded enrollment. They reached healthy in **6.751s** and
**9.293s**. This is not a real installed two-release full-system acceptance run.

The scoped suite includes real private SQLite, WAL, native file/data leases and
owned child jobs, successive current-clone source contexts, backup/restore,
current-data preservation after effects, and synthetic crash cuts. The actual
fixed TypeScript migration entry was exercised for source schemas 1 through 5
on new copies, preserving credentials/history/unknown-send records and original
bytes. A/B SCM, actual identity, service ACL, AppContainer and IPC observations
remain fixture contracts, not native acceptance.

The preceding expanded run was 584/2: one test incorrectly assumed an empty
SafeDirectory root despite its fixture marker; the other omitted JsonException
as an expected malformed-record refusal. The final run preserves exact tree
invariance and malformed-data refusal and passes both. Review also fixed native
cleanup ownership/retry, uncertain Go, current-data availability restart and
terminal restart retry findings before the final build. Targeted independent
reviews found no further P0/P1 in the reviewed final changes.

The two web skips are the Linux backup adapter and Windows privileged symlink
creation. No machine privilege/security change was attempted. The first default
sandbox run also hit expected Store chmod/fixture ACL restrictions; the private
authorized run is recorded separately rather than counted as production proof.

## Source and artifact identity

All **402** captured source inputs matched their raw SHA256 after final tests.
`windows-full-updater-source.json` also binds their committed Git blob IDs.

- Source manifest SHA256: `c5a939c032c0a4d1176676806c570ed262f0221af9eaee8a8551caad19a11bc9`.
- Machine-readable tests SHA256: `14b623b04336fc7716412ed4ec6db7122d8d664d5fb6ae08dbaaec838c85fb86`.
- Final scoped DLL SHA256: `1f1512edb0340b996807326e95a59cda1d816c779246ccf9b35e63b485779491`.
- Unapplied activation patch SHA256: `797f155c12c2043ee36768e65220978ffc0082bec2ca604034d5775219f0a0b2`.

The final C# source commit was built using .NET SDK 10.0.403. The machine-readable
report records raw log/script hashes and labels the older verified baseline
Platform helper used only by the two web shutdown fixtures. Logs, scripts,
incremental Git bundle and cumulative installer-base diff are retained under
`.local/delivery`; no remote publication occurred.

To reproduce the scoped suite after preparing a private offline .NET cache,
build `tests/windows-updater/Yahad.Production.Tests.csproj`, then run its Release
DLL with `--node <Node24.exe>`. Set a private, reasonably short TEMP/TMP, as in the
archived script. Reproduce the shared harness using its archived generator and
`existing-suite-budget.patch`; the installer-owned original Program.cs was not
edited. Synthetic idle observations and positive-fixture wait changes are
explicit in that patch; production health limits were not arbitrarily widened.

## Installer handoff and remaining gates

Use [the integration contract](../docs/WINDOWS-FULL-UPDATE-INSTALLER-HANDOFF.md)
and its unapplied activation patch for Authority, ServiceHost and Owner.
`git apply --check` passed. The installer must provision consent/anchor/source,
role ACLs, fixed Authority and A/B helper services, migration profile and native
Owner shortcuts, and build the complete source-bound system candidate package.

**Pending, not denied:** native Windows provisioning/repair/uninstall,
same-Authority-account cross-service logon and custody, own-object DACL access,
Runtime-user AppContainer isolation, real native probation/HTTP/provider release,
physical crash/reboot recovery and two successive synthetic installed full
packages in Auto followed by Notify with no auto-apply. The real ASUS install
and LAN test remain solely with the installer task after integration.

Authority/Owner entry points and the admitted Runtime activation remain disabled
in the actual source. No installed services/tasks, installed ACLs, profiles,
security settings, real updates, WhatsApp, pushes, PRs or releases were operated.
This handoff completes isolated implementation and its scoped checks; it does
not claim installed production readiness.

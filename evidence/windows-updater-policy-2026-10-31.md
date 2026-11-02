# Windows updater policy and UI handoff

This is an isolated code/test tranche, **not an enabled Windows automatic updater**.
Base: `268bef4f4bd09a56c78fe8257aee86046bf642fb` (installer tree
`ec2323e11712a3b493fe453ba17580848f8777ab`). The supplied bundle SHA-256 was
`37319270714c3c1f7a1a140a72f876cef4b7493691513977e34cdc46ffef879b`.
The updater began at main `6887f99f83ce245f68277c14c5e5818c02d8f5a8`;
all edited production preimages were equal after normalizing line endings to
the current installer source. The branch was moved onto the installer commit
before final validation, preserving its Store/receipt/packaging changes.
No repository or ancestor AGENTS.md, packaged .agents/.codex instructions or
repository SKILL.md were found in this checkout.

## Changes

- Explicitly approved enrollment gets a fresh ledger ID; template version,
  schema, origin, data/key/policy/pin anchors, absolute expiry, operation budget
  and payload bound are retained. Superseding enrollment revokes its predecessor.
  The existing 90-day/100-operation ceiling and schema are unchanged.
- Notify revokes the automatic grant before publishing the protected mode. It
  can contract authority during pending approval/work. It cancels a queued
  automatic operation, clears pending enrollment, and cannot mint authority.
- A queued operation rechecks approval before attempting runtime quiescence.
  Cancellation, expiry or revocation before disruption gets a durable cancelled
  outcome. Failures after disruption still retain recovery-required state.
- Expired, revoked, exhausted or out-of-scope routine approval does not enter a
  data transaction. A check returns `approval_required`; an explicit install
  asks for exact native owner approval. Disk/corrupt/uncertain failures retain
  the conservative recovery behavior.
- The ledger retains its native exclusive lease and waits at most two seconds
  for short concurrent checks. Busy status reports unknown limits. A failed
  contraction on a busy ledger has not published a durable RPC, so retry can
  remain safe. Delegation filenames must bind the stored delegation ID.
- The application maps approval-required to HTTP 409
  `UPDATES_APPROVAL_REQUIRED` and a specific Hebrew message. Protected delegation
  expiry/budget/status is sanitized for the parent UI. Native Notify remains
  reachable during pending/recovery phases while operator identity, freshness,
  enabled state and mode-control policy still gate submission. Legacy recovery
  gates remain unchanged. No lost POST/answer is retried automatically.
- The existing service controller now retains a safe startup-stage/elapsed-time
  observation with an atomically latched first failure. A separate one-shot root
  exit observation distinguishes an observed early exit from requested forced
  cleanup. Neither is a claim of tree quiescence. The authority health controller
  exposes these observations internally for diagnosis. No lifecycle deadline,
  launch, waiting, cleanup or grant-disposal behavior was changed.

## Test ownership and reproduction

`tests/windows-updater/` is a separate executable using the existing
`Yahad.Production.Tests` friend boundary. It introduces no new production
accessibility or environment/CLI activation path. It creates only private
synthetic files and never starts an installed service, opens a listener or
downloads/applies a real release. Example:

```powershell
& tests/windows-updater/run.ps1 -DotNet <absolute-dotnet.exe> -PackageCache <private-cache>
```

`tests/windows-updater-policy-ui.test.ts` adds mocked-native status/renewal,
Notify contraction, HTTP error, parent UI and authenticated Fastify injection
tests. The ordinary npm test glob includes this file.

The shared `installer/Yahad.Production.Tests/Program.cs` was not edited.
`tests/windows-updater/existing-suite-budget.patch` updates the fixture to
observe the approved fresh delegation ID and expect structured renewal after
budget exhaustion. Positive routed-install success waits use a bounded two minutes, with elapsed
time and timeout-phase diagnostics: the original 20-second total wait was shorter
than the existing stop-plus-admission/startup path. Production and negative-test
deadlines are unchanged. It retains both actual synthetic launches and the assertion
that no extra owner challenge is created. `git apply --check` succeeds on the
current installer source. For validation the existing suite was copied into a
private harness with exactly this patch; the product Authority/ServiceHost/Core
code was built from this checkout.

The two positive health-and-lease/reconciliation fixtures also use actual production
constructor defaults instead of their original 10-second startup/5-second lifecycle values.
The unchanged-bounds diagnostic reproduced readiness cancellation at five seconds;
there was no observed natural child exit before forced termination. With unchanged
production defaults, actual readiness took 9.815 seconds and the child then exited
0 after graceful stop, with tree-empty, held-lease and cleanup checks passing.
Only positive fixtures were corrected; negative tests and production limits
were not widened. Both diagnostic artifacts and the first failing full-run
reports were retained.

The existing suite uses the unprivileged Platform helper copied into this
workspace from the installer build. Its `--describe` identifies source
`268bef4f4bd09a56c78fe8257aee86046bf642fb`; the helper EXE SHA-256 is
`d4f326661603f2fe6b210a50fa053c72fe1ba4572d1611d7d886c8d2ca900499`.
Platform and Core sources are unchanged by this tranche. An attempted fresh
self-contained helper publication was blocked by an incomplete offline
WindowsDesktop runtime package cache and a timed-out NuGet request; the
source-matched helper avoids changing or repairing machine runtimes.

Final counts and exact source hashes accompany this document in
`windows-updater-policy-validation.json`. Earlier checks passed 60 focused
TypeScript tests with one expected Windows symlink-privilege skip; the untouched
existing endpoint suite passed 11 cases and failed its now-obsolete
exception-only budget assertion. The copied harness validates the revised
expectation. A first full copied-harness run passed 118 cases and hit that old
20-second second-update fixture deadline once. Its failure evidence was retained;
the success deadline correction was independently reviewed before rerunning.
This observation alone does not establish load as the cause.
The next full run passed both updates (15.037/14.330 seconds) but exposed the
five-second positive health fixture described above; its 118-pass/1-fail result
was also retained before the final full rerun.
That rerun exposed the same short bound in the positive reconciliation fixture
and a 20-second composition wait. Its later rapid recovery refusals retained
zero-byte WAL/32 KiB SHM residue from the interrupted private fixture; existing
WAL guards refused it before the later intended paths. The 114-pass/5-fail report
and residue were preserved without deleting or checkpointing them. The next run
uses a fresh private dataset, both positive health defaults, and bounded positive
router waits; the failure-injection probe wait and all negative bounds are unchanged.
That fresh-root run finished **105 passed, 14 failed**: the first positive health
case failed even with product defaults, followed by WAL refusals and cascades.
It remains a validation blocker. A prefix-preserving diagnostic later passed the
health target (3.468-second readiness, exit 0, held lease, cleanup clear) but had
seven earlier recovery probe/prerequisite failures. Its deeper temporary layout
adds 27 path characters (probe DB 257; WAL/SHM 261), a plausible confounder rather
than a proven cause. No WAL/SHM was deleted or checkpointed to clear a failure.
The exact original full-context health failure has not yet been captured with
the new internal observations; no blanket rerun or further limit increase is
claimed as a fix. The independent deterministic policy/diagnostic suite passes
22 cases. The 60-pass/one-skip web suite and six-case API/UI recheck remain passed.
Independent read-only review found and drove the fixes for ledger
contention, stale enrollment, and authorization before quiescence; the final
review was clear for this bounded tranche.

## Integration required before product activation

| Existing component | Current source / handoff |
| --- | --- |
| Authority/Owner | Production entries still disabled; native pipe adapters are compiled, not accepted on installed identities. |
| Installer consent/enrollment | Sole installer owner must wire explained Auto/Notify consent and protected enrollment, preserving the unelevated owner when another admin approves UAC. This tranche changes no installer file. |
| Native app transport | `UnconfiguredWindowsUpdateTransport` remains the production default. The real authenticated runtime adapter and native owner/status/recovery activation remain necessary. |
| Status mapping | Map C# `Delegation.State/ExpiresUnix/RemainingOperations` into lowercase JS `delegation` fields, and shrink submissions to the strict `{state}` result. Notify permits null/omitted delegation. JS request IDs are canonical UUIDs with hyphens; native frame IDs require lowercase `N` format. The adapter needs one stable, verified bijective mapping, not new IDs on a lost reply. Supply protected designated-parent identity and real installed version/check/update timestamps; page polling must not invent a successful background check. |
| Intake | Receipt selection remains protected and revalidated, but the fixed HTTPS `NehoraiHadad/yahad` stable-release downloader/protected publisher is still missing. Existing receipt fixtures are not release provenance. No signing-key project is introduced; hashes alone are not publisher authentication. |
| Scheduling / idle | No production scheduler, bounded retry/backoff or child-interaction/recording/in-flight-send idle handshake is enabled. Actual process-tree quiescence and data-lease primitives must be reused; a synthetic empty gate is not production proof. |
| Update safety | Existing transaction, recovery, backup and schema guards are exercised separately. Mandatory verified data+keys+policy backup/prior payload, update-wide rollback health orchestration, new-write-safe rollback and repeated-failure quarantine still require end-to-end wiring. Never restore over later writes or resend unknown sends. |
| Interrupted requests | A crash after Notify returns but before the scheduled cancellation callback writes Done still reconstructs as recovery-required. The regression explicitly asserts that window. Owner-authenticated observed-state reconciliation/UI remains required; this tranche does not infer safe completion. |
| Renewal / defer | An expired absolute template is rejected; it is never silently extended. A protected renewal policy/UI flow and explicit defer control are still required. Fresh enrollment within the existing template needs a fresh native owner answer. |
| Native acceptance | No real UAC, SCM lifecycle, cross-account ACL/IPC, service/task registration, reboot, live update or real WhatsApp test was attempted. Two synthetic Store transitions prove only the exercised fixture path. |

Bootstrap/Authority binary replacement remains the separately elevated installer
path. NativeSetupHost, receipt, Store, packaging, Pages and the other checkout
were not modified. There was no push, PR, merge, release or deployment. No
operation is awaiting approval; the remaining items above are implementation
and acceptance work, not successful production activation.

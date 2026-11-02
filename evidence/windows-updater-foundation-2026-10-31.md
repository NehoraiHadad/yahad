# Windows updater foundation checkpoint

This checkpoint extends the existing Authority / Owner / ServiceHost / Install.Core updater. It is isolated development, not installed Windows acceptance or completion of the subsequently requested unattended full-system profile.

## Source and coordination

- Historical checkpoint preserved: `50483e780b2973dfabf9f8baaa022fa172cf0aa5`, based on installer `268bef4f4bd09a56c78fe8257aee86046bf642fb`.
- New installer commit inspected and imported read-only: `887648e4ddf10015820a809369492672b117a0e5`. Its eight changed paths are in Setup and FirstInstall.Tests; updater changes do not overwrite them. Integration must retain this commit.
- Exact checkpoint input manifest: `windows-updater-foundation-source.json`, SHA256 `7b6bb822a1be1b690da33cb9ed115bc1c382dfadbe7744a2a849612a657d01b6`. Every listed file still matched after the checkpoint run. New full-system development files are outside that manifest.
- NativeSetupHost, installation receipt, Store and packaging source were not edited. The existing Format-2 `installation.json` remains intact; the updater uses the separate `update-authority.json` anchor in the existing FirstInstall root.
- Authority and Owner production entry points remain disabled. ServiceHost refuses legacy startup when an updater anchor is present until the combined admitted profile is enabled deliberately. No real installed service, task, ACL, AppContainer, update, WhatsApp, publication or deployment was operated.

## Implemented foundation

- Fixed GitHub stable native source, protected source enrollment, required archive digest, bounded redirects/download/extraction, strict native manifest/path/component checks and existing receipt/pointer publication.
- Protected cache rotation verifies source/receipt evidence and exact file bytes through the same exclusive native handle. Only verified archive-cache files are retired. Payloads, family data, backups and audit records are retained. Partial/unknown evidence fails closed.
- Actual SCM/pipe/job adapters compiled behind inactive entry points: waiting host admission, exact PID/creation time, protected identity/configuration, outer-job assignment, authenticated readiness and retained cleanup ownership.
- Durable browser activity admission, generation CAS, clean reload ownership, recording/draft/send guards, atomic app idle fence, parent deferral and explicit same-account stale-admission settlement. Settlement relies on an explicit user assertion about prior windows; it is never inferred from elapsed time.
- Manual Notify installation keeps its exact approved request queued for idle; ordinary busy checks do not spend delegation operations. Lost idle responses require confirmed id-bound cancellation, with uncertain framing remaining a recovery gate.
- Background scheduling, retry/backoff, protected deferral, repeated candidate failure quarantine and an asynchronous fresh-source check that outlives the short endpoint reply.
- Installed backup/restore adapter, actual retained generation and data-lease duplication, new-directory restore, scoped ACL publication, append-only update journal and exact Owner recovery. Completion publishes RPC Done before the terminal journal, while every unfinished journal independently gates startup.

## Tests actually executed

Checkpoint 5 compiled the scoped tests, Platform and Owner with **zero warnings/errors**. The scoped binary passed **202 / 202**, including actual private SQLite, real private child jobs and native file/lease operations. HTTP replies, SCM identities and installed-role metadata remain synthetic. These are not real installed-service or pipe-ACL acceptance tests.

Earlier checkpoint 3 passed 162 and failed 2. The failures found an expired queued operation bypassing validation during admission-only checks and a status reader racing a protected Done write. Both were fixed and passed checkpoint 5.

Checkpoint 4b passed 185 and failed the new actual WAL backup case. A focused same-byte experiment proved the cause: a database path of 258 characters produced a WAL path of 262 and SQLite error 14; the identical bytes opened at a shorter private path. The selected installed probe namespace now uses the full existing canonical 32-character DataId, instead of a 64-character path hash. Full path identity remains authenticated in recovery evidence. The regression logs the old 258/262 and selected 226/230 lengths, verifies the original WAL database hash remains unchanged, and passes backup/new-directory restore. No source WAL/SHM deletion, checkpoint or timeout widening was used.

Private web/API checkpoint: **15 / 15**. TypeScript compilation passed. Browser persistence/lock/settlement helper probes passed **20 / 20**; they do not establish React lifecycle or real-browser acceptance.

Local diagnostic artifacts are under `.local/checkpoint5-*`, `.local/wal-path-diagnostic.json` and `.local/wal-path-threshold.json`.

## Historical native-suite gate remains separate

The earlier complete shared production harness result, **105 passed / 14 failed**, is preserved. It was not overwritten by this new scoped suite. A later exact-short-environment prefix on `50483e7` passed 95 and failed 3, while its named health target passed. The isolated original first-handoff prefix passed 18/18 with all independent health/lease/quiescence verdicts successful. Those observations do not explain every historical failure. A final exact-source shared harness and native installed acceptance are still required; no repeated full native suite was run without a relevant new change.

## Updated product requirement

The human subsequently required full unattended updates for application, databases and updater components after one explained initial installer consent. Work continues on that explicit new profile. Legacy same-schema, 90-day / 100-operation grants remain unchanged and cannot authorize it. New full-policy consent, system candidate closure, bootstrap handoff and isolated migration/probation must be implemented and tested before the complete requirement can be claimed.

Installer integration must provision the separate anchor and source/mode records; immutable probe; Authority account/service; role ACLs; narrow cross-process admission rights; native Owner entry; startup/uninstall/recovery contracts; and the chosen full-system self-replacement recovery helper. The sole installer task retains ownership of those changes and real acceptance operations.

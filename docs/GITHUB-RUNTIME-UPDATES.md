# Explicit GitHub-HTTPS updater self-updates

This is a **new owner-enrolled profile**, not a signature-verification bypass.
It allows project releases to update the privileged worker, then the application
and supported database schema, without creating signing keys. Publication does
not activate it; an existing machine still needs the reviewed one-time local
bootstrap. No host deployment, provider interaction or real-data migration is
implied by local synthetic tests.

## Trust and immutable boundary

The owner delegates future updater-code authority to stable GitHub Releases in
**NehoraiHadad/yahad**, through certificate-verified HTTPS. The fixed repository,
allowed GitHub asset API/redirect hosts and bounded archive rules remain enforced.
There is no configurable URL, shell hook or destination path in a release/web
request. SHA-256, manifest digests and byte counts bind exact bytes; **they do not
independently authenticate the publisher**. A compromised GitHub publishing
account/repository can publish malicious privileged worker code under this trust
choice. The owner accepts that boundary explicitly rather than being asked to
manage a signing key.

The runtime archive contains only `updater.py`. The launcher, supervisor,
transport, helper ABI, source policy and systemd permissions are installed once
and cannot be changed through this release path. OS/Node/ffmpeg/Caddy changes,
permission/source changes or an incompatible immutable ABI require a separately
reviewed bootstrap. This supports compatible project upgrades end-to-end, not
all conceivable future host changes.

Signature-pinned installations remain signed. A missing/failed signature never
falls back to this profile. The HTTPS bootstrap refuses signature pins, signed
source policies, unknown direct workers and conflicting partial enrollment; it
never deletes keys or rewrites app trust.

## One-time bootstrap on an existing installation

Supported starting workers are the exact historical legacy worker and the exact
reviewed protocol-2 direct worker installed by the 0.5.0 manual transition, or
the exact new protocol-3 direct worker from the reviewed fresh installer. The
bootstrap verifies existing helper bytes, safe ownership/paths, concrete operator,
stable matching app journal, lock and loaded/quiet systemd units. Unknown layouts
or pending recovery are a stop, not permission to rerun fresh setup.

Prepare a local standalone artifact from the final reviewed checkout:

```sh
python3 scripts/package-github-upgrade.py /PRIVATE_NEW_OUTPUT
```

It contains the new bootstrap, its hash-pinned reusable manual helper module,
immutable supervisor/transport, the separate target-bound request helper with its
reviewed reusable module, and exact initial runtime JSON/ZIP. There are no
signatures, keys, credentials or family data. The outer archive hash must be
compared with the reviewed handoff. A checksum served beside an unchecked archive
is not an independent publisher identity check. This command is packaging only;
it does not publish an asset or provide a download URL.

After verifying and extracting the delivered artifact, use an ordinary owner
account and a local interactive terminal:

```sh
sudo python3 -I -B upgrade-worker-github.py --check-only
sudo python3 -I -B upgrade-worker-github.py
sudo python3 -I -B request-update-github.py
```

The mutation path requires the exact confirmation **`ENABLE GITHUB UPDATER`**.
This explicitly authorizes future privileged updater downloads from the fixed
owner repository. The source profile becomes enabled while existing app
`enabled`, auto/notify mode, operator, version pin, data/key paths and status bytes
remain unchanged. No new key, token or backup secret is created. Check-only and
blank/incorrect confirmation create no files and change no services.

The existing worker lock is held across confirmation/provisioning. Only active
updater timer/path schedulers are temporarily paused and restored; inactive ones
stay inactive. Existing app code/data are not replaced by bootstrap. It verifies
the initial worker through read-only capability probes, installs explicit
read-only unit boundaries, and atomically replaces the launcher **last**. The
capability probe reads the current schema to establish compatibility; it does not
migrate the database or contact a provider. Restored schedulers may immediately
execute an already authorized automatic update/request.

Save the printed prior scheduler states. SIGKILL, power loss, uncertain replacement
or a probe changing protected state can leave schedulers paused. Inspect the
read-only check and local state before restoring only previously active schedulers;
never blindly restore old worker code, clear journals or downgrade a migrated DB.
An exact completed enrollment is a no-op even after its runtime has advanced;
rerunning bootstrap does not reset policy, version history or the active runtime.

## Initial app install from the existing 0.5.0 UI

The third command is a **separate** local app-install flow, not an automatic step
inside bootstrap. It bridges the old UI, which cannot send the new target-bound
install contract. It first queues and correlates a fresh check. Under an existing
`auto` policy that check may already install; the helper warns about this and
verifies the resulting exact version/schema readiness without queuing a duplicate.
No mode is silently changed.

Otherwise it displays the root-reported version and release fingerprint and asks
for exactly `INSTALL <version>`. It rechecks the target, configuration/operator
and HTTPS enrollment under the existing worker lock, then queues an install bound
to that same `{version,fingerprint}`. A different latest release yields
`target_changed`, not silent substitution. Both worker and transport revalidate
that identity before updater/app mutation. It preserves an existing queue, prints
a correlation UUID before publication and waits for that UUID's terminal result,
matching target, exact installed version and recorded schema readiness. It never
opens the family DB, restores data or guesses that waking systemd means success.

If interrupted, use the printed UUID without queueing again:

```sh
sudo python3 -I -B request-update-github.py --status PRINTED_REQUEST_UUID
```

This status command is read-only and does not require a new owner/TTY confirmation.
Timeout means pending/unconfirmed, not canceled. A recovery-required or failed
request is a stop for inspection; the helper does not clear recovery, reset policy
or retry a different target automatically.

## Release publication and update order

Produce a no-key worker bundle locally:

```sh
node scripts/release-updater.mjs package-github 1.1.0 /PRIVATE_NEW_RUNTIME_OUTPUT
```

It produces `yachad-updater.json` and `yachad-updater.zip`, not an empty/fake
signature. Signed publication retains the separate `package VERSION OUT KEY`
command. Publish matching app and updater assets in a normal stable semantic app
Release only after reviewing and testing the exact candidate. Packaging itself
never publishes or activates anything.

Discovery uses GitHub’s `/releases/latest` selection, not a scan for the highest
semantic version. Versions still must advance. The target fingerprint binds all
named asset objects in that selected release, not merely its displayed version.
Changing/replacing release assets invalidates an already displayed target; perform
a fresh check and confirmation rather than reusing stale intent.

The normal sequence uses one fixed release/asset selection. Before committing
privileged updater code, transport checks the selected app's bounded JSON and ZIP,
including their exact manifest hash and archive integrity. The selection also
binds the updater runtime hash. Only then does it verify/probe/commit the compatible
updater. The worker performs a second bounded download, verifies the same selected
manifest/archive identity, and builds/smoke-tests the app before downtime. A later
fetch/build failure may therefore leave the newly committed updater installed;
it does not imply that the app or database was upgraded, and that worker is not
automatically downgraded.

The worker stops the app, writes and syncs an encrypted pre-update backup,
activates the app, allows its transactional migration as the app user, and checks
exact version, declared schema and actual SQLite schema. In notify mode, checks
only discover; installation needs an explicit valid
target-bound install request. In auto mode, checks follow the existing
owner-authorized automatic installation policy. Recovery/boot paths are offline.

An updater can advance its implementation/protocol within the fixed ABI. A DB
migration must explicitly support the actual previous schema and have a passing
synthetic fixture. Skipped unsupported schemas, incompatible ABI, unavailable
runtime/dependencies and storage failure cannot be made safe merely by labeling
a release compatible.

## Failure and verification contract

Pre-commit runtime failures retain/recover the previous verified worker. A worker
that has committed is **not** rolled back because subsequent app/DB work failed.
App recovery binds the runtime digest and previous/target schema. Old app code
may run only when the actual database still matches its prior schema. If a
migration committed and readiness then fails, `recovery_required` stops the app:
no automatic DB downgrade or restoration of stale messages/WhatsApp credentials.
Recovery may adopt an interrupted activation only when the persisted target
path/version/manifest digest, actual database schema and exact readiness all
match the journaled pair. Boot recovery does not prematurely claim that an app
is ready before it has started and passed that health check. It cannot mark an
unrelated request successful. A separately reviewed forward repair is preferable
to restoring old data.

Tests must cover source substitution, signature-profile non-regression, archive
and manifest tampering, replay/same-version replacement, inherited locking,
bootstrap cancellation/idempotence/conflicting enrollment, scheduler failures,
crash/durability boundaries, the joined worker→app→SQLite transition, release
selection races, exact readiness and pre/post-migration failures. Linux fixture
results do not establish ARM/systemd/live-provider or power-cut acceptance.
Those require separately authorized disposable-host acceptance before relying on
fully automatic operation on the actual installation.

## Five-minute release checks

New app configurations and new HTTPS enrollments use `checkHours: 0.08333333333333333`
(five minutes). The existing `yachad-update.timer` already uses
`OnUnitInactiveSec=5min`, with up to 20 seconds jitter and systemd's timer accuracy;
expect approximately five to six minutes after the previous service finishes,
not a strict wall-clock deadline. Existing explicit intervals are preserved.

The root transport and direct application worker have separate caches. Configure
both `/etc/yachad/updates.json` and (when enrolled)
`/etc/yachad/updater-source.json` consistently. Existing six-hour integer values
remain supported. Root policy accepts only finite numeric intervals from five
minutes through seven days; booleans, strings, zero and shorter intervals fail
closed. Normal transport failures retain the interval backoff. Worker locks,
failed-version suppression, explicit-request semantics and auto/notify policy
are unchanged. A manual request can bypass a scheduled cache as before.

When the exact app target is already healthy/idle and the enrolled runtime
manifest is byte-identical, automatic HTTPS checks fetch release metadata and
the small runtime manifest only. They do not download ZIPs or authorize app work.
Changed app targets/runtime manifests still undergo complete existing preflight.
The root transport's `checkedAt` advances; the application status's `checkedAt`
is not rewritten by this metadata-only path. No new app check/install is claimed.

### Existing Raspberry Pi

For the protocol-2 direct worker installed by the 0.5.0 one-time replacement,
this interval already works without replacing code. Inspect the existing files:

```sh
sudo sha256sum /usr/local/lib/yachad-updater/updater.py
systemctl cat yachad-update.timer
sudoedit /etc/yachad/updates.json
```

For that exact direct worker the SHA-256 is
`22cd73dc810d08d90e1ec6af4e4b482bc60a0f42b44e1bf874c78682375264cd`.
Change **only** `"checkHours": 6` to `"checkHours": 0.08333333333333333`;
keep mode, enablement, operator, trust, paths and all other settings unchanged.
No service restart, timer replacement or application restart is required: the
next existing timer invocation reads the configuration. Check the timer with
`systemctl list-timers yachad-update.timer --all`. In auto mode this can install
an already published newer release at the next check; in notify mode it only
notifies. This does not enable updater self-updates.

Do not apply fractional intervals to an older HTTPS enrollment: its immutable
supervisor/transport/request helper reject them. Those components require a
separately reviewed local transition; an app ZIP or runtime update cannot replace
that privileged boundary. The new bootstrap is for eligible direct-worker
transitions and is not a general migration of already enrolled older launchers.
Do not rerun a bootstrap or overwrite immutable files to bypass that check.

The five-minute bootstrap is a new `yachad-github-updater-bootstrap-1.0.1.zip`
artifact with runtime 1.0.1; never replace the already published 1.0.0 archive.
The runtime now records a worker-liveness heartbeat on scheduled deferred/no-op
transport results, preserving app `checkedAt` and its existing outcome. This
keeps parent controls usable after 24 hours without claiming a new app preflight.

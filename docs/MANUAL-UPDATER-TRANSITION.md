# Existing Pi: explicitly manual protocol-2 transition (no signatures)

This is a separate owner-run path, **not a bypass of the signed supervisor**.
It upgrades only the exact known legacy direct worker so that the existing app
updater can handle app 0.5.0 / database schema 2. It does not create signing keys,
enroll trust, download privileged code, install the app or migrate the database.
The signed supervisor and its enrollment path are unchanged.

**It does not provide unsigned automatic updater self-updates.** Future changes
to privileged worker code still require a separately reviewed manual procedure.
The existing normal app-update policy is preserved, including GitHub-HTTPS trust
on installations already configured for it; a signature-pinned app policy is
never changed to HTTPS-only trust.

## Exact supported installation

- Legacy worker from commit `07be02f`, SHA-256
  `6b1523a207ecff1ad7bff19c80d85809f4e453125bf5b5ecece64463465a6cad`.
- New protocol-2 direct worker (unchanged from reviewed 0.5.0), SHA-256
  `22cd73dc810d08d90e1ec6af4e4b482bc60a0f42b44e1bf874c78682375264cd`.
- Existing `data-lock.py` and `export-dependencies.py` must match their explicit
  reviewed LF or historical 0.3.2 CRLF hashes. Their bytes are **not replaced or
  normalized**. Unknown workers/helpers are refused, not guessed from a version.
- No existing/partial signed-supervisor enrollment, runtime journal, key, policy
  or unit drop-ins. This procedure will not downgrade a signed installation.
- Existing root-owned/private config and lock, concrete configured operator,
  stable matching app journal, no pending recovery/mode change/running request,
  no running or queued systemd worker/recovery job, no stale loaded units.

Run as the ordinary owner account via `sudo` in an interactive local terminal,
with no simultaneous updater/installer/admin commands. `--check-only` is read-only;
blank/no confirmation is cancellation. An exact current direct worker is a no-op,
with no scheduler toggling. Do not rerun the fresh installer against live data.

## What is preserved, and what is changed

The bootstrap never opens the app database, auth store, environment, master key
or backup secret. It never imports app code or probes the live SQLite database.
It reads only reviewed source/helpers and private updater configuration/journal.
Only `/usr/local/lib/yachad-updater/updater.py` is replaced: staged in its existing
root-owned directory, fsynced, atomically renamed, directory-fsynced and verified;
its owner/group/mode are retained. Helper files, systemd unit files/enablement,
release trust, operator, auto/notify choice, version pin and status bytes stay as-is.

The existing worker lock remains held across confirmation and replacement.
Only active updater timer/path units are temporarily paused and restored; already
inactive ones remain inactive. No app service is stopped by this bootstrap.
**Restoring a previous active scheduler may immediately run a queued request or
an already enabled automatic app update**, which is separate from bootstrap code.

Before replacement, errors retain the old worker. If an atomic replace fails
and the exact old inode/bytes/metadata are confirmed, prior schedulers are restored.
After an ambiguous replacement/durability failure, or detected config/helper/journal
change, schedulers stay paused for local inspection. No automatic DB/config restore
or downgrade is attempted. Scheduler restoration failures are reported as
unconfirmed rather than claimed successful. Ctrl+C/ordinary terminal termination
uses cleanup, but SIGKILL/power loss can interrupt it. Save the printed prior timer/
path runtime states. Inspect `--check-only` and local systemd state before restoring
only schedulers that were previously active. Never blindly roll back this worker
after schema 2 has committed, clear recovery journals or overwrite WhatsApp auth.

## Standalone delivery and commands

Build offline (from this reviewed checkout; no private key needed):

```sh
python3 scripts/package-manual-upgrade.py .local/manual-upgrade-0.5.0
```

The new destination must not exist. Outputs:

- `yachad-manual-upgrade-0.5.0.zip`, containing only
  `upgrade-worker-manual.py`, the pinned `updater.py`, and
  `request-update-manual.py`.
- `SHA256SUMS` for the archive and `FILES.sha256` for the three exact source files.

The archive hash must come from the separately reviewed handoff, not merely from
an unchecked download alongside the archive. Hashes check exact bytes; they are
not signatures or independent publisher authentication.

**Publication status:** the manual package is new and is not an asset of the
already published app 0.5.0 release. App publication alone does not publish this
bootstrap. No working `curl` URL is claimed. Delivery needs the exact prepared
archive (as a file handoff) or separate authorized publication and download/hash
read-back. The final handoff supplies its real SHA-256 and a concrete local shell
sequence; do not replace this with a raw `curl | sudo python` command.

After verification and extraction, these are the actual local commands inside
the extracted folder (the complete archive-verification sequence is in the final
handoff):

```sh
sudo python3 -I -B upgrade-worker-manual.py --check-only
sudo python3 -I -B upgrade-worker-manual.py
sudo python3 -I -B request-update-manual.py
```

The last command is **separate explicit app-install intent**. It preserves existing
policy and operator, queues one fresh correlated request as the app user without
overwriting an existing queue, then wakes the existing request service. Its printed
request ID supports correlation if interrupted. A queue/service exit alone is not
proof of app readiness. It uses the existing latest-release policy; this helper checks the resulting
version is exactly 0.5.0, which is an outcome assertion, not an immutable release
selector. An existing conflicting
version pin, disabled updates or pending recovery is a stop, not permission to reset
configuration. No real provider test is performed here.

A successful bootstrap alone says the worker can understand the schema-2 contract;
it does **not** say app 0.5.0 is installed. Successful app installation additionally
requires correlated terminal success, expected app version and schema-2 readiness.
After database schema 2 commits, failed health leaves the existing fail-closed
recovery path; old schema-1 app code must not be started against that database.

## Verification boundary

Tests use the exact historical worker fixture, reviewed current bytes, temporary
synthetic files and injected systemd/ownership effects. They cover checksum and
unsafe-file refusal, no-op/check/cancel, pending recovery, locks, scheduler races,
interruptions, atomic failures, existing data/config preservation and deterministic
packaging. These are not live Pi/systemd, ARM, power-loss or real WhatsApp acceptance.

# Immediate parent update requests

With the new root integration installed, a successful parent click wakes the
worker immediately instead of waiting for the five-minute timer. This starts
processing; downloading, building, backup, health checks or an update already in
progress can still take time. The UI keeps requested and confirmed mode separate.

## Event and authority boundary

- Existing session, parent/operator, CSRF, strict-input and pending gates remain.
  The web app gains no sudo, systemctl permission, shell or arbitrary command.
- The app writes/fsyncs a private temporary file, then publishes complete JSON
  with an atomic, no-overwrite hard link to `DATA_DIR/update-request.json`.
  Concurrent clicks cannot replace the first request. Temporary files are removed.
  Once the authoritative queue is published, wake-latch failure still returns
  queued: the request may already execute, with path-change/timer fallback.
- A second hard link, `DATA_DIR/update-request.wake`, is only a wake latch.
  `yachad-update-request.path` observes its existence and changes to the queue.
  The fixed root service clears the latch at **start**, never at exit. A click in
  the service shutdown/re-arm window therefore remains level-triggered. Queue
  content is not trusted merely because a path unit fired.
- The request service executes `updater.py requests`, not scheduled `run`.
  It waits for the existing worker flock, then rereads policy and queue so a
  click is not dropped while a timer worker is active. Root retains ownership,
  file-type/size, operator, action and age validation. Expired intent is rejected,
  not silently renewed after a long-running operation. Authenticated modern
  check/install requests receive a matching terminal `failed/expired` journal
  before removal, with no release fetch/install in that consuming invocation.
- Missing/deleted/invalid queue events never become scheduled checks or installs.
  An unchanged blocked queue is attempted once, not spun on. A bounded drain
  handles a subsequent click during processing, reloading applied mode between
  actions. A mode change alone still never fetches/installs a release.
- The original timer remains enabled for scheduled discovery, journal recovery
  and fallback after reboot, an old app, or event-service failure. Configuration
  or recovery failures still require repair; immediate wake-up does not bypass
  them. In auto mode, an explicit **check can install** a newer release, as
  before; in notify mode it only checks.

The latch avoids both `PathExists` on a permanently blocked request causing a
restart loop, and edge-only `PathChanged` missing a click while its service is
active. See the official [systemd.path reference](https://www.freedesktop.org/software/systemd/man/latest/systemd.path.html).

## Truthful status

`queuedRequest` identifies a still-queued action. Root writes `lastRequest`
(`check`/`install`, ID, running/succeeded/failed, start/finish times, error) before
removing that queue. Mode requests retain the separate pending/applied/rejected
`lastModeRequest` contract. A next serialized worker marks an orphaned running
request failed; interrupted work is never reported successful merely because
the queue disappeared. The fallback timer handles orphaned work without a new
queue event. Actual phases are checking, downloading, verifying, building,
stopping, backing_up, activating and checking_health, followed by real terminal
state. These are stages, not invented percentages. Older workers omit request
journals; the UI must report unconfirmed progress rather than infer success.

## Installation and compatibility

The guided owner installer installs the request path/service, configures both
watched paths and latch cleanup from the validated `DATA_DIR`, gives the request
service the same root-only mode-config exception and read-only secret paths as
the scheduled service, and enables/verifies the path only at successful
installation completion. Windows/WSL verifies it alongside the timers.

**An app ZIP does not install privileged helpers, units or drop-ins.** An existing
Pi needs a one-time reviewed owner/root upgrade of the worker, request service/
path and matching drop-ins, then daemon reload and path enable/verification.
Existing app/worker combinations keep the timer fallback. Do not install the
new path against an old worker: it does not understand the request-only command
and may treat a stale event as a scheduled run. Do not rerun the historical
0.3.2 bootstrap; it is version-pinned and not this upgrade. The current guided
installer intentionally refuses different existing managed helper bytes; it is
not a generic overwrite/migration tool. Preserve live data, keys, trust policy,
operator, applied mode and journal during owner review.

## Evidence and remaining acceptance

Synthetic tests cover complete atomic publication, concurrent clicks/private
permissions, symlink collision preservation, a real local flock wait with queue
replacement and policy reread, mode-to-check, stale/deleted events, malformed
input, blocked recovery/configuration, bounded draining, truthful journals and
installer wiring. They make no real GitHub/provider calls, start no services and
use no live family data.

Native-systemd timing/sandbox behavior still needs acceptance on a disposable
Linux/WSL/Pi installation: click while idle; mode then check; click during active
work and its exit window; repeated clicks; blocked queues without restart loops;
timer fallback; reboot persistence. Measure processing start rather than promise
instantaneous installation. This cloud container has no systemd PID 1: fixture
checks are not live-host acceptance and nothing was installed on the owner's Pi.

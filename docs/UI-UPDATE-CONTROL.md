# Parent control of automatic updates - 0.3.2 candidate

0.3.1 already has designated-parent Check GitHub, confirmed Install, current/
available versions and lifecycle/error status. 0.3.2 adds an auto/notify switch
through the existing request queue and root worker. An application release does
not replace the separately installed privileged worker or systemd integration.

## Effective state, requested state and permission

Only the configured operator family AND parent user may request mode changes.
Sessions, parent role and CSRF remain required. The strict API accepts only
`{action:"set_mode", mode:"auto"|"notify"}`. It cannot choose repository, signing
key, paths, credentials, operator identity or root `enabled`/policy flags.

Root configuration must have `enabled:true` AND `allowUiModeControl:true` before
UI mode control is available. Both are local owner gates; defaults remain false.
An optional local `installVersionPin` limits the first manual install to the
approved version even if `releases/latest` changes between check and install.
The installer clears it only after the manual update/acceptance gates. It is not
an API/UI-editable setting; leave it null for normal subsequent automatic releases.
Provision `allowUiModeControl:true` only after approved one-time setup, backup/
restore/off-device recovery and the first manual update/health acceptance. Root
can retain the gate closed. The UI never edits root configuration directly.

The switch shows the mode confirmed by the worker. A submitted request shows
separate pending text and keeps the previous effective switch value. It becomes
applied only after the worker reads/persists the private config and acknowledges
the request. While the panel is open, status refreshes every three seconds.
Network failure, stale status, local-policy rejection and pending write failure
do not imply success. The panel still offers explicit refresh/check/install.

The worker normally wakes about every five minutes; release checks default to
six hours. Choosing notify stops future automatic installs after acknowledgment;
it does not cancel an install/rollback already in progress. Keep the request timer
running in notify mode so checks/manual installs and mode requests are consumed.
Backup expiry remains separate: at most seven update backups and seven days;
the daily expiry timer does not delete the owner's separate off-device copy.

## Root handling and interruption

Queue inputs are bounded private regular files owned by the app user, opened
without following links. Root validates exact operator, action/mode, canonical
UUID, fields and ten-minute timestamp window, rejecting nonfinite timestamps.
Replay IDs are retained for twenty minutes, bounded at 64; saturation rejects
new changes rather than evicting still-valid replay protection.

Root journals a pending mode request AND its operator identity before removing
the queue file. Only `mode` is changed in the private root config; all other
fields must still match. An operator change rejects the former operator's intent.
Config write and acknowledgment are reconciled idempotently after interruption.
Failed acknowledgment writes retain root intent; UI reports pending/error until
it is confirmed. Recovery/rollback takes precedence over mode changes, and a mode
change run does not install a release in that same run. Existing worker locking
serializes these operations. No arbitrary root command or UI configuration editor
is added.

## Mixed versions and one-time installation

- New app + old 0.3.1 worker: mode control stays disabled, with a local-setup message.
  Existing check/install/status continue. Absence of capability is not permission.
- Old app + new worker: check/install remain compatible; the new fields are extra
  sanitized status. Existing legitimate API queue writes already use mode 0600.
- Existing root config without `allowUiModeControl`: defaults closed; no automatic
  security-sensitive policy expansion.

The owner reports that updater helpers, config, trust file, backup-secret file,
service and timer are all absent on the Pi. The one-time owner installer is being
prepared separately, with signed root staging, no-overwrite checks, hidden
passphrase/escrow and local confirmation before service actions. It must use the
matching approved signed mode-capable release, then install the authenticated
root helpers once. Publishing an app alone cannot solve the missing root worker.

At this commit, 0.3.1 is the published signed release and this 0.3.2 candidate is
not published or deployed. The desktop installer draft deliberately returns
`DRAFT_ONLY` before any action until release pins are updated after approval.
Do not run an obsolete-worker bootstrap and assume the UI switch will work.
Use the existing signing key/public fingerprint; no new credential is required.
Preserve `/var/lib/yachad/live`, `/etc/yachad/whatsapp.key`, existing env/linked
device state and Caddy CA. Do not rerun initial family setup.

No Pi operation, root provisioning, service restart, automatic activation, new
credential or release publication was performed while preparing this candidate.
Actual Pi ARM64/native-systemd/interruption and live history acceptance remain
separate owner-operated gates.

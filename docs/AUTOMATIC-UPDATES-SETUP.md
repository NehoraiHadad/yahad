# One-time owner setup for signed automatic updates (0.3.1)

Read [HISTORY.md](HISTORY.md) for the 0.3.1 history limits and real-account acceptance gate before rollout.

This is an **owner-operated runbook**, not a record of work already done on the Pi.
The development session did not connect to the Pi, deploy, enable a timer or
Actions, create a persistent signing key, or change WhatsApp/security settings.
Merge approval, signing-key approval, release publication and Pi activation are
separate owner decisions. A GitHub push or merge by itself does **not** update a Pi.

## Preserve the installation that already works

The owner reports a Pi 4B ARM64, Debian 13, 4 GB RAM, Node 24.22.1/npm 11.19.1,
ffmpeg, Caddy/systemd, and an existing private LAN HTTPS origin. The current **live**
configuration is `DATA_DIR=/var/lib/yachad/live`,
`MASTER_KEY_FILE=/etc/yachad/whatsapp.key`, `CONNECTOR=baileys`. Keep these exact
values. The mock store `/var/lib/yachad` and its different master key must stay
separate. Never run demo or first-run setup against the live database; never
regenerate a master key for an existing database. Preserve `/etc/yachad`, all data,
and Caddy's existing CA/storage. Do not replace the environment file with an example.

The reported WhatsApp sign-in and ready-health check are useful, but do not prove
group delivery, restart, backup restore, rollback or power-interruption acceptance.
No new WhatsApp activation is required or authorized by this runbook.

## 1. Approve and validate the exact candidate first

Use the draft PR's final SHA. Review the diff and run the following on an isolated
Linux test machine with Node 24, Python 3, OpenSSL and ffmpeg. Tests use synthetic
data and injected network/service effects. The root updater suite operates only
inside temporary fixture directories; it does not run the installed Pi worker.

```bash
npm ci --ignore-scripts --no-audit --no-fund
npm run typecheck
npm test
python3 -m unittest discover -s tests -p 'test_*.py' -v
# Repeat under root in an ISOLATED Linux fixture environment for the five
# root-ownership lifecycle tests. Preserve the Node 24 PATH when using sudo.
sudo python3 -m unittest discover -s tests -p 'test_*.py' -v
npm run build
```

Windows validation has explicit Linux-mode skips. It is not full Linux, ARM64,
systemd, fsync, kernel-lock, restore or hardware acceptance. Do not enable auto
mode until the staged checks in section 6 are complete.

## 2. Establish publisher trust — explicit owner approval required

No persistent signing credentials were generated during development. After
explicit approval, on a **private owner-controlled publishing machine**, outside
the repository and off the Pi:

```bash
# Owner-selected new private path; command refuses existing paths.
node scripts/release.mjs keygen /PRIVATE_PATH/release-signing.pem
openssl pkey -pubin -in /PRIVATE_PATH/release-signing.pem.pub -outform DER | sha256sum
```

Keep the private PEM in private offline storage with a recovery copy. Do not put
it in Git, on the Pi, in chat, or in Actions secrets. Publish only the public key.
Transfer that public key to the owner locally and compare its DER SHA-256
fingerprint through an independent trusted channel. A key downloaded from the
same untrusted release as an update is **not** a trust anchor. Root provisions the
trusted public key once. Key rotation is a separate deliberate local root action;
the worker never replaces trust from release contents. Loss of the private key
stops new signed releases; suspected compromise requires disabling updates and
reviewing/replacing trust locally.

For every approved stable release, from the reviewed clean checkout:

```bash
npm run typecheck && npm test && npm run build
node scripts/release.mjs package /PRIVATE_OUTPUT/v0.3.1 /PRIVATE_PATH/release-signing.pem
```

After owner approval, publish tag `v0.3.1` and a non-draft, non-prerelease GitHub
Release in `NehoraiHadad/yahad` with exactly these three matching assets:
`yachad-release.json`, `yachad-release.sig`, `yachad-app.zip`. All future releases
need a higher stable package version and a matching tag and manifest. Packaging is
offline and does not publish. No Actions workflow needs to be enabled. The owner
can sign/upload manually; automated publishing needs its own credentials approval.

The Ed25519 signature authenticates the exact manifest bytes; its SHA-256 and byte
count bind the archive. Checksums alone do not establish publisher trust. The
worker accepts only schema 1, stable increasing versions, a verified archive and
matching package version. It installs production dependencies from the lockfile
with lifecycle scripts disabled, as a separate builder account with no data/key
access, then runs a mock smoke test. GitHub or npm outages cause failure without
activating a release. Dependency availability and publisher trust remain relevant.

## 3. Install root helpers once, preserving current code and data

Only after approving the candidate and a maintenance window, use a local Pi owner
terminal. First confirm the actual layout and binaries (these checks show paths,
versions and ownership, not key contents):

```bash
node --version; npm --version; command -v node; command -v npm
readlink -f /opt/yachad/current
stat -c '%U:%G %a %n' /opt/yachad /opt/yachad/releases /var/lib/yachad/live /etc/yachad/whatsapp.key
curl --fail http://127.0.0.1:3000/health/ready
```

`current` must be a symlink to a root-owned release under `/opt/yachad/releases`;
the app and builder must not be able to write the release trees or parent links.
If the existing layout differs, stop and plan a data-preserving conversion; do
not delete or overwrite `current`. Keep the current release for rollback. The
following commands assume a reviewed candidate checkout is the current directory.

```bash
# Create the isolated builder only if it does not already exist.
id yachad-update || sudo useradd --system --home /var/lib/yachad-update-build/home --shell /usr/sbin/nologin yachad-update
sudo install -d -o root -g yachad -m 0750 /var/lib/yachad-updates
sudo install -d -o root -g root -m 0755 /var/lib/yachad-update-build
sudo install -d -o yachad-update -g yachad-update -m 0700 /var/lib/yachad-update-build/home
sudo install -d -o root -g root -m 0755 /usr/local/lib/yachad-updater
sudo install -d -o yachad -g yachad -m 0700 /var/backups/yachad-updates
sudo install -o root -g root -m 0644 deploy/updater.py deploy/data-lock.py deploy/export-dependencies.py /usr/local/lib/yachad-updater/
# New installation only: do not overwrite an existing updates.json.
sudo test ! -e /etc/yachad/updates.json
sudo install -o root -g root -m 0600 deploy/updates.json.example /etc/yachad/updates.json
sudo install -o root -g root -m 0644 /OWNER_TRANSFER/release-signing.pem.pub /etc/yachad/release-signing.pub
openssl pkey -pubin -in /etc/yachad/release-signing.pub -outform DER | sha256sum
```

Compare the fingerprint before proceeding. `/etc/yachad` and its ancestors must
be root-owned and not application-writable. Do not copy the private signing key.

Provision a separate local **backup-encryption passphrase**, only after owner
approval. If one already exists, preserve it. It is not the login password or
signing key. A generated backup secret must be escrowed in the owner's password
manager/off-device recovery storage; losing it makes the backups unusable.

```bash
sudo bash -euc 'test ! -e /etc/yachad/update-backup-passphrase; test ! -L /etc/yachad/update-backup-passphrase; umask 077; openssl rand -base64 48 > /etc/yachad/update-backup-passphrase'
```

Do not paste the secret into shell arguments, environment files, chat or logs.
The worker reads its root-only file and passes it to the unprivileged backup
helper on stdin. Backups contain encrypted database/media/auth and the master key.
They are sensitive even though encrypted. Retention is seven copies/seven days.
Test an authenticated restore into a **new isolated directory**, with networking
disabled and sessions revoked, before relying on them. Never restore over live
data or start a mock connector against the restored live store.

## 4. Configure exact paths and the designated parent

Find parent IDs from the correct live store, read-only:

```bash
sudo -u yachad python3 - <<'PY'
import sqlite3
with sqlite3.connect('file:/var/lib/yachad/live/app.sqlite?mode=ro', uri=True) as db:
    for row in db.execute("SELECT family_id,id,username FROM users WHERE role='parent'"):
        print(row)
PY
sudoedit /etc/yachad/updates.json
```

Keep `enabled:false`, `mode:"notify"` initially. Set `operator.familyId` and
`operator.userId` to that parent's actual IDs. Set `dataDirectory` to
`/var/lib/yachad/live` and `masterKeyFile` to `/etc/yachad/whatsapp.key`. Keep
`backupDirectory=/var/backups/yachad-updates`, `currentLink=/opt/yachad/current`,
`releasesDirectory=/opt/yachad/releases`, and `healthUrl=http://127.0.0.1:3000/health/ready`.
Use the actual absolute Node/npm executable paths from section 3. The public
repository needs `tokenFile:null`; do not create a GitHub credential for it.
Config stays root-owned mode 0600; the public key is root-owned and not writable
by app/builder. The application reads only sanitized status, never these secrets.

## 5. Install service integration in a maintenance window

Keep `/etc/yachad/yachad.env` unchanged, especially its live data/key paths and
WhatsApp approval. The app service uses `${DATA_DIR}` from that file for its kernel
lock. It still runs under `yachad`. Only the reviewed root worker can switch code.

```bash
sudo systemctl stop yachad-update.timer   # if previously installed
sudo systemctl stop yachad.service
sudo install -o root -g root -m 0644 deploy/yachad.service deploy/yachad-update.service deploy/yachad-update.timer deploy/yachad-update-recover.service deploy/yachad-update-backup-expiry.service deploy/yachad-update-backup-expiry.timer /etc/systemd/system/
sudo install -d -o root -g root -m 0755 /etc/systemd/system/yachad.service.d
sudo install -o root -g root -m 0644 deploy/update-recovery.conf /etc/systemd/system/yachad.service.d/
sudo systemctl daemon-reload
sudo systemctl start yachad-update-recover.service
sudo systemctl start yachad.service
curl --fail http://127.0.0.1:3000/health/ready
sudo systemctl start yachad-update.service  # disabled config: writes status, installs nothing
```

Preserve any existing site-specific systemd overrides. Do not overwrite Caddy,
its environment, CA or HTTPS trust. The worker's writable root `/var/lib/yachad`
already contains `/var/lib/yachad/live`; no extra access to a different store is
needed. If your actual paths differ, review sandbox writable paths before enabling.

The kernel data guard survives event-loop stalls and is released on process death;
the normal app, update backups and documented offline helpers share it. The
root-owned updater lock serializes workers, recovery and local account reset.
The boot unit is required before both application and update worker startup, so a
worker cannot accidentally make the application's first boot guard race its own
lock. Do not remove lock files to work around contention. Use the documented
wrapper for offline setup/backup; direct custom Node commands can bypass it.
The build parent is root-owned; only its private `home` child and individual
temporary build directories belong to the builder. Root preserves the verified
source separately and receives regular dependency files through a bounded ZIP
stream exported **as the builder**. Privileged code never follows paths under the
builder's control or copies builder-modified application source into a release.

## 6. Stage activation and verify on the actual installation

After the isolated Linux suite and restore drill pass, change only
`enabled:true, mode:"notify"`, then start `yachad-update.service` manually. Check
the parent update panel and `systemctl status yachad-update.service`. Until a
correct signed GitHub Release exists, a network/archive error is expected and
there is nothing to install. A draft PR or source ZIP is not a signed release.

Before choosing install, confirm an off-device backup/key recovery path and a
maintenance window. Install the approved release, then verify exact expected
`version`, `databaseSchema:1`, `ok:true` from loopback health, parent sign-in,
child sign-in/grants, group send/receive, media, existing WhatsApp link, and a
controlled service restart. Verify backup restore, invalid signature rejection,
failed build/backup, health-failure rollback, incompatible-schema refusal, lock
contention, and interrupted worker/reboot recovery on a **disposable clone**.
Do not power-cut the live SD card as a test. An interrupted activation restores
previous code and checks its version; it never restores/downgrades the database.
If the schema changed or rollback health fails, updates remain
`recovery_required` and the owner must investigate locally.

Once those gates pass and the owner approves autonomous installs, change
`mode:"auto"` and enable:

```bash
sudo systemctl enable --now yachad-update.timer
sudo systemctl enable --now yachad-update-backup-expiry.timer
systemctl list-timers yachad-update.timer yachad-update-backup-expiry.timer
```

The timer wakes about every five minutes; release checks default to every six
hours. Parent check/install requests are consumed on a timer wake. Updates require
internet access to GitHub/npm. A failed version is not automatically retried in a
loop; explicit parent install can retry it once visible after a check. An
interrupted or failed rollback blocks all new installs, even with updates disabled.
Disabling the timer does not undo a completed update or resolve recovery.

To stop future automatic installs, stop the update timer and set `enabled:false`
locally; allow any active worker to finish/recover. Do not force-switch `current`
while the worker is active. For `recovery_required`, preserve status, code trees,
data/key and backups; check the schema and the previous version on an isolated
copy. Fix the actual problem and rerun the root worker's `recover` command. Never
edit schema numbers or restore an older database automatically to make health pass.

## First-run browser setup and family accounts

Existing parent and child accounts do not need to be recreated. The new setup
flow is only for a **new empty store** on a deliberately authorized local setup
session. Configure its own data directory, key and origin first. For a new mock
installation only, from the reviewed release directory, after stopping its app:

```bash
# Example NEW mock store; never substitute the existing live store.
sudo -u yachad env DATA_DIR=/var/lib/yachad/new-mock MASTER_KEY_FILE=/etc/yachad/new-mock.key APP_ORIGIN=https://ACTUAL_PRIVATE_LAN_IP COOKIE_SECURE=true CONNECTOR=mock ALLOW_REAL_WHATSAPP=no SETUP_WRITE_ENV=false python3 /usr/local/lib/yachad-updater/data-lock.py /var/lib/yachad/new-mock node --import tsx scripts/setup.ts --web
```

Create that new private directory/key via the fresh-install guide first. The
command prints a private 256-bit setup link in the **local terminal**, valid for
15 minutes. Open it on the trusted household browser through existing HTTPS.
The fragment is removed from browser history and never sent in the request URL;
the capability goes only in a same-origin header. Anyone with the unconsumed
link can claim the empty store, so do not share or log it. Setup is disabled on
normal startup, after expiry or after a family exists, and is serialized against
simultaneous claims. It creates a parent with confirmed password, starts no
WhatsApp connection, and exits on Ctrl+C/timeout. Stop it, then start the normal
configured service. Reloading after removing the fragment requires reopening the
original local link if it is still valid. Never make a public setup listener.

Parents add children in the parent screen with a display name, unique username,
confirmed passphrase, then choose allowed groups. New children start with no
group access. Each account needs a different secret. Spaces, Unicode and
memorable random-word phrases are supported; the 14–256 character policy stays
in force. Password managers on personal devices reduce repeated typing. Shared
tablets should use separate browser profiles and sign out when handed over; the
child idle lock remains. No remote PIN-only authentication is introduced. Changing
your own password or resetting a child's password revokes that account's sessions.

For a forgotten **parent** passphrase, the Pi owner can use a local sudo terminal:

```bash
sudo python3 /opt/yachad/current/scripts/recover-account.py PARENT_USERNAME --data-dir /var/lib/yachad/live
```

This prompts twice with echo disabled, accepts spaces/paste using Python getpass,
and requires 14+ characters. It resets only that parent, revokes their sessions,
and preserves chats/auth/master keys. It takes the updater lock before stopping
the service and the shared data lock before writing. If the app was running it
is stopped/restarted; if it was stopped it remains stopped. `--no-restart` requires
an already stopped service. Do not pass the password on the command line. Child
resets belong in the parent UI. Recovery is not exposed over HTTP and does not
start or approve a new WhatsApp connection.
## Parent group picker and WhatsApp disconnect

The parent group card has a search field and bounded scrolling list for mobile
and tablet. Filtering never changes grants. Selected state comes from persisted
grants for the selected child; duplicate group names keep their distinct stable
IDs. Clear the search to see all groups again. Approval changes save immediately,
and repeat requests remain idempotent. No-results and no-child states explain the
next step. Group access remains server-enforced; search is only a display filter.

The connection card now exposes **Disconnect from WhatsApp** with a confirmation
that explicitly keeps chat history, media, children and grants. It requests
provider logout, clears local encrypted link auth and stale QR, and persists
`linked=0` before waiting for the network. The app will not reconnect that family
after restart until the parent deliberately connects again. Old provider callbacks
cannot restore auth/QR/state. Relinking requires the normal deliberate pairing.

If logout fails, times out after eight seconds, or no live socket is available,
the parent sees **remote revocation unconfirmed** and must remove the device in
the parent's WhatsApp **Linked devices** list if it still appears. A locally
disconnected application cannot guarantee remote revocation while offline. A
resolved logout in the pinned Baileys version only means that its remove-device
request was sent, not that the server acknowledged revocation. Therefore this
adapter always reports remote revocation as unconfirmed and asks the owner to
verify the phone's device list. The uncertainty survives app restart.

The separate destructive **disconnect and delete chat data** action remains in
the danger section with its own explicit confirmation. It is never part of the
ordinary disconnect button. Family/account deletion remains another separate
password-confirmed action. No live disconnect/relink was performed by development;
provider success, failure, timeout, stale-event, restart and child-denial checks use
synthetic accounts and injected socket transports only.

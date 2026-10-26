# GitHub automatic updates: new owner installation (0.3.2 candidate)

The owner chose to trust stable GitHub Releases from **NehoraiHadad/yahad** over
normal certificate-verified HTTPS. This new-install profile needs no release
signing key, public-key transfer, GitHub token or private-repository credentials.
It checks published Releases, never pulls commits or executes a source checkout.

The root worker uses `releases/latest`; drafts, prereleases and nonstable versions
are rejected. It downloads `yachad-release.json` and `yachad-app.zip` from asset
API URLs belonging to the fixed repository. Redirects remain restricted to GitHub
HTTPS hosts. TLS verification is not disabled. SHA-256 and byte counts detect
inconsistent/corrupt assets; **they do not authenticate the publisher separately
from GitHub/HTTPS**. Control of that repository's Releases is the trust boundary.

## One-time owner command, after publication and installer approval

The current Pi has no updater installed. The app remains 0.2.0 and does not need
initial family setup or WhatsApp relinking. The prepared consolidated file is
`deploy/install-updater.py`, using the existing worker, queue and units.

**Current status: preparation only.** Its entry point returns `DRAFT_ONLY` before
root/platform actions. Do not run or remove this guard yet. Stable v0.3.2 with the
matching two assets must first be approved, published and read back; installer
activation is a separate remaining approval gate. No publication/activation was
performed here. The earlier signed v0.3.1 release and existing private key are
preserved; neither is recreated or deleted.

After these gates, deliver the reviewed installer as `~/yachad-install-updater.py`
and run **one command in the owner's local terminal**:

```bash
sudo python3 ~/yachad-install-updater.py
```

The prepared flow downloads the expected stable v0.3.2 public assets into a
root-private snapshot, verifies manifest/hash/size and bounded extraction before
using helpers. It refuses unexpected existing files/config/units and any existing
signature pin. It provisions the isolated builder, root helpers, disabled notify
configuration and units, then pauses before service-affecting work. No editing of
the live environment, master key, family database or Caddy is needed.

The owner enters a separate backup phrase twice, hidden, after saving it outside
the Pi. It is never printed or passed in command arguments/environment. This
phrase protects encrypted recovery backups; it is unrelated to release trust or
GitHub login. Existing secrets are preserved and checked, never overwritten.

Exact local `MAINTENANCE` confirmation starts backup/restore verification and the
first manual update. The old 0.2.0 stdin backup helper is used while stopped; a
private isolated restore drill does not launch an app/provider. The original
service is restarted even on stop/cleanup failure. The owner copies an encrypted
recovery archive off-device and confirms `COPIED`. A root-only version pin keeps
the first install at the approved target even if a newer latest Release appears.

Version/schema/readiness plus controlled restart must pass. The owner then checks
parent/child login, grants, existing WhatsApp link and normal messages/stickers;
`ACCEPTED` confirms these checks. Only afterward does the flow enable automatic
updates, the designated-parent auto/notify UI gate and the two existing timers.
Update backup retention remains **at most seven copies and seven days**; keep the
separate off-device recovery copy. Default release checks are six hours, with the
request worker waking about every five minutes. The UI can check/install now and
choose auto versus notify; acknowledgment can take until the next worker wake.

Preserved paths: `/var/lib/yachad/live`, `/etc/yachad/whatsapp.key`, the current
release/environment and Caddy CA. No automatic database downgrade is added.
Native Pi ARM64/systemd/power interruption, real backup/off-device restore and
live WhatsApp acceptance remain owner-run checks, not desktop test claims.

## Compatibility and existing installations

New root config explicitly selects `releaseTrust:"github-https"`, fixed
`repository:"NehoraiHadad/yahad"`, `publicKeyFile:null`, `tokenFile:null`.
Missing `releaseTrust` means **signature**, preserving old installations. A
configured key, installed standard public pin or legacy/signature worker state
blocks switching to HTTPS trust. The installer never migrates an existing config
or deletes a key. UI requests cannot change trust, repository, paths or operator.
Existing signature-pinned installations retain their three-asset verification;
their legacy setup is documented in [AUTOMATIC-UPDATES-SETUP.md](AUTOMATIC-UPDATES-SETUP.md).

An application release does not replace the privileged worker. Provision the
matching worker once; otherwise the new app's mode switch remains unavailable.
See [UI control](UI-UPDATE-CONTROL.md) for requested versus applied semantics.

## Local release preparation; publication requires separate approval

After building the reviewed candidate, create the two public assets without a key:

```bash
node scripts/release.mjs package-https /PRIVATE_OUTPUT/v0.3.2
```

Publish both files together on stable `v0.3.2` from the approved merge, then read
them back and check exact bytes/hash/version before owner installation. This
command only packages locally; it does not connect to GitHub or publish. Signed
packaging remains available for existing pinned clients and is never an automatic
fallback or credential-generation step. Do not retry a previously denied merge
or publication under this code-preparation instruction.

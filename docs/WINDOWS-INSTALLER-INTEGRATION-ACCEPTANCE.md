# Integrated Windows candidate: acceptance boundary

This branch connects the full updater to the installer. Entry dispatch is wired
in candidate source so an isolated, explicitly authorized Windows acceptance
installation can exercise the actual implementation. It has not been installed
by this integration task. The existing acceptance gates in
`WINDOWS-FULL-UPDATE-INSTALLER-HANDOFF.md` remain requirements for family release;
the historical activation patch must not be applied a second time.

The legacy first-install receipt and full updater enrollment remain separate.
A legacy installation cannot silently gain unlimited automatic approval. A
fresh full-profile installer captures consent version 2 in the elevated Setup
window. The Owner is the account running elevated Setup: the signed-in user
under Admin Approval Mode, or the administrator whose credentials approved UAC.
The protected durable choice, first-parent identifiers and approval timestamp
survive exact-source retry, repair and preserved-data reinstall. Notify does
not publish an automatic grant. Existing routine grants retain their limits.

## Candidate source checks

The baseline delivery c541b191d67240e083eb1a2be79a002f3d649449 was verified against
its bundle and source manifest; it includes installer fix 887648e4. Network copy
improvements 2266bbf were applied separately at 0e1f60e. Final exact commit and
artifact hashes belong in the generated delivery manifest, not this mutable
progress description.

The installer seeds a source-bound baseline system slot, immutable probe,
A/B helpers, native enrollment and first Owner binding. Source validation uses
policy protocol 2. Startup success requires the actual Authority and Runtime
SCM generations plus a fresh immutable installer nonce and retained Runtime
admission, then local HTTP usability. An unrelated localhost response is not
successful installation. Consumed/stale readiness challenges cannot break
ordinary reboot or later updates.

Maintenance holds the existing update-serial lease, refuses unresolved update
journals, fences all four fixed services and observes stop before changing LAN
control records or deleting product files. LAN repair accepts only its two
protected before/after snapshots and preserves the current restore/migration
context. Only independently administrative-owned matching generated installer
partials can be discarded; update-owned or unrelated partials stay blocked.

Removal stores the exact Owner, mode, binary inventory and progress outside
both removable roots. Retry cannot switch from preservation to data deletion.
KeepData archives the currently verified bootstrap binaries, not a stale initial
payload. Restore preserves keys, data, enrollment and current version metadata.
The two Owner shortcuts are created by the unelevated original user and removed
only for the exact authenticated old installation ID after successful uninstall.

An incomplete first installation is not treated as successful removal. The
supported action is to rerun the exact original package to finish provisioning
before ordinary uninstall. Missing/malformed initial ownership evidence remains
fail-closed; it never authorizes manual or guessed directory cleanup. Native
interruption acceptance must distinguish this refusal from successful recovery.

## Build-only packaging

`python -m installer.windows.full_system_package` accepts a clean committed
source tree, the verified full application payload, self-contained Authority,
RuntimeHost, Owner and Helper build directories, the pinned Node distribution,
and a new output directory. It emits a format-2 installer ZIP/manifest and the
fixed `yahad-system-native-<version>.zip` update candidate. These are private
candidates, not publication, signatures, license clearance or machine acceptance.

All application/helper/packager/source identities must match. The fixed offline
migration entry and import closure, five native closures, private .NET/runtime
imports, Node pin, hashes and archive namespaces are checked. The family machine
does not run npm or download developer runtimes during installation.

## Proposed real Windows acceptance (not performed)

First refresh the ASUS legacy installation state read-only. Do not replay an
uncertain installer/uninstaller action or reuse an obsolete artifact. Cleanup of
the previous legacy attempt, if still needed, is a separate product operation
under its exact ownership/removal protections; never use manual cleanup to hide
a defect.

A full-profile test installation needs explicit authorization for:

- `C:\Program Files\Yahad.FirstInstall` and `C:\ProgramData\Yahad.FirstInstall`,
  plus fixed protected installer/removal diagnostic records beside the binary
  root. Diagnostics contain bounded stage/code/numeric evidence, no raw private
  path, token, SQL result, message payload or exception text.
- Runtime service `Yahad.Acceptance` under `NT SERVICE\Yahad.Acceptance`.
- Authority `Yahad.Acceptance.Authority` under its corresponding virtual account.
- RecoveryA and RecoveryB services under their own corresponding virtual accounts, using fixed
  A/B images and failure commands. These three registrations are automatic;
  Authority is delayed automatic and Runtime is Authority-started demand mode.
- Administrative ownership of newly created SCM objects, service SID type 1 and
  only `SeChangeNotifyPrivilege`. The original Owner gets service query rights;
  Authority/A/B get query/start/stop (0x35), never change-config/delete/write-DACL.
  Runtime gets only query-config/status (0x5) on these SCM objects.
  An existing foreign service owner is rejected, not silently repaired.
- Protected role-specific file ACLs: Authority owns updater control/transactions;
  Runtime modifies only its data, lease and temporary objects. Only the original
  Owner gets RX on the live Owner tool. Windows/System and Administrators retain
  full administrative access; no broad Users/Everyone modify grant is added.
- Runtime-account migration profile `Yahad.Migrate.<installationId>` without
  network capabilities or a loopback exemption. No approving-admin, LocalSystem,
  new privilege or alternate-account fallback is allowed.
- Native Owner shortcuts in that user's Start menu and the existing loopback
  browser handoff. This source task does not create either on the machine.

LAN is a separate explicit choice. Test initially on 127.0.0.1 unless the exact
LAN address/profile, separate proxy service, program-scoped Private/subnet
firewall rules and optional HTTP help page are included in the approval.
Certificate trust, remote/public access, real WhatsApp, microphone, sleep/reboot
and destructive data tests need their own bounded acceptance decisions.

Automatic application/bootstrap updates do not silently gain control of the
separately privileged LAN proxy/CA or replace the elevated Setup executable.
Changes to those separate components require the supported elevated installer;
this boundary must be stated when reporting automatic-update coverage.

Required installed acceptance still includes same-account helper service logon,
Runtime profile/lowbox provisioning, actual source-bound startup, two successive
synthetic installed updates (including schema change), Notify non-application,
unknown ACK/later-write preservation, failed startup/rollback/reboot recovery,
Store, repair, KeepData restore and complete removal. Synthetic unit tests do not
claim these machine operations passed. No real provider or public release is
needed to run synthetic installed acceptance after the precise approval.

Unsigned development distribution was chosen by the owner. Preserve that clear
label and the existing fixed HTTPS source/update integrity protections. Do not
claim independent publisher signatures or waive remaining license/native gates.

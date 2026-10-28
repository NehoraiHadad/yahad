# Native installer acceptance gates

Current dependency/lifecycle/access checkpoint: [Private runtime packaging and stage-one handoff](NATIVE-RUNTIME-PACKAGING.md). Earlier counts below describe their respective increments.

Current continuation: see [native app portability and dependency status](NATIVE-PORTABILITY.md). The feasibility-only results below describe the first increment.

Only synthetic, nonprivileged Windows x64 fixture checks have run in this branch. A passing fixture is not a successful installation or a supported-platform claim. “Target” below means intended validation target, not shipped support.

| Target | Build/runtime closure | Actual machine status |
| --- | --- | --- |
| Windows 11 x64, supported OS builds | Self-contained .NET GUI/fixture host built here; actual Node/app/proxy bundle missing | Host Windows x64 fixture/UI only. No clean VM, services, reboot or trust changes. |
| Debian 12/13 amd64 + systemd | Not packaged in this increment | Not run |
| Ubuntu 24.04 LTS amd64 + systemd | Not packaged in this increment | Not run |
| Raspberry Pi OS 64-bit Bookworm arm64 + systemd | Must build/test separately from amd64 | Not run |
| Windows ARM64, Windows 10 consumer editions, Pi armhf, Alpine/non-systemd, macOS | Outside initial support boundary | No claim |

## Fixture evidence versus machine requirements

| Scenario | Present fixture evidence | Required actual-machine acceptance |
| --- | --- | --- |
| Download/open with no runtime | Self-contained .NET publishing; no npm at destination | Clean supported Windows x64 snapshot without Node/Python/.NET SDK; offline launch and install; Mark of the Web/AV/code-signature UX |
| Parent consent | Labeled Hebrew consent-screen preview; process is `asInvoker` | Narrow explained UAC/polkit action, cancellation before/at elevation, no undisclosed firewall/CA change |
| Package safety | Hashes, sizes, traversal/ADS/device names, duplicates/case, symlink/reparse and unexpected files refused | Authenticode/transport identity, package replacement race, privileged no-follow handle boundary, hostile pre-existing directories |
| Private files | Native DACL create/owner check, reject broad ACL | Real service SIDs, rights matrix for app/updater/recovery/admin, standard-user isolation; inaccessible secrets and restricted pipe rights |
| Service lifecycle | Suspended start/job containment, descendants die on disposal and host crash | SCM start/stop/status, before-login start, recovery limits, service upgrade/rollback, reboot/shutdown deadline, graceful SQLite close, no orphan ffmpeg/proxy |
| Interrupted preparation | Injected IO failures before/after publish, journal recovery and immutable published fixture | Reboot/power loss at every durable transition, installer-engine rollback, volume semantics, resume same version, stable receipt, no cleanup of unknown files |
| Failure/retry | Injected generic IO and invalid package, cancellation/resume | Physical disk-full, ACL denial, actual AV lock/quarantine, network cut/offline package, proxy failure, failed CA consent, battery/sleep; actionable Hebrew codes |
| Network | RFC1918 boundary/adapter-selection tests and real occupied loopback bind | LAN listener, private firewall profile scope, multiple NICs/VPN, wrong interface, public profile refusal, DHCP change, sleep/wake/reboot, tablet routing |
| Browser onboarding | Existing app capability flow audited, no live wizard opened | Short expiry/renewal, fragment secret, Origin/Host checks, same-store parent lockout, no token in logs, first parent creation/child grant using synthetic data |
| TLS/tablet | Design/instructions only | Certificate SAN/fingerprint, local explicit trust, Android/iOS/tablet explicit trust; no warning bypass and no private-key export |
| App update | No app payload launched or updated | Bootstrap cannot overwrite app payload; updater cannot replace host; encrypted backup/isolated authenticated restore; schema compatibility barrier and health gates; preserve auth keys/policy/pins and backup retention |
| Uninstall/reinstall | No uninstall implementation | Default keep data/keys/backups/policy/receipt; reinstall resumes safe app authority; unrelated services/runtimes retained; explicit owner-only permanent removal on synthetic store |
| Linux package | Existing Linux safety foundations inspected only | Native dependency closure and install/repair/uninstall/reboot on each amd64 distro and physical Pi arm64; polkit and headless route separately |

## Test procedure for the next increment

Use disposable OS snapshots and only mock providers/synthetic family data. Record artifact digest, source commit, OS build/architecture, exact before/after state, steps, screenshots, measured result and remaining failures. Installations, persistent service access, firewall rules, certificate trust, credential enrollment and destructive removal are **not authorized on the owner's working computer** by this development task. No real WhatsApp linking or family data is necessary for this matrix.

Snapshot each durable phase: package verification, component staging, service registration, initial updater handoff, first launch, owner creation, verified backup, update activation, recovery clear and uninstall. Fault injection must test that no path overwrites the live DB on failure and that failed verification leaves recovery barriers closed. Unknown existing service/helper bytes or foreign install receipts must stop without “repairing” them.

Review keyboard focus/Tab/Shift+Tab, RTL reading order, 100/125/150/200% DPI, minimum supported window size, high contrast, screen-reader status announcements and long Hebrew error copy. Screenshot previews alone do not prove accessibility. No release or support badge is allowed until the relevant machine row is passed.

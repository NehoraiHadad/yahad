# Installation and automatic updates (0.4.0)

This is the current installation guide. The older `install-updater.py` and 0.3.2 owner guides are historical, machine-specific handoffs. Do not use them for a new installation.

**0.4.0 is published.** The installer requires a stable 0.4.0+ GitHub Release and refuses older artifacts before provisioning. Publication does not establish fresh Pi or Windows installation acceptance; fixture tests do not establish it either.

## One guided command

Use 64-bit Debian/Ubuntu with systemd, Node **24**, npm, Python 3, OpenSSL, ffmpeg and Caddy installed. These operating-system prerequisites and trusting the local HTTPS CA are one-time owner operations. The installer does not disable certificate checks or change firewall/router settings. Keep the server awake and reserve its private LAN IP in the router.

Download `yachad-app.zip` from the fixed repository's stable Release, extract it as your ordinary account, inspect the installer, then run from that folder:

```sh
sudo python3 deploy/install.py --origin https://YOUR_PRIVATE_LAN_IP
```

Use the actual LAN IPv4 in place of `YOUR_PRIVATE_LAN_IP`. No GitHub account, token, release signing key, application usernames or account passwords are entered in the terminal.

For a fresh installation, the guide asks one short yes/no confirmation before system changes. It prepares isolated `yachad` and `yachad-update` accounts, root-owned release files, helper scripts, service units and a private backup secret. It fetches stable assets from `NehoraiHadad/yahad` through verified HTTPS and reuses the existing worker's manifest, size, hash and safe-extraction checks. Hashes establish consistency, not independent publisher authentication.

It prints a **private 15-minute setup link**. Open that link in the browser and create the first parent. The normal service handles setup and stays running if the terminal closes; there is no Ctrl+C transition. The parent is signed in immediately, can add children and approve their groups. New children have zero group permissions. The tablet URL and selected username are shown without displaying a password.

After parent creation, the installer creates an encrypted local backup, verifies an isolated authenticated restore, restarts the app and verifies health/version/schema. It then enables automatic stable-release updates and the designated parent's auto/notify UI controls, verifies timers and reports `COMPLETE`. There is no required off-device transfer, long typed acceptance word, or notify-only default completion. A brief app interruption occurs during backup/integration. A blank yes/no confirmation cancels before changes.

### HTTPS and WhatsApp

On a fresh installation, a separate managed `yachad-web.service` serves only the chosen private LAN IP using Caddy internal TLS. Existing Caddy configuration and CA files are not replaced. If another service already owns that address/port, installation stops rather than replacing it. Trust the **public Caddy root certificate** on each browser/tablet using your OS certificate settings; never copy its private key. Do not bypass browser certificate warnings. Existing installations keep their HTTPS arrangement.

The fresh default is a mock connector, with a separate new data store. Real WhatsApp is an unofficial connection and still requires explicit local owner activation as described in [PI.md](PI.md), then the parent's deliberate Link action. Do not change an existing store between mock and real. No real account is contacted by tests.

### Existing installation

**For routine app upgrades, use the existing updater / parent update panel. Do not rerun the installer or setup wizard.** The 0.3.4 immediate-request worker integration remains compatible with 0.4.0; an older worker is not silently replaced. The installer is only for fresh installation, resuming that same interrupted setup, or a separately reviewed one-time OS integration—not the normal upgrade path. For an explicitly reviewed integration, update the app to 0.4.0+ through the existing updater first, then use the inspected matching release folder. `--origin` is optional for existing installs because it reads the existing environment. Existing `DATA_DIR`, master-key path, live authentication, account data, Caddy and CA remain in place. Paths inside `/var/lib/yachad` and `/etc/yachad` are discovered, including custom live-store names. Nonstandard service layouts or conflicting files are refused for local review.

Existing update enablement, auto/notify mode, UI-control permission and version pin are preserved, as are existing service enablement preferences. Only **fresh** installs receive the auto-update defaults and automatic service activation. Existing installations receive no implicit policy reset or timer/path re-enable; the completion message distinguishes this from fresh setup. If an enabled updater has a disabled/missing request path or timer, a completed rerun reports a repair requirement rather than claiming updates work or changing the owner's choice.

Known historical CRLF copies of the five unchanged 0.3.2 helper/unit files are accepted **only by exact reviewed hashes**, and retained byte-for-byte. This supports the actual mixed installation with a 0.3.4 LF worker/request units and older CRLF supporting files. Unknown edits and older worker versions still require a separately reviewed migration; do not alter manifests, normalize installed files or delete them to force acceptance. The entire helper set is checked before helper writes.

Release source uses canonical LF via `.gitattributes`; packaging rejects CRLF in privileged deployment sources before creating an archive. Use the verified release ZIP rather than a newline-converted Windows checkout. Neither a line-ending comparison nor a version string substitutes for the known helper identity checks.

The root updater is an OS service component; application releases do not silently replace it. This guide preserves an existing signature policy and designated operator, never silently turns a signature-pinned installation into HTTPS-only trust, and checks the effective service guard. A root-only service override permits atomic writes to updater mode configuration while the environment, master key, backup secret and existing trust/token files stay read-only in the updater's sandbox. This corrects the old service's read-only `/etc` mode-change failure.

### Immediate update buttons

On the completed new installation, parent check/install/mode clicks wake a
request-only worker through a systemd path, rather than waiting five minutes.
An operation already in progress still finishes safely; downloads and backups
are not instantaneous. The five-minute timer remains a fallback. Existing Pi
installations need a reviewed one-time root integration upgrade; updating app
files alone cannot enable this. See [immediate requests and status](IMMEDIATE-UPDATE-REQUESTS.md).

### Interrupted or repeated execution

Only to resume the same interrupted installation (not to upgrade a working household), rerun its original command from the same inspected release folder. Known state and protected files are checked before continuing; existing keys/secrets/config are preserved. A completed installation verifies app health, helper/boundary identities and, when updates are enabled, active/enabled request path and timers. It preserves the parent's later auto/notify preference and version pin. A deliberately disabled update policy stays disabled; the message does not claim active updates. Interrupted timer activation reuses the verified local backup. Conflicting configuration, pending recovery, unsafe ownership/symlinks or a partially written unverified release stop with a local diagnostic instead of overwriting state. Never reset receipt flags by hand or delete the existing store/key to retry.

Local encrypted backups keep at most 7 copies and at most 7 days. Both local backup and data can be lost with the device/SD card. An off-device recovery copy remains optional. The private backup secret stays `/etc/yachad/update-backup-passphrase`; do not paste it into chat/logs.

## Windows

The implementation path is **Windows + WSL2/Ubuntu + systemd**, using the same Linux application, installer and updater. It is not a native Win32 service or a second updater implementation.

Install WSL2/Ubuntu using [Microsoft's instructions](https://learn.microsoft.com/en-us/windows/wsl/install), enable [systemd](https://learn.microsoft.com/en-us/windows/wsl/systemd) if the distribution does not already use it, and install the Linux prerequisites above inside that distribution. Keep app data on its Linux filesystem, not `/mnt/c`. The downloaded release/launcher may be on Windows; the installed app/data live in WSL.

### Check readiness first (no installation changes)

From the extracted release folder in an ordinary PowerShell terminal, replace the address with the intended private LAN IPv4 address:

```powershell
.\deploy\install-windows.ps1 -Distribution Ubuntu -Origin https://192.168.1.10 -CheckOnly
```

The check identifies missing WSL, an absent or WSL1 distribution, missing Linux Python/sudo/WSL utilities, unreadable release location, and runtime prerequisites. Systemd, Node 24, npm, ffmpeg, OpenSSL and fresh-install Caddy problems are reported together instead of starting the installer and failing one command at a time. Dependencies belong **inside the selected Linux distribution**, not just on Windows. Node must be available to system services, not only through an interactive shell version manager. Checks may start the selected WSL distribution; they do not install packages, ask for sudo, register tasks, enable WSL/systemd, or change execution policy, firewall, certificates or permissions. `-PlanOnly` remains an offline outline and does not check readiness.

| What is missing | Next step |
| --- | --- |
| WSL / selected distribution / WSL2 | Follow the linked Microsoft WSL instructions; use the exact installed name from `wsl --list --verbose`. No automatic install or conversion is performed. |
| Python 3, sudo or WSL utilities | Prepare the missing Linux packages inside that distribution. |
| systemd | Follow the linked Microsoft systemd instructions, then reopen the distribution. |
| Node / npm | Prepare Node **24** with npm inside Linux, available on the service PATH. |
| ffmpeg / OpenSSL / Caddy | Prepare the named Linux package; Caddy is needed for fresh managed HTTPS, not to replace an existing HTTPS setup. |
| Release path | Extract the complete release into a simple local folder readable by WSL. |
| PowerShell refuses the script under local policy | Stop and consult the machine owner/admin; do not bypass execution or security policy. |

A successful check is **not** full acceptance: downloading releases, sudo authorization, existing-file safety, bindable LAN address, tablet routing, HTTPS trust and reboot/logon behavior are checked later or on the actual machine. No probe opens public registration or replaces an existing Pi/household setup.

For a **fresh installation only**, after prerequisites pass:

```powershell
.\deploy\install-windows.ps1 -Distribution Ubuntu -Origin https://YOUR_PRIVATE_LAN_IP -StartAtLogin
```

The wrapper repeats the readiness check, then runs the same guided Linux installer interactively. Declining its confirmation reports `CANCELED` and registers/starts no Windows task. Failure or interruption reports `PAUSED`; do not delete setup data or keys. Follow the interrupted-setup guidance above, not a clean reinstall. Only after Linux health and each service/path/timer is active **and enabled** does `-StartAtLogin` register a current-user logon task that keeps the selected distro awake. It saves no sudo password or GitHub credential, preserves an unrelated task with the same name, and does not change Windows features, firewall rules or certificate trust. `-PlanOnly` prints the steps without calling WSL or registering a task. Without `-StartAtLogin`, keep WSL running yourself.

Windows must remain awake and the user signed in for this logon task. Updates do not run while Windows sleeps, the distro is stopped, or the user is signed out. LAN tablet access depends on [WSL networking](https://learn.microsoft.com/en-us/windows/wsl/networking): default NAT and mirrored mode behave differently. Verify the selected LAN IP is bindable inside WSL and tablet HTTPS access works; this guide does not silently open ports or alter network policy. Reboot/logon persistence, real WSL task registration, network routing and local CA trust require owner acceptance on the target Windows machine. They were not performed during development.

## Checks before calling an installation accepted

- Loopback `/health/ready`: correct release, `ok: true`, schema 1.
- Browser: parent setup, child creation, zero default groups, deliberate grants and tablet login.
- Updater: auto active, request path and timers active/enabled; UI switch confirms requested versus applied mode.
- Reboot/logon persistence and HTTPS/CA trust on the actual target OS and tablet.
- Real WhatsApp linking/stickers/history only when the owner deliberately tests them. History is partial and asynchronous; a submitted request is not restored content.

### Release acceptance matrix (not fixture-test claims)

Before claiming unattended installation/update support, exercise a disposable native-systemd Linux target and a real Windows WSL2/Ubuntu target: fresh setup; the mixed 0.3.4 LF/CRLF existing installation; repeat with notify mode, version pin and disabled preferences; interrupted setup; restart/reboot/logon; tablet HTTPS/CA trust; idle click, click during an update and its exit window; timer fallback and blocked queues without loops. Unknown helper bytes must fail before replacement. Verify data, account authentication, keys, Caddy and existing mode/pin are unchanged. Windows checks must include actual LAN routing and awake/logon behavior; fixtures do not establish these.

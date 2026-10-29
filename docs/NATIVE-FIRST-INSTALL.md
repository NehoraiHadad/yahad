# First-install Windows service installer

This focused branch implements one installer file, one Windows application
service, and the existing browser onboarding. It does not depend on the frozen
two-role update authority. The app payload uses the published 0.5.3 metadata,
schema 5 and protocol 2, plus the existing native Windows portability changes.

`Yahad.Setup` embeds the app, private Node/ffmpeg/Caddy, production node_modules,
native file helper, service executable and private .NET runtime closure. There
is no target-machine npm, Python, Docker, WSL or runtime download. Build-time
tools remain separate from the family-facing executable.

The normal UI runs unelevated. One standard UAC consent starts the same verified
installer image as an elevated helper. A bounded local-only channel verifies
kernel process IDs, retained process identity, exact image hashes, user SID and
session. The helper derives fixed paths and service settings itself. JSON/CLI
cannot select an executable, installation directory, credential, owner claim or
privileged arbitrary operation. The browser opens under the original user.

Only the elevated helper may perform installation. It checks collisions, creates
new protected locations, verifies every embedded member before publication,
registers the service demand-start first, provisions the first-install records,
then enables delayed automatic start and starts the service. Readiness requires
the actual application child handshake, SCM Running and loopback HTTP readiness.
Existing families go to normal login; existing unfinished web setup may be
reopened/renewed by the authenticated original Windows owner without resetting
the database, key, policy, pins or family. No family/WhatsApp wizard is rebuilt.

The service uses the existing contained process tree, clean launch environment,
private FFmpeg path and continuous exclusive Store lease. Administrator-owned
payload/control/key files are read-only to the service; only data/temporary paths
are writable. The initial empty database needs a hash-bound administrator-owned
read/delete grant, consumed through its retained handle before mutable startup.
The service cannot recreate that grant. A missing database never becomes an
implicit reset. SQLite reconciles genuine crash-left WAL under the exclusive
lease; no manual sidecar deletion or immutable read bypass is used.

Normal uninstall validates the original protected install index and exact
service configuration, stops the service, observes process exit, removes only
owned binary/service/uninstall registration, and keeps ProgramData data, keys,
policy/pins and recovery material. Unknown or changed files are preserved. The
uninstall UI relocates to a private temporary folder so installed executable
files can be removed without forcing a reboot. Temporary installer/runtime
caches and Windows-owned service events/profile bookkeeping may remain.

The first package binds only `127.0.0.1` and opens numeric loopback HTTP, which
the existing app explicitly supports. Caddy is bundled but not started. No
firewall, CA trust, domain, tunnel, UPnP or public exposure is configured. The
family server runs as a Windows service and survives closing the installer;
LAN/tablet access and its secure certificate path remain a separate explicit
configuration. The acceptance package uses mock WhatsApp and does not enroll
or call a real provider.

## Exact ASUS changes proposed, not executed

- Service `Yahad.Acceptance`, display `יחד — שרת משפחתי`, own process under the
  passwordless virtual account `NT SERVICE\Yahad.Acceptance`; unrestricted service
  SID, only `SeChangeNotifyPrivilege`, delayed automatic startup. No separate
  authority service, new Windows user or supplied password. Service image is
  `C:\Program Files\Yahad.FirstInstall\host\Yahad.ServiceHost.exe --service`.
- New owned binary root `C:\Program Files\Yahad.FirstInstall` and state root
  `C:\ProgramData\Yahad.FirstInstall`. Files receive administrator ownership at
  creation, with System/Administrators full control, service read/execute for
  immutable files and modify only for data/temporary. Registered owner receives
  binary execution and service query access, not protected data writes.
- Initial installation ID/data ID, 32-byte master key and short-lived first-parent
  capability are generated locally only during the approved install. Existing
  state is authenticated/preserved and never silently replaced.
- One chosen loopback port from 47123–47139, confirmed from the actual server.
  Competing applications are not stopped or reconfigured. No external listener.
- One HKLM Add/Remove Programs entry
  `Software\Microsoft\Windows\CurrentVersion\Uninstall\Yahad.FirstInstall`,
  plus an optional original-user Start Menu `Yahad.url` containing only the
  loopback URL, never the one-use setup capability.
- No feature enablement, VM, forced reboot, global runtime install, shared-service
  removal, security-product bypass, trust enrollment or provider call.

Before any execution, populate the exact Setup SHA256 and manifest, repeat only
these service/path collision checks, and obtain owner approval for install,
local synthetic-family verification and the specified data-preserving uninstall
on ASUSHADAD. Automatic-start behavior is configured but before-login/reboot,
cross-account UAC and actual service/ACL/token acceptance cannot be proved by
synthetic tests. Do not claim a completed machine installation or clean-machine
proof from this developer host. Do not enable landing-page download yet.

The `--verify-package` mode is a non-mutating build/audit command; it validates
the embedded payload without opening UI, requesting elevation or touching host
installation state. It is not part of the family-facing flow.

WIC classification is limited to the documented `windowscodecs.dll` OS component:
[Microsoft WIC API library table](https://learn.microsoft.com/en-us/windows/win32/wic/-wic-api).
One-use deletion uses the Windows `BOOLEAN` file disposition structure:
[FILE_DISPOSITION_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_disposition_info).
Signing, full corresponding-source/transitive-license distribution review and
actual host acceptance remain release gates; no signing keys are enrolled here.

# Read-only Windows test options: ASUSHADAD

Observed after the health repair tests, 2026-10-29 UTC. No feature was enabled,
VM created/started, installer run, guest disk read, networking changed or account
permission modified. Registry, feature, service, module and uninstall metadata
were queried only. No usable or authorized disposable guest was established.

| Observation | Result |
| --- | --- |
| Windows SKU | Registry EditionID `Core` (Home), DisplayVersion `24H2`, build `26100.9457`. ProductName says `Windows 10 Home`; report these raw values rather than using that name to misidentify the build. |
| Sandbox executable | `System32/WindowsSandbox.exe` absent. |
| Hyper-V management | `vmms` service absent; Hyper-V PowerShell module / `Get-VM` unavailable. Existing VMs and snapshots therefore could not be enumerated. This does not prove that no guest exists anywhere. |
| Other virtualization infrastructure | `vmcompute` and `HvHost` running, Manual startup. This does not identify a test VM or snapshot. |
| Readily discoverable VM products | No VMware, VirtualBox, QEMU or Parallels matching uninstall entries were found. No exhaustive disk scan or vendor executable was run. |
| Optional feature states | All six online read-only feature queries returned `The requested operation requires elevation`. Actual enabled/disabled state is unknown; no elevated Windows process or UAC prompt was launched. |

The attempted feature reads were `Containers-DisposableClientVM`,
`Microsoft-Hyper-V-All`, `Microsoft-Hyper-V-Hypervisor`,
`Microsoft-Hyper-V-Management-PowerShell`, `VirtualMachinePlatform`, and
`HypervisorPlatform`. The OS elevation requirement blocked those reads, not an
automatic approval-review rejection. Other inventory reads completed. There is
no pending or uncertain operation to repeat.

Microsoft lists Windows Sandbox as unsupported on Home and Hyper-V's role as
unavailable on Windows 10/11 Home. Consequently, do not propose simply enabling
either feature on this Home/Core machine or using unofficial enablement scripts.
[Sandbox edition requirements](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/),
[Hyper-V requirements](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/get-started/install-hyper-v).
Build 26100 corresponds to the Windows 11 24H2 family in Microsoft's
[release information](https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information).

## Smallest viable route and exact approval boundary

Prefer an existing disposable Windows 11 x64 guest on another already configured
host, supplied by the owner with console access and a named clean snapshot. No
such host/guest is currently identified or accessible through this inventory.
That route avoids changing ASUSHADAD. It still needs explicit permission to start
the named guest, transfer only verified artifacts, apply the already specified
two service registrations and scoped Program Files/ProgramData ACLs inside it,
exercise synthetic owner/delegation/lifecycle tests, and revert the exact snapshot.
NIC isolation must already be established or specifically authorized for that
guest; it is not permission to change the home's networking.

If a new local guest is preferred instead, that is a separate setup request:
select and verify a supported virtualization installer, validate hardware and
coexistence with the currently running virtualization infrastructure, allocate
specific storage/memory/CPU, obtain owner-provided licensed Windows media,
create an isolated guest/network configuration and a rollback snapshot. Installing
that product, changing hypervisor/BIOS/boot features or rebooting ASUSHADAD,
creating a VM, configuring networking and any cost/licensing transaction each
need explicit scoped authorization. None is selected or performed here.

The existing `authority-vm-acceptance.json` remains execution-ineligible and
unapproved. Complete the missing production channel/window/SCM/intake composition
first, then freeze its new executable/dependency hashes plus the guest ID,
snapshot and test-user SIDs into that single manifest. The currently rebuilt
disabled health-adapter binary is not a substitute for those positive acceptance
artifacts. No host modification or blanket-permission request is necessary to
continue safe source implementation.

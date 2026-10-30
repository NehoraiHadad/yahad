> Historical account of checkpoint 2621ea6. The current installed entry and its remaining authority gates are described in [NATIVE-INSTALLED-ADMISSION.md](NATIVE-INSTALLED-ADMISSION.md).

# Native SCM host source checkpoint

This slice starts from preserved `ce4f62b` in `C:\projects\yahad-native-service`, branch `feat/native-service-host`. It implements `Yahad.ServiceHost`, a real Windows own-process SCM executable. It contains no service/account installation APIs and does not turn the earlier feasibility executable into a service wrapper.

## Production entry and authorization

`--service` immediately connects through `StartServiceCtrlDispatcherW`; the native ServiceMain registers HandlerEx before reporting status. Strongly held Winapi delegates, a terminated service table and the seven-DWORD status ABI implement the transport. No native SCM API was invoked during this development slice. `--describe` is a separate read-only artifact description, not a launch/authorization bypass; other process arguments are refused.

The installed path first checks an actual Windows token observation against `NT SERVICE\Yahad.Acceptance`, the enabled service-logon group, non-administrator status and the exact required-privilege set `SeChangeNotifyPrivilege`. Unexpected privileges are rejected even if disabled. It then reads the fixed ProgramData bootstrap descriptor through the existing admin-owned, application-read-only, private-ACL/no-follow/single-link boundary. The descriptor admits only schema, service name, installation ID and activation receipt digest; duplicate/extra/foreign/executable fields fail closed.

The installed activation resolver is deliberately unconfigured and always refuses. It does not fabricate signature, active-release, recovery or owner-authorization facts. Even an otherwise valid descriptor cannot start an app until the sole updater's authenticated activation and recovery adapter is implemented. No production credential, signing key, sample activation receipt or machine bootstrap file was created.

## Lifecycle and process adapters

The controller reports START_PENDING for actual startup milestones, RUNNING only after app readiness, STOP_PENDING during shutdown/cleanup and STOPPED only after confirmed quiescence (or when no app was started). It advertises stop/shutdown controls while running; a startup-stop race cancels initialization. The handler merely signals an asynchronous request and returns. Unsupported controls are refused; interrogate has no side effect. It never implements a second payload update authority.

Startup authorization is bounded and late grants are disposed after cancellation. Read-only recovery/start barriers are also bounded, including an adapter that ignores cancellation. Such barriers must never execute restore/activation writes. The concrete native process launcher returns its owned Job Object synchronously, assigns the suspended process before resume, pins/checks its executable image, builds an explicit allowlisted empty-PATH environment and assigns the private FFmpeg path. Full payload authentication/dependency pinning remains the activation resolver's responsibility.

The production child-control adapter reuses the current-identity local-only pipe policy, checks the connecting PID against the retained job root and bounds readiness frames. Graceful shutdown uses that private channel; whole-job exit and termination use actual native accounting. This pipe adapter was compiled but **not opened or exercised** in this slice. An unexpected root exit fails the service without automatically restarting the old app or changing data.

Failed cleanup retries through the audited AppLifecycle disposal contract. Unknown quiescence does not publish STOPPED, success or a recovery grant; the native entry exits unsuccessfully, with job-handle teardown as last-resort containment. This exit is not proof of observed recovery or safe restore.

## Non-installing validation

The dedicated suite passed **34/34** checks, zero failures/skips and zero compiler warnings/errors. SCM transport/status tests use a fake API. Child-control tests use an isolated file-based test adapter, not a listening endpoint. Four tests use actual private Node processes and descendants in native jobs: graceful stop, wrong peer observation, startup timeout and unexpected root exit. They verify empty PATH, dropped hostile NODE_OPTIONS, explicit FFmpeg path and actual complete process-tree exit. No real WhatsApp, family data or network listener is involved.

Other cases cover token-policy rejection, actual current-token rejection against the service SID, strict bootstrap schema, bootstrap authorization failure, unconfigured activation, registration/status ordering, rejected SCM arguments, cancellation/late-grant revocation, noncooperative recovery barriers, nonblocking stop dispatch, shutdown ordering, failed health, unknown cleanup and status-transport failure. Reading the current token is not acceptance of an actual service-account token.

`installer/test-service-host.ps1` uses private SDK/Node/cache paths and restores its process environment. `-Publish` produces a self-contained **folder**, keeping CLR/bootstrap files explicit rather than relying on implicit runtime extraction. The executable hash alone is insufficient: every managed/native runtime file must match the companion artifact manifest. The local artifact is unsigned development output; family/release readiness remain false.

## Actual acceptance still blocked

No service was registered or started, no account was created, no machine-directory ACL or network/trust setting changed, no VM was configured and no listeners were opened. The old synthetic plaintext fixtures remain private and unexported. This source is separate from them.

Real SCM dispatch, virtual-account/token behavior, service object DACL enforcement, boot/session-0 operation, real pipe peer isolation under the service identity and OS stop/shutdown deadlines need an approved disposable Windows VM. Existing access has not established such a Windows guest with console/snapshot control. Positive app startup additionally needs the authenticated installed activation/recovery implementation; the current executable must fail closed.

The proposed first VM test is intentionally **identity/SCM/fail-closed startup only**. It uses a demand-start own-process `Yahad.Acceptance` service under its dedicated virtual account, an exact hashed artifact, minimal required privileges and no bootstrap descriptor. Expected result is stopped with Win32 1066/service-specific 1002 after identity passes. Identity failure (1001), RUNNING, any child/listener or machine data write is a failed acceptance. The exact artifact-bound manifest is delivered separately after publication; no installation is authorized by producing it.

Safe next code work: authenticated activation/start-admission integration and installer/rollback adapters with synthetic fixtures. VM/owner approval is required for registration, actual token/ACL checks, autostart/reboot and OS uninstall acceptance. Firewall/CA/provider/publication actions remain separate scopes. The existing web UI continues to own family/WhatsApp setup.

Primary references: [dispatcher](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-startservicectrldispatcherw), [handler registration](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-registerservicectrlhandlerexw), [control handler](https://learn.microsoft.com/en-us/windows/win32/services/service-control-handler-function). Pending checkpoints represent actual milestones; no timer pretends that a stalled initializer is progressing.


## Built artifact and exact proposal

The executable/managed host source is `615b2919b9b7b74e70ace52b2351abc361406cba`; its extracted `--describe` reports that revision. The self-contained folder includes .NET 10.0.14 and exact cached runtime license/notices plus the project license. The cached runtime package SHA512 matched its recorded checksum; this is consistency evidence, not a new publisher-signature claim.

`Yahad.ServiceHost.exe`: 162,304 bytes, SHA256 **`84fce1f972ff9b4060e7df14a40b0a162b7a193cdaaffbae772869d1d3bf57bd`**. The complete archive `yahad-service-host-615b291-win-x64.zip` is **35,871,898 bytes**, SHA256 **`a213b7daf517fb0ee389feed26ed485c670e0e4b76e33796d7ca41cebfa2cd2a`**. All **195 files / 190 PE modules** passed the runtime/dependency checks and byte verification in a new extraction. Only extracted `--describe` ran, with empty PATH and no DOTNET_ROOT; native SCM was not invoked. This does not establish a clean-machine result.

`evidence/native-service-host/artifact/proposed-vm-test.json` binds the exact executable/archive/file-manifest hashes, account, privileges, demand-start persistence, paths, ACLs, expected failure status and rollback. VM/snapshot IDs are deliberately unset and `authorized=false`. The entire `package/` folder is required; the 162 KiB apphost alone is not the self-contained artifact. A positive app launch is not part of this proposal and remains refused by the unconfigured installed activation adapter.

# Full Windows update profile: installer integration contract

This profile is under isolated development. `Yahad.Authority/Program.cs` still
reports production disabled. A compiled adapter or a synthetic test is not
installed Windows acceptance. Do not retry the ASUS installation with an
app-only updater package and call it the completed automatic-update profile.

The source base includes installer commit
`887648e4ddf10015820a809369492672b117a0e5`, merged at
`060e241fe064e492dd8756a2172c0439bf288903`. Preserve those installer fixes.
The setup host, receipt, Store and packaging remain owned by the installer task.

## Consent and authorization

The initial unelevated, authenticated original Windows Owner accepts consent
version 2 for `full-system-v1`. Explain that routine updates include the
application, database schema, Authority, RuntimeHost, Owner, probe and handoff
helper. Explain the selected Auto or Notify behavior and how to revoke it.
Capture the original Owner before UAC; a different administrator approving UAC
must not become the Owner.

Provision native Owner shortcuts to the fixed protected
`bootstrap/owner/Yahad.Owner.exe` with exactly `--confirm <installationId>` and
`--recover <installationId>`. The canonical lowercase GUID is only the pipe
address; the existing transport independently verifies the fixed SCM server,
process generation and token, and the server authenticates the original Owner.
The compiled `InstalledOwnerEntrypoint` reads no Authority-only anchor and
accepts no paths, accounts or URLs. The recovery shortcut works without the web
application after the Owner entry-point patch and native provisioning pass.
Give only that designated Owner RX on the live Owner component closure. The
bootstrap writer and live closure validator already support this exact narrow
ACE; the immutable anchor and generic state-role ACLs remain strict.

`FullSystemInstallerEnrollment.Prepare` is a pure provisioning-plan builder. It
returns enrollment, initial grant, mode and designated-parent binding bytes.
The installer writes them with the established protected roles and pins their
enrollment SHA256 as the optional `FullSystemPolicyDigest` in the immutable
`update-authority.json` anchor. It must preserve that enrollment during repair.
The builder does not authenticate consent, write files, change ACLs or register
services itself. The existing installer Owner challenge must supply that proof.

Format-2 mode uses `GrantKind: system`. The new automatic grant remains valid
until revoked. Existing routine grants retain their original same-schema,
90-day and 100-operation limits; neither the profile pin nor grant may be
inferred from an old enrollment. Notify does not auto-apply. A Notify install
uses an exact, expiring native Owner approval and does not enable Auto.

## Fixed source and payload

Use the existing Authority scheduler and NativeCandidateIntake transport, with
the fixed HTTPS `NehoraiHadad/yahad` stable GitHub source. The full asset is
`yahad-system-native-{version}.zip`. There are no user-entered URLs, accounts,
tokens, command lines or publisher-selection switches.

The archive contains a format-2 `windows-system-candidate` system manifest,
the existing application payload and its `bundle.json`, and five bounded
bootstrap component closures. Source tag/commit proof, canonical forward
version, Windows/architecture/schema/protocol compatibility, manifest and
archive digests, paths, PE architecture and complete file closures are checked
before publication. Application, helper and packaging build inputs must bind
the same source commit. Hashes establish consistency with the selected HTTPS
source; this unsigned distribution does not provide independent signing-key
publisher authentication.

Declare the fixed offline migration entry `app/server/native-migrate-v1.mjs`,
supported source schemas and target schema in the application manifest. Its bundle
contains only the schema-helper source closure, recorded in `legal/server-bundle.json`. It imports no Store,
configuration, server, ChatService or messaging provider.

## Native provisioning

Keep fixed SCM image paths and service accounts. Do not delegate
`SERVICE_CHANGE_CONFIG` to the updater: that also permits account changes.
Delegate only the required fixed-service query/start/stop rights and the narrow
bootstrap/control file rights. Application Runtime remains separate from the
Authority. Remote IPC is rejected and local peers are checked by actual OS
identity and process generation.

Provision the migration AppContainer for the actual Runtime account, named
`Yahad.Migrate.{InstallationId}`, with no network capabilities and no loopback
exemption. Provisioning under the approving administrator's account is wrong.
`InstalledMigrationProfile.EnsureForCurrentRuntime` performs this from the
authenticated Runtime service before its waiting admission. An absent or
inaccessible Runtime user profile is an explicit acceptance failure; there is
no administrator, LocalSystem or alternate-account fallback.
The update transaction may change ACL/MIC only on its generated shadow payload,
new clone, work and temporary directories. It normalizes the output only after
observing root and whole job exit. It never runs the migration under Authority.

Seed `system-bootstrap-current.json` and the corresponding verified baseline
system slot. The selection is Authority-written and Runtime-readable; include
the policy/source pins and exact component closure. Keep the immutable baseline
probe intact. The shared operational-probe selector revalidates a present slot
selection and never falls back from malformed or inaccessible state.

The helper boot/recovery registration contract must preserve at least one
complete runnable helper closure while replacing the other. The A/B bootstrap
code uses the same policy, source, serial lease and transaction journals; it is
not a second download or update scheduler. Initial registrations and their
service-account support require native installer acceptance. Do not rely only
on SCM failure actions to recover an executable which cannot start.

Use `BootstrapRecoveryProvisioningContract.Describe(A|B)` for the exact fixed
registrations: `<AuthorityService>.RecoveryA` and `.RecoveryB`, automatic
own-process services, each running as its own `NT SERVICE\<service-name>` virtual account. Their fixed
images are `bootstrap/handoff-a/Yahad.Authority.exe` and
`bootstrap/handoff-b/Yahad.Authority.exe`, with arguments
`--system-recovery-service A` or `B`. The declarative contract supplies the
cross-slot failure actions and primary failure command. Both automatic slots
also perform independent boot reconciliation when the primary executable
cannot start. Initial service creation, failure-action registration, repair and
uninstall belong to the installer; updater code cannot reconfigure services.

Normal live handoff also starts the selected fixed SCM helper service. It does
not rely on an Authority child process surviving SCM termination. Actual SCM
PID, creation time, executable closure, account and nonce bind custody before
Go. The replacement Authority adopts the retained runtime/data/global owners
and helper job before waiting for whole-job exit and replacing helper bytes.

After validating the immutable installation anchor and actual service role,
the Runtime and Authority publish narrow access on their own process objects.
`PublishAuthorityCustodyAccess` grants only the compiled Authority/A/B principals
process rights `0x101141` for helper custody, and token query `0x8` only.
It grants no Runtime or Owner custody access. The separate peer publishers
allow authenticated process observation and the fixed Runtime stop/job rights.
Preserve the expected minimal service privileges (`SeChangeNotifyPrivilege`)
and verify native readback/cross-service opens; no debug privilege or general
token-duplication permission is required.

Microsoft describes virtual accounts as `NT SERVICE\<SERVICENAME>` with
automatically managed credentials ([service account documentation](https://learn.microsoft.com/windows/security/identity-protection/access-control/service-accounts)).
Using one service's virtual account for the other two registrations failed
native provisioning with Win32 1057. Each service now uses its own account;
explicit fixed peer grants replace the unsupported shared-account assumption.
Runtime receives SCM query-config/status only (`0x5`), while Authority/A/B
receive query/start/stop (`0x35`), never configuration or administrative rights.
Nonce-bound child probes are not SCM custody actors. Native acceptance of the
corrected profile remains required; no privileged account fallback exists.

## Transaction and recovery behavior

Actual application idle acknowledgment precedes stop. Browser drafts, recording,
child activity, in-flight requests and unknown sends block admission. Acquire
actual process/job quiescence and exclusive data/update leases. Verify encrypted
data, keys, policy and prior payload backup before bootstrap disruption.

The bounded helper adopts actual duplicated owners and acknowledges custody
before the old Authority can exit. The replacement Authority authenticates the
handoff, adopts ownership, waits for the old helper to exit and finishes helper
replacement. A reboot reconciles observed files/processes and durable journals,
not an assumption that the last intent completed.

Every full application update prepares a new data copy. A schema change runs
only the fixed offline entry in the Runtime-account AppContainer; Authority
independently checks SQLite, closure, keys, policy, token and process cleanup.
The original data is retained. Successive updates bind the actual current clone
as their source, including its previous restore context.

Candidate startup uses typed probation: local readiness is checked while HTTP
mutations and provider startup remain held. The transaction durably records
effects intent before its one generation-bound release command. An unknown ACK
is never retried as the same command and permanently forbids restoring old data
over possible new writes. Before effects, exact previous-source return is safe;
after effects, recovery preserves current data and applies the schema-safe
return/forward-repair rules. Repeated failed candidates are quarantined.
If automatic authorization was revoked after effects became possible, a
bounded availability restart can boot the already committed current version
with the current data and fresh local health. It cannot publish a new version,
migrate or restore data, reissue the old effects command, or renew Auto.

The protected root router completion must be written only after replacement
Authority reconciliation. A successful helper spawn or handoff is not an
application-update success. Native Owner status/recovery remains available with
the web application stopped.

## Acceptance gates

The isolated source tests exercise synthetic HTTP/SCM/identity observations,
private real SQLite copies, owned private process jobs, journal crash boundaries
and authorization failures. They do not establish real service ACLs,
AppContainer provisioning, cross-service virtual-account support, reboot
recovery, the packaged installer consent path or an installed two-update run.

Before activating the entry points in an integrated installer package, require:

- Exact committed-source compilation, relevant application/updater tests and
  independent review, with all remaining failures named rather than hidden.
- Native initial provisioning/repair/uninstall checks for the combined layout,
  including recovery launch when the primary image cannot start and crash cuts
  while either helper slot is being replaced.
- Actual Runtime-user profile/AppContainer creation and isolated migration,
  same-account helper-service logon, own-object ACL publication and cross-logon
  handle transfer under the final installer identities and service privileges.
- Two successive compatible synthetic installed releases in Auto with no
  repeated UAC, then Notify with no automatic apply. Include a schema-changing
  release, idle blockers, disk/offline failures, unhealthy startup and reboot.
- Lost acknowledgments and later writes preserve current data and unknown send
  outcomes; no real WhatsApp or real public release is needed for acceptance.

No installed services, tasks, AppContainer profiles, machine security settings,
releases or installed updates were created or changed by this isolated task.

The former entry-point integration patch has been superseded by the integrated
source and removed. This handoff describes the earlier isolated task; use the
current installer source and validation status for activation readiness. The
initial designated-parent binding returned by
the enrollment builder must use the actual first-install application identity
and authenticated original Owner; the pure builder does not discover either.

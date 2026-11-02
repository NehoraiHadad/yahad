# Source integration review, 2026-11-01

Scope: isolated integration/windows-full-installer-060; updater baseline
c541b191d67240e083eb1a2be79a002f3d649449, preserving installer887648e4 and
network wording2266bbf (applied0e1f60e). Exact final source and artifact digests
are recorded by the separate frozen delivery manifest after compilation.

Independent source review covered full role storage, native service registration,
consent/elevation, first Owner binding, A/B Helper publication, actual-generation
readiness, current-clone LAN maintenance, durable uninstall and preserved-data
reinstall/deletion. Reviewed corrections include administrative SCM ownership,
no existing-owner adoption, pre-commit-cut receipt publication, bounded exact
installer residue handling, preserved consent timestamps/mode, state-only UI
presence and exact old-ID Owner shortcut cleanup. Final targeted source review
reported no remaining concrete blocker in this reviewed scope. This is not
native security, reboot, real-provider or installed two-update acceptance.

Interim checks before final frozen-source build: 609 updater-policy cases passed
(no Node-native extension in that run), 16 full-installer pure cases, 2 hidden
WPF screenshot scenarios, 6 real SQLite/synthetic-identity cases, 3 private-file
atomic-publication cases, 31 scoped web/onboarding cases, 19 synthetic packaging
unit tests; typecheck and Vite build passed. Final frozen delivery must rerun
relevant shared suites and report its precise counts; these interim results do
not claim an unrun final-head suite. Intermediate fixture/build failures and
sandbox restrictions remain in the private logs, not rewritten as passes.

The sandbox refused private temporary fixture access and Vite realpath. The
same tests/builds were run through the official approved desktop-user tool
route; no alternate process/sandbox/IPC workaround was used. No real service,
installation, network/trust/ACL setting, family database, provider connection,
public release or repository publication changed in this task.

Limits: incomplete first-install ownership/removal remains fail-closed and
requires completing the exact-source preparation; native ownership/first-record,
shared virtual-account helper logon, lowbox profile, actual Store/installer,
SCM/reboot/two-update acceptance remain separate gates. See
`docs/WINDOWS-INSTALLER-INTEGRATION-ACCEPTANCE.md` for precise intended effects.

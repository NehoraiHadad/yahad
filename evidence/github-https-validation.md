# GitHub HTTPS trust preparation - 2026-10-26

The owner chose fixed public repository Releases over verified HTTPS without a
release-signature requirement for this new installation. No merge, publication,
live provider/Pi operation or persistent credential change was performed. The
existing signing key and published v0.3.1 remain untouched.

Passed in Ubuntu WSL Linux, Python 3.12.3 root **temporary fixtures only**:
**63 tests, zero failures/skips** = 42 worker tests + 21 owner-setup tests.
[Final output](github-https-python-0.3.2.txt) includes actual POSIX file ownership,
modes, locks and atomic files. Systemd, account, provider and network effects are
injected. An initial portable archive fixture omitted `deploy/yachad.service`;
added that fixture file and reran, preserving production checks.

Coverage includes explicit fixed-repository/no-token/no-key policy, default
signature compatibility, legacy/signature-state and installed-pin refusal,
stable latest only, asset repository scope, TLS/redirect policy, manifest hash/
size/schema/version bounds, no-signature download, backup-before-activation,
backup failure and health rollback under HTTPS trust. Existing crash/recovery,
no database downgrade, private queue and designated-parent mode tests still pass.

Owner-setup cases preserve synthetic live env/key/database across repeated
preparation; verify hidden escrow, no overwrite/link/FIFO, pause before service
actions, isolated backup/restore/export, uncertain stop and cleanup failure,
effective service override guard, acceptance gates, existing-pin refusal and
foreign/size/hash-inconsistent assets. Entry-point `DRAFT_ONLY` is still asserted
before platform access. AUTO_READY in test output is simulated, not activation.

Node24.19.0 local `package-https` produced exactly two assets with no key/network;
manifest bytes/hash/version matched the archive and safe extraction succeeded.
[Packaging output](github-https-package-0.3.2.txt). Application source is unchanged
from `04b5436d`, whose Linux 70 app tests, typecheck, production build,
production-only SMOKE_OK and synthetic Chrome UI/sticker checks remain applicable.
No app tests were skipped; they were not redundantly rerun for Python/docs changes.

Automatic approval **rejected removing DRAFT_ONLY**, because this instruction
permits code preparation and publication/read-back/activation prerequisites have
not passed. The rejected edit did not execute. No retry or workaround was used;
the guard is retained. Activation remains pending separate approval, not uncertain.
The previously denied merge/sign/publication action was not retried either.

Pending: exact candidate review and separately approved stable publication with
read-back, installer activation approval, then owner maintenance and Pi ARM64/
systemd/power-interruption/off-device restore/live provider acceptance. The new
owner flow needs neither release signing nor GitHub credentials. Retention and
local maintenance/acceptance safeguards remain; see [setup](../docs/GITHUB-UPDATES-SETUP.md).

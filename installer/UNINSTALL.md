# Windows uninstall choices

The new native removal screen defaults to **keep data**. The explicit full-removal
choice explains the local scope and requires the exact Hebrew word `מחיקה` before
the destructive button becomes enabled. The scrollable RTL content and fixed
action footer support the minimum 760×620 window; radio buttons, input and actions
have keyboard order and accessible labels. A normal installer opened over an
existing installation offers an uninstall-options link without requiring a command.

Both choices stop and remove only `Yahad.Acceptance`, remove verified owned binary
files under `C:\Program Files\Yahad.FirstInstall`, remove the exact Yahad uninstall
registration, and remove the original owner's matching Start Menu URL when present.
Foreign/modified files and ambiguous ownership stop removal instead of widening it.

* Keep data: preserves `C:\ProgramData\Yahad.FirstInstall`, including databases,
  family/chat records, keys, local WhatsApp authentication, local backups and private
  app/runtime payloads. Reinstall uses the existing protected identity and records.
* Full removal: deletes that entire local ProgramData root after an observed clean
  service stop/removal and successful exclusive data lease. This includes local
  backups inside the root. It does not perform secure disk erasure, unlink a remote
  WhatsApp device, delete exported backups elsewhere, or remove downloaded installers,
  OS extraction caches, shared runtimes or unrelated services.

The protected pending receipt is
`C:\Program Files\Yahad.FirstInstall.uninstall-state.json`, beside both removable
trees. It binds the original owner, exact installation and selected removal mode.
It remains through partial purge and final binary-directory cleanup; retry must use
the same choice. An interrupted full purge can already have removed some data and
the error UI says so. Reinstallation refuses while this receipt remains. Deletion
refuses reparse points, hardlinks, unexpected ACLs and locked files. Failure can
leave files for a same-choice retry; it never claims that partial deletion is intact.

Validation uses synthetic files, native no-follow handles, injected interruption,
locked/hardlinked files and offscreen WPF renders. It does **not** install/remove a
real service or exercise privileged uninstall on the working ASUS installation.
The running installed 9fc810d image is unchanged and still has its previous behavior;
these choices are available in the newly built installer, not silently patched in.

The recovery installer reports a fixed operation stage and numeric Windows error
or HRESULT instead of discarding these details. After validating the original
owner and removal choice, it also tries to write
`C:\Program Files\Yahad.FirstInstall.uninstall-diagnostic.json`. This file contains
only an opaque installation binding, removal mode, UTC time, Setup source revision
and sanitized error identifiers. It contains no exception messages, private paths,
file lists, payloads, account identifiers or credentials. Administrators and SYSTEM
can write it; the original owner can read it. Existing files must pass ownership,
no-link, bounded-format and installation-binding checks before replacement or
deletion. A logging failure never hides the original failure. Successful removal
deletes its owned diagnostic file before deleting the pending removal receipt.
The diagnostic file never authorizes removal or changes its fixed targets. A
legacy `INSTALL_FAILED` without these details does not identify the failing call.

`--repair-uninstall-receipt-ui` is a separate repair-only operation for the exact
six-ACE inherited Program Files receipt left by the audit-privilege bug. It shows
the fixed target and proposed descriptor and requires explicit consent plus the
ordinary authenticated original-owner UAC exchange. Runtime repair requires
separate approval for that one existing file. It never stops a service, removes
data or writes a diagnostic file, and it returns before uninstall continuation.
The ordinary uninstall reader has no relaxed-ACL fallback.

Repair requires an independent, strictly protected in-tree state in `committed`,
the original kernel-authenticated owner, the fixed owned service cleanly stopped,
and matching installed host/Setup hashes. Every legacy field/member must match
the protected state except the exact `removing_all_data` phase. The no-follow,
single-link legacy lease remains held across comparison, ACL protection, strict
re-read and unchanged-byte verification. Success displays the shared before/after
SHA-256; a missing anchor, changed source/binary, wrong owner, unsupported ACL,
different removal choice or interrupted/partial state refuses repair. ACL setters
change only Owner and Access sections, preserving Group and audit policy.

Store authorization fixtures prepare dependencies in the same private temporary
directory used by the admitted child. Their separate preparation budget is 120
seconds; product/lifecycle/grant/fault deadlines are unchanged. A preparation
failure confirms exact job-tree cleanup with a fresh bounded token and blocks
remaining Store cases instead of treating them as passed. These cases verify
prepared-cache authorization behavior, not cold-start performance. Seed markers
distinguish script entry (after the tsx preload), Store import and Store closure.

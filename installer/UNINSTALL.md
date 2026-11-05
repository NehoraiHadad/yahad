# Windows uninstall choices

The new native removal screen defaults to **keep data**. The explicit full-removal
choice explains the local scope and requires the exact Hebrew word `מחיקה` before
the destructive button becomes enabled. The scrollable RTL content and fixed
action footer support the minimum 760×620 window; radio buttons, input and actions
have keyboard order and accessible labels. A normal installer opened over an
existing installation offers an uninstall-options link without requiring a command.

Windows Settings starts the registered `Yahad.Setup.exe --uninstall` from an
unelevated shell, so the binary tree grants Users read and execute; without that
Windows refuses with "cannot access the specified device, path, or file" before any
UAC prompt. An installation made under the older ACL gains this entry on its next
upgrade; until then, start the same command from an elevated terminal.

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

The installer reports a fixed operation stage and numeric Windows error or HRESULT
in its result instead of discarding these details. It writes no diagnostic file.

Store authorization fixtures prepare dependencies in the same private temporary
directory used by the admitted child. Their separate preparation budget is 120
seconds; product/lifecycle/grant/fault deadlines are unchanged. A preparation
failure confirms exact job-tree cleanup with a fresh bounded token and blocks
remaining Store cases instead of treating them as passed. These cases verify
prepared-cache authorization behavior, not cold-start performance. Seed markers
distinguish script entry (after the tsx preload), Store import and Store closure.

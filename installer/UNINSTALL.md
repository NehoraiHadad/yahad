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

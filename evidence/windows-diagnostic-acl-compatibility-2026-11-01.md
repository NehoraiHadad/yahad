# Diagnostic ACL compatibility and authenticated progress

The native partial-install cancellation removed its services and two product roots,
then stopped at `UNINSTALL_DIAGNOSTIC_CHANGED` while retaining its protected intent.
The existing diagnostic had an Administrators owner, protected DACL and the original
owner's exact `0x1200a9` ACE. The full-installer writer emitted that read/execute mask,
but its reader accepted only the `0x120089` read mask. This establishes an ACL-policy
mismatch; the failure code alone does not establish an installation-ID mismatch.

The full writer now reuses the existing read-only uninstall-diagnostic descriptor.
The reader additionally accepts only the exact old owner mask with no ACE flags.
Administrative ownership, protection, expected principal, no-write checks and the
installation-ID check remain unchanged. No live ACL is repaired or broadened.

The elevated child also sends the existing `checking` progress phase after validating
the authenticated request and before package loading/preflight. Previously the UI
could keep displaying its UAC-waiting message during this work.

Regression cases cover the actual descriptor accessor against the reader, the old
producer descriptor, rejection of writes/foreign principals/inherited legacy ACEs,
and refusal to treat diagnostic readability as installer-metadata authority. These
are policy tests, not a claim of native on-disk or live-install acceptance.

Validation: the focused uninstall/recovery suite passed 43 cases and the full
installer/cancellation suite passed 51 cases. Build and independent diff review
passed. The first new negative-test fixture used an owner alias absent from the
legacy descriptor; that test-only replacement was corrected before the final run.
The live ACL and protected removal intent remain unchanged pending the patched
product's normal authenticated resume.

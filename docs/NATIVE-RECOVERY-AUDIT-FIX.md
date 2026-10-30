# Recovery audit corrections after e3388c6

This delta addresses the three independent review findings: plaintext SQLite validation copies under failed backup artifacts, encoded receipt size exceeding its reader's cap, and the extra receipt file exceeding the artifact count budget. The active data directory is never overwritten. Production service/activation/authentication gates from the previous checkpoint remain unchanged.

## Plaintext validation staging

SQLite copies now live only under the private sibling catalog `.recovery-probes`, never inside `backup-*`. Both backup inspection and restored-tree validation use this path. A backup directory is not created until the probe has succeeded and its private cleanup has succeeded.

Each probe scope has a protected private DACL and an HMAC-authenticated ownership record binding its generated name, data-path digest, purpose, worker PID and lease generation. That record is flushed/published before any SQLite bytes are copied. A real exclusive native lease protects the scope while it is in use. The next offline admission reconciles abandoned scopes under the existing source lease and observed job boundary.

Cleanup verifies ownership, file ACL/link/path boundaries and the exact permitted member names. It removes plaintext before ownership metadata, preserving the authenticated record if a file is locked or deletion fails. Unknown, tampered, unowned-payload or incomplete-ownership-record residues remain private and block admission. An empty/recognized-marker-only directory left before ownership commit or after completed data deletion contains no payload and can be removed. Cleanup never kills a process based on a stored PID and never treats that PID as proof that a service has stopped.

Ordinary exception/cancellation cleanup is useful but is not the crash boundary. The boundary is structural separation from exportable artifacts plus authenticated, fail-closed reconciliation on subsequent admission. A controlled worker kill after copying a valid SQLite DB verifies this behavior. The test also holds the residue DB with an exclusive Windows handle and corrupts the ownership MAC: both prevent admission without deleting the data or its intent. Once the fixture lock/MAC fault is removed, a new admission verifies and removes the residue.

Deletion is ordinary filesystem deletion, not secure erasure. Private residues can persist until reconciliation. Failed artifacts made by the old e3388c6 fixture may still contain the old `backup-*/probe` subtree; this delta does not silently modify historical fixture evidence. Such folders must remain privately quarantined and must not be exported. Receipt verification already refuses missing receipts or unexpected members. Any future exporter must require that complete verification and export only the verified backup directory, never the fixture root, probe catalog or restore workspace.

## Shared receipt and count budgets

The same **4 MiB encoded-envelope limit** now governs metadata preflight, envelope writing and receipt reading. The outer envelope has explicit JSON encoding for its base64 payload and hexadecimal MAC, so encoded length is deterministic. Preflight uses the actual member names/sizes and observed schema, fixed-width digest placeholders and exact chunk overhead. Encryption fills fixed-size chunks; the real final envelope must equal the preflight length before publication.

Preflight occurs after private SQLite inspection and before creating a backup artifact or encrypting payload files. Inputs exceeding the aggregate encoded budget are refused at that point. The individual maxima of 10,000 records and 200 characters per member do not promise that every combination fits the aggregate byte budget. Unicode/default inner JSON escaping is included in the measured serialized bytes.

Payload/restore membership remains capped at **10,000 records**, including the three identity/policy records. The backup artifact separately allows **10,001 files**: one ciphertext per record plus `receipt.json`. It still requires exact membership; an additional file is refused. Member path limits are also checked before payload writes rather than only during final verification.

## Observed validation

The dedicated suite passes **36/36**, zero skips, including the existing actual schema-5 Store round trip. New cases cover actual inspector exception, cancellation after copying, killed-process reconciliation, locked/tampered residue refusal, metadata-only versus unowned-payload handling, exact codec/intake capacity and the next-byte overflow, 10,000 long-path metadata records, actual 200/201-character source names, and a real **10,000-ciphertext + receipt** artifact accepted by the verifier with the next file rejected. The encoded-limit metadata tests do not claim that a 10,000-file source was encrypted/restored end to end; the artifact verifier case creates and checks the actual files.

The existing native harness passes **81/81** with zero compiler warnings/errors. Evidence is in `evidence/native-recovery-audit-fix`. These remain synthetic Windows x64 fixtures, not machine-installation, power-loss or production export acceptance. No service, host security/access setting, listener, production credential, active-data selection, deployment or publication was changed.

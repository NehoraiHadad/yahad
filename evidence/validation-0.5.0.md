# 0.5.0 local candidate verification — 2026-10-27

Unreleased candidate. No host data, credentials, WhatsApp connection, production signing key, trust pin, deployment or provider was changed.

## Scope
- Parent all-groups catalog is distinct from permission to read retained conversation content; child grants unchanged.
- One canonical account name for display/login, including Hebrew. Atomic schema 1→2 migration preserves immutable IDs, password hashes, sessions, grants and encrypted data; no username/display aliases.
- Account rename, targeted management, responsive login details, truthful update/recovery states.
- Explicit app schema-migration manifest and minimum updater protocol, schema-aware fail-closed recovery.
- Independent signed root-worker runtimes, stable supervisor, fixed-source transport, one-time reviewed bootstrap and offline packaging.

## Executed
- Final version 0.5.0: 176 application tests passed, zero failed/skipped.
- TypeScript typecheck and Vite production build passed.
- Production synthetic fresh/schema-upgrade smoke passed (schema 2, migration sources [1]).
- Python aggregate: 224 discovered, 167 executed passed, 57 explicit platform/privilege skips.
- Independent review included 19 supervisor, 15 transport, 4 worker protocol, 2 packaging and 31 bootstrap checks. Real ephemeral test signatures and joined bootstrap→transport→supervisor→dispatch exercised; no production key created.
- Regression cases cover stale permission responses, repeated requests, interrupted schema transactions, preserved data, invalid signatures, replay versus local fallback and injected persistence boundaries.

## Required before live acceptance
- Provision a production updater signing key in an owner-approved secure signing environment; keep its private part off the Pi and out of Git/archives.
- Explicitly enroll the matching public trust pin and reviewed bootstrap on the host. Root self-update policy defaults disabled until explicitly enabled; parent notify/auto and explicit-install semantics are honored.
- Publish exact reviewed code/assets only with publication authorization; currently local candidate only.
- Browser geometry/focus, Windows/WSL installation and actual Pi ARM/systemd/reboot/power-loss behavior remain unverified. Unit/fixture tests are not substitutes.
- A committed incompatible DB migration cannot safely roll back to old code. Recovery preserves the DB/auth and stops; it never automatically overwrites newer data with a backup. When the app is stopped, its status UI may be unavailable.

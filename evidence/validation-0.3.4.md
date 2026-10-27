# 0.3.4 shutdown and responsive update requests

Base: published 0.3.3 commit `f8f7884ad80ff9fe58e7716caee9d30c5f6fb0b4`.
The reviewed shutdown and immediate-request/progress changes are taken from
`d343087f8062e8d37d22405682421403786ac7e3`; unrelated 0.4 onboarding, installers,
Windows integration, appearance changes and bootstrap changes are excluded.

## Scope and preservation

- Close SSE transports in `preClose`, before HTTP draining; reject new streams
  during shutdown and retain even logically ended transports until actual close.
- Publish complete private requests atomically, latch prompt worker activation,
  serialize work under the existing lock, and report correlated real progress.
  Prompt processing does not mean downloads/builds/backups finish instantly.
- Only the parent's transient background-refresh behavior is ported to the main
  UI; authentication failures and explicit action failures remain visible.
- Database schema, store/auth persistence, WhatsApp connector, master-key paths,
  backup/restore implementation and historical installer are unchanged. There
  is no database reset, credentials deletion, account unlink or new pairing step.
  Dependencies are unchanged; package and lock metadata are version 0.3.4.

## Validation of this candidate

Node 24.19.0 / npm 11.9.0, isolated synthetic cloud workspace:

- TypeScript typecheck passed.
- Full application suite: **92 passed**, zero failures/skips. This includes
  synthetic encrypted database/media/WhatsApp-auth backup/restore and three
  shutdown regressions: open authenticated SSE, reconnect rejection, and
  already-ended buffered transport cleanup. Immediate backup succeeds after
  graceful app close and normal data-lock release.
- Production build passed; production-mode updater smoke returned `SMOKE_OK`.
- Python updater suite: **80 discovered, 37 passed, 43 root/platform-dependent
  skipped**. This is not full privileged/native-systemd acceptance.
- The live-SSE shutdown regression previously failed against the unchanged
  0.3.2 server. Buffered-end coverage verifies the preClose teardown contract;
  it is not a claim that Node 24's idle-socket fallback also hangs.

No real provider, family data, Pi operation or deployment was used. This slice
does not claim visual-browser or native-systemd timing/sandbox acceptance.

## Existing-installation boundary

An application ZIP does not install the privileged worker, request path/service
or matching data-directory and protected-file overrides. Prompt wake-up requires
the separate reviewed owner/root integration; the existing timer remains the
fallback. Do not rerun the historical version-pinned installer as an upgrade.

The shutdown fix cannot repair the old running 0.3.2 process before the new app
is installed. The first transition still needs a safe owner-controlled clean
shutdown; never delete locks, bypass backup, reset the database or unlink the
WhatsApp account to force it. Publishing a release is not device acceptance.

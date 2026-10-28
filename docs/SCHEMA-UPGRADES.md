# Account-name schema migration and safe update boundaries

**Unreleased 0.5.0 candidate.** Local implementation/tests are not publication,
production trust provisioning, host installation or live WhatsApp acceptance.

## What changes in schema 2

One `name` is both the visible account name and the login name, including Hebrew.
`name_key` is only its normalized uniqueness/login index, not a second name or
an alias. The migration retains the original login name, not the former display
label (which might have been a contextual label such as “אני”). The old
`username` and `display` columns and API aliases do not remain.

The 1 → 2 transaction preserves account/family IDs, password hashes, sessions,
paired-device references, grants, messages, media references, encrypted WhatsApp/
Signal auth and the existing master key. Names are normalized using the same
rules as new accounts; invalid names or a normalization collision abort the
whole transaction rather than guessing a replacement name or suffix. Foreign
keys are checked before commit. No real provider is contacted by migration tests.

## Existing machines need an explicitly authorized one-time bootstrap

An already installed schema-1 worker rejects a schema-2 app manifest before
staging. Its root-owned source lives outside app releases and cannot update
itself by installing an app ZIP. Republishing the app or falsely labeling it
schema 1 does not solve that boundary and is unsafe.

The guided fresh installer contains the current direct worker and requires an
app-manifest format-2 release (this candidate uses DB schema 2). Its resume path still refuses changed managed helpers; do not rerun it
against an existing household to overwrite that refusal. An existing machine
needs the reviewed worker-bootstrap procedure before the first schema-2 release.
This is a root-code change requiring separate local authorization, not app
publication or deployment approval inferred from a code review. Development here
has **not** executed that bootstrap or changed any host/service/security setting.

`deploy/upgrade-worker.py` is the one-time enrollment path for either the exact
reviewed historical worker or the exact reviewed current direct worker. It pins
the immutable supervisor/transport, verifies an independently signed initial
worker runtime, provisions the separate public key and root source policy, and
replaces the existing fixed `updater.py` entrypoint with a stable launcher **last**.
Runtime files live under the existing root-owned update state directory; existing
service entrypoints and enablement remain unchanged. It adds explicit read-only
unit drop-ins for the pin/policy/immutable code and reloads systemd. These are
reviewed bootstrap boundary changes, not a claim of byte-identical host settings.

The app config, app-release trust, operator, auto/notify mode, version pin, data,
keys and app journal are preserved. Unknown helper bytes, a different existing
key, pending recovery or an unsafe path stop rather than being overwritten.
Existing active updater timer/path units are temporarily paused and restored.
A detected probe mutation instead leaves schedulers paused for local recovery;
no config/database is silently restored. SIGKILL/power loss can also leave a
scheduler paused: ordinary exception cleanup is not a power-loss guarantee.

The updater signing key authorizes **privileged root code**. Its Ed25519 public
DER SHA-256 must be verified independently, not copied from the same untrusted
bundle/download. The private key stays off the host and out of chat/Git. There
is no generated production key, implicit pin or silently enabled policy here.
After separate owner approval, the single local command has this shape:

```bash
sudo python3 -I -B deploy/upgrade-worker.py \
  --runtime-dir /PRIVATE_PATH/SIGNED_RUNTIME_BUNDLE \
  --public-key /PRIVATE_PATH/VERIFIED_UPDATER_PUBLIC.pem \
  --public-key-sha256 INDEPENDENTLY_VERIFIED_DER_SHA256
```

Use `--check-only` for eligibility/signature inspection without provisioning or
service changes. Independent updater downloads are **disabled by default**.
`--enable-updater-updates` is an explicit extra opt-in at initial provisioning;
an idempotent rerun preserves an existing policy instead of changing it. Existing
automatic app updates may resume when the original timers/path are restored.
The bootstrap itself neither installs an app release nor migrates the database.

## Reusable app/database contract

App manifest format 2 retains archive/version/hash/size and adds:

```json
{"format":2,"databaseSchema":2,"databaseMigrationsFrom":[1],"minimumUpdaterProtocol":2}
```

Schema IDs are positive integers bounded at 1,000,000. Migration sources must be
an explicit unique list of at most 32 lower schemas: no booleans, duplicates,
self-transition entry or downgrade. Same-schema releases need no migration.
The publisher must supply a real synthetic smoke fixture for every declared
source; a declaration alone is not a passing migration test. The current shared
`src/database-schema.ts` declares target 2/source 1. The worker can process a
future declared 2 → 3 migration without a new hard-coded schema limit.

A higher required worker protocol is rejected before downtime. It never selects
a root artifact/key/URL. The authenticated app manifest is persisted by root in
the release tree; observed DB schema must match the installed app's metadata.
Missing legacy metadata is a narrow schema-1 bridge, never permission to infer
support for an unknown schema. Old manifest format 1 is schema-1-only.

## Independently authenticated updater self-update

The immutable supervisor and transport are **outside** the replaceable bundle.
The root runtime envelope has its own type/version/hash/size/protocols and an
Ed25519 signature anchored to `/etc/yachad/updater-signing.pub`. The versioned
bundle contains only `updater.py`: no units, keys, hooks, helper replacements,
configuration or arbitrary paths. Worker protocols/app manifest formats can
advance within the fixed supervisor/helper/config/state ABI. A change to that
immutable boundary or trust pin requires a separately reviewed bootstrap.

The independent root policy `/etc/yachad/updater-source.json` fixes the repository
and stable channel. It downloads optional `yachad-updater.json`,
`yachad-updater.sig`, `yachad-updater.zip` from the same normal stable app Release;
updater runtime version is independent of the app tag. Do not create an
updater-only nonsemantic `latest` release. An unchanged app version can still
carry a new signed updater. An app manifest or web request cannot supply a URL,
key or executable command. Ordinary GitHub-HTTPS app trust is **not root-code
authority**.

With independent updates enabled, normal checks also honor the existing app
`enabled` and auto/notify policy. A fresh, bounded explicit `install` request from
the configured operator can upgrade the signed runtime in notify mode, then the
worker consumes that same untouched queue entry to install the app. Check-only,
foreign/expired/replayed requests do not authorize root installation. Policy and
intent are rechecked under the same lock before staging and after both probes.
Recovery/boot paths are offline and never fetch a new runtime. Missing optional
assets or rejected signatures leave the installed verified runtime available;
a higher-minimum-protocol app still fails before downtime if no valid compatible
runtime can be installed. Root transport outcomes are local sanitized state,
not a promise of a new remote status UI.

Staging verifies signature, exact flat archive allowlist and immutable version
identity before execution. Two bounded, explicit read-only capability probes
run with the inherited worker lock; files/directories/journals/pointer switches
are fsynced. Pre-commit failure can restore the authenticated previous runtime.
Signed network replay/downgrade is rejected separately from that local recovery.
After runtime commit, an app/DB failure **never** triggers rollback to an older
worker. The app journal binds the active runtime digest from its first checking
phase through downtime/recovery, preventing recovery by a mismatched runtime.

Offline publisher packaging (no publication or key provisioning):

```bash
node scripts/release-updater.mjs package 1.0.0 /PRIVATE_OUTPUT/updater-1.0.0 /PRIVATE_KEY/updater.pem
```

This packaging/signing command requires an approved POSIX environment with Node
24, Python 3 and a private-key file protected as 0600. It is **not a Windows
signing workflow**. A Windows-only publisher must first choose an approved
compatible signing environment or a separately reviewed Windows signing path;
no private-key transfer to the cloud is assumed or authorized. The fingerprint
in the bootstrap example is a literal placeholder, not a production pin.

Tests use disposable synthetic keys only. Real signing-key custody/fingerprint,
release publication and actual host activation remain explicit owner decisions.

## Preflight, activation and rollback

1. Validate the exact candidate: type checks, application tests, updater tests,
   built UI and production smoke. The builder smoke creates only synthetic
   stores and exercises both a fresh schema-2 database and the complete schema-1
   account-name migration. The builder never receives household data or keys.
2. Verify the authenticated app manifest/archive and schema transition contract.
   Install dependencies with scripts disabled and run the schema-targeted smoke
   as the isolated builder **before** any service downtime.
3. Journal the active runtime digest, previous app path/version and previous/target DB schema; stop the
   app; confirm the DB has not changed schema unexpectedly; create and sync the
   encrypted pre-update backup using the old app as the data owner. Record its
   exact private archive filename (`backupId`) before activation; an absent or
   ambiguous receipt aborts activation.
4. Atomically switch app code, then start it. Migration is an application
   transaction, never root-executed package code. Readiness must match the exact
   candidate version, manifest schema and actual SQLite schema.
5. On success, keep current and previous code. On failure, stop the app first.
   Restore old code only if the actual DB schema still matches the journaled
   previous schema. A failed migration that fully rolled back can therefore
   recover to schema 1; a later schema-2 → schema-2 code update can also roll back.

**Material difference:** if schema 2 committed and readiness then fails, old
schema-1 code is not started. The worker leaves `recovery_required`, preserves
the migrated database/auth, and blocks automatic/boot recovery from running the
wrong code. Because the application is stopped, its normal web/status endpoint
is unavailable; a stale open page is not a guaranteed recovery console. Local
owner inspection is required—there is no new privileged HTTP endpoint. This deliberately prefers stopped service over corrupting data or
silently restoring stale WhatsApp credentials. Do not reset the phase, point
`current` at schema-1 code, downgrade the helper, or rerun setup as a workaround.

Authorized recovery must inspect the actual schema/journal and preserved
candidate. Prefer a separately reviewed forward repair that keeps the current
schema/data. If recovery instead requires an old backup, explicitly account for
newer messages, revoked access and evolving WhatsApp/Signal auth; authenticate
and inspect it in a **new isolated directory** first. Never auto-restore over the
live store or run restored auth alongside the original. Existing encrypted
backup format `YACHAD01`/metadata version 1 is an archive format, not DB schema 1.

## Verification boundary

`tests/test_schema_upgrade.py` runs real temporary SQLite and filesystem fixtures
with injected network/build/service/ownership effects. It covers schema gates,
version/schema readiness, backup ordering, build failure before downtime,
pre-migration code rollback, post-migration fail-closed recovery, same-schema-2
rollback and downgrade refusal. `scripts/update-smoke.ts` exercises the real
production migration with synthetic encrypted data and no provider connection.
Local password recovery is schema-2/name-only; it preserves auth and conversations
and revokes only the reset account's sessions/devices. Unrecognized characters on
an older Python Unicode runtime fail closed rather than selecting another account.

Additional synthetic coverage:

- `test_runtime_upgrade_integration.py`: real transport, ephemeral Ed25519,
  real supervisor staging/activation and an actual inherited-lock child; notify
  install upgrades the runtime then dispatches the preserved app intent.
- `test_updater_supervisor.py`: tamper/key/protocol/path/replay rejection,
  signed-but-unstartable candidates, locking, preflight mutation detection,
  injected crashes at durability boundaries and no post-app runtime rollback.
- `test_worker_upgrade.py`: full one-time bootstrap, exact source pins,
  fresh direct-worker enrollment, independent pin/default-disabled policy,
  scheduler/boundary behavior, retries and later signed runtime acquisition.
- `test_updater_transport.py`: disabled/notify/check-only behavior, strict install
  authority, under-lock revalidation, stale-status serialization and no network
  on boot/recovery.

These are not evidence of a live Pi/systemd deployment, power-loss durability,
ARM execution or real WhatsApp reconnection. Those acceptance checks remain
separate from local development and require the appropriate host authorization.

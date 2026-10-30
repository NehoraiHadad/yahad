# Installer release identity

The root `package.json` is the numeric release-version source. It is not copied
into new hardcoded native constants. Python build tools require a canonical
stable version and equality with both `package-lock.json` version fields.
The shared `Yahad.Install.Core` assembly embeds the same package file; its
`ApplicationRelease.Version` is read from that trusted build resource rather
than a mutable installed file or environment variable.

The full payload, installer manifest, Setup package/payload admission, initial
receipt, service receipt validation, preserved-data reinstall and uninstall
registry DisplayVersion share this identity. Exact source/digest/owner/data/key
checks are retained. A matching version alone cannot authorize an upgrade,
reinstall or replacement of an existing installation.

Application-updater archives continue to use the existing format-2 manifest,
schema/migration declarations and updater protocol. The application updater does
not replace the installer-owned host. An existing host retains its compiled
original-install identity; an unrelated freshly compiled host is not silently
allowed to adopt its receipts. No installed receipt, key or policy is rewritten
by this build-source change.

Before staging, the packager checks the locally built Platform and ServiceHost
`--describe` audit output against both application version and exact compiled
artifact/Core source commits. Old host/helper directories cannot be mixed into
a new source package just because their PE/runtime dependencies still match.

The small native bootstrap must be rebuilt using a pin for the retained full
installer produced from the same clean commit. Its build refuses version/source
drift and requires actual matching retained bytes before a download URL can be
configured. The old checked-in 0.5.3 pin describes real historical bytes and is
preserved; it now fails the current-version build gate. Do not invent a new pin
or change its version label without building and verifying a fresh artifact.
The retained installer's non-installing `--verify-package` audit must also match
its embedded manifest, Setup and Core source identities. These are own-build
consistency checks; they do not authenticate a publisher or establish signing.

Pure Python tests cover canonical parsing, lock drift, stale/foreign pins and
retained-byte checks. Native `--version-only <source-root>` tests compare the
embedded version with source, test future versions without a new source
constant, reject malformed identities, refuse legacy/foreign installer packages,
and retain receipt repair barriers. Existing synthetic first-install cases cover
preserved reinstall, package membership/hashes and private file boundaries.

These tests are not a host installation. The signing, corresponding-source,
transitive-notice and actual-machine gates in the existing native acceptance
and distribution documents remain unchanged. No service, network, trust or
active installation change is performed or authorized by this fix.

# Contributing

Use a branch and a pull request. Keep changes small enough to review, describe
the behavior changed and record checks actually run. See [development setup](docs/DEVELOPMENT.md).

Commit source, tests, synthetic fixtures, dependency lockfiles, build scripts,
license notices and useful documentation together. `package.json` and
`package-lock.json` are the application version source; native binaries derive
their identity from the committed source. Do not update dependencies incidentally.

Do not commit generated installers, archives, caches, raw logs, local screenshots,
real databases, credentials or WhatsApp sessions. Keep generated output under
`.local/`, or the existing ignored `installer/artifacts/` and `installer/.scratch/`.
Review `git diff --cached` and `git status`. Ignore rules are not a secrets scanner.

Use Git history and issues/PRs instead of copied backup directories. A local commit
protects against file edits; only a successful push puts it on GitHub. Generated
binaries belong in reviewed Release assets. Preserve component notices and source obligations.

Tests must own their processes/jobs, cancel and await pending starts, close them in
teardown and remove only validated owned fixtures. Cleanup failure is a test failure.
Never sweep temporary folders or kill processes by broad names. Run the smallest
affected test first; report permission-dependent cases as blocked or skipped.

Service installation, real-provider connection, firewall/trust changes and
destructive acceptance are separate from development builds. Do not relax ownership,
ACL, receipt or updater guards to make a test pass.

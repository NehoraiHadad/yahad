# Development from Git

## Prerequisites

- Git and **Node.js 24.x**, including npm. Use the committed lockfile; keep the
  exact Baileys dependency unless deliberately reviewing a dependency change.
- Python **3.12+** for packaging and deployment tests (standard library).
- Windows x64, **.NET 10 SDK** and PowerShell 7 for native Windows builds.
  Linux can build/test the web app; Windows service/security behavior needs Windows.

Run commands from the repository root. Generated output belongs in ignored
`dist/`, `native/`, `node_modules/`, `.local/`, native `bin/obj`,
`installer/.scratch/` and `installer/artifacts/`. No other developer's log folders,
receipts or absolute machine paths are needed.

## Application

```sh
npm ci --ignore-scripts
npm run typecheck
npm run build
```

On Windows, build the private file-boundary helper before running the app or tests
that use the real Store:

```powershell
dotnet publish installer/Yahad.Platform/Yahad.Platform.csproj -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.14 -p:RestoreConfigFile=installer/NuGet.Config -o native
```

This creates developer output, not a service installation. Do not reuse an older
helper copied from an installed application.

`npm run dev` serves disposable mock data at `http://127.0.0.1:4173`.
The QA preview binds all interfaces, creates test sessions and resets its QA data
on startup. Use an isolated development environment. It is separate from
`npm start`, which reads service configuration. Do not use a real family's data.

First-run QA instead of a pre-created family:

```powershell
$env:QA_FIRST_RUN = '1'
npm run dev
# After stopping the preview:
Remove-Item Env:QA_FIRST_RUN
```

On a POSIX shell: `QA_FIRST_RUN=1 npm run dev`. First-run port: 4174.
`.env.example` documents service settings, not a ready production configuration.
Generate local keys with `npm run setup` only when intentionally configuring a
separate service. Never commit keys, .env or connector state.

## Tests

Inspect `tests/` and run the affected file first:

```sh
node --import tsx --test tests/windows-updater-policy-ui.test.ts
npm test
python -m unittest discover -s tests -p "test_*.py"
```

Python deployment tests include Linux/root-only cases; they are not all portable
to Windows. Keep skips and failures explicit.

Windows synthetic test entry points:

```powershell
./installer/build.ps1
./tests/windows-updater/run.ps1 -DotNet (Get-Command dotnet).Source
./installer/test-service-host.ps1 -DotNet (Get-Command dotnet).Source -Node (Get-Command node).Source -PackageCache C:/path/to/nuget-cache
./installer/test-native-recovery.ps1 -DotNet (Get-Command dotnet).Source -Node (Get-Command node).Source -PackageCache C:/path/to/nuget-cache
./installer/test-production.ps1 -DotNet (Get-Command dotnet).Source -Node (Get-Command node).Source -PackageCache C:/path/to/nuget-cache
```

Read script parameters first. Some integration suites need a prepared app/helper
payload; see [Windows packaging](../installer/README.md). Native ACL tests require
an ordinary Windows user token. Some link tests require privileges the machine
may lack. A sandbox denial is blocked coverage, not a reason to weaken guards.

Teardown helpers in `installer/test-support/` own fixture paths and process jobs.
Keep cleanup in finally on constructor, assertion and cancellation failures.
Never run a second heavy suite while the first still owns jobs/fixtures. Put raw
output under `.local/`; summarize results in the PR.

## Packaging and maintenance

[Windows packaging](../installer/README.md) describes the tracked package builder.
It does not install, publish or apply updates. Existing self-hosted release tools
remain `scripts/release.mjs`, `scripts/release-updater.mjs` and
`scripts/package-github-upgrade.py`; consult [Updates](UPDATES.md) for their
different payload contracts.

Commit coherent source and tests together, run affected checks and push the branch.
GitHub is the shared source record. Build output is regenerated from source and
declared inputs, not maintained as another copy of the repository.

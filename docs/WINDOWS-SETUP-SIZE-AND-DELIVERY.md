# Windows Setup: size and delivery options

Status: **design notes, not implemented.** Two questions raised after release 0.6.5
(Setup 287 MB, app zip 2.4 MB), each checked against
[WINDOWS-APP-PACKAGE-PLAN.md](WINDOWS-APP-PACKAGE-PLAN.md) ("the plan"). Both would
change a plan decision, so neither is built here; each ends with a recommendation
for the maintainer to decide.

## B. One .NET runtime for host, Updater and Platform

### Measured

On the cached infrastructure tree 1.0.0 (SHA-256 of every same-named file):

- `host/`, `updater/` and `platform/` each hold 192 files, about 77 MiB.
- 188 of the 192 names are byte-identical in all three, `Yahad.Install.Core.dll`
  included: 76.78 MiB per copy. Only each component's own `.exe`, `.dll`,
  `.deps.json` and `.runtimeconfig.json` differ (0.22-0.25 MiB).
- One shared set deflates (level 9) to 34.0 MiB. The whole tree is 433.7 MiB raw,
  209.3 MiB deflated.
- The pinned SDK's `shared/Microsoft.NETCore.App/10.0.14` and `hostfxr.dll` are the
  same bytes as the self-contained copies.

**Any one-runtime layout saves 2 x 34 MiB (about 71 MB) of the Setup, 287 to about
216 MB (-25%), and 153.6 MiB per installed infrastructure tree (307 MiB while the
`Previous` tree is kept during an upgrade, plan 8.1).**

### Options

| Option | Verdict |
| --- | --- |
| **(a) Framework-dependent images, one `infra\<v>\dotnet\`**, apphost search limited to the app-relative path (`AppHostRelativeDotNet=..\dotnet`, `AppHostDotNetSearch=AppRelative`, SDK 10.0.403 supports both) | Sound, with one fact still to prove by test (below). |
| (a2) Three self-contained apphosts published into one folder | Same saving, but not a supported SDK layout; a NuGet that brings another `System.*` version collides. Fallback only. |
| (b) One executable with modes | Rejected: merges the SYSTEM Updater, the Runtime host and Platform's command surface into one image for no extra saving. |
| (c) `PublishTrimmed` | Trimmed outputs differ per app, so they can't be shared; dominated by (d). |
| (d) NativeAOT (plan decision 6) | Removes all three runtime copies: the larger saving and the plan's chosen end state. Needs AOT-safe JSON, a P/Invoke review and an AOT CI test. |

Checks of (a) against the plan:

- **Accounts and ACLs (4.2).** `dotnet\` sits under the binary root and inherits
  `BinaryAcl` (`NativeSetupHost.cs:34`, `NativeInstallFiles.cs:19,24`). Every
  reader can already read all three component folders, so nobody gains a right,
  and the SYSTEM Updater still loads only bytes SYSTEM and Administrators write.
- **Runtime selection.** With the default search, `DOTNET_ROOT(_X64)` or the
  registered install location could select a runtime outside the tree. Platform
  is started by Node, whose environment the app controls, so the apphost must be
  app-relative only. **Unverified:** that an `AppRelative`-only apphost ignores a
  hostile `DOTNET_ROOT` and registered location at run time. That must be a test
  (`COREHOST_TRACE=1`, `DOTNET_ROOT` at an empty folder), not an assumption.
- **Upgrade and rollback (8.1).** The runtime is a member of `infra\<v>` and of
  its `infra-tree.json`, so it switches and rolls back with the images. One
  runtime per tree, never shared across trees.
- **Identity checks.** Unchanged: Platform's SCM PID/image check
  (`UpdaterClient.cs:97-102`), the Updater's own-folder rule
  (`UpdaterService.cs:33-35`), `InstalledLayout.cs:29-34`.
- **Uninstall (8.4).** The inventory derives from the tree records, so `dotnet\`
  is covered.
- **Tooling.** `validate_native` requires a private runtime closure and refuses a
  `framework` reference (`installer/windows/setup_package.py:217-230`); it would
  validate a framework reference plus one `dotnet/` root instead, and run
  `--describe` from the staged tree. `INFRA_REQUIRED`, `InstallPackage.cs:31-32`
  and `PairFixture.cs` gain the `dotnet/` member.

The Setup engine is not part of any sharing: it is WPF
(`Microsoft.WindowsDesktop.App`), runs before any tree exists, and uninstall
relaunches a private copy outside the tree (`SetupOperation.cs:81-88`).

### Recommendation

(a) changes the plan: it replaces the per-image private runtime (2a, 10
"Provenance per component", decision 6's chosen route). (a) followed later by (d)
would rewrite `validate_native` twice. **Keep decision 6 (NativeAOT) as the route
for the size saving, as its own infrastructure-track PR.** Choose (a) only if an
AOT trial shows a component cannot be compiled AOT; then only that component uses
`dotnet\`, and the five tests are: hostile `DOTNET_ROOT` and registered location
ignored; `dotnet\` removed fails closed with no fallback to `%ProgramFiles%\dotnet`;
the validator refuses self-contained remnants, a second framework version or a
non-app-relative apphost; machine acceptance install, upgrade A->B, rollback,
uninstall; a Setup size gate.

## C. An infrastructure-only Setup that downloads the app at first install

**Proposal.** `Yahad.Setup` embeds only `infra/`. At first install it fetches the
latest signed `yahad-windows-release-<v>.json(.sig)` and `yahad-windows-app-<v>.zip`
from GitHub, verifies them and installs the pair. A release that changes no
infrastructure then builds no new Setup.

**Today (the plan).** Every release publishes a Setup embedding the cached
infrastructure and the new app (decision 7), so first install works offline
(decision 3). The manifest names a Setup of the release's own version
(`ReleaseManifest.cs:61`, `installer/windows/release_manifest.py:62`).

### What changes

1. **Offline install is lost** (decision 3). First install gains a network step
   before anything is prepared: no install from a copied Setup on a machine with
   poor or no internet, on a LAN-only or captive-portal network, during a GitHub
   outage, or past GitHub's 60 unauthenticated API calls per hour per IP. The only
   GitHub client turns the proxy off (`UseProxy=false`,
   `installer/Yahad.Updater/GitHubReleaseSource.cs:19`) and allows four hosts
   (`:16`); right for the SYSTEM Updater, but a first run behind a proxy or a
   TLS-inspecting antivirus would now fail inside Setup, after UAC. Resume after a
   crash must come from a persisted, verified stage, never a fresh `latest`.
2. **Two downloads.** The Setup (still about 280 MB, see B: the app is about 11 MB
   of it) and then, inside Setup, the API listing, manifest, signature and app zip.
   The second can fail after consent, so Setup needs downloading/retry/offline
   states. `--verify-package` can no longer name the app that will be installed.
3. **Trust at first install.** No installed key exists yet, so the root is the key
   compiled into the Setup (`TrustedReleaseKey.Embedded`, written to
   `release-key.spki` at first install, `NativeSetupHost.cs:169-174`). TLS to
   GitHub is transport only; the app must be bound to a signature over that key.
   - Reusable: `ReleaseVerification.Verify` (`ReleaseManifest.cs:115-126`:
     signature over raw bytes, `yahad-release-v2` prefix, strict parse, key id) and
     `AppPackage.Open` (`AppPackage.cs:20-68`: size, SHA-256, member policy,
     `app-tree.json` = `treeSha256`). The download code lives in `Yahad.Updater`,
     which Setup doesn't reference, so it moves to `Install.Core`.
   - New: a keyless build must refuse (today it still installs,
     `ReleaseManifest.cs:91-92`); a first-install admission rule in place of
     `ReleaseSelection.Select` (which needs an installed pointer): the release's
     infrastructure version, tree digest and contract range equal the Setup's own,
     `requiresContract` supported; a freshness floor (any older signed manifest
     still verifies and could be replayed); a refusal when `latest` needs newer
     infrastructure than the Setup has ("download the new Setup").
     `minimumFrom` still applies over a kept `uninstalled` record; `Previous` and
     rollback don't apply.
4. **The landing page's fixed link** `releases/latest/download/Yahad.Setup.exe`
   (`landing/dist/index.html:149`, pinned by `landing/tests/static.test.py:150`,
   which forbids versioned links):
   - (a) keep uploading a Setup on every release: it is byte-identical within one
     infrastructure version, so it is copied from the cache, not built. Saves build
     time, not upload;
   - (b) link to the last infrastructure release: a versioned link the test forbids
     and a landing redeploy per infrastructure release;
   - (c) skip the Setup on app-only releases: the link returns 404. Not acceptable.
5. **Release command and Updater rules.**
   - `collectReleaseAssets` checks one exact set of nine names; it becomes two sets
     (nine, and seven without the two Setups), or stays nine under (a) with a check
     that the Setup equals the cached infrastructure Setup by bytes.
   - The manifest's `infrastructure.setup` must name `SetupName(Version)`; an
     app-only release can't name a Setup it doesn't carry, so the field becomes
     "Setup of infrastructure X in release tag T" unless (a).
   - **Updater stall:** a machine that missed an infrastructure release while
     `latest` is app-only gets `Select` = Setup (`ReleaseManifest.cs:141`), finds no
     Setup asset and throws `RELEASE_SETUP_SIZE` (`UpdaterEngine.cs:186-187`) on
     every check, forever, because it reads only `/releases/latest`
     (`GitHubReleaseSource.cs:14`). Fixing that needs a second, tag-based URL, or (a).
   - Plan 8.2 (a full Setup over the same infrastructure installs its embedded app)
     has nothing left to install.
6. **Release-time saving.** Measured 2026-11-16 on the maintainer machine, reusing
   cached infrastructure, after this PR's build changes (machine under heavy
   concurrent load, so wall times are high; CPU time is the stable figure):
   `system-package` 87-102 s wall (56 s CPU, nearly all of it deflating the
   455 MB infrastructure tree) and `setup-publish` 82 s, out of a 1207 s
   `package.ps1` run whose largest step is the one offline `npm ci` (app-stage,
   950 s). C would remove about 3 minutes (15%) of an app-only release; the app
   zip, the npm restore and the web build stay. Upload is unchanged under link
   option (a) of item 4.

### Alternatives

- **Keep the embedded-app Setup but publish it only on infrastructure releases.**
  Keeps offline install, but has the same manifest and Updater problems as C, and a
  new install runs an older app until the 03:00-04:59 window (decision 4) or a
  manual "update now".
- **C with link option (a).** No Setup build on app-only releases; landing link,
  `/releases/latest`-only Updater and exact asset set all still hold. Still loses
  offline install and still uploads about 280 MB per release.

### Recommendation

**Keep decisions 3 and 7 (an embedded-app Setup per release). Don't adopt C now.**
After this PR the Setup half of an app-only release is the `setup-publish` step
and its audit (see the timing table), against the loss of offline/LAN install,
proxy-sensitive first runs, a new trust floor, a manifest schema change and a
second Updater URL (or a re-upload every release anyway). The size lever is B
(NativeAOT), which shrinks every Setup without touching first-install trust.

If the maintainer still wants C, the plan changes are: decision 3 (online first
install with proxy policy, retry UI and a freshness floor); decision 7 (Setup built
once per infrastructure version, with link option (a) or (b)); 4.4 (distributable
Setup and installed engine converge); 5.1 (`infrastructure.setup` named by
infrastructure version and tag); 8.2/8.3 (app from a verified download stage,
persisted resume, mandatory key); 9.1/9.3 (first-install admission, Setup-track
stage carries the app zip); 10 (two asset sets or a byte check, and the landing
test).

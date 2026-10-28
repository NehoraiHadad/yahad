# Integrated 0.5.1 browser QA — disposable synthetic data only

This is a source-candidate acceptance procedure, **not an installer or deployment**.
Record the exact `git rev-parse HEAD` and `git rev-parse HEAD^{tree}` before testing.
No browser or real-device acceptance is implied by the automated tests.

## Prepare a fresh local checkout

Requirements: Node **24.x**, npm, and a local terminal. Use a new directory, never
an installed household application or a directory containing real family data.

```sh
node --version
npm ci --ignore-scripts
npm run typecheck
npm test
npm run build
npm run dev
```

`npm run dev` runs `scripts/preview.ts`, **not Vite's development server**. It serves
the built `dist/` assets on port 4173 and always uses the mock connector. It creates
a fresh synthetic family and **deletes/recreates `.local/qa-data` in this checkout
on each start**. Never use this harness for persistent data. Do not expose it
publicly: its QA frame routes create test sessions automatically.

Open these exact URLs:

- Parent desktop: `http://127.0.0.1:4173/__qa/frame/desktop?role=parent&reset=1`
- Parent tablet: `http://127.0.0.1:4173/__qa/frame/tablet?role=parent&reset=1`
- Parent mobile: `http://127.0.0.1:4173/__qa/frame/mobile?role=parent&reset=1`
- Seeded child: `http://127.0.0.1:4173/__qa/frame/mobile?role=child&reset=1`

Use separate browser contexts for parent and child; those routes set a session
cookie for the selected role. For a portrait **768×1024** acceptance viewport,
first visit the parent frame to establish the synthetic session, then open
`http://127.0.0.1:4173/` in that same context at 768×1024. The named tablet frame
itself is 1024×768. Mobile acceptance is **390×844**. Do not mistake a surrounding
iframe's dimensions for the application's actual viewport.

Stop with Ctrl+C. No system service is installed. Do not run `npm start`, an
installer, updater activation, systemd commands, provider login, signing/key
enrollment, or deployment as part of this QA.

## Zero-child and newly created account flow

For a separate empty synthetic family, stop the previous harness and run:

```sh
QA_FIRST_RUN=1 npm run dev
```

Open `http://127.0.0.1:4174/__qa/setup`. This mode has its own disposable
`.local/qa-first-run` directory. Complete setup with synthetic names and a
test-only password, inspect the zero-child state, then add accounts in the UI.
Account names are unique and use the **one-name model**: the same name is shown
and used for login. A new child starts with **no grants**.

For a newly added child, use a separate browser context and the normal login
page with that synthetic child's name/password. The `role=child` shortcut above
is for the seeded child only; it does not select newly added children.

## Acceptance checklist

1. Inspect zero, one, two, and many children, long Hebrew/English names, mobile RTL,
   portrait tablet, desktop, bounded roster scrolling, and group search with no results.
2. Create a child → confirm the returned account is selected → grant one group.
   Grant a different group to a sibling. Verify actions/feedback name the exact child.
3. Rename, test a conflicting existing name, reset a child's password, cancel/close
   dialogs, switch children, refresh, and use Back/Forward. No child-account deletion
   is in scope; “removal” here means **revoking a group grant** only.
4. Repeat clicks and switch selection around a pending save. Old feedback must not
   appear under another child. A failed/uncertain write must not claim success;
   re-read the server state before another change.
5. Revoke the first child's grant. That child loses access; the sibling's grants and
   the parent's retained conversation are unchanged. Parent viewing must never
   create child grants. Previously uncollected/deleted history is not recovered.
6. Check keyboard navigation, focus after a filtered checkbox row disappears,
   modal Escape/Cancel and focus restoration, layout overflow, and scroll behavior.
7. Record browser/version, viewport, source commit/tree, actual findings and any
   screenshots. Distinguish observed behavior from source/component-only tests.

## Update UI: honest limitations

The default mock harness does **not** run a privileged updater and does not
simulate a live install. Do not fabricate a recovery console or claim that an
unavailable application can serve a page in a fresh browser. If the app is
stopped, a fresh page load may simply fail; an already-loaded panel must say its
current status is unavailable, not promise recovery or report invented success.

The committed deterministic tests cover target changes, lost response → reload
→ duplicate request, queued/running state, reopen, authorization loss and recovery
barriers without changing a real host:

```sh
node --import tsx --test --test-concurrency=1 tests/update-progress-ui.test.ts tests/update-target-binding.test.ts
```

These are component-handler and synthetic HTTP tests, **not browser acceptance**.
No live Pi, Windows/WSL service, real provider, signing key or household data is
needed or authorized by this procedure.

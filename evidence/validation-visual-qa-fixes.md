# 0.5.1 desktop QA repair candidate

Scope: fixes following synthetic desktop QA of source `b0b9856733b1fb88ca8f5b6257dc35fa589174cf`
(tree `afca478046876353fb7fedf5392c92c9e01e2d11`). The local equivalent base is
`d6d1f1f42011d536189553eed1c1542bed0779af`.

## Changes

- Override inherited full-width add-child button on desktop; retain mobile full-width placement.
- Deterministically sort the child roster by name and ID. Scroll only the roster to reveal the selected child after creation, rename or selection.
- Preserve the selected child for this parent in the current browser tab, then reconcile it against the server's child list. Client selection does not grant access.
- Recover dialog focus when a submitting control becomes disabled, keeping Escape/Tab usable. Restore focus to the opener or accessible section heading after dismissal.
- Show duplicate-name errors as errors with field association and `aria-invalid`; use consistent password labels.
- Keep grant failures in their child/group-scoped feedback. Do not assert that a failed network request was delivered. Global errors have an explicit dismiss button and an eight-second lifetime; navigation clears stale errors.
- Isolate mixed-direction names, correct singular group counts and neutral Hebrew copy. Put technical history counters under synchronization details while preserving incomplete-history warnings.
- Integrate account-bound browser Back/Forward navigation and CLOSED EventSource recovery. Server verification remains mandatory before displaying conversation data. Obsolete streams cannot deliver events and session expiry cancels recovery.

## Verification

On the combined source candidate `730f4a8989d28ef4e36304dc338acdff46b94e72`
(tree `c3fdfda46f8f6c7a01772a1760d2bb9da909869a`):

- TypeScript typecheck: passed.
- Node tests: **224 passed, zero failures, zero skipped**.
- Production Vite build: passed.
- `git diff --check`: passed.
- Independent source review of navigation/reconnect boundary completed; no authorization widening found.

Tests cover deterministic order, account-scoped remembered selection, storage failures,
removed children, bidi markup, singular counts, error field association, focus recovery,
explicit toast dismissal/lifetime, navigation history, terminal stream recovery, expiry,
and the existing authorization/duplicate-action tests. Geometry, real DOM focus and browser
history acceptance still require the browser checks below; component and mock-history tests
are not a browser acceptance result.

## Browser recheck required

Use `docs/QA-INTEGRATED-051.md` on a fresh synthetic checkout. Recheck desktop1280×800,
mobile390×844 and portrait tablet768×1024. Particularly:

1. Add-child header width, 0/1/many children, long Hebrew/English names.
2. New selected child visible in a scrolled roster; rename and reload preserve selection.
3. Submit success, name conflict, pending save, Escape/Cancel, focus restoration and keyboard Tab.
4. Failed grant remains scoped to the correct child/group; no persistent toast hides filters.
5. Management → chats → group → Back → Forward, including repeated clicks and reload.
6. CLOSED event stream recovery without accepting stale events; actual session expiry remains terminal.
7. Parent opens an ungranted same-family group while the child still receives no access.

No corrected-build screenshot acceptance, mobile/tablet acceptance, live Pi/ARM installation,
real-provider test, updater activation, merge or release is claimed here. The runtime updater
implementation is unchanged by these frontend repairs.

## Mobile/tablet source follow-up

Muse's Firefox mobile/tablet report identified an additional long-account-name dialog
heading overflow and loss of a newly typed draft during connection recovery. The heading
now wraps within its flex space, bidi-isolates the name, and preserves the close control.
Local new-message drafts and attachments remain in the mounted, hidden/disabled composer
while permissions are being reverified. Confirmed removal, revocation, account changes or
leaving the conversation destroy that draft. No draft is persisted to storage. Server-derived
edit text is cleared, and delayed microphone permission cannot start recording while
suspended. Independent review caught and verified both latter guards before integration.
Recheck these two cases on the corrected build; source/component tests do not constitute
Firefox geometry or actual disconnected-browser acceptance.

## Notification non-obstruction follow-up

Muse's corrected Firefox mobile/tablet report (Slack 1793188543.424909) covers remote
325af5b/tree abae2e63 and reports the original issues, long heading and plain-text draft
recovery fixed. This is peer browser evidence, not a complete acceptance sign-off; its six
screenshots were not attached to that report. That candidate's Node count is **231**, while
224 above describes the earlier tested candidate, not the final mobile corrections.

The remaining eight-second notification/composer overlap is fixed in 0550d9a: notifications
occupy a reserved row above the app, with bounded scrollable text and an independent close
button. The app uses the remaining viewport height, including the existing wide-desktop
body padding. The content subtree remains mounted when notifications appear/disappear.
Independent source review passed. Browser recheck must verify the notification with focused
composer, long errors, narrow/mobile and wide-desktop screens; this source fix is not a claim
of corrected pixels. Attachment suspension, revocation deletion controls and delayed microphone
permissions still need browser evidence; no Pi or real-provider acceptance is implied.

Exact notification source checks: **232 Node tests passed, 0 failed/skipped**, typecheck
and production build passed. Documentation-only evidence update follows the tested source.

## Manual notification dismissal focus

Muse's source-verified report 1793190396.186319 on 55cc91c/tree73cfbf4e reports
mobile/tablet notification geometry and draft preservation passing, with auto-expiry
retaining composer focus. It identifies manual dismissal leaving focus on body. The
correction tracks the last valid outside control and restores it only on explicit close,
only if focus has not moved elsewhere and that exact control remains connected, visible,
enabled and outside no active modal boundary. Hidden/revoked composers cannot regain
focus. Timer expiry and unmount do not trigger restoration. Tests use synthetic DOM
objects; browser verification of this final focus correction is still required.

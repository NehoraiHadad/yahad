# Parent child-management candidate — 2026-10-28

## Scope and integration contract

- Based on reviewed `e5accd0cbf22493d5cbac07dbfa5fdb0204c40f9` (0.5.0).
- UI-only child-management work. No server, Store, provider, deployment, permission-policy or real-family changes.
- Integrate with parent access fix `54fcbd77a6e43dc04786ae63fc4bf052062aa94a`. Revocation and privacy copy assumes that fix: parent retains known family groups independently of child grants.
- Existing overview fields and endpoint payloads are unchanged. Accounts and selections use durable IDs, never names or array positions.

## Implemented flow

1. See all child accounts, per-child approved count/status and group-name summary in a bounded, keyboard-scrollable roster.
2. Select a child and see their name/login and groups in the same workspace. Add account, rename and reset password have distinct controls.
3. Search/filter all, approved or available groups. Native checkboxes apply only to the named child. Saves stay pending until a fresh overview confirms the exact child/group state.
4. Create account → returned ID selected → fresh overview → explicit first-group prompt. Creation notice clears on a grant attempt or a selection change.
5. Rename keeps the durable selection and grants. Unknown/removed account targets cannot silently fall back to modifying the parent's password.
6. Signature is inside the selected child's workspace; tablet instructions also name that child. Parent account settings and connection controls remain separate.
7. Initial load, error/retry, no children, no groups, no search results, stale overview, busy, failed and uncertain writes have distinct states. Repeat clicks are synchronously guarded; late completions are invalidated on child switch/unmount.

## Automated evidence

- `npm run typecheck`: passed. `npm run build`: passed (Vite, 1,614 modules).
- `npm test`: **190 passed, 0 failed**, including server auth/profile/grant tests, repeated parent-action races, 20-child structural limits, per-child rendered controls and exact child/group request payloads.
- Actual component-handler tests use deterministic hooks, not a browser. They cover rapid repeated clicks, fixed request target, sibling grants not confirming a save, stale/lost responses, revoke confirmation, and unmount/switch late completion.
- Account settings regressions cover cancel, account switch/draft discard, repeated submits, pending target lock, uncertain mutation retry lock and missing-child safety.
- These tests do **not** establish browser geometry, keyboard focus behavior, visual polish or real-device acceptance.

## Current-reference evidence and visual limit

The parent's authorized Slack review provided three synthetic 0.5.0 screenshots, downloaded and inspected as pixels in this run:

- `m03-first-run-ready-section-empty.png` (Slack file `F0CCUPNBRQ9`): tablet details and signature shown without an account selected.
- `m06-ready-section-child-no-groups.png` (`F0CCLKX2ANP`): no-groups state, followed by detached signature settings.
- `m07-group-picker-yosef.png` (`F0CCMJ1932R`): small child dropdown, generic selected-child labels and stale account-created notice above the grant list.

They are **current-reference screenshots**, not screenshots of this candidate and not our independently captured before/after audit.

Candidate visual capture is pending: the supported cloud CUA browser could not reach the disposable mock server at `http://127.0.0.1:4185` (`ERR_CONNECTION_REFUSED`). Inspecting a failed error-page tab was denied because its protocol was not http/https. No tunnel, proxy, alternate browser-control technology or policy bypass was used. The mock server itself started on localhost; no real provider was connected.

## Combined candidate acceptance still required

- Render current combined commit at 390×844, tablet and desktop. Check 0, 1, 2 and 20 children; long names and large group lists.
- Run create → returned child selected → grant → switch → revoke → rename → refresh, verifying child access is grants-only while parent known-group access remains available.
- Verify filters/search clear on child switch; old save/created notices never appear under another child's name.
- Test keyboard navigation, roster/list scrolling, checkbox focus after a filtered row disappears, modal focus restoration, Escape/Cancel, delayed responses and repeated clicks.
- Capture actual screenshots for the combined candidate. Do not call this visual acceptance complete from source or component tests.

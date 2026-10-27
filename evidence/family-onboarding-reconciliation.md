# Family onboarding reconciliation (unreleased)

Base: 0.3.4 local source commit `1d82667`. Separate worktree; original `yahad-next` and `yahad-034` left unchanged. Remaining candidate changes from `d343087` ported without deleting 0.3.3/0.3.4 validation and update-mode repair evidence. Root coordinates final combined commit and exact-commit checks.

## UI corrections

- Hebrew sibling names now suggest unused visible usernames (`child`, `child2`, …), keeping manual edits intact; server uniqueness remains authoritative.
- New-child confirmation is scoped to the actual selected, loaded child and cleared when selecting another.
- Account settings extracted to a focused component: synchronous in-flight lock; target and close locked pending; target-specific success; password clearing; inline associated errors; focus trap, Escape and return-to-opener behavior.
- Ambiguous account-creation transport or malformed response disables blind retries and freezes submitted identity/secret values. Parent is directed to inspect existing accounts/login; closing the child form reloads the overview.
- At the initial UI-only checkpoint, store/schema, connectors, service, updater worker, update controls and request units were unchanged. Subsequent owner-requested group-photo work adds connector/service/API handling; final combined evidence covers that additional scope. Store/schema and privileged updater/request units remain unchanged.

## Checks and limits

- Typecheck and production build passed.
- Full application suite: 101 passed, 0 failed/skipped at UI completion (installer reviewer still working separately).
- Password reset actual-handler harness verifies repeat submit, pending target switch/close/Escape, original target, named success, cleared secrets, inline mismatch without a request. This is deterministic React-hook unit coverage, not a browser.
- Independent targeted review/test pass: 15/15. Subsequently strengthened success assertion to require the full target-specific sentence (targeted 2/2 pass).
- Chromium cannot launch in this cloud sandbox: explicit `/tmp` profile fails at process singleton `socket()` with EPERM. Browser geometry, focus traversal, native validation, Back/Forward and mobile visual acceptance remain unverified. Prior candidate screenshots are historical, not new visual evidence.
- No Pi/Windows host changes, provider calls, push, release or deployment. Windows candidate remains WSL2/systemd only, not native Windows.

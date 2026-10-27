# Setup and management polish validation

Date: 2026-10-27. Base local commit: 9c1deed8b69ccee6e7f32eabf4ae3abc906988ed (published 0.4.0 source tree).

Scope: persisted-overview-derived parent progress, per-child authorized group title summaries, Windows/WSL read-only prerequisite preflight, installation documentation corrections. No authentication expansion, provider calls, deployment, release, version bump, or installed-host changes.

Validation:
- Node application tests: 129 passed, 0 failed, 0 skipped, including successful/uncertain mutation and superseding overview refresh regressions.
- Python: 122 discovered, 65 passed, 57 privileged/platform skipped.
- Independent reviews found and resolved an overlapping overview refresh race and a protected-directory nonroot preflight failure; regression tests included.
- Existing account password change and child password reset passed synthetic API acceptance; forgotten-parent recovery remains local-only, tablet login remains username/password.
- PowerShell test cases expanded but not executed: no PowerShell runtime available. Native Windows/WSL behavior is not verified.
- Browser visual QA not executed: Chromium socket / preview network interfaces blocked by runtime; attempted elevation unavailable. No new screenshot or live Pi acceptance claimed.
- npm wrapper attempted its unrelated registry notification and hit network policy after tests. Installed TypeScript/Vite binaries used directly for final checks without registry access.

Logs are local diagnostic artifacts, not evidence of deployed acceptance. Existing evidence/ux-0.4.0 is historical and excluded from this change.

- Final installed TypeScript typecheck and Vite production build: passed.

# Update UX and shutdown validation

Own-cloud candidate on top of c4c671d; not deployed. Published 0.3.3 remains separate.

- Final application tests: 94 passed, 0 failed/skipped.
- TypeScript and Vite production build: passed with installed local binaries.
- Python: 107 discovered, 50 passed, 57 root-only skipped.
- Independent review fixes: best-effort wake after authoritative queue publication; expired request terminal acknowledgement; request identity rather than unrelated phase for UI acknowledgement; explicit authorization-loss UX; SSE transport tracking through actual close.
- Synthetic regression: original 0.3.2 authenticated SSE blocks graceful app close; patched preClose completes and allows immediate encrypted synthetic backup. Actual Pi backup failure is confirmed but this specific cause still needs host evidence.
- Static systemd unit verification passed; actual systemd/Windows/WSL and real browser interaction remain unverified. Additional SSE ended-response coverage verifies defensive teardown, not another demonstrated Node24 hang.
- Existing Pi needs a separate reviewed root integration upgrade for immediate request units/helper. App release ZIP alone cannot install these components.
- npm's update notification contacted a policy-blocked registry during aggregate orchestration; application test output confirmed completion. Remaining typecheck/build and Python were run using installed local tools without fetching dependencies.

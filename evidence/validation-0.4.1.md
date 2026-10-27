# Yahad 0.4.1 validation

2026-10-27. Builds on published 0.4.0, local baseline 9c1deed8.

Scope: parent setup progress and per-child group-title overview, Windows/WSL prerequisite preflight and docs, child display names with username fallback/duplicate disambiguation, parent chat all/by-child group filter. Parent chat navigation remains available regardless of setup state. Filter does not impersonate children or change parent permissions/history access. Database schema remains 1. Existing updater/request services, keys, data and installation policy are not replaced by normal app update.

Independent reviews resolved:
- overlapping overview refresh and uncertain mutation race;
- protected configuration directory preflight PermissionError;
- ordinary message refresh incorrectly unmounting composer/dropping draft;
- account-settings test harness helper resolution.

Bounded request drain test now uses atomic inode replacement, preserving timestamp, instead of assuming unlink/recreate gets a new stamp. Production updater unchanged. All 17 request tests pass on Python 3.12.14/3.13.5, target repeated 100 times on 3.12. Python 3.11.6 original reported environment unavailable; exact native failure not reproduced.

Python full gate: 122 discovered, 65 passed, 57 privileged/platform skips.

Limits: real browser UI/React lifecycle and Windows/WSL native execution not run due known cloud socket/network restrictions and unavailable PowerShell. New asynchronous controller/filter tests and source contracts are not a substitute for device acceptance. No live WhatsApp/Pi or real-family data tests performed. These limits remain disclosed.

Final application gate: 139 tests passed, zero failures/skips; TypeScript typecheck and Vite production build passed.

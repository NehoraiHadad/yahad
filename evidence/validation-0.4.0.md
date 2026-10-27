# 0.4.0 combined validation

Base: published 0.3.4, remote commit 69cc546f738b8c4379015210fd56f77f36d33c9d (tree a14874bd5eb8fa0ccf1b149f4da00187e9865ff9).

## Scope

Family onboarding and safe account settings; admin checkbox overflow containment; authenticated, bounded group-photo proxy/cache; lazy searchable Unicode 17 picker with all 3,944 fully-qualified sequences including 2,030 tone variants; authorized conversation sticker reuse/upload preview; opt-in reduced-motion-aware celebrations. Guided installation preserves existing policy and recognizes exact historical CRLF files; Windows adapter is WSL2/Ubuntu, not native Windows.

Database schema, persistent store/auth and installed privileged updater/request-unit sources remain unchanged. Existing upgraded Pi receives ordinary app code; it must not rerun the fresh installer. New photo access preserves existing family/grant boundaries and makes no chat authorization changes.

## Root validation, 2026-10-27

On the frozen combined source in the own-cloud workspace (Node 24.19.0):
- TypeScript typecheck passed.
- Full application suite: 121 passed, zero failed/cancelled/skipped.
- Production build passed; update smoke returned SMOKE_OK.
- Python: 116 discovered, 59 passed, 57 privileged/platform skips.
- git diff --check passed.
- Main JS 301.04 kB / 95.74 kB gzip; lazy picker 328.23 kB / 42.27 kB gzip.

Independent focused reviews covered UI race handling, photo SSRF/auth/cache and timeout boundaries, installer security/provenance/cancellation, exact Unicode sequences, and reduced-motion/lifecycle behavior. Findings were fixed before this aggregate run. Unicode source and bundled licenses are documented in docs/EMOJI-STICKERS.md.

## Limits

Chromium launch is blocked by socket EPERM even with an explicit temporary profile. No fresh browser geometry, keyboard/mobile visual acceptance, real WhatsApp photo fetch, native systemd/Windows/WSL installation or Pi acceptance is claimed. PowerShell is unavailable. The admin scroll cause is supported by CSS containment analysis and a failing-before structural regression, not a measured browser before/after. Historical screenshots are not new evidence. No real family data or provider calls were used in tests.

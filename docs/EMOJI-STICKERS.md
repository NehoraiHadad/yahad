# Emoji and sticker picker

The picker bundles `unicode-emoji-json` **0.9.0** locally, with 1,914 Unicode 17 base entries in nine categories. Names are searchable in English; authored Hebrew category labels and common keyword aliases provide additional discovery (not a full Hebrew translation). Native fonts render the emoji. New symbols may not render on older devices. An additional generated table exposes all 2,030 fully-qualified skin-tone variants from the official Unicode 17 emoji-test data, across 330 bases, including mixed-tone handshakes/couples. Select a tone-capable base to choose its exact variant; no modifier sequences are invented. Together the base catalogue and tone table cover 3,944 fully-qualified Unicode 17 emoji. This is not a claim to every WhatsApp feature.

The picker and data load as a bundled same-origin chunk on demand, with loading/cancel and retry UI. Results are shown 80 at a time with an explicit show-more button. Search, category selection, Escape, a visible close button, keyboard focus containment/restoration, RTL layout, and mobile sizing are provided. Recent selections and sticker favorites can be kept by the caller in conversation-lifetime React state, without storing message/media URLs in localStorage.

Sticker selection uses only already-authorized current-conversation media passed by the caller, or the existing capability-gated WebP upload route. Selecting a sticker creates an attachment preview; it never sends immediately. The helper only reads same-origin application media paths, rejects redirects, limits bytes while streaming, checks MIME and RIFF/WEBP header, and supports cancellation/timeouts. Existing server authorization and media upload validation remain authoritative. Baileys sends and receives sticker messages; we did not establish an API that grants the app access to the complete WhatsApp sticker catalogue. No WhatsApp artwork or third-party sticker assets are bundled.

## Primary sources and licenses

- Package/repository: https://github.com/muan/unicode-emoji-json (version 0.9.0; MIT; npm unpacked size 838,487 bytes).
- Unicode standard: https://www.unicode.org/versions/Unicode17.0.0/
- Unicode license: https://www.unicode.org/license.txt
- Baileys: https://github.com/WhiskeySockets/Baileys and the installed `@whiskeysockets/baileys` 7.0.0-rc14 `lib/Types/Message.d.ts` / `lib/Utils/messages.js`.
- MIT notice: [licenses/unicode-emoji-json-MIT.txt](licenses/unicode-emoji-json-MIT.txt).
- Unicode notice: [licenses/UNICODE-LICENSE-V3.txt](licenses/UNICODE-LICENSE-V3.txt).

Both license texts are included in `docs/licenses` and must accompany redistribution of the compiled picker data. Runtime usage does not request an emoji CDN, external search provider, or sticker catalogue.

Variant generation: `node scripts/generate-emoji-variants.mjs <local-emoji-test.txt>`; source https://www.unicode.org/Public/17.0.0/emoji/emoji-test.txt ; source SHA-256 `1d8a944f88d7952f7ef7c5167fef3c67995bcae24543949710231b03a201acda`. The generator refuses unmapped names; two explicit neutral-person aliases map to the established kiss/couple bases. Generated data includes Unicode provenance and uses the same bundled Unicode license.

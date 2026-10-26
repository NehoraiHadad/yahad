# Incoming stickers - 0.3.2 candidate

Real Baileys protobuf messages inherit `conversation: null`. The previous
normalizer tested only for `undefined`, classified these media messages as empty
text, and never reached sticker download/rendering. This is reproduced with the
pinned provider's protobuf classes, without a provider connection or real chats.
The normalizer now requires an actual string for plain text. The same regression
covers images, audio, video and documents affected by that null default.

Static and animated WebP stickers use the existing authenticated, grant-scoped
encrypted media store and inline WebP endpoint. Sanitized directPath-only sticker
objects use the pinned provider's supported lower-level download transport after
the existing source checks; no URL is invented. Download size, connection and
grant-epoch guards remain in force. No unapproved group is downloaded or retained.

Unsupported Lottie/non-WebP content, unavailable downloads, image decoding errors
and legacy empty messages display an explanation instead of an empty bubble.
Receiving the same authorized message again can repair missing media or the old
empty-text classification; existing usable media is deduplicated. This does not
fetch or promise restoration of old stickers by itself. Provider retention,
redelivery and the existing history-request limitations still apply.

Six synthetic regressions cover actual protobuf defaults, validated direct-path
transport, static and animated WebP byte preservation/inline MIME/encryption,
cross-family rejection, missing-media/legacy repair, duplicate suppression,
unsupported bytes, download failure and revoke/regrant during an active download.
The animated fixture is a generated 16x16 two-frame test pattern, not user content.
Real-account and Pi/Android acceptance remain pending.

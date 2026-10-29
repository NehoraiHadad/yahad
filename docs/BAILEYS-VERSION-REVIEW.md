# Baileys release and poll review (2026-10-29)

The dependency remains exactly `@whiskeysockets/baileys@7.0.0-rc14`; neither package.json nor package-lock.json was modified.

Direct official npm registry dist-tags for both `@whiskeysockets/baileys` and `baileys` returned `latest=7.0.0-rc14`, `legacy=6.7.24`. GitHub marks rc14 Latest (2026-07-29 01:14 UTC, 7e7b075); 6.7.24 is the highest non-prerelease release in the examined official list (00:57 UTC, e062994). npm latest is a distribution tag, not a semantic-version promise that a release lacks an RC suffix. A numerically stable-looking 6.x is not the maintained recommendation: official SECURITY.md supports 7.x and calls <=6.x unmaintained. Therefore no downgrade or master switch is justified merely by the word stable. No newer official released dependency was identified that was necessary for this stage.

Sources checked:

- https://registry.npmjs.org/-/package/@whiskeysockets%2Fbaileys/dist-tags
- https://registry.npmjs.org/-/package/baileys/dist-tags
- https://github.com/WhiskeySockets/Baileys/releases
- https://github.com/WhiskeySockets/Baileys/blob/master/SECURITY.md#L46-L60
- https://github.com/WhiskeySockets/Baileys/blob/v7.0.0-rc14/src/Utils/process-message.ts#L221-L242
- https://github.com/WhiskeySockets/Baileys/blob/v7.0.0-rc14/src/Utils/process-message.ts#L689-L736
- https://github.com/WhiskeySockets/Baileys/issues/2342
- https://github.com/WhiskeySockets/Baileys/pull/2870
- https://github.com/WhiskeySockets/Baileys/blob/v7.0.0-rc14/src/Utils/messages.ts
- https://github.com/WhiskeySockets/Baileys/blob/v7.0.0-rc14/WAProto/WAProto.proto

Correction to the initial assessment: the disabled automatic vote handler does not mean the library lacks basic decryption. decryptPollVote is exported and functional against synthetic encrypted fixtures. The application now uses it for bounded received-vote processing; complete results and vote sending remain separate capabilities.

Issue #2342 reports manual decryption with creator LID/voter PN in an rc.9 direct chat; it closed stale, not as proof of an upstream fix. Open PR #2870 (latest observed 3332d58) instead describes deviceless creator and voter in the same chat addressing mode, and documents additional stanza/retry markers. These observations are not a universal addressing rule. The receiver tests explicit PN/PN, LID/LID and mixed synthetic contexts and uses only actual provider-observed identities, letting authentication decide. It does not invent aliases from numeric equality.

The isolated research probe under evidence/message-support-audit/poll-helper-contract.test.ts passed five cases. It additionally demonstrates why upstream updateMessageWithPollUpdate alone is insufficient: it drops withdrawal tombstones, has no stale-event timestamp guard, does not merge PN/LID aliases and the aggregator can expose unknown hashes. The product has explicit guards for these behaviors.

Synthetic test fixtures use only official pinned crypto primitives with the published helper derivation. That is a test oracle/contract probe, not a production encryptor or proof of wire compatibility. There was no WhatsApp connection, real auth access, family-data read, dependency install or fork adoption.

Rollback: revert the local feature commits or restore the prior locked code. Keep the additive encrypted tables/triggers so prior binaries cannot expose or retain private poll state through an old-code rewrite; restoring a matching pre-feature synthetic/operational backup is a separate data-rollback choice. No dependency rollback is needed. A future official dependency change must preserve lock integrity and run auth/session, normalization, generation, media, queue, privacy and scoped API regression tests before any separately authorized device validation.

# Read-only poll display: integration handoff

Final HEAD: `0df8235015623631676793941a57bc1e7f22cd6c`. Separate poll commit: `0df8235015623631676793941a57bc1e7f22cd6c`. Parent/prerequisite: `cce99eaeb4e0b5f9c63b3fe5f40214ed4fb66c14`. Integrated base: `0bf1e9d4d71221fcb77c6aa2b2213aaf4ea0a5ae`. Branch: `fix/message-core-compatibility`.

The poll commit adds verified regular text V1/V2/V3 question/options display and an explicit notice that voting/results are unavailable. Unverified variants retain precise fallbacks. No create/vote/results implementation, dependency change, raw/key/answer/context retention, discarded-content recovery or outgoing poll operation was added.

| Verification | Result |
|---|---|
| Affected synthetic suites at final source | 105 total; 103 pass; 0 fail; 2 existing Linux-only skips |
| New poll cases included above | 10 pass |
| TypeScript | exit 0; poll-typecheck.log is empty on success |
| Local Vite build | exit 0; poll-build.log |
| Independent review | no blocker; poll-review.md |
| Whitespace / tracked / staged / conflicts | clean |
| Remaining untracked files | audit evidence only in evidence/message-support-audit/ |

Tests cover protobuf defaults, V1/V2/V3, unsupported V4/V5/quiz/image/image-option/vote/result variants, minimum projection, temporary wrappers/metadata, history and reverse arrival, edits/deletions/replay, child grants and family/conversation isolation, XSS-safe bounded React rendering, byte budgets and disabled provider actions. Rendering was checked by React SSR, not a live browser.

The optional broad Node run was stopped before reaching the socket-based `shutdown-sse.test.ts` suite, to honor this stage's no-network instruction. Its partial log is not counted as a completed suite. The affected suites use Fastify injection and a fake socket. Prior full-suite Windows mode/symlink limitations are documented in the earlier VALIDATION.md; they are not relabeled as newly passing checks.

For future integration use integration-manifest.json: it contains full ordered commit hashes, this commit's files, all message-fix files, the prerequisite head, exact test counts and unchanged pinned dependency hashes. The untracked evidence folder is not part of the commit series. No onboarding branch was merged or modified in this task.

No push, publication, installation, installer/system-permission change, network access, real WhatsApp/provider connection or family-data read occurred.

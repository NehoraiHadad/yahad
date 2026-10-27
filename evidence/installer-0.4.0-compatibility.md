# 0.4.0 installer compatibility / safety gates

Read-only review of d343087 identified a real existing-installation blocker:
0.3.4 app/request-worker activation left five historical 0.3.2 helpers/units with
CRLF bytes. The guide compared raw bytes to LF checkout files and would reject
that healthy installation again. Changes below apply only to the 0.4.0 candidate;
the published 0.3.4 release and owner integration artifact were not changed.

## Changes

- Exact historical hash pairs for five unchanged helper/unit sources from
  3624743148c453f55c6b51ffb4051986f3e79cc0. Only the current expected LF hash plus
  its known historical CRLF hash is accepted; bytes are retained, not rewritten.
  Unknown edits, different paths and genuinely older workers still fail closed.
- Entire helper set preflight before helper writes; no generic worker migration.
- Completed reruns check helper/boundary identities and enabled/active request
  path/timers when the update policy is enabled. Disabled policy stays disabled.
- Effective updater units require exact loaded fragments, reviewed drop-in sets,
  and no pending daemon reload. Runtime/global/vendor overrides cannot silently
  expand the root boundary; conflicts require owner review, not overwrite.
- Unfinished status, including preparing and rollback_pending_health, blocks setup
  instead of being overwritten with healthy status.
- Existing enabled/mode/UI-control/version-pin and service enablement preserved.
  Fresh setup still enables automatic updates. Existing setup no longer silently
  chooses auto, clears a pin, or reenables a deliberately disabled service.
- Cancellation has distinct nonzero exit status 2 so the Windows adapter cannot
  register/start a logon task after the owner cancels Linux setup.
- Windows adapter verifies active AND enabled per unit, and describes WSL2 rather
  than native Windows. It does not change network rules or certificate trust.
- Canonical LF .gitattributes plus offline packaging rejection for CRLF privileged
  deploy sources. No normalization of installed host files.

## Validation

`python -m unittest discover -s tests -p 'test_*.py'`:
116 total; 59 passed, 57 root/native capability skips (2026-10-27 cloud run).
Coverage added: mixed LF/CRLF existing layout, unknown hashes/versions/paths,
preserved notify/disabled/pin policy, disabled request path without reenable,
unknown worker, effective overridden/stale unit, unfinished status, cancellation,
and LF-preserving/CRLF-rejecting offline archive packaging.

Windows mock suite expanded to nine cases, including disabled units and canceled
Linux setup. PowerShell is unavailable in this cloud environment, so this suite
was not executed here. Existing fixture checks do not prove native systemd timing,
real WSL task registration, network routing or CA trust.

## Remaining acceptance

Disposable native Linux and Windows WSL2/Ubuntu: fresh setup; mixed existing
0.3.4 upgrade; repeated/interrupted runs; explicit disabled preferences; reboot/
logon; idle and active-worker/exit-window clicks; timer fallback; blocked queues;
tablet LAN HTTPS and CA trust. No real host install, external provider calls,
firewall/certificate changes, credentials, publication or deployment performed.

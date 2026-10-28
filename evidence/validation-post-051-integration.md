# Unreleased 0.5.2 local integration

Base: released-equivalent local acc89330e513b5a56f6a917d90af6d151eb315da,
tree9b028eb0612bb93885c7e07b7f8ddcd591b44d97. This work is not publication,
a new release, a Pi installation or a change to existing account passwords.

## Integrated scope and reviewed boundaries

- Child-device setup: three clear steps, selectable site address/name, correct
  loopback warning, selected-account password reset and separate parent navigation.
- Five-minute metadata schedule for eligible new configuration; metadata-only
  unchanged targets avoid repeated archive downloads. Scheduled worker heartbeat
  preserves usability without claiming a fresh full app check. Existing settings
  are not silently rewritten. Older immutable HTTPS launchers require a separately
  reviewed local transition; an app archive cannot install that boundary.
- Bootstrap/runtime1.0.1 has new name/pins; published1.0.0 and the0.5.0 manual
  transition are not overwritten.
- Provider-discovered private conversations remain family-scoped; parent access
  does not grant children access. Existing explicit child grants apply to either
  kind. Membership discovery reconciles departed groups and gates early live and
  historical group ingress. Stale generations cannot revoke a current catalog.
- Account-local unread state is zero for historical backfill and old databases;
  only new incoming live notify events accrue counts. Baileys append/offline/replay
  is historical, not an invented unread count. Actual focused visible rows clear
  only their exact IDs; hidden tabs/modal-covered views do not. Provider receipts
  remain independently capability-gated. Activity ordering is newest-first with
  stable ID ties.
- Four-character minimum applies to new/reset credentials; old long credentials
  remain valid and unchanged. Salted scrypt is unchanged. Durable account and
  network guessing budgets use atomic reservations, expiry and bounded storage;
  authenticated success does not erase the network budget. A targeted account
  can be temporarily locked after eight failed attempts; other account budgets
  remain separate. Local recovery clears only its own account budget.

## Integration review

Worker deltas were independently reviewed. Review caught and corrected stale
membership admission, modal-obscured unread acknowledgement, removal of existing
provider receipt behavior, metadata no-op liveness expiry, and Baileys append
classification. Root reviewed the integration-only timestamp/copy/version/legacy
schema test changes. Conflicts retained the new ChildTabletSetup component and
both independent CSS additions. No source was published.

Database schema stays2: new unread and credential throttle tables are additive.
The combined regression removes both tables from a synthetic existing schema2
DB and verifies readiness, password hashes, active sessions, grants and history
are preserved on reopen; no old unread is fabricated. Production schema1→2 smoke
uses a provider-shaped synthetic numeric identifier (not a real account).

Final automated counts and source/artifact identities are recorded in the local
integration receipt. Browser/visual acceptance of this combined candidate is
pending, as are any physical Pi, ARM/systemd or live-provider checks. Previous
0.5.1 screenshots do not establish acceptance of this new candidate.

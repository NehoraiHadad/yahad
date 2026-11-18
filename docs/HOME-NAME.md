# Home name: a publicly trusted HTTPS name for the home network

Status: design, revision 3 (after Codex design review rounds 1-2).

## Goal and non-goals
- A device on the home Wi-Fi opens Yahad at `https://<label>.<zone>` with no warning and no
  certificate step.
- Application traffic stays on the home network. The name resolves to a private RFC1918 address.
- The Cloudflare API token never reaches the home computer.
- Opt-in. The no-provider path (private CA, IP origin) stays exactly as it is, stays reachable, and
  remains the fallback. Every failure of the name path leaves the IP path working.
- Not in scope: Linux/Pi parity, reaching Yahad from outside the home, WebAuthn.

## Trust model (stated honestly)
Whoever controls the zone's DNS (the maintainer's Cloudflare account, or the name service Worker that
holds its token) can point any label at a server of their choosing, obtain a publicly trusted
certificate for it, and present a fake Yahad login page to a device that opens the name. They do not
need to be on the family's network. The IP path does not have this dependency. Mitigations: the token
lives only as a Worker secret; the Cloudflare account uses strong 2FA; a CAA record on the zone name (not the apex) allows only
Let's Encrypt; the name is opt-in and the parent is told what it depends on. Families other than the
maintainer would have to trust the maintainer for this; that is a decision for later, with a dedicated
domain on the Public Suffix List.

## Configuration
Build-time, in `installer/Directory.Build.props`: `YahadHomeNameService` (HTTPS URL of the Worker) and
`YahadHomeNameZone` (e.g. `home.nehoraihadad.com`). Empty: the option is not offered. Both are
infrastructure inputs.

## 1. Name service (Cloudflare Worker, `name-service/`, its own package.json)
Small and DNS-only: it never runs ACME, never sees a CSR, key or certificate.

- One Durable Object per label (SQLite storage): `secretSha256`, `address`, `challenge[]`,
  `lastSeen`, `deleted` (tombstone). A Durable Object executes one request at a time, so every change to a
  label is serialized, including against deletion.
- One `Budget` Durable Object: atomic global counters and caps (total live labels, registrations per
  day, Cloudflare API writes per day). Each change reserves budget before calling Cloudflare.
- Workers rate-limiting binding per client IP on `register`.
- Auth: `Authorization: Bearer <label>.<secret>`; the secret is 256-bit, compared by SHA-256 in
  constant time; never logged.

Endpoints (JSON):
- `POST /v1/register` → `{label, secret}`. Label: 12 chars base32 from the CSPRNG.
- `PUT /v1/address` `{ipv4}` → the label's A record (DNS-only). Only RFC1918 IPv4.
- `PUT /v1/challenge` `{values: [..]}` → `_acme-challenge.<label>` TXT, 1-2 base64url values of
  43 chars (dns-01 digests); replaces earlier values.
- `DELETE /v1/challenge` → removes the label's TXT.
- `DELETE /v1/name` → removes A and TXT, leaves a tombstone. Idempotent.
A credential whose label is tombstoned or expired gets `410 Gone`; the home then discards it and
registers again (a new name: devices sign in once more).
New labels are capped at 5 per day globally, which also bounds new Let's Encrypt certificates to 35 per
week, under the 50-per-week registered-domain limit; renewals of an existing name are exempt from that
limit, so existing homes always keep capacity. Per label: at most 10 address writes and 20 challenge writes per day. Every authenticated call updates
`lastSeen`. A daily Cron Trigger removes labels unseen for 150 days (renewal touches them every few
weeks), so a computer that was uninstalled offline does not leave records forever.

## 2. ACME client on the home computer (C#, `Yahad.Install.Core`, no new dependency)
Minimal RFC 8555 client: directory, nonce, account (ES256, key kept on disk), order for exactly one dns
identifier, dns-01 via the name service, finalize with a CSR from a new P-256 key, download the chain.
After issuance it checks: the leaf's only SAN is the name, its public key is ours, it chains to a
publicly trusted root with system trust, and its validity is current. The private key never leaves the
computer. Renewal is due at 2/3 of the current certificate's own lifetime (LE lifetimes are shrinking:
64 days from 2027-02, 45 from 2028-02), then retried with backoff. Tested against Pebble.

## 3. Windows installer
Files (new; no field is added to any existing record, because strict NativeJson in an older release
would reject it on rollback):
- `InstallLocations.State/home-name.json` (Setup-written, read by the Runtime host):
  `{Format:1, Name, Address}`. Its presence is the commit point of the whole name path.
- `LanProxyPaths.State/home-name-owner.json` (Setup-owned, install ACL):
  `{Format:1, Phase: registered|active|failed|removed, Label, Name, Failure?}`.
- `LanProxyPaths.State/name/` — the only subtree the LAN service may write (data ACL):
  `credential.json` (label, secret; written by Setup), `account.json` (ACME account key),
  `live/{cert.pem,key.pem}`, `next/` for renewal staging.

Name lifecycle: LAN off (the owner turns phone access off) deletes the name at the Worker
(best effort) and discards the local credential. Uninstall with keep-data does not touch the name or the
credential: `UninstallAsync` passes "keep the name" to the disable path. Uninstall with delete-data
deletes the name (best effort, short timeout).

Setup (option „כתובת קבועה בלי תעודה במכשירים”, shown only when configured):
1. The IP path is configured and committed exactly as today (phase `enabled`).
2. Name transaction: register (credential persisted before any other call; reuse it if present),
   set A, issue the certificate into `name/live`, write the Caddyfile with the name site, restart the
   proxy, check that the system resolver returns the selected address for the name (DNS-rebind
   protection on the router shows up here) and that `https://<name>/` answers with system trust, then
   write `home-name.json` and restart the Runtime service.
   Each step first persists the owner phase (`registered` → `issued` → `configured`) and is
   idempotent, so a crash resumes. Ordering makes every intermediate state safe: the Caddyfile may carry
   the name site before `home-name.json` exists (the app still runs on the IP origin and rejects the
   name's origin), never the reverse. On a rerun with a phase other than `active`, Setup first restores
   the IP-only Caddyfile and removes `home-name.json`, then resumes. A credential with no owner phase after
   it (a crash right after registering) is also unfinished, so the label is reused.
   An `active` name is checked on every rerun, not trusted: its credential and a valid live certificate are
   present and the name service still knows the label (setting the unchanged address is a no-op there that
   refreshes it). A 410 discards the credential and certificates and the transaction runs again with a new
   label; an expired certificate runs it again too; an unreachable service keeps the name. A redo first
   uncommits the name (home-name.json, then the IP-only Caddyfile) and only then discards anything it
   served from. If it fails, the Runtime is restarted onto the IP origin, even when the proxy restart fails.
   Turning the option off always runs the removal, so a credential with no owner phase is removed too.
3. Any failure: the Caddyfile is written without the name site, `home-name.json` does not exist, the
   owner record says `failed` with a code, the proxy is restarted on the IP site. The IP path stays
   committed. Running Setup again resumes.

Caddyfile: `LanDeployment.Create` takes an optional name; the name site binds the same address, uses
`tls <live/cert.pem> <live/key.pem>`, and has the same route as the IP site (the `/health` and
`/api/setup` 404 guard included).

Runtime host (`InstalledLaunchResolver`): the name is used only when all hold: the origin is private-IP
HTTPS, `home-name.json` is valid and its `Address` equals the origin host, and the active app tree's
`deploy/lan-capabilities.json` declares `"homeName": true` (an app-only rollback to an app without it
runs on the IP origin, exactly as today). Then: `APP_ORIGIN=https://<name>`,
`APP_ADDITIONAL_ORIGINS=https://<ip>,http://127.0.0.1:<port>`, and a new `APP_HOME_ADDRESS=https://<ip>`
which the app uses (instead of `APP_ORIGIN`) to validate the join page and fingerprint
(`parseHomeNetwork`). An older host ignores the file and runs on the IP origin.

LAN service renewal: the controller checks daily. When due: ACME into `name/next` (both files written and
flushed), validate, then a planned replacement: stop the Caddy child with the existing confirmed-stop
path, write `name/swap.json`, rename `live`→`prev`, rename `next`→`live`, delete `swap.json`, start the
child, verify the name serves the new leaf; on failure (the child does not start, or does not serve the
new leaf) swap back and start again. A swap that fails at `next`→`live` puts `prev` back before it throws. Before every Caddy start the service recovers an interrupted swap: with `swap.json`
present, a missing `live` is restored from `next` if complete and movable, otherwise from `prev`; then the journal
is removed (best effort: a journal that cannot be removed while `live` exists is harmless). The address check and STOP keep their current precedence. Calls go out from the
service identity with no proxy, like Setup's probes.

Upgrade and rollback: `WriteProxyConfig` includes the name site when the owner phase is `active`. A
rollback to an older Setup rewrites the Caddyfile without it, and the older host runs on the IP origin:
consistent. Moving forward again restores the name site.

LAN off: `DELETE /v1/name` (best effort), remove `home-name.json`, owner phase `removed`.
Uninstall keep-data: everything stays; a reinstall reuses the label. Delete-data: best-effort
`DELETE /v1/name` with a short timeout, then the tree goes; if offline, the Worker's expiry cleans up.

## 4. Web app
`siteOrigin` becomes the name. The home-address card shows the name. If the home-network join env is
present it also says: if the address does not open on a device (some routers block it, or a guest
network), use the home-network join page — the existing steps.
Cookies are host-only: each device signs in once on the new address.

## Verified constraints (official docs, 2026-11-18)
- LE: 50 new certificates per registered domain per 7 days (shared with everything on
  `nehoraihadad.com`); non-ARI renewals limited to 5 duplicates per 7 days; failed validations 5 per
  identifier per hour.
- Workers Free: 10 ms CPU per request (waiting does not count), 50 subrequests, 100k requests/day.
- Cloudflare API tokens scope to whole zones, not records.
- Free zone record cap: 200 (zones created after 2024-09-01) or 1000.
- Caddy does not watch certificate files.
- DNS-rebind protection is on by default in FRITZ!Box and pfSense; optional elsewhere.

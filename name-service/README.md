# Yahad name service

A Cloudflare Worker that gives each opted-in home a DNS name `<label>.<ZONE_NAME>` pointing at its
private (RFC 1918) address, and publishes the `_acme-challenge` TXT values the home computer needs
for a Let's Encrypt dns-01 certificate. It is DNS-only: it never runs ACME and never sees a CSR, a
key or a certificate. Design: `docs/HOME-NAME.md`, section "1. Name service".

## API

`GET /` (and `HEAD /`) serves a short Hebrew page explaining the service, linking to Yahad's landing
page and its developer (the logo is the landing page's published asset). Everything else is the API.

All bodies are JSON. Errors are `{"error": "<code>"}`. Authenticated calls send
`Authorization: Bearer <label>.<secret>`.

| Call | Body | Success |
| --- | --- | --- |
| `POST /v1/register` | none (a random label), or `{"label": "hadad-home"}` (that label) | `201 {label, secret}` |
| `PUT /v1/address` | `{"ipv4": "192.168.1.20"}` | `204` (A record, DNS-only, TTL 60) |
| `PUT /v1/challenge` | `{"values": ["<43 chars>", ...]}` (1-2) | `204` (replaces earlier values) |
| `DELETE /v1/challenge` | none | `204` |
| `DELETE /v1/name` | none | `204` (idempotent; leaves a tombstone) |

Error codes: `400 bad_request | invalid_label | invalid_address | invalid_challenge`, `401 unauthorized`,
`404 not_found`, `405 method_not_allowed`, `409 label_taken`, `410 gone` (tombstoned or expired: register again),
`429 rate_limited | daily_limit | label_daily_limit` (with `Retry-After`),
`502 upstream_error`, `503 capacity | budget_exhausted | not_configured`.

Labels, random or chosen: 3-20 characters of `[a-z0-9-]`, a letter or digit at both ends, no `--`
anywhere (so never an `xn--` punycode label), and not reserved (`www`, `api`, `mail`, `admin`, ... the
list is `RESERVED_LABELS` in `src/format.ts`). Uppercase is refused, not folded. A random label is 12
base32 characters. `test/label-cases.json` is the rule's table; the installer's tests read the same file.
A register body other than none or exactly `{"label": "<string>"}` (JSON, at most 1 KiB) is
`400 bad_request`; a label that breaks the rule is `400 invalid_label`. A chosen label the service holds
in any status (live, being cleaned up, deleted or expired) is `409 label_taken`: a label is never handed
out twice, so a name stays with the home that first had it, and certificates issued for it can never
serve another home. Chosen labels share the register rate limit, the daily limit and the live-label cap
with random ones.

Every certificate name is public: Let's Encrypt logs each issued certificate in Certificate
Transparency logs, which anyone can search. A chosen label (`hadad-home`) is therefore public and says
something about the home; a random label reveals nothing beyond the fact that it exists.

Limits: 5 new labels per UTC day, `LIVE_LABELS_CAP` live labels, `CF_WRITES_PER_DAY` Cloudflare
writes per UTC day, and per label 10 address writes and 20 challenge writes per UTC day. Labels unseen
for 150 days are removed by the daily cron.

## Develop

```sh
npm ci
npm test            # vitest in the Workers runtime; Cloudflare's API is faked
npx tsc --noEmit
```

## Deploy (maintainer)

1. **API token.** Cloudflare dashboard → My Profile → API Tokens → Create Token → Custom token.
   Permissions: `Zone` / `DNS` / `Edit`. Zone Resources: `Include` / `Specific zone` / the one zone that
   holds `ZONE_NAME`. Nothing else. Copy the token once; it is never written to a file.
2. **Log in** (in this directory): `npx wrangler login`.
3. **Store the token as a secret:** `npx wrangler secret put CF_API_TOKEN` and paste it at the prompt.
4. **Set the zone.** In `wrangler.jsonc` set `vars.ZONE_NAME` (the suffix, e.g.
   `yahad.nehoraihadad.com`) and `vars.ZONE_ID` (dashboard → the zone → Overview → API → Zone ID).
   Until both are set, every call answers `503 not_configured`.
5. **Deploy:** `npx wrangler deploy`. This creates the two Durable Object classes (SQLite storage),
   the daily cron and the register rate limiter.
6. **Custom domain.** Dashboard → Workers & Pages → `yahad-name-service` → Settings → Domains &
   Routes → Add → Custom domain, `yahad-names.nehoraihadad.com` (outside `ZONE_NAME`, so the CAA below does not apply to it). The Worker has no `workers.dev` URL.
   Its HTTPS URL is the installer's `YahadHomeNameService`; `ZONE_NAME` is `YahadHomeNameZone`.
7. **CAA.** In the zone's DNS add, on the `ZONE_NAME` host (e.g. `yahad`):
   - `CAA 0 issue "letsencrypt.org"`
   - `CAA 0 issuewild ";"`

   CAA lookups climb from `<label>.<ZONE_NAME>` to the nearest record, so every home name may be
   issued only by Let's Encrypt and never as a wildcard. Putting the record on `ZONE_NAME` rather than
   on the zone apex leaves the certificates Cloudflare issues for the rest of the domain (including the
   Worker's custom domain) unaffected.

Check after deploying: `curl -i -X POST https://names.example/v1/register` returns `201` with a
label and secret; `DELETE /v1/name` with that credential returns `204`.

Do not log or paste the token or any secret; the Worker never logs either.

import { createScheduledController, reset, runInDurableObject } from "cloudflare:test";
import { env } from "cloudflare:workers";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BUDGET_NAME } from "../src/budget";
import { EXPIRY_MS } from "../src/env";
import { sha256Hex } from "../src/format";
import worker from "../src/index";
import { FakeCloudflare, TOKEN } from "./fake-cloudflare";

const ZONE = "home.example.com";
const D1 = "ungWv48Bz-pBQUDeXa4iI7ADYaOWF3qctBD_YfIAFa0";
const D2 = "LPJNul-wow4m6DsqxbninhsWHlwfp0JecwQzYpOLmCQ";
const D3 = "47DEQpj8HBSa-_TImW-5JCeuQeRkm5NMpJWZG3hSuFU";
const DAY = 24 * 60 * 60 * 1000;

let fake: FakeCloudflare;
let ipCounter = 0;

beforeEach(() => {
  fake = new FakeCloudflare();
  fake.install();
});

afterEach(async () => {
  vi.restoreAllMocks();
  await reset();
});

function call(method: string, path: string, init: { auth?: string; body?: unknown; ip?: string } = {}) {
  const headers: Record<string, string> = { "CF-Connecting-IP": init.ip ?? `198.51.100.${++ipCounter % 250}` };
  if (init.auth !== undefined) headers.Authorization = init.auth;
  if (init.body !== undefined) headers["Content-Type"] = "application/json";
  const request = new Request(`https://names.example.com${path}`, {
    method,
    headers,
    body: init.body === undefined ? undefined : JSON.stringify(init.body),
  });
  return worker.fetch(request, env);
}

async function register(): Promise<{ label: string; secret: string; auth: string }> {
  const response = await call("POST", "/v1/register");
  expect(response.status).toBe(201);
  const body = (await response.json()) as { label: string; secret: string };
  return { ...body, auth: `Bearer ${body.label}.${body.secret}` };
}

function budget() {
  return env.BUDGET.get(env.BUDGET.idFromName(BUDGET_NAME));
}

function labelStub(label: string) {
  return env.LABELS.get(env.LABELS.idFromName(label));
}

async function errorCode(response: Response): Promise<string> {
  return ((await response.json()) as { error: string }).error;
}

describe("register", () => {
  it("returns a label and a 256-bit secret, stores only the SHA-256", async () => {
    const { label, secret } = await register();
    expect(label).toMatch(/^[a-z2-7]{12}$/);
    expect(secret).toMatch(/^[A-Za-z0-9_-]{43}$/);
    const state = await labelStub(label).inspect();
    expect(state?.secretSha256).toBe(await sha256Hex(secret));
    expect(JSON.stringify(state)).not.toContain(secret);
    expect((await budget().describe(label))?.status).toBe("live");
    expect(fake.calls).toHaveLength(0);
  });

  it("caps new labels at 5 per UTC day", async () => {
    for (let i = 0; i < 5; i++) await register();
    const response = await call("POST", "/v1/register");
    expect(response.status).toBe(429);
    expect(await errorCode(response)).toBe("daily_limit");
    expect(Number(response.headers.get("Retry-After"))).toBeGreaterThan(0);
  });

  it("caps live labels", async () => {
    const now = Date.now();
    // Fill the 60-label cap over earlier days (5 per day).
    for (let i = 0; i < 60; i++) {
      const outcome = await budget().reserveRegistration(`filler${String(i).padStart(6, "a")}`, now - (31 + Math.floor(i / 5)) * DAY, 60);
      expect(outcome).toBe("reserved");
    }
    const response = await call("POST", "/v1/register");
    expect(response.status).toBe(503);
    expect(await errorCode(response)).toBe("capacity");
  });

  it("rate-limits registrations per client IP", async () => {
    const statuses: number[] = [];
    for (let i = 0; i < 4; i++) statuses.push((await call("POST", "/v1/register", { ip: "203.0.113.7" })).status);
    expect(statuses.slice(0, 3)).toEqual([201, 201, 201]);
    expect(statuses[3]).toBe(429);
  });

  it("never hands out a label twice", async () => {
    expect(await budget().reserveRegistration("aaaaaaaaaaaa", Date.now(), 150)).toBe("reserved");
    expect(await budget().reserveRegistration("aaaaaaaaaaaa", Date.now(), 150)).toBe("collision");
  });
});

describe("auth", () => {
  it("rejects missing, malformed and wrong credentials with 401", async () => {
    const { label, secret } = await register();
    const wrong = secret.slice(0, 42) + (secret[42] === "A" ? "E" : "A");
    for (const auth of [undefined, "", `Bearer ${label}`, `Bearer ${label}.${wrong}`, `Bearer bbbbbbbbbbbb.${secret}`, `Bearer ${label}.${secret}x`]) {
      const response = await call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.10" } });
      expect(response.status, String(auth)).toBe(401);
      expect(await errorCode(response)).toBe("unauthorized");
    }
    expect(fake.calls).toHaveLength(0);
  });

  it("updates lastSeen on every authenticated call", async () => {
    const { label, auth } = await register();
    await labelStub(label).inspect();
    const before = (await labelStub(label).inspect())!.lastSeen;
    await new Promise((resolve) => setTimeout(resolve, 5));
    expect((await call("DELETE", "/v1/challenge", { auth })).status).toBe(204);
    expect((await labelStub(label).inspect())!.lastSeen).toBeGreaterThan(before);
  });
});

describe("address", () => {
  it("creates a DNS-only A record with TTL 60, then updates it in place", async () => {
    const { label, auth } = await register();
    expect((await call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.10" } })).status).toBe(204);
    let records = fake.find("A", `${label}.${ZONE}`);
    expect(records).toHaveLength(1);
    expect(records[0]).toMatchObject({ content: "192.168.1.10", ttl: 60, proxied: false });
    const id = records[0].id;

    expect((await call("PUT", "/v1/address", { auth, body: { ipv4: "10.0.0.5" } })).status).toBe(204);
    records = fake.find("A", `${label}.${ZONE}`);
    expect(records).toEqual([expect.objectContaining({ id, content: "10.0.0.5" })]);
    expect(fake.writes().map((c) => c.method)).toEqual(["POST", "PATCH"]);
    expect(fake.calls.every((c) => c.authorization === `Bearer ${TOKEN}`)).toBe(true);
    expect(fake.calls[0].path).toContain("/zones/0123456789abcdef0123456789abcdef/dns_records");
  });

  it("a deletion racing address writes never leaves a record behind", async () => {
    const { label, auth } = await register();
    const results = await Promise.all([
      call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.10" } }),
      call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.11" } }),
      call("DELETE", "/v1/name", { auth }),
    ]);
    // Arrival order is not fixed: a write that runs after the deletion gets 410.
    expect([204, 410]).toContain(results[0].status);
    expect([204, 410]).toContain(results[1].status);
    expect(results[2].status).toBe(204);
    expect(fake.records.size).toBe(0);
    expect((await budget().describe(label))?.status).toBe("gone");
  });

  it("two concurrent first writes create a single record", async () => {
    const { label, auth } = await register();
    await Promise.all([
      call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.10" } }),
      call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.11" } }),
    ]);
    expect(fake.find("A", `${label}.${ZONE}`)).toHaveLength(1);
  });

  it("does not write when the address is unchanged", async () => {
    const { auth } = await register();
    await call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.10" } });
    const before = fake.writes().length;
    expect((await call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.10" } })).status).toBe(204);
    expect(fake.writes().length).toBe(before);
  });

  it("accepts only RFC 1918 IPv4 in an exact body", async () => {
    const { auth } = await register();
    for (const ipv4 of ["172.15.0.1", "172.32.0.1", "192.168.01.1", "10.0.0.1.1", "8.8.8.8", "127.0.0.1", 167772161]) {
      const response = await call("PUT", "/v1/address", { auth, body: { ipv4 } });
      expect(response.status, String(ipv4)).toBe(400);
      expect(await errorCode(response)).toBe("invalid_address");
    }
    for (const body of [{}, { ipv4: "10.0.0.1", extra: 1 }, ["10.0.0.1"], "10.0.0.1"]) {
      expect((await call("PUT", "/v1/address", { auth, body })).status).toBe(400);
    }
    expect(fake.calls).toHaveLength(0);
  });

  it("adopts an existing record when the stored id is unknown", async () => {
    const { label, auth } = await register();
    const id = fake.seed({ type: "A", name: `${label}.${ZONE}`, content: "192.168.1.9", ttl: 60, proxied: false });
    expect((await call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.10" } })).status).toBe(204);
    expect(fake.find("A", `${label}.${ZONE}`)).toEqual([expect.objectContaining({ id, content: "192.168.1.10" })]);
    expect(fake.writes().map((c) => c.method)).toEqual(["PATCH"]);
  });

  it("recreates the record when it was removed at Cloudflare", async () => {
    const { label, auth } = await register();
    await call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.10" } });
    fake.records.clear();
    expect((await call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.11" } })).status).toBe(204);
    expect(fake.find("A", `${label}.${ZONE}`)).toEqual([expect.objectContaining({ content: "192.168.1.11" })]);
    expect((await labelStub(label).inspect())!.aRecordId).toBe(fake.find("A", `${label}.${ZONE}`)[0].id);
  });

  it("caps address writes at 10 per label per day", async () => {
    const { auth } = await register();
    for (let i = 0; i < 10; i++) {
      expect((await call("PUT", "/v1/address", { auth, body: { ipv4: `192.168.1.${i + 1}` } })).status).toBe(204);
    }
    const response = await call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.200" } });
    expect(response.status).toBe(429);
    expect(await errorCode(response)).toBe("label_daily_limit");
  });

  it("refuses when the global Cloudflare write budget is spent, before calling Cloudflare", async () => {
    const { auth } = await register();
    expect(await budget().reserveWrites(500, Date.now(), 500)).toBe(true);
    const response = await call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.10" } });
    expect(response.status).toBe(503);
    expect(await errorCode(response)).toBe("budget_exhausted");
    expect(fake.writes()).toHaveLength(0);
  });

  it("reports a Cloudflare failure as a short code", async () => {
    const { auth } = await register();
    fake.failNext("POST", 500);
    const response = await call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.10" } });
    expect(response.status).toBe(502);
    expect(await response.text()).toBe('{"error":"upstream_error"}');
  });

  it("counts every Cloudflare write against the daily budget", async () => {
    const { auth } = await register();
    await call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.10" } });
    await call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.11" } });
    expect(await budget().usedToday("cf_write", Date.now())).toBe(2);
  });
});

describe("challenge", () => {
  const txtName = (label: string) => `_acme-challenge.${label}.${ZONE}`;

  it("writes quoted TXT values and replaces earlier ones", async () => {
    const { label, auth } = await register();
    expect((await call("PUT", "/v1/challenge", { auth, body: { values: [D1, D2] } })).status).toBe(204);
    expect(fake.find("TXT", txtName(label)).map((r) => r.content).sort()).toEqual([`"${D1}"`, `"${D2}"`].sort());
    expect(fake.find("TXT", txtName(label)).every((r) => r.ttl === 60 && r.proxied === false)).toBe(true);

    expect((await call("PUT", "/v1/challenge", { auth, body: { values: [D3] } })).status).toBe(204);
    expect(fake.find("TXT", txtName(label)).map((r) => r.content)).toEqual([`"${D3}"`]);
    // D3 replaces both: one record is updated in place, the other deleted.
    expect(fake.writes().map((c) => c.method)).toEqual(["POST", "POST", "PATCH", "DELETE"]);
  });

  it("deletes the TXT records", async () => {
    const { label, auth } = await register();
    await call("PUT", "/v1/challenge", { auth, body: { values: [D1, D2] } });
    expect((await call("DELETE", "/v1/challenge", { auth })).status).toBe(204);
    expect(fake.find("TXT", txtName(label))).toHaveLength(0);
    expect((await labelStub(label).inspect())!.txtRecords).toEqual([]);
  });

  it("validates values", async () => {
    const { auth } = await register();
    for (const values of [[], [D1, D2, D3], [D1, D1], [D1.slice(1)], [`${D1}=`], [`"${D1}"`], D1]) {
      const response = await call("PUT", "/v1/challenge", { auth, body: { values } });
      expect(response.status, JSON.stringify(values)).toBe(400);
      expect(await errorCode(response)).toBe("invalid_challenge");
    }
    expect(fake.calls).toHaveLength(0);
  });

  it("caps challenge writes at 20 per label per day", async () => {
    const { auth } = await register();
    for (let i = 0; i < 10; i++) {
      expect((await call("PUT", "/v1/challenge", { auth, body: { values: [D1] } })).status).toBe(204);
      expect((await call("DELETE", "/v1/challenge", { auth })).status).toBe(204);
    }
    const response = await call("PUT", "/v1/challenge", { auth, body: { values: [D2] } });
    expect(response.status).toBe(429);
    expect(await errorCode(response)).toBe("label_daily_limit");
  });

  it("adopts a TXT record whose create was not recorded", async () => {
    const { label, auth } = await register();
    fake.seed({ type: "TXT", name: txtName(label), content: `"${D1}"`, ttl: 60, proxied: false });
    expect((await call("PUT", "/v1/challenge", { auth, body: { values: [D1] } })).status).toBe(204);
    expect(fake.find("TXT", txtName(label))).toHaveLength(1);
    expect(fake.writes()).toHaveLength(0);
  });
});

describe("delete and tombstone", () => {
  it("removes A and TXT, leaves a tombstone, is idempotent, and later calls get 410", async () => {
    const { label, auth } = await register();
    await call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.10" } });
    await call("PUT", "/v1/challenge", { auth, body: { values: [D1] } });
    expect((await call("DELETE", "/v1/name", { auth })).status).toBe(204);
    expect(fake.records.size).toBe(0);
    expect((await labelStub(label).inspect())!.deleted).toBe(true);
    expect((await budget().describe(label))?.status).toBe("gone");

    expect((await call("DELETE", "/v1/name", { auth })).status).toBe(204);
    for (const [method, path, body] of [
      ["PUT", "/v1/address", { ipv4: "192.168.1.10" }],
      ["PUT", "/v1/challenge", { values: [D1] }],
      ["DELETE", "/v1/challenge", undefined],
    ] as const) {
      const response = await call(method, path, { auth, body });
      expect(response.status).toBe(410);
      expect(await errorCode(response)).toBe("gone");
    }
  });

  it("a tombstone does not answer a wrong secret with 410", async () => {
    const { label, auth } = await register();
    await call("DELETE", "/v1/name", { auth });
    const other = `Bearer ${label}.${D1}`;
    expect((await call("PUT", "/v1/address", { auth: other, body: { ipv4: "10.0.0.1" } })).status).toBe(401);
  });

  it("keeps the tombstone in cleanup when Cloudflare fails, and the cron finishes it", async () => {
    const { label, auth } = await register();
    await call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.10" } });
    fake.failNext("DELETE", 500);
    expect((await call("DELETE", "/v1/name", { auth })).status).toBe(204);
    expect(fake.records.size).toBe(1);
    expect((await budget().describe(label))?.status).toBe("cleanup");
    expect((await call("PUT", "/v1/address", { auth, body: { ipv4: "10.0.0.1" } })).status).toBe(410);

    await worker.scheduled(createScheduledController({ scheduledTime: Date.now() }), env);
    expect(fake.records.size).toBe(0);
    expect((await budget().describe(label))?.status).toBe("gone");
  });

  it("a deleted label no longer counts against the live cap", async () => {
    const { auth } = await register();
    await call("DELETE", "/v1/name", { auth });
    const now = Date.now();
    for (let i = 0; i < 59; i++) {
      await budget().reserveRegistration(`filler${String(i).padStart(6, "a")}`, now - (31 + Math.floor(i / 5)) * DAY, 60);
    }
    expect((await call("POST", "/v1/register")).status).toBe(201);
  });
});

describe("expiry", () => {
  it("a credential unseen for more than 150 days gets 410 and its records are removed", async () => {
    const { label, secret, auth } = await register();
    await call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.10" } });
    const later = Date.now() + EXPIRY_MS + DAY;
    const outcome = await labelStub(label).setAddress(await sha256Hex(secret), later, "192.168.1.11");
    expect(outcome).toEqual({ status: 410, code: "gone" });
    expect(fake.records.size).toBe(0);
    expect((await budget().describe(label))?.status).toBe("gone");
  });

  it("the daily cron removes stale labels and leaves fresh ones", async () => {
    const stale = await register();
    await call("PUT", "/v1/address", { auth: stale.auth, body: { ipv4: "192.168.1.10" } });
    const fresh = await register();
    await call("PUT", "/v1/address", { auth: fresh.auth, body: { ipv4: "192.168.1.11" } });

    // The fresh label is seen again 100 days later; the stale one never is.
    const day100 = Date.now() + 100 * DAY;
    expect((await labelStub(fresh.label).clearChallenge(await sha256Hex(fresh.secret), day100)).status).toBe(204);

    const day160 = Date.now() + 160 * DAY;
    await worker.scheduled(createScheduledController({ scheduledTime: day160 }), env);

    expect(fake.find("A", `${stale.label}.${ZONE}`)).toHaveLength(0);
    expect(fake.find("A", `${fresh.label}.${ZONE}`)).toHaveLength(1);
    expect((await labelStub(stale.label).inspect())!.deleted).toBe(true);
    expect((await budget().describe(stale.label))?.status).toBe("gone");
    expect((await budget().describe(fresh.label))?.status).toBe("live");
    expect((await call("PUT", "/v1/address", { auth: stale.auth, body: { ipv4: "10.0.0.1" } })).status).toBe(410);
  });

  it("the cron corrects a lagging index entry instead of expiring a label that was seen", async () => {
    const { label, secret, auth } = await register();
    await call("PUT", "/v1/address", { auth, body: { ipv4: "192.168.1.10" } });
    const seen = Date.now() + 100 * DAY;
    await labelStub(label).clearChallenge(await sha256Hex(secret), seen);
    // Simulate a lost report: the index still holds the registration time.
    await runInDurableObject(budget(), (_instance, state) => {
      state.storage.sql.exec("UPDATE labels SET last_seen = 0 WHERE label = ?", label);
    });
    expect((await budget().describe(label))?.lastSeen).toBe(0);

    await worker.scheduled(createScheduledController({ scheduledTime: Date.now() + 160 * DAY }), env);
    expect((await budget().describe(label))).toEqual({ status: "live", lastSeen: seen });
    expect(fake.find("A", `${label}.${ZONE}`)).toHaveLength(1);
  });

  it("processes a bounded batch per run", async () => {
    const now = Date.now();
    for (let i = 0; i < 45; i++) {
      await budget().reserveRegistration(`stale${String(i).padStart(7, "a")}`, now - (200 + Math.floor(i / 5)) * DAY, 150);
    }
    expect(await budget().dueForExpiry(now - EXPIRY_MS, 1000, now)).toHaveLength(45);
    await worker.scheduled(createScheduledController({ scheduledTime: now }), env);
    expect(await budget().dueForExpiry(now - EXPIRY_MS, 1000, now)).toHaveLength(5);
    await worker.scheduled(createScheduledController({ scheduledTime: now }), env);
    expect(await budget().dueForExpiry(now - EXPIRY_MS, 1000, now)).toHaveLength(0);
  });
});

describe("routing and configuration", () => {
  it("answers unknown paths and methods with short codes", async () => {
    expect((await call("GET", "/")).status).toBe(404);
    expect((await call("GET", "/v1/register")).status).toBe(405);
    expect((await call("POST", "/v1/name")).status).toBe(405);
  });

  it("answers 503 not_configured until the zone is set", async () => {
    const response = await worker.fetch(
      new Request("https://names.example.com/v1/register", { method: "POST" }),
      { ...env, ZONE_ID: "" },
    );
    expect(response.status).toBe(503);
    expect(await errorCode(response)).toBe("not_configured");
  });
});

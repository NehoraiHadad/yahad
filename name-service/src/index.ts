// Yahad name service: DNS-only. It never runs ACME and never sees a CSR, key or certificate.
// Spec: docs/HOME-NAME.md, "1. Name service".

import { Budget, BUDGET_NAME } from "./budget";
import { EXPIRY_BATCH, EXPIRY_MS, readSettings, type Env } from "./env";
import {
  isChallengeValues,
  isRfc1918,
  newLabel,
  newSecret,
  parseBearer,
  secondsUntilNextUtcDay,
  sha256Hex,
} from "./format";
import { LabelObject, type Outcome } from "./label";

export { Budget, LabelObject };

const MAX_BODY_BYTES = 1024;
const REGISTER_ATTEMPTS = 3;

function json(status: number, body: unknown, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", "Cache-Control": "no-store", ...headers },
  });
}

function failure(status: number, code: string, retryAfter?: number): Response {
  return json(status, { error: code }, retryAfter === undefined ? {} : { "Retry-After": String(retryAfter) });
}

function fromOutcome(outcome: Outcome): Response {
  if (outcome.status === 204) return new Response(null, { status: 204, headers: { "Cache-Control": "no-store" } });
  return failure(outcome.status, outcome.code ?? "error", outcome.retryAfter);
}

/** Reads a JSON object whose keys are exactly `keys`. Null for anything else. */
async function readBody(request: Request, keys: string[]): Promise<Record<string, unknown> | null> {
  const type = request.headers.get("Content-Type") ?? "";
  if (!/^application\/json(\s*;.*)?$/i.test(type)) return null;
  const text = await request.text();
  if (new TextEncoder().encode(text).length > MAX_BODY_BYTES) return null;
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    return null;
  }
  if (value === null || typeof value !== "object" || Array.isArray(value)) return null;
  const actual = Object.keys(value).sort();
  const expected = [...keys].sort();
  if (actual.length !== expected.length || actual.some((key, i) => key !== expected[i])) return null;
  return value as Record<string, unknown>;
}

function budget(env: Env) {
  return env.BUDGET.get(env.BUDGET.idFromName(BUDGET_NAME));
}

function labelObject(env: Env, label: string) {
  return env.LABELS.get(env.LABELS.idFromName(label));
}

async function register(request: Request, env: Env, liveLabelsCap: number, now: number): Promise<Response> {
  if (env.REGISTER_LIMITER !== undefined) {
    const ip = request.headers.get("CF-Connecting-IP") ?? "unknown";
    const { success } = await env.REGISTER_LIMITER.limit({ key: `register:${ip}` });
    if (!success) return failure(429, "rate_limited", 60);
  }
  for (let attempt = 0; attempt < REGISTER_ATTEMPTS; attempt++) {
    const label = newLabel();
    const admitted = await budget(env).reserveRegistration(label, now, liveLabelsCap);
    if (admitted === "collision") continue;
    if (admitted === "capacity") return failure(503, "capacity");
    if (admitted === "daily_limit") return failure(429, "daily_limit", secondsUntilNextUtcDay(now));
    const secret = newSecret();
    let stored: "created" | "exists";
    try {
      stored = await labelObject(env, label).init(label, await sha256Hex(secret), now);
    } catch (error) {
      // Nothing was created at Cloudflare; release the label so it does not hold capacity.
      await budget(env).setStatus(label, "gone");
      throw error;
    }
    if (stored === "exists") {
      await budget(env).setStatus(label, "gone");
      continue;
    }
    return json(201, { label, secret });
  }
  return failure(503, "unavailable");
}

async function handle(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);
  const route = `${request.method} ${url.pathname}`;
  const known = ["/v1/register", "/v1/address", "/v1/challenge", "/v1/name"];
  if (!known.includes(url.pathname)) return failure(404, "not_found");
  const allowed: Record<string, string[]> = {
    "/v1/register": ["POST"],
    "/v1/address": ["PUT"],
    "/v1/challenge": ["PUT", "DELETE"],
    "/v1/name": ["DELETE"],
  };
  if (!allowed[url.pathname].includes(request.method)) return failure(405, "method_not_allowed");

  const settings = readSettings(env);
  if (settings === null) return failure(503, "not_configured");
  const now = Date.now();

  if (route === "POST /v1/register") return register(request, env, settings.liveLabelsCap, now);

  const credential = parseBearer(request.headers.get("Authorization"));
  if (credential === null) return failure(401, "unauthorized");
  const secretSha256 = await sha256Hex(credential.secret);
  const stub = labelObject(env, credential.label);

  switch (route) {
    case "PUT /v1/address": {
      const body = await readBody(request, ["ipv4"]);
      if (body === null) return failure(400, "bad_request");
      if (typeof body.ipv4 !== "string" || !isRfc1918(body.ipv4)) return failure(400, "invalid_address");
      return fromOutcome(await stub.setAddress(secretSha256, now, body.ipv4));
    }
    case "PUT /v1/challenge": {
      const body = await readBody(request, ["values"]);
      if (body === null) return failure(400, "bad_request");
      if (!isChallengeValues(body.values)) return failure(400, "invalid_challenge");
      return fromOutcome(await stub.setChallenge(secretSha256, now, body.values));
    }
    case "DELETE /v1/challenge":
      return fromOutcome(await stub.clearChallenge(secretSha256, now));
    case "DELETE /v1/name":
      return fromOutcome(await stub.deleteName(secretSha256, now));
    default:
      return failure(404, "not_found");
  }
}

async function expireStale(env: Env, now: number): Promise<void> {
  const due = await budget(env).dueForExpiry(now - EXPIRY_MS, EXPIRY_BATCH, now);
  for (const label of due) {
    try {
      await labelObject(env, label).expire(label, now);
    } catch (error) {
      console.error(`expiry of ${label} failed: ${error instanceof Error ? error.name : "error"}`);
    }
  }
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      return await handle(request, env);
    } catch (error) {
      console.error(`unhandled: ${error instanceof Error ? error.name : "error"}`);
      return failure(500, "internal_error");
    }
  },

  async scheduled(controller: ScheduledController, env: Env): Promise<void> {
    if (readSettings(env) === null) return;
    await expireStale(env, controller.scheduledTime);
  },
} satisfies ExportedHandler<Env>;

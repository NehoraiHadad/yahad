import type { Budget } from "./budget";
import type { LabelObject } from "./label";

export interface Env {
  LABELS: DurableObjectNamespace<LabelObject>;
  BUDGET: DurableObjectNamespace<Budget>;
  /** Optional: absent in configurations without the rate-limiting binding. */
  REGISTER_LIMITER?: RateLimit;
  /** DNS suffix the labels live under, e.g. home.nehoraihadad.com. */
  ZONE_NAME: string;
  /** Cloudflare zone id (32 hex) of the zone that contains ZONE_NAME. */
  ZONE_ID: string;
  /** Secret (wrangler secret put CF_API_TOKEN). Zone:DNS:Edit on that one zone. */
  CF_API_TOKEN: string;
  LIVE_LABELS_CAP: string | number;
  CF_WRITES_PER_DAY: string | number;
}

type NameServiceEnv = Env;

declare global {
  namespace Cloudflare {
    // Types `env` from "cloudflare:workers" (used by the tests).
    interface Env extends NameServiceEnv {}
  }
}

// Fixed by the design (docs/HOME-NAME.md, "1. Name service").
export const NEW_LABELS_PER_DAY = 5;
export const ADDRESS_WRITES_PER_LABEL_PER_DAY = 10;
export const CHALLENGE_WRITES_PER_LABEL_PER_DAY = 20;
export const EXPIRY_DAYS = 150;
export const EXPIRY_MS = EXPIRY_DAYS * 24 * 60 * 60 * 1000;
export const RECORD_TTL_SECONDS = 60;
/**
 * Labels the cron handles per invocation. Each costs the Worker one Durable Object subrequest,
 * plus one for the Budget query; Workers Free allows 50 subrequests per invocation. Leftovers
 * are handled on the next run.
 */
export const EXPIRY_BATCH = 40;

export interface Settings {
  zoneName: string;
  zoneId: string;
  apiToken: string;
  liveLabelsCap: number;
  cfWritesPerDay: number;
}

const ZONE_ID_PATTERN = /^[0-9a-f]{32}$/;
const HOSTNAME_PATTERN = /^(?=.{1,200}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$/;

function positiveInteger(value: string | number | undefined): number | null {
  const text = String(value ?? "").trim();
  if (!/^[1-9][0-9]{0,6}$/.test(text)) return null;
  return Number(text);
}

/** Null when the deployment is not configured; the Worker then answers 503 not_configured. */
export function readSettings(env: Env): Settings | null {
  const zoneName = (env.ZONE_NAME ?? "").trim().toLowerCase();
  const zoneId = (env.ZONE_ID ?? "").trim().toLowerCase();
  const apiToken = env.CF_API_TOKEN ?? "";
  const liveLabelsCap = positiveInteger(env.LIVE_LABELS_CAP);
  const cfWritesPerDay = positiveInteger(env.CF_WRITES_PER_DAY);
  if (!HOSTNAME_PATTERN.test(zoneName) || !ZONE_ID_PATTERN.test(zoneId) || apiToken === "") return null;
  if (liveLabelsCap === null || cfWritesPerDay === null) return null;
  return { zoneName, zoneId, apiToken, liveLabelsCap, cfWritesPerDay };
}

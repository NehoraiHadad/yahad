// Minimal Cloudflare DNS v4 client for one zone. It never logs the token or request headers.

import { RECORD_TTL_SECONDS } from "./env";

export const CF_API_BASE = "https://api.cloudflare.com/client/v4";

export type RecordType = "A" | "TXT";

export interface DnsRecord {
  id: string;
  /** For TXT, the value without the surrounding quotes. */
  content: string;
}

/** A Cloudflare API call failed. Carries only the HTTP status and Cloudflare's numeric codes. */
export class CloudflareError extends Error {
  constructor(
    readonly status: number,
    readonly codes: number[],
  ) {
    super(`cloudflare_api_${status}${codes.length ? `_${codes.join("_")}` : ""}`);
    this.name = "CloudflareError";
  }
}

interface Envelope {
  success?: boolean;
  errors?: { code?: number }[];
  result?: unknown;
}

function wireContent(type: RecordType, content: string): string {
  // TXT content is sent quoted, as Cloudflare recommends; the values are base64url, so they
  // never contain a quote or a backslash.
  return type === "TXT" ? `"${content}"` : content;
}

function plainContent(type: RecordType, content: string): string {
  if (type === "TXT" && content.length >= 2 && content.startsWith('"') && content.endsWith('"')) {
    return content.slice(1, -1);
  }
  return content;
}

export class DnsClient {
  constructor(
    private readonly zoneId: string,
    private readonly token: string,
  ) {}

  async list(type: RecordType, name: string): Promise<DnsRecord[]> {
    const query = new URLSearchParams({ type, "name.exact": name, per_page: "100" });
    const result = await this.call("GET", `?${query}`);
    if (!Array.isArray(result)) throw new CloudflareError(502, []);
    return result.map((record) => toRecord(type, record));
  }

  async create(type: RecordType, name: string, content: string): Promise<string> {
    const result = await this.call("POST", "", body(type, name, content));
    return toRecord(type, result).id;
  }

  /** False when the record no longer exists. */
  async update(id: string, type: RecordType, name: string, content: string): Promise<boolean> {
    try {
      await this.call("PATCH", `/${encodeURIComponent(id)}`, body(type, name, content));
      return true;
    } catch (error) {
      if (error instanceof CloudflareError && error.status === 404) return false;
      throw error;
    }
  }

  /** Idempotent: a record that is already gone counts as deleted. */
  async remove(id: string): Promise<void> {
    try {
      await this.call("DELETE", `/${encodeURIComponent(id)}`);
    } catch (error) {
      if (error instanceof CloudflareError && error.status === 404) return;
      throw error;
    }
  }

  private async call(method: string, suffix: string, payload?: object): Promise<unknown> {
    const url = `${CF_API_BASE}/zones/${encodeURIComponent(this.zoneId)}/dns_records${suffix}`;
    const headers: Record<string, string> = { Authorization: `Bearer ${this.token}` };
    if (payload !== undefined) headers["Content-Type"] = "application/json";
    const response = await fetch(url, {
      method,
      headers,
      body: payload === undefined ? undefined : JSON.stringify(payload),
    });
    let envelope: Envelope = {};
    try {
      envelope = (await response.json()) as Envelope;
    } catch {
      // A non-JSON body is treated like any other failure below.
    }
    if (!response.ok || envelope.success !== true) {
      const codes = (envelope.errors ?? [])
        .map((error) => error.code)
        .filter((code): code is number => typeof code === "number");
      throw new CloudflareError(response.ok ? 502 : response.status, codes);
    }
    return envelope.result;
  }
}

function body(type: RecordType, name: string, content: string) {
  return {
    type,
    name,
    content: wireContent(type, content),
    ttl: RECORD_TTL_SECONDS,
    proxied: false,
  };
}

function toRecord(type: RecordType, value: unknown): DnsRecord {
  const record = value as { id?: unknown; content?: unknown } | null;
  if (record === null || typeof record.id !== "string" || typeof record.content !== "string") {
    throw new CloudflareError(502, []);
  }
  return { id: record.id, content: plainContent(type, record.content) };
}

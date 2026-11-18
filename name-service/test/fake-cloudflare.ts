// An in-memory stand-in for the Cloudflare DNS v4 records API, installed as the global fetch.
// The Worker and its Durable Objects run in the same isolate as the tests, so they see it too.

import { vi } from "vitest";
import { CF_API_BASE } from "../src/cloudflare";

export const ZONE_ID = "0123456789abcdef0123456789abcdef";
export const TOKEN = "test-token";

export interface FakeRecord {
  id: string;
  type: string;
  name: string;
  content: string;
  ttl: number;
  proxied: boolean;
}

export interface Call {
  method: string;
  path: string;
  body?: Record<string, unknown>;
  authorization: string | null;
}

export class FakeCloudflare {
  readonly records = new Map<string, FakeRecord>();
  readonly calls: Call[] = [];
  private nextId = 1;
  private failures: { method: string; status: number }[] = [];

  /** The next call with this method fails with this HTTP status. */
  failNext(method: string, status: number): void {
    this.failures.push({ method, status });
  }

  /** Adds a record behind the service's back (e.g. a create whose id was lost). */
  seed(record: Omit<FakeRecord, "id">): string {
    const id = `rec${this.nextId++}`;
    this.records.set(id, { id, ...record });
    return id;
  }

  find(type: string, name: string): FakeRecord[] {
    return [...this.records.values()].filter((record) => record.type === type && record.name === name);
  }

  writes(): Call[] {
    return this.calls.filter((call) => call.method !== "GET");
  }

  install(): void {
    vi.spyOn(globalThis, "fetch").mockImplementation(async (input, init) => {
      const request = new Request(input as RequestInfo, init);
      if (!request.url.startsWith(`${CF_API_BASE}/`)) throw new Error(`unexpected fetch: ${request.url}`);
      return this.handle(request);
    });
  }

  private async handle(request: Request): Promise<Response> {
    const url = new URL(request.url);
    const text = request.method === "GET" || request.method === "DELETE" ? "" : await request.text();
    const body = text ? (JSON.parse(text) as Record<string, unknown>) : undefined;
    this.calls.push({
      method: request.method,
      path: url.pathname + url.search,
      body,
      authorization: request.headers.get("Authorization"),
    });
    if (request.headers.get("Authorization") !== `Bearer ${TOKEN}`) return envelope(403, false, [10000]);
    const failure = this.failures.findIndex((f) => f.method === request.method);
    if (failure >= 0) {
      const [{ status }] = this.failures.splice(failure, 1);
      return envelope(status, false, [1000]);
    }
    const match = /^\/client\/v4\/zones\/([^/]+)\/dns_records(?:\/([^/]+))?$/.exec(url.pathname);
    if (match === null || match[1] !== ZONE_ID) return envelope(404, false, [7003]);
    const id = match[2];

    if (request.method === "GET" && id === undefined) {
      const type = url.searchParams.get("type");
      const name = url.searchParams.get("name.exact");
      const result = [...this.records.values()].filter(
        (record) => (type === null || record.type === type) && (name === null || record.name === name),
      );
      return envelope(200, true, [], result);
    }
    if (request.method === "POST" && id === undefined) {
      const record = validRecord(body);
      if (record === null) return envelope(400, false, [9000]);
      const identical = [...this.records.values()].some(
        (r) => r.type === record.type && r.name === record.name && r.content === record.content,
      );
      if (identical) return envelope(400, false, [81058]);
      const created = { id: `rec${this.nextId++}`, ...record };
      this.records.set(created.id, created);
      return envelope(200, true, [], created);
    }
    if (request.method === "PATCH" && id !== undefined) {
      const existing = this.records.get(id);
      if (existing === undefined) return envelope(404, false, [81044]);
      const record = validRecord(body);
      if (record === null) return envelope(400, false, [9000]);
      const updated = { ...existing, ...record };
      this.records.set(id, updated);
      return envelope(200, true, [], updated);
    }
    if (request.method === "DELETE" && id !== undefined) {
      if (!this.records.delete(id)) return envelope(404, false, [81044]);
      return envelope(200, true, [], { id });
    }
    return envelope(405, false, [10405]);
  }
}

function validRecord(body: Record<string, unknown> | undefined): Omit<FakeRecord, "id"> | null {
  if (body === undefined) return null;
  const { type, name, content, ttl, proxied } = body;
  if ((type !== "A" && type !== "TXT") || typeof name !== "string" || typeof content !== "string") return null;
  if (typeof ttl !== "number" || typeof proxied !== "boolean") return null;
  return { type, name, content, ttl, proxied };
}

function envelope(status: number, success: boolean, codes: number[], result: unknown = null): Response {
  return Response.json(
    { success, errors: codes.map((code) => ({ code, message: "fake" })), messages: [], result },
    { status },
  );
}

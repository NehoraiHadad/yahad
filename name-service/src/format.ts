// Formats of everything a client sends or receives. Every check is strict: a value either has
// exactly the documented shape or it is rejected.

const BASE32_ALPHABET = "abcdefghijklmnopqrstuvwxyz234567"; // RFC 4648, lowercase.
export const LABEL_LENGTH = 12;
const LABEL_PATTERN = /^[a-z2-7]{12}$/;

// 32 bytes in unpadded base64url are 43 characters. The last character carries 4 data bits and
// 2 zero bits, so only 16 of the 64 symbols can appear there in a canonical encoding.
const DIGEST_B64URL_PATTERN = /^[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]$/;

export function isLabel(value: string): boolean {
  return LABEL_PATTERN.test(value);
}

/** A 256-bit value (a secret, or a dns-01 digest) in canonical unpadded base64url. */
export function isDigest43(value: string): boolean {
  return DIGEST_B64URL_PATTERN.test(value);
}

export function newLabel(): string {
  // 32 divides 256, so taking the low 5 bits of each random byte is unbiased.
  const bytes = crypto.getRandomValues(new Uint8Array(LABEL_LENGTH));
  let label = "";
  for (const byte of bytes) label += BASE32_ALPHABET[byte & 31];
  return label;
}

export function newSecret(): string {
  return base64url(crypto.getRandomValues(new Uint8Array(32)));
}

export function base64url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export async function sha256Hex(value: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return toHex(new Uint8Array(digest));
}

function toHex(bytes: Uint8Array): string {
  let hex = "";
  for (const byte of bytes) hex += byte.toString(16).padStart(2, "0");
  return hex;
}

function fromHex(hex: string): Uint8Array {
  const bytes = new Uint8Array(hex.length / 2);
  for (let i = 0; i < bytes.length; i++) bytes[i] = parseInt(hex.slice(i * 2, i * 2 + 2), 16);
  return bytes;
}

const SHA256_HEX = /^[0-9a-f]{64}$/;

/** Constant-time comparison of two SHA-256 hex digests. */
export function digestsEqual(a: string, b: string): boolean {
  if (!SHA256_HEX.test(a) || !SHA256_HEX.test(b)) return false;
  return crypto.subtle.timingSafeEqual(fromHex(a), fromHex(b));
}

/**
 * Parses `Authorization: Bearer <label>.<secret>`. Returns null for anything that is not exactly
 * that shape.
 */
export function parseBearer(header: string | null): { label: string; secret: string } | null {
  if (header === null) return null;
  const match = /^Bearer ([a-z2-7]{12})\.([A-Za-z0-9_-]{43})$/.exec(header);
  if (match === null) return null;
  const [, label, secret] = match;
  if (!isLabel(label) || !isDigest43(secret)) return null;
  return { label, secret };
}

/**
 * Strict dotted-quad: exactly four decimal octets 0-255, no leading zeros (so "010" is not
 * silently read as octal or decimal), no whitespace, no other notation.
 */
export function parseIpv4(value: string): [number, number, number, number] | null {
  if (!/^(0|[1-9][0-9]{0,2})(\.(0|[1-9][0-9]{0,2})){3}$/.test(value)) return null;
  const octets = value.split(".").map(Number);
  if (octets.some((octet) => octet > 255)) return null;
  return octets as [number, number, number, number];
}

/** RFC 1918 private ranges only: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16. */
export function isRfc1918(value: string): boolean {
  const octets = parseIpv4(value);
  if (octets === null) return false;
  const [a, b] = octets;
  if (a === 10) return true;
  if (a === 172) return b >= 16 && b <= 31;
  if (a === 192) return b === 168;
  return false;
}

/** 1-2 distinct dns-01 digests (43-char base64url). */
export function isChallengeValues(values: unknown): values is string[] {
  if (!Array.isArray(values) || values.length < 1 || values.length > 2) return false;
  if (!values.every((value) => typeof value === "string" && isDigest43(value))) return false;
  return new Set(values).size === values.length;
}

export function utcDay(now: number): string {
  return new Date(now).toISOString().slice(0, 10);
}

export function secondsUntilNextUtcDay(now: number): number {
  const next = new Date(now);
  next.setUTCHours(24, 0, 0, 0);
  return Math.max(1, Math.ceil((next.getTime() - now) / 1000));
}

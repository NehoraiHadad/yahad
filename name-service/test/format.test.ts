import { describe, expect, it } from "vitest";
import {
  digestsEqual,
  isChallengeValues,
  isDigest43,
  isLabel,
  isRfc1918,
  newLabel,
  newSecret,
  parseBearer,
  parseIpv4,
  sha256Hex,
} from "../src/format";

const DIGEST = "ungWv48Bz-pBQUDeXa4iI7ADYaOWF3qctBD_YfIAFa0"; // base64url(SHA-256("abc"))

describe("labels and secrets", () => {
  it("generates 12-char lowercase RFC 4648 base32 labels", () => {
    const seen = new Set<string>();
    for (let i = 0; i < 200; i++) {
      const label = newLabel();
      expect(label).toMatch(/^[a-z2-7]{12}$/);
      expect(isLabel(label)).toBe(true);
      seen.add(label);
    }
    expect(seen.size).toBe(200);
  });

  it("rejects labels of the wrong length or alphabet", () => {
    for (const bad of ["abcdefghijk", "abcdefghijklm", "ABCDEFGHIJKL", "abcdefghijk0", "abcdefghijk1", "abcdefghijk8", "abcdef-hijkl"]) {
      expect(isLabel(bad)).toBe(false);
    }
  });

  it("generates 256-bit secrets as canonical 43-char base64url", () => {
    for (let i = 0; i < 50; i++) {
      const secret = newSecret();
      expect(secret).toHaveLength(43);
      expect(isDigest43(secret)).toBe(true);
    }
  });

  it("accepts only canonical 43-char base64url", () => {
    expect(isDigest43(DIGEST)).toBe(true);
    expect(isDigest43(DIGEST.slice(0, 42))).toBe(false);
    expect(isDigest43(`${DIGEST}A`)).toBe(false);
    expect(isDigest43(`${DIGEST.slice(0, 42)}h`)).toBe(false); // non-zero padding bits
    expect(isDigest43(`${DIGEST.slice(0, 42)}=`)).toBe(false);
    expect(isDigest43(DIGEST.replace("-", "+"))).toBe(false);
  });

  it("hashes with SHA-256 and compares digests", async () => {
    const a = await sha256Hex("abc");
    expect(a).toBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    expect(digestsEqual(a, await sha256Hex("abc"))).toBe(true);
    expect(digestsEqual(a, await sha256Hex("abd"))).toBe(false);
    expect(digestsEqual(a, a.slice(0, 62))).toBe(false);
    expect(digestsEqual(a, "")).toBe(false);
  });
});

describe("bearer credentials", () => {
  const label = "abcdefgh2345";
  it("parses Bearer <label>.<secret>", () => {
    expect(parseBearer(`Bearer ${label}.${DIGEST}`)).toEqual({ label, secret: DIGEST });
  });
  it("rejects every other shape", () => {
    for (const bad of [
      null,
      "",
      `bearer ${label}.${DIGEST}`,
      `Bearer  ${label}.${DIGEST}`,
      `Bearer ${label}${DIGEST}`,
      `Bearer ${label}.${DIGEST} `,
      `Bearer ${label.toUpperCase()}.${DIGEST}`,
      `Bearer ${label}.${DIGEST}.x`,
      `Bearer ${label}.${DIGEST.slice(1)}`,
      `Basic ${label}.${DIGEST}`,
    ]) {
      expect(parseBearer(bad)).toBeNull();
    }
  });
});

describe("IPv4 and RFC 1918", () => {
  it("parses strict dotted quads", () => {
    expect(parseIpv4("192.168.1.20")).toEqual([192, 168, 1, 20]);
    expect(parseIpv4("0.0.0.0")).toEqual([0, 0, 0, 0]);
    for (const bad of ["192.168.1", "192.168.1.1.1", "192.168.01.1", "010.0.0.1", "256.1.1.1", "192.168.1.1 ", " 192.168.1.1", "192.168.1.-1", "192.168.1.1/24", "0x0a.0.0.1", "3232235777", "192.168..1", "1e2.0.0.1", "１９２.168.1.1"]) {
      expect(parseIpv4(bad), bad).toBeNull();
    }
  });

  it("accepts exactly 10/8, 172.16/12 and 192.168/16", () => {
    for (const ok of ["10.0.0.0", "10.255.255.255", "172.16.0.1", "172.31.255.255", "192.168.0.1", "192.168.255.254"]) {
      expect(isRfc1918(ok), ok).toBe(true);
    }
    for (const bad of [
      "172.15.255.255",
      "172.32.0.1",
      "192.167.1.1",
      "192.169.1.1",
      "11.0.0.1",
      "9.255.255.255",
      "127.0.0.1",
      "169.254.1.1",
      "100.64.0.1",
      "8.8.8.8",
      "0.0.0.0",
      "255.255.255.255",
      "010.1.1.1",
      "10.1.1.01",
      "10.1.1.1.1",
      "10.1.1",
    ]) {
      expect(isRfc1918(bad), bad).toBe(false);
    }
  });
});

describe("challenge values", () => {
  const other = "LPJNul-wow4m6DsqxbninhsWHlwfp0JecwQzYpOLmCQ";
  it("accepts one or two distinct digests", () => {
    expect(isChallengeValues([DIGEST])).toBe(true);
    expect(isChallengeValues([DIGEST, other])).toBe(true);
  });
  it("rejects anything else", () => {
    for (const bad of [[], [DIGEST, other, DIGEST], [DIGEST, DIGEST], [DIGEST.slice(1)], [`"${DIGEST}"`], [42], "x", null, {}]) {
      expect(isChallengeValues(bad), JSON.stringify(bad)).toBe(false);
    }
  });
});

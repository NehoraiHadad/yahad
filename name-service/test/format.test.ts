import { describe, expect, it, vi } from "vitest";
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
  RESERVED_LABELS,
  sha256Hex,
} from "../src/format";
import labelCases from "./label-cases.json";

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

  // label-cases.json is shared with the installer's tests (HomeNameTests), so both sides apply one rule.
  it("applies the shared label rule: 3-20 of [a-z0-9-], letter or digit at both ends, no --, not reserved", () => {
    expect([...RESERVED_LABELS].sort()).toEqual([...labelCases.reserved].sort());
    for (const good of labelCases.valid) expect(isLabel(good), JSON.stringify(good)).toBe(true);
    for (const bad of labelCases.invalid) expect(isLabel(bad), JSON.stringify(bad)).toBe(false);
  });

  it("never generates a reserved label", () => {
    // "autodiscover" is 12 base32 characters: a draw that spells it is drawn again.
    const spelled = new Uint8Array([..."autodiscover"].map((c) => "abcdefghijklmnopqrstuvwxyz234567".indexOf(c)));
    const spy = vi.spyOn(crypto, "getRandomValues").mockImplementationOnce(<T extends ArrayBufferView | null>(array: T): T => {
      (array as unknown as Uint8Array).set(spelled);
      return array;
    });
    const label = newLabel();
    expect(spy).toHaveBeenCalledTimes(2);
    expect(label).not.toBe("autodiscover");
    expect(isLabel(label)).toBe(true);
    spy.mockRestore();
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
    expect(parseBearer(`Bearer hadad-home.${DIGEST}`)).toEqual({ label: "hadad-home", secret: DIGEST });
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
      `Bearer ab.${DIGEST}`,
      `Bearer xn--abc.${DIGEST}`,
      `Bearer -abc.${DIGEST}`,
      `Bearer www.${DIGEST}`,
      `Bearer ${"a".repeat(21)}.${DIGEST}`,
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

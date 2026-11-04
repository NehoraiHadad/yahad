export function committedKey(repo: string): Buffer;
export function requireKeyMatches(keyFile: string, spki: Buffer): void;
export function requireBuiltKey(packageDirectory: string, spki: Buffer): { manifestName: string; manifest: { keyId: string; version: string; sourceCommit: string; setup: { name: string; sha256: string; bytes: number } } };

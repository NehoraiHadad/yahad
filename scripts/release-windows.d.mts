type Options = Record<string, string | boolean>;
type Built = { version: string; commit: string; setupFile: string };
type Stage = (context: { clone: string; root: string; keyFile: string; inputs: unknown }) => Promise<{ assets: string; spki: Buffer }>;
export class Refusal extends Error {}
export function committedKey(repo: string): Buffer;
export function requireKeyMatches(keyFile: string, spki: Buffer): void;
export function requireBuiltKey(packageDirectory: string, spki: Buffer): { manifestName: string; manifest: { keyId: string; version: string; sourceCommit: string; setup: { name: string; sha256: string; bytes: number } } };
export function collectReleaseAssets(directory: string, version: string, spki: Buffer): { name: string; path: string; bytes: number }[];
export function install(args: { repo: string; options: Options; build?: (context: { clone: string; root: string; keyFile: string; inputs: unknown }) => Promise<Built>; launch?: (file: string) => void }): Promise<Built>;
export function publish(args: { repo: string; options: Options; confirm?: (question: string) => Promise<string>; stage?: Stage; log?: (line: string) => void }): Promise<{ version: string; commit: string; published: boolean }>;

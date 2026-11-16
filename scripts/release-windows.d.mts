type Options = Record<string, string | boolean>;
type Built = { version: string; commit: string; setupFile: string };
type Stage = (context: { clone: string; root: string; keyFile: string; inputs: unknown; infra: Infra }) => Promise<{ assets: string }>;
type Infra = { source?: string; sourceCommit?: string; digest?: string };
export type InfraPlan = { mode: 'first' | 'build' | 'reuse'; digest: string; sourceCommit?: string };
export class Refusal extends Error {}
export function committedKey(repo: string): Buffer;
export function requireKeyMatches(keyFile: string, spki: Buffer): void;
export function requireBuiltKey(packageDirectory: string, spki: Buffer): { manifestName: string; manifest: { format: number; product: string; keyId: string; version: string; sourceCommit: string } };
export function collectReleaseAssets(directory: string, version: string, spki: Buffer): { name: string; path: string; bytes: number }[];
export function planInfrastructure(args: { head: { version: string; digest: string }; baseline: { version: string; sourceCommit: string } | null; digestAt: (commit: string) => string }): InfraPlan;
export function withInfrastructureSource<T>(repo: string, plan: InfraPlan, work: (infra: Infra) => Promise<T>): Promise<T>;
export function runtimeVersionFor(appVersion: string): string;
export function requireRuntimeAboveBootstrap(runtimeVersion: string, bootstrapSource: string): void;
export function install(args: { repo: string; options: Options; build?: (context: { clone: string; root: string; keyFile: string; inputs: unknown }) => Promise<Built>; launch?: (file: string) => void }): Promise<Built>;
export function publish(args: { repo: string; options: Options; confirm?: (question: string) => Promise<string>; stage?: Stage; plan?: (context: { repo: string; inputs: unknown; spki: Buffer; latest: string }) => Promise<InfraPlan>; log?: (line: string) => void }): Promise<{ version: string; commit: string; published: boolean }>;

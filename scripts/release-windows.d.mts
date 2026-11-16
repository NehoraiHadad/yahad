type Options = Record<string, string | boolean>;
type Built = { version: string; commit: string; setupFile: string };
type Stage = (context: { clone: string; root: string; keyFile: string; inputs: unknown; infra: Infra }) => Promise<{ assets: string }>;
// The resolved infrastructure source: `source` is a worktree at sourceCommit when it is not the commit being built.
type Infra = { source?: string; sourceCommit: string; digest: string };
export type InfraPlan = { mode: 'first' | 'build' | 'reuse'; digest: string; sourceCommit?: string };
// infra_package.py cached: the build record of an infra cache entry.
export type CachedInfrastructure = { format: number; infrastructureVersion: string; contractMin: number; contractMax: number; sourceCommit: string; inputDigest: string; treeSha256: string; releaseKeyId: string | null };
export type InfraState = { version: string; digest: string; cache: string; cached: CachedInfrastructure | null };
type Inputs = ReturnType<typeof resolveInputs>;
type State = (repo: string, inputs: Inputs) => InfraState;
export class Refusal extends Error {}
export function committedKey(repo: string): Buffer;
export function requireKeyMatches(keyFile: string, spki: Buffer): void;
export function requireBuiltKey(packageDirectory: string, spki: Buffer): { manifestName: string; manifest: { format: number; product: string; keyId: string; version: string; sourceCommit: string } };
// What `Yahad.Setup.exe --verify-package` reports of its embedded package (auditSetup).
export type SetupReport = {
  infrastructure: { version: string; contractMin: number; contractMax: number; sourceCommit: string; treeSha256: string };
  app: { version: string; sourceCommit: string; requiresContract: number; treeSha256: string; databaseSchema: number; databaseMigrationsFrom: number[] };
  minimumFrom: { app: string; infrastructure: string };
  setupSourceCommit: string; coreSourceCommit: string; releaseKeyId: string | null;
};
type Audit = (setupFile: string) => SetupReport;
export function auditSetup(setupFile: string): SetupReport;
export function checkWindowsRelease(directory: string, version: string, spki: Buffer, audit?: Audit): unknown;
export function collectReleaseAssets(directory: string, version: string, spki: Buffer, audit?: Audit): { name: string; path: string; bytes: number }[];
export function planInfrastructure(args: { head: { version: string; digest: string }; baseline: { version: string; sourceCommit: string } | null; digestAt: (commit: string) => string }): InfraPlan;
export function cachedInfrastructure(repo: string, python: string, cache: string, version: string): CachedInfrastructure | null;
export function infrastructureState(repo: string, inputs: Inputs): InfraState;
export function resolveInfrastructure(args: { built: string; state: InfraState; plan?: InfraPlan | null; inHistory: (commit: string) => boolean }): { sourceCommit: string; digest: string };
export function historyOf(repo: string, built: string): (commit: string) => boolean;
export function withInfrastructure<T>(args: { repo: string; built: string; state: InfraState; plan?: InfraPlan | null }, work: (infra: Infra) => T | Promise<T>): Promise<T>;
export function withInfrastructureSource<T>(repo: string, built: string, infra: { sourceCommit: string; digest: string }, work: (infra: Infra) => T | Promise<T>): Promise<T>;
export function buildSigned(args: { repo: string; keyFile: string; out: string; inputsOf: () => Inputs; infra: Infra }): unknown;
export function buildRelease<T = unknown>(args: { repo: string; keyFile: string; out: string; options: Options; state?: State; sign?: (args: { repo: string; keyFile: string; out: string; inputsOf: () => Inputs; infra: Infra }) => T }): Promise<T>;
export function resolveInputs(home: string, options: Options): { pwsh: string; NodeDirectory: string; NpmCache: string; VendorDirectory: string; DotNet: string; PackageCache: string; InfraCache: string; Python: string };
export function runtimeVersionFor(appVersion: string): string;
export function requireRuntimeAboveBootstrap(runtimeVersion: string, bootstrapSource: string): void;
export function install(args: { repo: string; options: Options; build?: (context: { clone: string; root: string; keyFile: string; inputs: unknown; infra: Infra }) => Promise<Built>; launch?: (file: string) => void; state?: State }): Promise<Built>;
export function publish(args: { repo: string; options: Options; confirm?: (question: string) => Promise<string>; stage?: Stage; plan?: (context: { repo: string; inputs: unknown; spki: Buffer; latest: string }) => Promise<InfraPlan>; state?: State; audit?: Audit; log?: (line: string) => void }): Promise<{ version: string; commit: string; published: boolean }>;

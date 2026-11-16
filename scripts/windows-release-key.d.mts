export const PREFIX: Buffer;
export const PRODUCT: string;
export function keyIdOf(spki: Buffer): string;
export function requireReleaseManifest(bytes: Buffer, expectedKeyId: string): { keyId: string };
export function verifyRelease(spki: Buffer, manifestBytes: Buffer, signature: Buffer): { keyId: string } | null;

import {
  randomBytes,
  createCipheriv,
  createDecipheriv,
  scrypt as scryptCb,
  timingSafeEqual,
  createHash,
} from 'node:crypto';
import { promisify } from 'node:util';
const scrypt = promisify(scryptCb);
export const token = () => randomBytes(32).toString('base64url');
export const hash = (s: string) => createHash('sha256').update(s).digest('hex');
export function seal(key: Buffer, data: Buffer | string, aad: string): Buffer {
  const iv = randomBytes(12),
    c = createCipheriv('aes-256-gcm', key, iv);
  c.setAAD(Buffer.from(aad));
  const body = Buffer.concat([c.update(data), c.final()]);
  return Buffer.concat([iv, c.getAuthTag(), body]);
}
export function open(key: Buffer, data: Uint8Array, aad: string): Buffer {
  const b = Buffer.from(data);
  const d = createDecipheriv('aes-256-gcm', key, b.subarray(0, 12));
  d.setAAD(Buffer.from(aad));
  d.setAuthTag(b.subarray(12, 28));
  return Buffer.concat([d.update(b.subarray(28)), d.final()]);
}
export async function passwordHash(password: string) {
  if (password.length < 14 || password.length > 256) throw new Error('Use 14–256 characters');
  const salt = randomBytes(16);
  const derived = (await scrypt(password, salt, 64)) as Buffer;
  return salt.toString('hex') + ':' + derived.toString('hex');
}
export async function verifyPassword(password: string, value: string) {
  const [s, h] = value.split(':');
  if (!s || !h || password.length > 256) return false;
  const derived = (await scrypt(password, Buffer.from(s, 'hex'), 64)) as Buffer;
  const expected = Buffer.from(h, 'hex');
  return derived.length === expected.length && timingSafeEqual(derived, expected);
}

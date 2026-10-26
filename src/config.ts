import { readFileSync, statSync } from 'node:fs';
import { resolve } from 'node:path';
import { z } from 'zod';
export interface Config {
  dataDir: string;
  masterKey: Buffer;
  connector: 'mock' | 'baileys';
  realAllowed: boolean;
  host: string;
  port: number;
  origin: string;
  secureCookie: boolean;
  maxMedia: number;
  maxStorage: number;
  childIdleMs: number;
  sessionMs: number;
  updateStatusFile?: string;
  diagnosticsEnabled?: boolean;
  diagnosticsRetentionDays?: number;
  diagnosticsFlushSeconds?: number;
  tls?: { cert: Buffer; key: Buffer };
}
export function loadConfig(): Config {
  const env = z
    .object({
      DATA_DIR: z.string().default('./data'),
      MASTER_KEY_FILE: z.string(),
      CONNECTOR: z.enum(['mock', 'baileys']).default('mock'),
      ALLOW_REAL_WHATSAPP: z.string().default('no'),
      HOST: z.string().default('127.0.0.1'),
      PORT: z.coerce.number().int().min(1).max(65535).default(3000),
      APP_ORIGIN: z.url(),
      COOKIE_SECURE: z.enum(['true', 'false']).default('true'),
      MAX_MEDIA_MB: z.coerce.number().min(1).max(32).default(16),
      MAX_STORAGE_MB: z.coerce.number().min(64).default(2048),
      CHILD_IDLE_MINUTES: z.coerce.number().min(1).max(120).default(15),
      SESSION_HOURS: z.coerce.number().min(1).max(48).default(12),
      DIAGNOSTICS_ENABLED: z.enum(['true', 'false']).default('true'),
      DIAGNOSTICS_RETENTION_DAYS: z.coerce.number().int().min(1).max(7).default(7),
      DIAGNOSTICS_FLUSH_SECONDS: z.coerce.number().int().min(60).max(3600).default(300),
    })
    .parse(process.env);
  const path = resolve(env.MASTER_KEY_FILE);
  const st = statSync(path);
  if (st.mode & 0o077) throw new Error('Master key file must have mode 0600');
  const masterKey = Buffer.from(readFileSync(path, 'utf8').trim(), 'base64');
  if (masterKey.length !== 32) throw new Error('Master key must be 32 random bytes in base64');
  if (env.CONNECTOR === 'baileys' && env.ALLOW_REAL_WHATSAPP !== 'I_APPROVE_UNOFFICIAL_LINK')
    throw new Error('Real WhatsApp needs explicit local approval');
  const origin = new URL(env.APP_ORIGIN).origin;
  if (
    env.COOKIE_SECURE === 'false' &&
    !['localhost', '127.0.0.1', '[::1]'].includes(new URL(origin).hostname)
  )
    throw new Error('Insecure cookies allowed only on localhost');
  return {
    dataDir: resolve(env.DATA_DIR),
    masterKey,
    connector: env.CONNECTOR,
    realAllowed: env.ALLOW_REAL_WHATSAPP === 'I_APPROVE_UNOFFICIAL_LINK',
    host: env.HOST,
    port: env.PORT,
    origin,
    secureCookie: env.COOKIE_SECURE === 'true',
    maxMedia: env.MAX_MEDIA_MB * 1024 ** 2,
    maxStorage: env.MAX_STORAGE_MB * 1024 ** 2,
    childIdleMs: env.CHILD_IDLE_MINUTES * 60_000,
    sessionMs: env.SESSION_HOURS * 3600_000,
    updateStatusFile: process.env.UPDATE_STATUS_FILE,
    diagnosticsEnabled: env.DIAGNOSTICS_ENABLED === 'true',
    diagnosticsRetentionDays: env.DIAGNOSTICS_RETENTION_DAYS,
    diagnosticsFlushSeconds: env.DIAGNOSTICS_FLUSH_SECONDS,
    ...(process.env.TLS_CERT_FILE && process.env.TLS_KEY_FILE
      ? {
          tls: {
            cert: readFileSync(process.env.TLS_CERT_FILE),
            key: readFileSync(process.env.TLS_KEY_FILE),
          },
        }
      : {}),
  };
}

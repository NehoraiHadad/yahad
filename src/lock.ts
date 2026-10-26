import lockfile from 'proper-lockfile';
import { join } from 'node:path';
import { mkdirSync, chmodSync } from 'node:fs';
export async function acquireDataLock(dir: string) {
  mkdirSync(dir, { recursive: true, mode: 0o700 });
  chmodSync(dir, 0o700);
  return lockfile.lock(dir, {
    lockfilePath: join(dir, 'process.lock'),
    realpath: true,
    stale: 10000,
    update: 3000,
    retries: 0,
  });
}

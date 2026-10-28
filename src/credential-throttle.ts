import { hash } from './crypto.js';
import type { Store } from './store.js';

/** Durable, bounded guessing budget. No credentials, raw account names or IPs are stored. */
export class CredentialThrottle {
  constructor(private store: Store) {}
  reserve(account: string, ip: string, now = Date.now()): boolean {
    // One child's mistakes must not exhaust the household's network budget.
    // Account budgets follow stable IDs across name changes and IP rotation.
    const limits = [{ key: hash(account), limit: 8 }, { key: hash(`ip:${ip}`), limit: 80 }];
    let allowed = false;
    this.store.tx(() => {
      this.store.run('DELETE FROM credential_attempts WHERE until<=?', now);
      if (this.store.one('SELECT count(*) AS n FROM credential_attempts').n >= 10000) return;
      const rows = limits.map(({ key }) => this.store.one('SELECT n,until FROM credential_attempts WHERE key=?', key));
      if (limits.some(({ limit }, i) => rows[i] && rows[i].n >= limit)) return;
      limits.forEach(({ key }, i) => this.store.run(
        'INSERT INTO credential_attempts(key,n,until) VALUES(?,?,?) ON CONFLICT(key) DO UPDATE SET n=excluded.n',
        key, (rows[i]?.n || 0) + 1, now + 15 * 60_000));
      allowed = true;
    });
    return allowed;
  }
  clear(account: string) {
    // Never erase network abuse history when an attacker has one valid account.
    this.store.run('DELETE FROM credential_attempts WHERE key=?', hash(account));
  }
}

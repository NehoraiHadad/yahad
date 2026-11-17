// The address other devices open is the server's own origin (APP_ORIGIN), which
// /api/me returns to a parent as siteOrigin. It is not necessarily the address this
// browser is on: Windows Setup opens the owner's loopback entry, which works on
// that computer only.

export function isLoopbackOrigin(origin: string) {
  const host = new URL(origin).hostname;
  return host === 'localhost' || host.endsWith('.localhost') || host === '[::1]' || /^127\./.test(host);
}

// The same rule as NetworkPlan.IsPrivateLanV4 in the Windows installer.
function isPrivateLanV4(host: string) {
  const parts = host.split('.');
  if (parts.length !== 4 || parts.some(part => !/^(0|[1-9]\d{0,2})$/.test(part) || Number(part) > 255)) return false;
  const [a, b] = parts.map(Number);
  return a === 10 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168);
}

/** The address to give another device, or null when the server is reachable from
 * this computer only. */
export function sharedAddress(siteOrigin?: string): string | null {
  if (!siteOrigin) return null;
  try { return isLoopbackOrigin(siteOrigin) ? null : new URL(siteOrigin).origin; }
  catch { return null; }
}

/** Windows home-network mode (HTTPS on a private IPv4 address) serves a join page
 * that explains the home certificate; null for every other deployment. */
export function homeJoinPage(address: string): string | null {
  const url = new URL(address);
  return url.protocol === 'https:' && isPrivateLanV4(url.hostname) ? url.origin + '/home-network' : null;
}

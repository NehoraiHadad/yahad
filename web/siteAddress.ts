// The address other devices open is the server's own origin (APP_ORIGIN), which
// /api/me returns to a parent as siteOrigin. It is not necessarily the address this
// browser is on: Windows Setup opens the owner's loopback entry, which works on
// that computer only.

export type HomeNetwork = { joinPage: string; certificateSha256: string };

export function isLoopbackOrigin(origin: string) {
  const host = new URL(origin).hostname;
  return host === 'localhost' || host.endsWith('.localhost') || host === '[::1]' || /^127\./.test(host);
}

/** The address to give another device, or null when the server is reachable from
 * its computer only. */
export function sharedAddress(siteOrigin?: string): string | null {
  if (!siteOrigin) return null;
  try { return isLoopbackOrigin(siteOrigin) ? null : new URL(siteOrigin).origin; }
  catch { return null; }
}

/** The address a tablet opens: the one this browser is on, which already works from
 * another device (a Tailscale or home address), unless it is this computer's own
 * loopback entry; then the server's address. */
export function tabletAddress(currentOrigin: string, siteOrigin?: string): string | null {
  return isLoopbackOrigin(currentOrigin) ? sharedAddress(siteOrigin) : new URL(currentOrigin).origin;
}

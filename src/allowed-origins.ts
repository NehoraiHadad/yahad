/** Explicit opt-in only. These are additional same-origin entry points, not CORS grants. */
export function parseAdditionalOrigins(value: string, secureCookie: boolean): string[] {
  if (!value.trim()) return [];
  const entries = value.split(',').map((entry) => entry.trim());
  if (entries.length > 8 || value.length > 4096) throw new Error('Too many additional origins');
  const origins = entries.map((entry) => {
    let url: URL;
    try { url = new URL(entry); }
    catch { throw new Error('Additional origins must be exact HTTPS or loopback origins'); }
    const localHttp = url.protocol === 'http:' && ['127.0.0.1', '[::1]', 'localhost'].includes(url.hostname);
    if (!(url.protocol === 'https:' || localHttp) || !url.hostname || entry.includes('*') ||
        url.username || url.password || url.search || url.hash || url.pathname !== '/' ||
        (entry !== url.origin && entry !== url.origin + '/'))
      throw new Error('Additional origins must be exact HTTPS or loopback origins');
    return url.origin;
  });
  // Even a local development primary origin must not silently downgrade a remote session.
  if (!secureCookie) throw new Error('Additional origins require secure cookies');
  return [...new Set(origins)];
}

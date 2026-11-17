/** What a parent is shown about the Windows home network: the join page the proxy
 * serves and the home certificate's fingerprint. The Windows service host passes
 * them from Setup's record; no other deployment serves a join page, so without them
 * the app shows no join instructions. */
export type HomeNetwork = { joinPage: string; certificateSha256: string };

export function parseHomeNetwork(joinPage: string | undefined, certificateSha256: string | undefined, origin: string): HomeNetwork | undefined {
  if (!joinPage && !certificateSha256) return undefined;
  if (!joinPage || !certificateSha256) throw new Error('APP_JOIN_PAGE and APP_HOME_CERTIFICATE_SHA256 come together');
  if (!/^([0-9A-F]{2}:){31}[0-9A-F]{2}$/.test(certificateSha256)) throw new Error('APP_HOME_CERTIFICATE_SHA256 is not a SHA-256 fingerprint');
  const site = new URL(origin);
  // The two pages the home-network proxy serves for this very address.
  if (site.protocol !== 'https:' || (joinPage !== `http://${site.hostname}/` && joinPage !== `${site.origin}/home-network`))
    throw new Error('APP_JOIN_PAGE is not a join page of APP_ORIGIN');
  return { joinPage, certificateSha256 };
}

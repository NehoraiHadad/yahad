/** What a parent is shown about the Windows home network: the join page the proxy
 * serves and the home certificate's fingerprint. The Windows service host passes
 * them from Setup's record; no other deployment serves a join page, so without them
 * the app shows no join instructions. When the site is reached by a name, the
 * home-network (IP) address is carried too, as the fallback for devices that cannot
 * open the name. */
export type HomeNetwork = { joinPage: string; certificateSha256: string; homeAddress?: string };

const privateIPv4 = /^(10\.\d+\.\d+\.\d+|172\.(1[6-9]|2\d|3[01])\.\d+\.\d+|192\.168\.\d+\.\d+)$/;

/** APP_HOME_ADDRESS: exactly `https://<private IPv4>`, nothing else. */
function parseHomeAddress(value: string): URL {
  let url: URL;
  try { url = new URL(value); } catch { throw new Error('APP_HOME_ADDRESS is not a URL'); }
  if (url.protocol !== 'https:' || url.origin !== value || !privateIPv4.test(url.hostname))
    throw new Error('APP_HOME_ADDRESS is not an https origin on a private IPv4 address');
  return url;
}

export function parseHomeNetwork(
  joinPage: string | undefined, certificateSha256: string | undefined, origin: string, homeAddress?: string,
): HomeNetwork | undefined {
  if (!joinPage && !certificateSha256) return undefined;
  if (!joinPage || !certificateSha256) throw new Error('APP_JOIN_PAGE and APP_HOME_CERTIFICATE_SHA256 come together');
  if (!/^([0-9A-F]{2}:){31}[0-9A-F]{2}$/.test(certificateSha256)) throw new Error('APP_HOME_CERTIFICATE_SHA256 is not a SHA-256 fingerprint');
  // The join page belongs to the home-network address: APP_HOME_ADDRESS when the site
  // is reached by a name, otherwise the site's own origin.
  const site = homeAddress ? parseHomeAddress(homeAddress) : new URL(origin);
  // The two pages the home-network proxy serves for this very address.
  if (site.protocol !== 'https:' || (joinPage !== `http://${site.hostname}/` && joinPage !== `${site.origin}/home-network`))
    throw new Error(homeAddress ? 'APP_JOIN_PAGE is not a join page of APP_HOME_ADDRESS' : 'APP_JOIN_PAGE is not a join page of APP_ORIGIN');
  return homeAddress ? { joinPage, certificateSha256, homeAddress: site.origin } : { joinPage, certificateSha256 };
}

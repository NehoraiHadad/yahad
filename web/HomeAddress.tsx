import React, { useEffect, useState } from 'react';
import { Copy } from 'lucide-react';
import { copyText } from './ErrorReference';
import { isLoopbackOrigin, sharedAddress, type HomeNetwork } from './siteAddress';

/** A read-only value with a copy button. */
export function CopyField({ label, value }: { label: string; value: string }) {
  const [copied, setCopied] = useState<'idle' | 'done' | 'failed'>('idle');
  useEffect(() => setCopied('idle'), [value]);
  return <div className="tablet-login-field copy-field">
    <label>{label}<input readOnly dir="ltr" value={value} onFocus={event => event.currentTarget.select()}/></label>
    <button type="button" className="text-button" onClick={() => {
      copyText(value).then(() => setCopied('done'), () => setCopied('failed'));
    }}><Copy size={14} aria-hidden="true"/>{copied === 'done' ? 'הועתק' : 'העתקה'}</button>
    {copied === 'failed' && <span role="status">ההעתקה לא זמינה בדפדפן הזה. אפשר לסמן את הטקסט ולהעתיק אותו.</span>}
  </div>;
}

/** The join page Setup set up and the fingerprint a new device compares against.
 * The plain-HTTP page opens without a warning; the HTTPS one only after it. */
export function JoinSteps({ home }: { home: HomeNetwork }) {
  const plain = home.joinPage.startsWith('http://');
  return <>
    <p className="tablet-step-note">{plain
      ? 'בפעם הראשונה, פותחים במכשיר החדש את דף ההצטרפות. הוא מסביר איך להוריד את תעודת הבית ולבדוק אותה לפני שנכנסים ליחד.'
      : 'בפעם הראשונה הדפדפן במכשיר החדש יציג אזהרת אבטחה גם לפני דף ההצטרפות, כי הדף זמין רק בכתובת המאובטחת. אל תדלגו על האזהרה לפני שבדקתם את התעודה.'}</p>
    <CopyField label="דף ההצטרפות" value={home.joinPage}/>
    <CopyField label="טביעת האצבע של תעודת הבית, להשוואה במכשיר החדש" value={home.certificateSha256}/>
  </>;
}

/** The parent's lasting place, on the computer Yahad runs on, for the address other
 * devices open, so it does not depend on the Setup window that first showed it. */
export function HomeAddress({ siteOrigin, homeNetwork, currentOrigin }: {
  siteOrigin?: string; homeNetwork?: HomeNetwork; currentOrigin: string;
}) {
  const address = sharedAddress(siteOrigin);
  if (!address || !isLoopbackOrigin(currentOrigin)) return null;
  return <section className="parent-card home-address" aria-labelledby="home-address-title">
    <h2 id="home-address-title">כתובת יחד למכשירים אחרים</h2>
    <p>הכתובת שבדפדפן כאן פועלת רק במחשב הזה. בטלפון או בטאבלט פותחים את הכתובת הזו:</p>
    <CopyField label="כתובת יחד" value={address}/>
    {homeNetwork && <>
      <p className="tablet-step-note">המכשיר צריך להיות מחובר לאותה רשת ביתית.</p>
      <JoinSteps home={homeNetwork}/>
    </>}
  </section>;
}

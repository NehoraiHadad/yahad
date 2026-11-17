import React, { useEffect, useState } from 'react';
import { Copy } from 'lucide-react';
import { copyText } from './ErrorReference';
import { homeJoinPage, isLoopbackOrigin, sharedAddress } from './siteAddress';

/** A read-only address with a copy button. */
export function CopyField({ label, value }: { label: string; value: string }) {
  const [copied, setCopied] = useState<'idle' | 'done' | 'failed'>('idle');
  useEffect(() => setCopied('idle'), [value]);
  return <div className="tablet-login-field copy-field">
    <label>{label}<input readOnly dir="ltr" value={value} onFocus={event => event.currentTarget.select()}/></label>
    <button type="button" className="text-button" onClick={() => {
      copyText(value).then(() => setCopied('done'), () => setCopied('failed'));
    }}><Copy size={14} aria-hidden="true"/>{copied === 'done' ? 'הועתק' : 'העתקה'}</button>
    {copied === 'failed' && <span role="status">ההעתקה לא זמינה בדפדפן הזה. אפשר לסמן את הכתובת ולהעתיק אותה.</span>}
  </div>;
}

/** Why a new device warns about the certificate, and where the explanation is. */
export function JoinPageNote({ join }: { join: string }) {
  return <>
    <p className="tablet-step-note">בפעם הראשונה הדפדפן במכשיר החדש יציג אזהרת אבטחה, כי תעודת הבית נוצרה במחשב
      שבו יחד מותקנת. דף ההצטרפות מסביר איך לבדוק את התעודה לפני שממשיכים.</p>
    <CopyField label="דף ההצטרפות" value={join}/>
  </>;
}

/** The parent's lasting place for the address other devices open, so it does not
 * depend on the Setup window that first showed it. */
export function HomeAddress({ siteOrigin, currentOrigin }: { siteOrigin?: string; currentOrigin: string }) {
  const address = sharedAddress(siteOrigin);
  if (!address) return null;
  const join = homeJoinPage(address);
  const here = isLoopbackOrigin(currentOrigin);
  return <section className="parent-card home-address" aria-labelledby="home-address-title">
    <h2 id="home-address-title">כתובת יחד למכשירים אחרים</h2>
    <p>{here ? 'הכתובת שבדפדפן כאן פועלת רק במחשב הזה. בטלפון או בטאבלט פותחים את הכתובת הזו:'
      : 'בטלפון או בטאבלט פותחים את הכתובת הזו:'}</p>
    <CopyField label="כתובת יחד" value={address}/>
    {join && <>
      <p className="tablet-step-note">המכשיר צריך להיות מחובר לאותה רשת ביתית.</p>
      <JoinPageNote join={join}/>
    </>}
  </section>;
}

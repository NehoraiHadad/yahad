import React from 'react';
import { ArrowLeft, KeyRound, Tablet } from 'lucide-react';
import type { familyProgress } from './FamilyProgress';

type Child = ReturnType<typeof familyProgress>['children'][number];
export function ChildTabletSetup({ child, loaded, stale, busy, origin, onPassword, openChat }: {
  child?: Child; loaded: boolean; stale: boolean; busy: boolean; origin: string;
  onPassword: (id: string) => void; openChat: () => void;
}) {
  // The parent's current origin is not necessarily reachable from another device.
  const hostname = new URL(origin).hostname;
  const loopback = hostname === 'localhost' || hostname.endsWith('.localhost') || hostname === '[::1]' || /^127\./.test(hostname);
  const ready = loaded && !stale && !!child?.ready;
  const status = !loaded ? 'טוענים את מצב ההכנה…' : stale ? 'ממתינים לאימות מצב ההכנה מול השרת.'
    : !child ? 'בחרו חשבון ילד ברשימת הילדים. אם עדיין אין חשבון, הוסיפו אותו תחילה.'
    : !child.groups.length ? 'לפני הכניסה, בחרו לפחות שיחה אחת עבור החשבון הזה.'
    : !child.ready ? 'השיחות נשמרו. חברו את חשבון WhatsApp כדי להמשיך.'
    : 'החשבון מוכן לכניסה. את השלבים הבאים מבצעים בטאבלט של הילד או הילדה.';
  return <section className="parent-card tablet-setup" id="family-tablet" tabIndex={-1} aria-labelledby="tablet-setup-title">
    <header className="tablet-setup-heading">
      <Tablet size={28} aria-hidden="true"/>
      <div><span className="eyebrow">כניסה במכשיר של הילד או הילדה</span>
        <h2 id="tablet-setup-title">{child ? <>מכניסים את <bdi>{child.name}</bdi> ליחד</> : 'מכינים את הטאבלט לכניסה'}</h2>
        <p role="status" className={ready ? 'tablet-ready' : ''}>{status}</p>
        {loaded && !stale && !ready && <a href={child?.groups.length ? '#family-connection' : '#children-title'}>
          {child?.groups.length ? 'לחיבור החשבון' : 'לניהול הילדים והשיחות'}</a>}
      </div>
    </header>
    {loaded && !stale && child && <ol className="tablet-setup-steps" aria-label="שלבי הכניסה בטאבלט">
      <li><h3><span aria-hidden="true">1</span> פותחים את יחד בטאבלט</h3>
        <p>{loopback ? 'הכתובת הנוכחית פועלת רק במכשיר הזה. בטאבלט צריך לפתוח את כתובת השרת ברשת הביתית.' : 'פותחים בדפדפן בטאבלט את הכתובת הזו:'}</p>
        <label className="tablet-login-field">{loopback ? 'הכתובת במכשיר הזה בלבד' : 'כתובת יחד'}
          <input readOnly dir="ltr" value={origin} onFocus={event => event.currentTarget.select()}/>
        </label>
        <p className="tablet-step-note">זו כתובת האתר, לא כתובת של הטאבלט. כתובת מקומית דורשת חיבור לאותה רשת ביתית.</p>
      </li>
      <li><h3><span aria-hidden="true">2</span> נכנסים לחשבון הילד</h3>
        <label className="tablet-login-field">שם לכניסה
          <input readOnly dir="auto" value={child.name} onFocus={event => event.currentTarget.select()}/>
        </label>
        <p>משתמשים בסיסמה שבחרתם לחשבון הזה, לא בסיסמת ההורה.</p>
        <button type="button" className="text-button" disabled={busy} onClick={() => onPassword(child.id)}>
          <KeyRound size={16} aria-hidden="true"/> איפוס הסיסמה של <bdi>{child.name}</bdi>
        </button>
      </li>
      <li><h3><span aria-hidden="true">3</span> בודקים שהכול מופיע</h3>
        <p>לאחר הכניסה בטאבלט, ודאו שהשם הנכון מופיע ושמוצגות רק השיחות שאישרתם לחשבון.</p>
        <p className="tablet-step-note">המסך הזה מציג הכנה בלבד. הוא אינו מאשר שהטאבלט התחבר.</p>
      </li>
    </ol>}
    <footer className="tablet-parent-navigation">
      <div><strong>רוצים לפתוח שיחות בחשבון ההורה?</strong><p>הפעולה הזו פותחת את השיחות שלכם כאן, ולא מחברת את הטאבלט.</p></div>
      <button type="button" className="outline-button" onClick={openChat}>לשיחות של ההורה <ArrowLeft size={18} aria-hidden="true"/></button>
    </footer>
  </section>;
}

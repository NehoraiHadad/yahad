import React, { useEffect, useRef, useState } from 'react';
import { api, ApiError } from './api';
import { newClientId } from './clientId';
import type { UsageStatus } from '../src/usage';
import { usageMinutes } from './UsageBudget';

const timezoneChoices = [
  ['Asia/Jerusalem', 'ישראל — שעון ירושלים (כולל שעון קיץ)'],
  ['Europe/London', 'בריטניה — לונדון'], ['Europe/Paris', 'צרפת — פריז'],
  ['America/New_York', 'ארצות הברית — ניו יורק'], ['America/Los_Angeles', 'ארצות הברית — לוס אנג׳לס'],
  ['Australia/Sydney', 'אוסטרליה — סידני'], ['UTC', 'UTC — זמן עולמי'],
];
const timezoneName = (zone: string) => timezoneChoices.find(([id]) => id === zone)?.[1] || zone;

export function ChildUsageSettings({ childId, name, disabled }: { childId: string; name: string; disabled: boolean }) {
  const [status, setStatus] = useState<UsageStatus | null>(null), [minutes, setMinutes] = useState(''), [unlimited, setUnlimited] = useState(true);
  const [bonus, setBonus] = useState(''), [zone, setZone] = useState(''), [busy, setBusy] = useState(false), [error, setError] = useState(''), [notice, setNotice] = useState('');
  const mounted = useRef(true), generation = useRef(0), edited = useRef(false), working = useRef(false);
  const pendingBonus = useRef<{ requestId: string; day: string; dayKey: string; minutes: number } | null>(null);
  const path = '/parent/usage/' + encodeURIComponent(childId);
  const accept = (value: UsageStatus) => {
    setStatus(value);
    if (!edited.current) { setUnlimited(value.minutes === null); setMinutes(String(value.minutes ?? '')); setZone(value.timezoneConfirmed ? value.pendingTimezone || value.timezone : ''); }
  };
  useEffect(() => {
    mounted.current = true;
    const load = async () => {
      if (working.current) return;
      const request = ++generation.current;
      try {
        const value = await api<UsageStatus>(path);
        if (mounted.current && request === generation.current) { accept(value); setError(''); }
      } catch (e) { if (mounted.current && request === generation.current) { setStatus(null); setError((e as Error).message); } }
    };
    void load(); const timer = setInterval(() => void load(), 10000);
    return () => { mounted.current = false; generation.current++; clearInterval(timer); };
  }, [childId]);
  const act = async (operation: () => Promise<UsageStatus | void>, message: string | ((value: UsageStatus) => string)) => {
    if (!mounted.current || disabled || working.current || !status) return;
    working.current = true; generation.current++; setBusy(true); setError(''); setNotice('');
    try {
      const result = await operation();
      const value = result || await api<UsageStatus>(path);
      if (!mounted.current) return;
      edited.current = false;
      accept(value); setNotice(typeof message === 'function' ? message(value) : message);
    } catch (e) { if (mounted.current) setError((e as Error).message); }
    finally { working.current = false; if (mounted.current) setBusy(false); }
  };
  const number = Number(minutes), bonusNumber = Number(bonus);
  const valid = minutes !== '' && Number.isSafeInteger(number) && number >= 0 && number <= 1000000;
  const locked = disabled || busy || !status;
  return <section className="child-usage-settings" dir="rtl" aria-label={`זמן שימוש של ${name}`} aria-busy={busy}>
    <h4>זמן שימוש של <bdi>{name}</bdi></h4>
    <p>מכסה יומית אישית. ללא הגבלה עד שבוחרים מכסה. אפס דקות עוצר את השימוש.</p>
    <form data-usage-action="timezone" aria-label="אזור הזמן המשפחתי" onSubmit={e => {
      e.preventDefault(); if (!zone) return;
      void act(async () => { await api('/parent/usage-timezone', 'PUT', { timezone: zone }); return api<UsageStatus>(path); },
        value => value.pendingTimezone ? 'השינוי נקבע לאיפוס עתידי. המכסה והתוספת הנוכחיות נשמרות עד המעבר.' : 'אזור הזמן המשפחתי נשמר. אפשר להגדיר מכסה.');
    }}>
      {!status?.timezoneConfirmed && <p className="usage-setup-note" role="status">לפני שמתחילים למדוד, בוחרים אזור זמן למשפחה. עד לבחירה אין ספירה ולא מופעלת מכסה.</p>}
      <fieldset disabled={locked}><label>איפה המשפחה נמצאת?
        <select value={zone} onChange={e => { edited.current = true; setZone(e.target.value); }}>
          <option value="">בחירת אזור זמן</option>
          {timezoneChoices.map(([id, label]) => <option key={id} value={id}>{label}</option>)}
          {zone && !timezoneChoices.some(([id]) => id === zone) && <option value={zone}>{zone}</option>}
        </select></label>
        <button className="outline-button" type="submit" disabled={!zone}>{status?.timezoneConfirmed ? 'שמירת שינוי אזור זמן' : 'אישור אזור הזמן והתחלת המדידה'}</button>
      </fieldset>
      {status?.timezoneConfirmed && <p>איפוס בחצות לפי {timezoneName(status.timezone)}.</p>}
      {status?.pendingTimezone && status.timezoneChangesAt && <p role="status">מעבר ל־{timezoneName(status.pendingTimezone)} ב־{new Intl.DateTimeFormat('he-IL', { dateStyle: 'medium', timeStyle: 'short', timeZone: status.pendingTimezone }).format(status.timezoneChangesAt)} לפי השעון החדש. עד אז המכסה והתוספת של המחזור הנוכחי נשמרות; לא יינתן איפוס נוסף בדרך.</p>}
    </form>
    {status && <p className="usage-summary" role="status">היום ({status.day}): נוצלו {usageMinutes(status.usedMs)} דק׳ · {status.remainingMs === null ? 'ללא הגבלה' : `נותרו ${usageMinutes(status.remainingMs)} דק׳`} · תוספת להיום: {usageMinutes(status.bonusMs)} דק׳</p>}
    <form onSubmit={e => { e.preventDefault(); if (unlimited || valid && status?.timezoneConfirmed) void act(() => api<UsageStatus>(path, 'PUT', { minutes: unlimited ? null : number }), `המכסה של ${name} נשמרה.`); }}>
      <fieldset disabled={locked || !status?.timezoneConfirmed}><label><input type="checkbox" checked={unlimited} onChange={e => { edited.current = true; setUnlimited(e.target.checked); }}/> ללא הגבלת זמן</label>
        <label>דקות ליום<input type="number" inputMode="numeric" min="0" max="1000000" step="1" value={minutes} disabled={unlimited} onChange={e => { edited.current = true; setMinutes(e.target.value); }}/></label>
        <button className="outline-button" type="submit" disabled={!unlimited && (!valid || !status?.timezoneConfirmed)}>שמירת מכסה</button></fieldset>
    </form>
    <form onSubmit={e => {
      e.preventDefault(); if (!mounted.current || !status?.timezoneConfirmed || disabled || working.current) return;
      if (!pendingBonus.current && bonus !== '' && Number.isSafeInteger(bonusNumber) && bonusNumber > 0 && bonusNumber <= 1000000)
        pendingBonus.current = { requestId: newClientId(), day: status.day, dayKey: status.dayKey, minutes: bonusNumber };
      const request = pendingBonus.current;
      if (request) void act(async () => {
        try {
          const value = await api<UsageStatus>(path + '/bonus', 'POST', request);
          pendingBonus.current = null; if (mounted.current) setBonus(''); return value;
        } catch (error) {
          if (error instanceof ApiError && ['BONUS_DAY_CHANGED', 'BONUS_CONFLICT', 'INVALID_INPUT'].includes(error.code)) pendingBonus.current = null;
          throw error;
        }
      }, value => value.dayKey === request.dayKey ? `נוסף זמן להיום עבור ${name}.` : 'התוספת שויכה למחזור הקודם. לא נוסף זמן להיום.');
    }}><fieldset disabled={locked || !status?.timezoneConfirmed}>
      <label>תוספת דקות להיום בלבד<input type="number" inputMode="numeric" min="1" max="1000000" step="1" value={bonus} disabled={!!pendingBonus.current} onChange={e => setBonus(e.target.value)}/></label>
      <button className="outline-button" type="submit" disabled={!pendingBonus.current && !(bonus !== '' && Number.isSafeInteger(bonusNumber) && bonusNumber > 0 && bonusNumber <= 1000000)}>{pendingBonus.current ? 'בדיקה חוזרת של התוספת' : 'הוספת זמן להיום'}</button>
    </fieldset></form>
    <p className="small-note">הספירה פועלת כשהחלון גלוי ובמיקוד. לאחר חמש דקות ללא פעולה מופיעה בקשת המשך, גם בזמן קריאה. סגירה או ניתוק פתאומי עשויים להוסיף עד 15 שניות.</p>
    {error && <p role="alert">{error}</p>}{notice && <p role="status">{notice}</p>}
  </section>;
}

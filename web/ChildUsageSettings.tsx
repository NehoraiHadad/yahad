import React, { useEffect, useRef, useState } from 'react';
import { api, ApiError } from './api';
import { newClientId } from './clientId';
import type { UsageStatus } from '../src/usage';
import { usageMinutes } from './UsageBudget';

export function ChildUsageSettings({ childId, name, disabled }: { childId: string; name: string; disabled: boolean }) {
  const [status, setStatus] = useState<UsageStatus | null>(null), [minutes, setMinutes] = useState(''), [unlimited, setUnlimited] = useState(true);
  const [bonus, setBonus] = useState(''), [zone, setZone] = useState('UTC'), [busy, setBusy] = useState(false), [error, setError] = useState(''), [notice, setNotice] = useState('');
  const mounted = useRef(true), generation = useRef(0), edited = useRef(false), working = useRef(false);
  const pendingBonus = useRef<{ requestId: string; day: string; minutes: number } | null>(null);
  const path = '/parent/usage/' + encodeURIComponent(childId);
  const accept = (value: UsageStatus) => {
    setStatus(value);
    if (!edited.current) { setUnlimited(value.minutes === null); setMinutes(String(value.minutes ?? '')); setZone(value.timezone); }
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
    if (disabled || working.current || !status) return;
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
    {status && <p className="usage-summary" role="status">היום ({status.day}): נוצלו {usageMinutes(status.usedMs)} דק׳ · {status.remainingMs === null ? 'ללא הגבלה' : `נותרו ${usageMinutes(status.remainingMs)} דק׳`} · תוספת להיום: {usageMinutes(status.bonusMs)} דק׳</p>}
    <form onSubmit={e => { e.preventDefault(); if (unlimited || valid) void act(() => api<UsageStatus>(path, 'PUT', { minutes: unlimited ? null : number }), `המכסה של ${name} נשמרה.`); }}>
      <fieldset disabled={locked}><label><input type="checkbox" checked={unlimited} onChange={e => { edited.current = true; setUnlimited(e.target.checked); }}/> ללא הגבלת זמן</label>
        <label>דקות ליום<input type="number" inputMode="numeric" min="0" max="1000000" step="1" value={minutes} disabled={unlimited} onChange={e => { edited.current = true; setMinutes(e.target.value); }}/></label>
        <button className="outline-button" type="submit" disabled={!unlimited && !valid}>שמירת מכסה</button></fieldset>
    </form>
    <form onSubmit={e => {
      e.preventDefault(); if (!status || disabled || working.current) return;
      if (!pendingBonus.current && bonus !== '' && Number.isSafeInteger(bonusNumber) && bonusNumber > 0 && bonusNumber <= 1000000)
        pendingBonus.current = { requestId: newClientId(), day: status.day, minutes: bonusNumber };
      const request = pendingBonus.current;
      if (request) void act(async () => {
        try {
          const value = await api<UsageStatus>(path + '/bonus', 'POST', request);
          pendingBonus.current = null; if (mounted.current) setBonus(''); return value;
        } catch (error) {
          if (error instanceof ApiError && ['BONUS_DAY_CHANGED', 'BONUS_CONFLICT', 'INVALID_INPUT'].includes(error.code)) pendingBonus.current = null;
          throw error;
        }
      }, value => value.day === request.day ? `נוסף זמן להיום עבור ${name}.` : 'התוספת שויכה ליום הקודם. לא נוסף זמן להיום.');
    }}><fieldset disabled={locked}>
      <label>תוספת דקות להיום בלבד<input type="number" inputMode="numeric" min="1" max="1000000" step="1" value={bonus} disabled={!!pendingBonus.current} onChange={e => setBonus(e.target.value)}/></label>
      <button className="outline-button" type="submit" disabled={!pendingBonus.current && !(bonus !== '' && Number.isSafeInteger(bonusNumber) && bonusNumber > 0 && bonusNumber <= 1000000)}>{pendingBonus.current ? 'בדיקה חוזרת של התוספת' : 'הוספת זמן להיום'}</button>
    </fieldset></form>
    <p>איפוס בחצות לפי אזור הזמן המשפחתי: <bdi>{status?.timezone || zone}</bdi>. אזור הזמן קבוע לאחר תחילת המדידה או מתן תוספת.</p>
    {status && status.usedMs === 0 && status.bonusMs === 0 && <form onSubmit={e => { e.preventDefault(); void act(async () => { await api('/parent/usage-timezone', 'PUT', { timezone: zone }); return api<UsageStatus>(path); }, 'אזור הזמן המשפחתי נשמר.'); }}>
      <fieldset disabled={locked}><label>אזור זמן משפחתי (IANA)<input dir="ltr" value={zone} placeholder="Asia/Jerusalem" onChange={e => { edited.current = true; setZone(e.target.value); }}/></label><button className="text-button" type="submit">שמירת אזור זמן</button></fieldset>
    </form>}
    <p className="small-note">הספירה פועלת כשהחלון גלוי ובמיקוד. לאחר חמש דקות ללא פעולה מופיעה בקשת המשך, גם בזמן קריאה. סגירה או ניתוק פתאומי עשויים להוסיף עד 15 שניות.</p>
    {error && <p role="alert">{error}</p>}{notice && <p role="status">{notice}</p>}
  </section>;
}

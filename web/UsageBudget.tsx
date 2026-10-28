import React, { useEffect, useRef } from 'react';
import type { UsageStatus } from '../src/usage';
import type { UsageReason } from './useUsageBudget';
import { useDialogFocus } from './useDialogFocus';

export const usageMinutes = (ms: number) => Math.round(ms / 6000) / 10;
export function UsageIndicator({ status }: { status: UsageStatus | null }) {
  if (!status) return null;
  return <p className={'usage-indicator ' + (status.remainingMs !== null && status.remainingMs <= 120000 ? 'usage-warning' : '')} role="status">
    {status.remainingMs === null ? 'ללא הגבלת זמן' : `נשארו ${Math.ceil(status.remainingMs / 60000)} דק׳ להיום`}
    <small>נעשה שימוש ב־{usageMinutes(status.usedMs)} דק׳ · {status.timezone}</small>
  </p>;
}
export function UsageOverlay({ reason, status, resume, logout }: {
  reason: UsageReason; status: UsageStatus | null; resume: () => void; logout: () => Promise<void>;
}) {
  const ref = useRef<HTMLElement>(null);
  useDialogFocus(ref, !!reason);
  useEffect(() => {
    if (reason) for (const media of document.querySelectorAll<HTMLMediaElement>('video,audio')) media.pause();
  }, [reason]);
  if (!reason) return null;
  return <div className="usage-backdrop" dir="rtl"><section ref={ref} className="usage-overlay" role="dialog" aria-modal="true" aria-labelledby="usage-title">
    <h2 id="usage-title">{reason === 'exhausted' ? 'זמן השימוש להיום הסתיים' : reason === 'idle' ? 'עדיין כאן?' : reason === 'offline' ? 'ממתינים לחיבור' : 'הזמן מושהה'}</h2>
    <p>{reason === 'exhausted' ? 'אפשר לבקש מההורה להוסיף זמן להיום. השיחות ייפתחו אחרי עדכון המכסה.' : reason === 'idle' ? 'אחרי חמש דקות ללא פעולה עוצרים את הספירה. לחצו להמשך הקריאה והשיחה.' : 'נבדוק את המכסה וההרשאות לפני פתיחת השיחות מחדש.'}</p>
    {status && <UsageIndicator status={status}/>}
    {reason === 'exhausted' && status && <p>המכסה מתחדשת בחצות לפי {status.timezone}.</p>}
    <p className="muted">טיוטה מקומית נשמרת בחלון הזה בלבד. החזרה לשיחה אינה שולחת אותה.</p>
    <button className="primary" onClick={resume}>בדיקה והמשך</button>
    <button className="text-button" onClick={() => void logout()}>יציאה מהחשבון</button>
  </section></div>;
}

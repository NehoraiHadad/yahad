import React, { useEffect, useRef, useState } from 'react';
import type { UsageStatus } from '../src/usage';
import type { UsageReason } from './useUsageBudget';
import { useDialogFocus } from './useDialogFocus';
import { createUsageBadgeClock, formatUsageTime } from './usageBadgeClock';

export const usageMinutes = (ms: number) => Math.round(ms / 6000) / 10;
export function UsageIndicator({ status, paused = true }: { status: UsageStatus | null; paused?: boolean }) {
  const clock = useRef<ReturnType<typeof createUsageBadgeClock> | null>(null);
  if (!clock.current) clock.current = createUsageBadgeClock();
  const [, tick] = useState(0);
  useEffect(() => {
    if (paused || !status || status.remainingMs === null || status.exhausted) return;
    const timer = setInterval(() => tick(n => n + 1), 1000);
    return () => clearInterval(timer);
  }, [paused, status?.remainingMs === null, status?.exhausted]);
  if (!status) return null;
  const remaining = clock.current.update(status, !paused && !status.exhausted, performance.now());
  const warning = remaining !== null && remaining > 0 && remaining <= 120000;
  const announcement = !paused && warning ? remaining <= 60000 ? 'נותרה פחות מדקת שימוש.' : 'נותרו פחות משתי דקות שימוש.' : '';
  return <><p className={'usage-indicator ' + (warning || remaining === 0 ? 'usage-warning' : '')} role="timer" aria-live="off" aria-label={paused ? 'זמן שימוש מושהה בחלון הזה' : 'יתרת זמן השימוש'}>
    <span>{remaining === null ? 'ללא הגבלת זמן' : <>זמן נותר <bdi className="usage-time">{formatUsageTime(remaining)}</bdi></>}</span>
    <small>{paused ? 'מושהה בחלון הזה' : status.pendingTimezone ? 'עד איפוס המכסה' : 'להיום'}</small>
    {!status.timezoneConfirmed && <small>המדידה תתחיל לאחר בחירת אזור זמן בידי ההורה.</small>}
  </p><span className="sr-only" role="status" aria-live="polite">{announcement}</span></>;
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
    <h2 id="usage-title">{reason === 'setup' ? 'נדרשת בחירת אזור זמן' : reason === 'exhausted' ? 'זמן השימוש להיום הסתיים' : reason === 'idle' ? 'עדיין כאן?' : reason === 'offline' ? 'ממתינים לחיבור' : 'הזמן מושהה'}</h2>
    <p>{reason === 'exhausted' ? 'אפשר לבקש מההורה להוסיף זמן להיום. השיחות ייפתחו אחרי עדכון המכסה.' : reason === 'idle' ? 'אחרי חמש דקות ללא פעולה עוצרים את הספירה. לחצו להמשך הקריאה והשיחה.' : 'נבדוק את המכסה וההרשאות לפני פתיחת השיחות מחדש.'}</p>
    {status && <UsageIndicator status={status}/>}
    {reason === 'setup' && <p>ההורה צריך לבחור אזור זמן למשפחה כדי שהמכסה תתחדש בזמן המתאים.</p>}
    {reason === 'exhausted' && status && <p>{status.pendingTimezone ? 'המחזור הנוכחי יסתיים במעבר המתוזמן לאזור הזמן החדש.' : `המכסה מתחדשת בחצות לפי ${status.timezone}.`}</p>}
    <p className="muted">טיוטה מקומית נשמרת בחלון הזה בלבד. החזרה לשיחה אינה שולחת אותה.</p>
    <button className="primary" onClick={resume}>בדיקה והמשך</button>
    <button className="text-button" onClick={() => void logout()}>יציאה מהחשבון</button>
  </section></div>;
}

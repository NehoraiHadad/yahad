import React, { useEffect, useRef, useState } from 'react';
import type { FamilyChild } from './FamilyProgress';
import type { ChildAccessChange } from './childManagementState';
import { useDialogFocus } from './useDialogFocus';

export function ChildAccessSettings({ child, disabled, stale, onChange }: {
  child: FamilyChild; disabled: boolean; stale: boolean; onChange: (change: ChildAccessChange) => Promise<boolean>;
}) {
  const [confirmation, setConfirmation] = useState<ChildAccessChange | null>(null);
  const [saving, setSaving] = useState(false), [result, setResult] = useState<ChildAccessChange | null>(null);
  const working = useRef(false), mounted = useRef(true), dialog = useRef<HTMLDivElement>(null);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  useDialogFocus(dialog, !!confirmation);
  const changed = !!confirmation && (child.id !== confirmation.childId ||
    !!child.paused === confirmation.paused || (child.accessRevision ?? 0) !== confirmation.revision);
  const close = () => { if (!working.current) setConfirmation(null); };
  const submit = async () => {
    if (!mounted.current || working.current || !confirmation || disabled || changed) return;
    const request = confirmation;
    working.current = true; setSaving(true); setResult(null);
    try { await onChange(request); }
    catch { /* Parent refreshes authoritative state even after a lost response. */ }
    finally {
      working.current = false;
      if (mounted.current) { setSaving(false); setConfirmation(null); setResult(request); }
    }
  };
  const confirmed = result && !stale && result.childId === child.id && !!child.paused === result.paused &&
    (child.accessRevision ?? 0) === result.revision + 1;
  return <section className="child-access-settings" aria-label={`גישה לחשבון של ${child.name}`} aria-busy={saving}>
    <h4>גישה לחשבון של <bdi>{child.name}</bdi></h4>
    <p role="status">{stale ? 'ממתינים לאימות מצב הגישה מול השרת.' : child.paused ? 'הגישה מושהית. אי אפשר להיכנס או להשתמש בחשבון.' : 'הגישה פעילה, בהתאם להגדרות זמן השימוש.'}</p>
    <p>השהיה מנתקת את מכשירי הילד ושומרת את הסיסמה, השיחות והגדרות הזמן. הזמן שנותר נשמר; מכסת היום ממשיכה להתחדש כרגיל.</p>
    <button className="outline-button" type="button" disabled={disabled || saving} onClick={() => {
      if (working.current || disabled) return;
      setResult(null); setConfirmation({ childId: child.id, paused: !child.paused, revision: child.accessRevision ?? 0 });
    }}>{child.paused ? 'הפעלת הגישה מחדש' : 'השהיית הגישה'}</button>
    {result && <p role={confirmed || stale ? 'status' : 'alert'}>{stale ? 'לא ניתן לאמת את הפעולה. ממתינים לרענון לפני שינוי נוסף.' : confirmed
      ? result.paused ? 'הגישה הושהתה וכל המכשירים נותקו.' : 'הגישה הופעלה מחדש. צריך להיכנס שוב; חיבורים ישנים נשארים מנותקים.'
      : 'לא התקבל אישור לפעולה. מוצג המצב האחרון מהשרת; בדקו אותו לפני ניסיון נוסף.'}</p>}
    {confirmation && <div className="modal-backdrop"><div className="modal" ref={dialog} role="dialog" aria-modal="true"
      aria-labelledby="child-access-title" aria-describedby="child-access-description" aria-busy={saving}
      onKeyDown={e => { if (e.key === 'Escape') close(); }}>
      <h2 id="child-access-title">{confirmation.paused ? 'להשהות' : 'להפעיל מחדש'} את הגישה של <bdi>{child.name}</bdi>?</h2>
      <p id="child-access-description">{confirmation.paused
        ? 'כל המכשירים המחוברים ינותקו ולא יהיה אפשר להיכנס עד להפעלה מחדש. הסיסמה, הרשאות השיחות והגדרות הזמן יישמרו. תוכן שכבר הורד למכשיר אינו נמחק מרחוק.'
        : 'אפשר יהיה להיכנס מחדש עם אותה סיסמה ובהתאם לזמן שנותר. מכשירים שהתנתקו לא יתחברו אוטומטית.'}</p>
      {changed && <p role="alert">מצב הגישה השתנה. סגרו את החלון ובדקו את המצב המעודכן.</p>}
      <div className="child-account-actions">
        <button className="outline-button" type="button" disabled={saving} onClick={close}>ביטול</button>
        <button className="primary" type="button" disabled={disabled || saving || changed} onClick={() => void submit()}>
          {saving ? 'שומרים…' : confirmation.paused ? 'אישור השהיה' : 'אישור הפעלה מחדש'}</button>
      </div>
    </div></div>}
  </section>;
}

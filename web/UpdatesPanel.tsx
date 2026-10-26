import React, { useState } from 'react';
import { Download, RefreshCw } from 'lucide-react';
import { api } from './api';
import type { updateStatus } from '../src/updates';
type Status = Awaited<ReturnType<typeof updateStatus>>;
const phases: Record<string,string> = { rolling_back:'משחזרים את הגרסה הקודמת', recovery_required:'נדרשת בדיקת בעל המכשיר — העדכונים נעצרו', rollback_pending_health:'הגרסה הקודמת הוחזרה; ממתינים לבדיקת תקינות', idle:'אין עדכון ממתין', available:'גרסה חדשה זמינה', preparing:'מכינים את הגרסה',
  stopping:'עוצרים את השירות לעדכון', backing_up:'יוצרים גיבוי מוצפן', activating:'מחליפים גרסה', checking_health:'בודקים שהשירות עלה',
  healthy:'הגרסה עלתה בהצלחה', rolled_back:'הגרסה הקודמת הוחזרה', failed:'העדכון לא הושלם', incompatible:'העדכון דורש התקנה מתוכננת' };
export function UpdatesPanel() {
  const [open,setOpen] = useState(false), [data,setData] = useState<Status | null>(null),
    [busy,setBusy] = useState(false), [error,setError] = useState(''), [notice,setNotice] = useState(''), [confirm,setConfirm] = useState(false);
  async function load() { setBusy(true); setError(''); try { setData(await api('/parent/updates')); }
    catch(e) { setError((e as Error).message); } finally { setBusy(false); } }
  async function request(action: 'check' | 'install') {
    setBusy(true); setError(''); try { const r = await api('/parent/updates','POST',{action});
      setNotice(r.state === 'pending' ? 'כבר ממתינה בקשה. שירות העדכון יאסוף אותה בבדיקה הקרובה.' : 'הבקשה נשמרה. שירות העדכון יאסוף אותה בתוך כחמש דקות; אפשר לרענן את המצב.');
      setConfirm(false); await load();
    } catch(e) { setError((e as Error).message); } finally { setBusy(false); }
  }
  return <section className="parent-card diagnostics-card">
    <button className="diagnostics-toggle" aria-expanded={open} onClick={() => {setOpen(!open); if(!open) void load();}}>
      <Download size={20}/><span><b>עדכוני מערכת</b><small>גרסאות חדשות והתקנה מבוקרת</small></span><span>{open?'סגירה':'פתיחה'}</span>
    </button>
    {open && <div className="diagnostics-body">
      {error && <p className="error" role="alert">{error}</p>}
      {busy && !data && <p role="status">טוענים…</p>}
      {data && (!data.configured ? <p>עדכונים מרחוק עדיין לא הוגדרו לחשבון הזה. מנהל ה־Pi צריך להגדיר פעם אחת ריפו, מפתח אימות וחשבון הורה מורשה. אין כאן חיבור פעיל ל־GitHub.</p> : <>
        <p><b>{phases[data.phase]}</b> · גרסה נוכחית: <span dir="ltr">{data.currentVersion || 'לא זוהתה'}</span>{data.latestVersion && <> · גרסה אחרונה: <span dir="ltr">{data.latestVersion}</span></>}</p>
        <p>{data.enabled ? data.mode === 'auto' ? 'התקנה אוטומטית הופעלה בהגדרות מנהל ה־Pi.' : 'בדיקה מתוזמנת פעילה; התקנה דורשת לחיצה כאן.' : 'שירות העדכון כבוי.'}</p>
        {data.checkedAt && <p className="diagnostics-caption">בדיקה אחרונה: {new Date(data.checkedAt).toLocaleString('he-IL')}</p>}
        {(data.stale || data.error) && <p className="diagnostics-notice">{data.stale ? 'לא התקבל מצב עדכני משירות העדכון. ' : ''}{data.error ? `הפעולה נעצרה בשלב ${data.error}. יש לבדוק את שירות העדכון ב־Pi.` : ''}</p>}
        <p className="diagnostics-caption">כל חבילה נבדקת מול מפתח חתימה שהותקן מראש. לפני החלפה נוצר גיבוי מוצפן. העדכון מנתק זמנית את הצ׳אט; אם בדיקת העלייה נכשלת, נעשה ניסיון להחזיר את הקוד הקודם.</p>
        <div className="diagnostics-actions"><button className="outline-button" disabled={busy || !data.enabled || data.stale} onClick={() => void request('check')}><RefreshCw size={16}/>בדיקה ב־GitHub</button>
          {data.phase === 'available' && <button className="outline-button" disabled={busy || data.stale || !data.enabled} onClick={() => setConfirm(true)}><Download size={16}/>התקנת העדכון</button>}
        </div>
        {confirm && <div className="diagnostics-notice"><p>להתקין את הגרסה הזמינה? השיחות יתנתקו לזמן קצר במהלך הגיבוי וההפעלה מחדש.</p><button className="outline-button" disabled={busy} onClick={() => void request('install')}>התקנה</button> <button className="text-button" onClick={() => setConfirm(false)}>ביטול</button></div>}
      </>)}
      {notice && <p role="status">{notice}</p>}
      <div className="diagnostics-actions"><button className="text-button" disabled={busy} onClick={() => void load()}><RefreshCw size={16}/>רענון מצב</button></div>
    </div>}
  </section>;
}

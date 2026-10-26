import React, { useState } from 'react';
import { Activity, Download, RefreshCw, Trash2 } from 'lucide-react';
import { api } from './api';
import type { DiagnosticsReport, Operation } from '../src/diagnostics';

const labels: Record<Operation, string> = { db_read: 'קריאה מהמסד', db_write: 'שמירה במסד',
  db_commit: 'סיום עסקה במסד', media_read: 'קריאת קובץ מדיה', media_write: 'כתיבת קובץ מדיה',
  api: 'מענה של השרת', audio_encode: 'עיבוד הקלטה', connector: 'חיבור WhatsApp', media_download: 'הורדת מדיה מהחיבור' };
const errors: Record<string, string> = { io: 'קלט/פלט', no_space: 'אין מקום', readonly: 'אחסון לקריאה בלבד',
  busy: 'אחסון תפוס', permission: 'הרשאות קובץ', internal: 'כשל תפעולי', integrity: 'קובץ אבחון לא תקין' };
const bytes = (n: number | null) => n === null ? 'לא זמין' : n >= 1024 ** 3 ? (n / 1024 ** 3).toFixed(1) + ' GiB' : (n / 1024 ** 2).toFixed(1) + ' MiB';
const ms = (n: number) => n < 1 ? '<1 ms' : Math.round(n).toLocaleString('he-IL') + ' ms';

export function DiagnosticsPanel() {
  const [open, setOpen] = useState(false), [data, setData] = useState<DiagnosticsReport | null>(null),
    [busy, setBusy] = useState(false), [error, setError] = useState(''), [confirmClear, setConfirmClear] = useState(false);
  async function refresh() {
    setBusy(true); setError('');
    try { setData(await api('/parent/diagnostics')); }
    catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  }
  const storageSlow = data?.metrics.some(x => ['db_write', 'db_commit', 'media_write'].includes(x.operation) && x.count >= 20 && (x.p95UpperMs === null || x.p95UpperMs >= 250));
  const diskLow = data?.storage.freeBytes !== null && data?.storage.freeBytes !== undefined && data.storage.freeBytes < 512 * 1024 ** 2;
  return <section className="parent-card diagnostics-card">
    <button className="diagnostics-toggle" aria-expanded={open} onClick={() => {
      setOpen(!open); if (!open) void refresh();
    }}><Activity size={20} /><span><b>מצב המערכת</b><small>אחסון, ביצועים ועדות לתקלות</small></span><span>{open ? 'סגירה' : 'פתיחה'}</span></button>
    {open && <div className="diagnostics-body">
      <p>מדדים תפעוליים מצטברים בלבד. אין כאן תוכן שיחות, שמות קבוצות או יומן פעילות של הילדה.</p>
      {error && <p className="error" role="alert">{error}</p>}
      {busy && !data && <p role="status">טוענים את מצב המערכת…</p>}
      {data && <>
        {!data.enabled && <p className="diagnostics-notice">איסוף מדדי פעולות כבוי בהגדרות השרת. נתוני המשאבים הנוכחיים עדיין זמינים.</p>}
        {(diskLow || storageSlow || data.persistenceError) && <p className="diagnostics-notice" role="status">
          {diskLow && 'המקום הפנוי נמוך מ־512 MiB. '}
          {storageSlow && 'נמדדו פעולות אחסון ממושכות; כדאי לבדוק את האחסון לצד עומס השרת. '}
          {data.persistenceError && `שמירת האבחון אינה תקינה: ${errors[data.persistenceError]}. המדדים בזיכרון זמינים, אך ייתכן שלא ישרדו אתחול. `}
          המדדים אינם מאבחנים לבדם כרטיס תקול.
        </p>}
        <div className="diagnostics-stats">
          <div><small>מקום פנוי באחסון</small><b dir="ltr">{bytes(data.storage.freeBytes)}</b></div>
          <div><small>זיכרון תהליך השרת</small><b dir="ltr">{bytes(data.process.rssBytes)}</b></div>
          <div><small>עומס CPU של התהליך</small><b dir="ltr">{data.process.sample ? data.process.sample.cpuPercent.toFixed(1) + '%' : 'ממתינים לדגימה'}</b></div>
          <div><small>עיכוב לולאת השרת · P95</small><b dir="ltr">{data.process.sample?.eventLoopP95Ms != null ? ms(data.process.sample.eventLoopP95Ms) : 'ממתינים לדגימה'}</b></div>
        </div>
        <p className="diagnostics-caption">מסד: <span dir="ltr">{bytes(data.storage.databaseBytes)}</span> · יומן WAL: <span dir="ltr">{bytes(data.storage.walBytes)}</span>. משאבי השרת משותפים לתהליך כולו; מדדי הפעולות בהמשך שייכים למשפחה המחוברת בלבד.</p>
        {data.metrics.length ? <div className="diagnostics-table-wrap"><table>
          <caption>פעולות שנמדדו ב־{data.retentionDays} הימים האחרונים</caption>
          <thead><tr><th>פעולה</th><th>דגימות</th><th>P95 עד</th><th>הארוכה ביותר</th><th>כשלים</th></tr></thead>
          <tbody>{data.metrics.map(m => <tr key={m.operation}><th>{labels[m.operation]}</th><td>{m.count.toLocaleString('he-IL')}</td><td dir="ltr">{m.p95UpperMs === null ? '>30,000 ms' : ms(m.p95UpperMs)}</td><td dir="ltr">{ms(m.maxMs)}</td><td>{Object.entries(m.errors).length ? Object.entries(m.errors).map(([k, n]) => `${errors[k]}: ${n}`).join(' · ') : '—'}</td></tr>)}</tbody>
        </table></div> : <p>עדיין אין מספיק נתונים. המדדים ייאספו במהלך שימוש רגיל.</p>}
        {data.metrics.filter(m => m.mibPerSecond !== null && ['media_read','media_write'].includes(m.operation)).map(m =>
          <p key={m.operation} className="diagnostics-caption">{labels[m.operation]}: <b dir="ltr">{m.mibPerSecond!.toFixed(1)} MiB/s</b> בזמן הפעולות שנמדדו. מטמון מערכת ההפעלה כלול; זו אינה מהירות הכרטיס הגולמית.</p>)}
        <p className="diagnostics-caption">P95 הוא גבול עליון מקורב לפי טווחי זמן. CPU נמדד ביחס לליבה אחת ועשוי לעבור 100%; עיכוב הלולאה נדגם ברזולוציה של 20 ms. מדדי CPU ולולאה מתעדכנים כל 15 שניות ונמחקים באתחול.</p>
        <p className="diagnostics-caption">שמירה מוצפנת של סיכומים לפי שעה, עד {data.retentionDays} ימים, אחת ל־{Math.round(data.flushSeconds / 60)} דקות. כיבוי פתאומי עלול לאבד את הדגימות שטרם נשמרו. {data.lastSavedAt ? `שמירה אחרונה: ${new Date(data.lastSavedAt).toLocaleString('he-IL')}.` : 'טרם נשמר סיכום לדיסק.'}</p>
      </>}
      <div className="diagnostics-actions">
        <button className="outline-button" disabled={busy} onClick={() => void refresh()}><RefreshCw size={16} />רענון</button>
        <a className="outline-button" href="/api/parent/diagnostics?download=1" download><Download size={16} />הורדת דוח</a>
        <button className="text-button" onClick={() => setConfirmClear(!confirmClear)}><Trash2 size={16} />מחיקת מדדי המשפחה</button>
      </div>
      {confirmClear && <div className="diagnostics-notice"><p>למחוק את הסיכומים שנאספו למשפחה הזו? מדדים חדשים ימשיכו להיאסף אם האיסוף פעיל.</p><button className="outline-button" disabled={busy} onClick={async () => {
        setBusy(true); try { await api('/parent/diagnostics', 'DELETE'); setConfirmClear(false); await refresh(); }
        catch (e) { setError((e as Error).message); } finally { setBusy(false); }
      }}>מחיקה</button></div>}
    </div>}
  </section>;
}

import React, { useEffect, useRef, useState } from 'react';
import { CheckCircle2, Clock3, Download, LoaderCircle, RefreshCw, TriangleAlert } from 'lucide-react';
import { csrf } from './api';
import { createUpdatePoller, reconcileUpdateRequest, updateActionsDisabled, updateProgress, type ConfiguredUpdate,
  type LocalUpdateRequest, type UpdateAction, type UpdateStatus } from './updateProgress';

export class RejectedUpdateRequest extends Error {}
export class UpdateAccessError extends Error {
  constructor(readonly status: 401 | 403) {
    super(status === 401 ? 'החיבור הסתיים. צריך להיכנס שוב.' : 'אין הרשאה לצפייה במצב העדכונים. המידע הקודם הוסתר; יש לבדוק את הרשאת החשבון במחשב השרת.');
  }
}

function expiredSession() { window.dispatchEvent(new Event('session-expired')); }
export async function readUpdateStatus(signal: AbortSignal): Promise<UpdateStatus> {
  const response = await fetch('/api/parent/updates', { credentials: 'same-origin', cache: 'no-store', signal });
  if (response.status === 401) expiredSession();
  if (response.status === 401 || response.status === 403) throw new UpdateAccessError(response.status);
  if (!response.ok) throw new Error('Status unavailable');
  return response.json();
}
export async function submitUpdateRequest(action: UpdateAction, mode?: 'auto' | 'notify'): Promise<{ state: 'queued' | 'pending' }> {
  const controller = new AbortController(), timeout = setTimeout(() => controller.abort(), 15_000);
  try {
    const response = await fetch('/api/parent/updates', { method: 'POST', credentials: 'same-origin', signal: controller.signal,
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
      body: JSON.stringify({ action, ...(action === 'set_mode' ? { mode } : {}) }) });
    if (response.status === 401) expiredSession();
    if (!response.ok) {
      // A lost response or server error can occur after durable queue publication.
      // Only a definite client rejection is safe to present as "not accepted".
      if (response.status >= 400 && response.status < 500 && response.status !== 408)
        throw new RejectedUpdateRequest('השרת לא אישר את הבקשה. רעננו את המצב ובדקו שהעדכונים זמינים ושהחשבון מורשה.');
      throw new Error('Request outcome unknown');
    }
    const result = await response.json();
    if (!['queued', 'pending'].includes(result.state)) throw new Error('Request outcome unknown');
    return result;
  } finally { clearTimeout(timeout); }
}

export function UpdateProgressStatus({ data, local = null, reconnecting = false }: {
  data: ConfiguredUpdate; local?: LocalUpdateRequest | null; reconnecting?: boolean;
}) {
  const progress = updateProgress(data, local);
  const Icon = reconnecting ? RefreshCw : progress.kind === 'queued' ? Clock3 : progress.kind === 'running'
    ? LoaderCircle : progress.kind === 'success' ? CheckCircle2 : progress.kind === 'failure' ? TriangleAlert : Download;
  return <div className="update-mode-status" role="status" aria-live="polite" aria-atomic="true">
    <b><Icon size={18} aria-hidden="true" /> {progress.title}</b>
    {progress.action && <p>הפעולה שהתבקשה: {progress.action}</p>}
    <p>{progress.detail}</p>
    {reconnecting && <p>החיבור או דיווח המצב אינם זמינים כרגע. בזמן הפעלה מחדש זה עשוי להיות זמני. המצב שלמעלה הוא הדיווח האחרון, לא תוצאת כשל; ננסה שוב אוטומטית, ואין צורך ללחוץ שוב.</p>}
    {data.updatedAt && <small>דיווח אחרון מהשירות: {new Date(data.updatedAt).toLocaleString('he-IL')}</small>}
  </div>;
}

export function UpdatesPanel() {
  const [open, setOpen] = useState(false), [data, setData] = useState<UpdateStatus | null>(null);
  const [checked, setChecked] = useState(false);
  const [busy, setBusy] = useState(false), [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState(''), [notice, setNotice] = useState('');
  const [reconnecting, setReconnecting] = useState(false), [confirm, setConfirm] = useState(false);
  const [accessDenied, setAccessDenied] = useState(false);
  const [choice, setChoice] = useState<'auto' | 'notify' | null>(null);
  const [local, setLocal] = useState<LocalUpdateRequest | null>(null);
  const mounted = useRef(true), sending = useRef(false), dataRef = useRef(data), localRef = useRef(local);
  const accessDeniedRef = useRef(false);
  const poller = useRef<ReturnType<typeof createUpdatePoller<UpdateStatus>> | null>(null);
  function rememberRequest(value: LocalUpdateRequest | null) { localRef.current = value; setLocal(value); }
  const configured = data?.configured ? data : null;
  const progress = configured ? updateProgress(configured, local) : null;
  const active = !!local || !!progress?.active;
  const activeRef = useRef(active); activeRef.current = active;
  // Read once after a page reload too: an already-running worker must remain
  // visible in the collapsed heading without requiring another user click.
  const shouldPoll = !accessDenied && (!checked || open || active || reconnecting);

  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  useEffect(() => {
    if (!shouldPoll) return;
    const controller = createUpdatePoller({ read: readUpdateStatus, active: () => activeRef.current,
      onValue(value) {
        setRefreshing(false); setChecked(true);
        // During restart a temporarily unreadable status must not replace the
        // last real worker report with "not configured", or clear an operation.
        if (!value.configured && dataRef.current?.configured) { setReconnecting(true); return; }
        setReconnecting(false); dataRef.current = value; setData(value);
        const following = reconcileUpdateRequest(value, localRef.current);
        if (following !== localRef.current) rememberRequest(following);
      },
      onError(failure) {
        setRefreshing(false);
        if (failure instanceof UpdateAccessError) {
          accessDeniedRef.current = true; setAccessDenied(true); setChecked(true);
          dataRef.current = null; setData(null); rememberRequest(null);
          setReconnecting(false); setError(failure.message); setNotice(''); setChoice(null); setConfirm(false);
        } else setReconnecting(true);
      },
    });
    poller.current = controller; controller.refresh();
    return () => { controller.stop(); if (poller.current === controller) poller.current = null; };
  }, [shouldPoll]);

  function refresh() {
    setRefreshing(true);
    if (accessDenied) { accessDeniedRef.current = false; setAccessDenied(false); setError(''); }
    else poller.current?.refresh();
  }
  async function request(action: UpdateAction, mode?: 'auto' | 'notify') {
    if (sending.current || updateActionsDisabled(configured, localRef.current, reconnecting, busy) || !configured) return;
    sending.current = true; setBusy(true); setError(''); setNotice('');
    rememberRequest({ action, mode, state: 'sending', previousRequestId: configured.lastRequest?.id,
      previousModeId: configured.lastModeRequest?.id });
    try {
      const result = await submitUpdateRequest(action, mode);
      if (!mounted.current || accessDeniedRef.current) return;
      if (localRef.current) rememberRequest({ ...localRef.current, state: 'queued' });
      if (result.state === 'pending') setNotice('כבר יש בקשה בטיפול. לא נשלחה בקשה נוספת; מציגים את המצב שמדווח השירות.');
      setConfirm(false); setChoice(null); poller.current?.refresh();
    } catch (e) {
      if (!mounted.current || accessDeniedRef.current) return;
      if (e instanceof RejectedUpdateRequest) { rememberRequest(null); setError(e.message); }
      else {
        if (localRef.current) rememberRequest({ ...localRef.current, state: 'uncertain' });
        setReconnecting(true);
      }
      poller.current?.refresh();
    } finally { sending.current = false; if (mounted.current) setBusy(false); }
  }

  const pendingMode = configured?.queuedMode || configured?.lastModeRequest?.state === 'pending';
  const disabled = updateActionsDisabled(configured, local, reconnecting, busy);
  return <section className="parent-card diagnostics-card">
    <button className="diagnostics-toggle" aria-expanded={open} onClick={() => { setOpen(!open); if (!open && !accessDenied) { setRefreshing(true); poller.current?.refresh(); } }}>
      <Download size={20}/><span><b>עדכוני האפליקציה</b><small aria-live="polite">{accessDenied ? 'אין הרשאה לצפייה במצב העדכונים' : reconnecting ? 'מנסים להתחבר מחדש — נשמר הדיווח האחרון' : progress?.title || 'גרסאות מ־GitHub, גיבוי ושליטה שלכם'}</small></span><span>{open ? 'סגירה' : 'פתיחה'}</span>
    </button>
    {open && <div className="diagnostics-body" aria-busy={busy || refreshing}>
      {error && <p className="error" role="alert">{error}</p>}
      {refreshing && !data && <p role="status">בודקים את המצב…</p>}
      {reconnecting && !configured && !accessDenied && <p role="status" className="diagnostics-notice">לא ניתן לקבל את מצב שירות העדכון כרגע. מנסים שוב אוטומטית; אין עדיין תוצאת הצלחה או כשל.</p>}
      {data && !configured && !reconnecting && <p>רכיב העדכון עדיין לא הוגדר במחשב השרת. המתקין החד־פעמי מגדיר אותו; אין צורך בחשבון GitHub נוסף.</p>}
      {configured && <>
        <UpdateProgressStatus data={configured} local={local} reconnecting={reconnecting}/>
        <p>גרסה נוכחית שדווחה: <span dir="ltr">{configured.currentVersion || 'טרם דווחה'}</span>{configured.latestVersion && <> · גרסה זמינה: <span dir="ltr">{configured.latestVersion}</span></>}</p>
        <div className="update-mode-status"><b>המצב הפעיל כעת</b><p>{!configured.enabled ? 'העדכונים מושהים בהגדרות השרת' : configured.mode === 'auto' ? 'עדכונים אוטומטיים פועלים' : 'הודעה על גרסה חדשה — ההתקנה לבחירתכם'}</p></div>
        <p>לפני התקנה נשמר גיבוי מקומי מוצפן, ואחריה נבדקת תקינות האפליקציה. ייתכן ניתוק קצר בזמן עדכון. ההתקדמות מציגה רק שלבים שדווחו בפועל, ללא אחוזים משוערים.</p>
        {pendingMode && <p role="status" className="diagnostics-notice">השינוי שהתבקש: {(configured.queuedMode?.mode || configured.lastModeRequest?.mode) === 'auto' ? 'עדכונים אוטומטיים' : 'הודעה בלבד'}. המצב הפעיל למעלה יתעדכן לאחר שהשינוי יושלם. אין צורך ללחוץ שוב.</p>}
        {!configured.queuedMode && configured.lastModeRequest?.state === 'applied' && <p role="status">הבחירה האחרונה נשמרה והופעלה.</p>}
        {!configured.queuedMode && configured.lastModeRequest?.state === 'rejected' && <p role="alert">שינוי המצב האחרון לא הוחל. המצב הפעיל למעלה נשמר. רעננו ונסו שוב; אם זה חוזר, נדרשת בדיקה במחשב השרת.</p>}
        {configured.lastModeRequest?.state === 'pending' && configured.lastModeRequest.error && <p role="alert">לא ניתן לשמור את שינוי המצב בגלל תקלה בהגדרת השירות. נדרשת בדיקה במחשב השרת; בדיקות גרסה חדשות עשויות להמתין עד לתיקון.</p>}
        {!configured.modeControlAllowed && <p>שינוי המצב זמין להורה המוגדר כאחראי לעדכונים, לאחר השלמת המתקין במחשב השרת.</p>}
        <button type="button" className="outline-button" disabled={disabled || !configured.modeControlAllowed} onClick={() => setChoice(configured.mode === 'auto' ? 'notify' : 'auto')}>{configured.mode === 'auto' ? 'מעבר להודעה בלבד' : 'הפעלת עדכונים אוטומטיים'}</button>
        {choice && <div className="diagnostics-notice" role="group" aria-label="אישור שינוי מצב העדכונים"><p>{choice === 'auto' ? 'להתקין אוטומטית גרסאות יציבות חדשות? ייתכן ניתוק קצר מהשיחות בזמן התקנה.' : 'לעבור להודעה בלבד? גרסאות חדשות יוצגו כאן, ותבחרו מתי להתקין.'}</p><button className="outline-button" disabled={disabled} onClick={() => void request('set_mode', choice)}>כן, {choice === 'auto' ? 'הפעלת עדכונים אוטומטיים' : 'מעבר להודעה בלבד'}</button><button className="text-button" disabled={busy} onClick={() => setChoice(null)}>ביטול</button></div>}
        {configured.checkedAt && <p className="diagnostics-caption">בדיקת גרסה אחרונה: {new Date(configured.checkedAt).toLocaleString('he-IL')}</p>}
        {configured.stale && <p className="diagnostics-notice">דיווח השירות ישן. ממשיכים לבדוק, אך אין אישור להתקדמות חדשה ואין לשלוח בקשה נוספת כרגע.</p>}
        <div className="diagnostics-actions"><button className="outline-button" disabled={disabled} onClick={() => void request('check')}><RefreshCw size={16}/>{configured.mode === 'auto' ? 'בדיקה ועדכון אוטומטי' : 'בדיקת גרסה חדשה'}</button>{configured.phase === 'available' && <button className="outline-button" disabled={disabled} onClick={() => setConfirm(true)}><Download size={16}/>התקנת הגרסה הזמינה</button>}</div>
        {confirm && <div className="diagnostics-notice"><p>ההתקנה תשמור גיבוי ותנתק לרגע את האפליקציה. להתחיל כעת?</p><button className="outline-button" disabled={disabled} onClick={() => void request('install')}>כן, התקנה עכשיו</button><button className="text-button" disabled={busy} onClick={() => setConfirm(false)}>ביטול</button></div>}
      </>}
      {notice && <p role="status">{notice}</p>}
      <div className="diagnostics-actions"><button className="text-button" disabled={refreshing} onClick={refresh}><RefreshCw size={16}/>{refreshing ? 'מרעננים את המצב…' : accessDenied ? 'בדיקת הרשאה מחדש' : 'רענון המצב'}</button></div>
    </div>}
  </section>;
}

import React, { useEffect, useRef, useState } from 'react';
import { Download, RefreshCw } from 'lucide-react';
import { api } from './api';
import type { updateStatus } from '../src/updates';
type Status = Awaited<ReturnType<typeof updateStatus>>;
const phases: Record<string,string> = {idle:'הכול מעודכן',available:'גרסה חדשה זמינה',preparing:'מכינים את העדכון',stopping:'עוצרים לרגע את האפליקציה',backing_up:'שומרים גיבוי מוצפן',activating:'מתקינים את הגרסה החדשה',checking_health:'בודקים שהאפליקציה תקינה',healthy:'העדכון הושלם בהצלחה',rolled_back:'חזרנו לגרסה הקודמת',failed:'העדכון לא הושלם',incompatible:'הגרסה אינה מתאימה למערכת',rolling_back:'חוזרים לגרסה הקודמת',recovery_required:'נדרשת בדיקה מקומית לפני המשך עדכון',rollback_pending_health:'הגרסה הקודמת ממתינה לבדיקת תקינות'};
export function UpdatesPanel() {
  const [open,setOpen]=useState(false),[data,setData]=useState<Status|null>(null),[busy,setBusy]=useState(false),[error,setError]=useState(''),[notice,setNotice]=useState(''),[confirm,setConfirm]=useState(false),[choice,setChoice]=useState<'auto'|'notify'|null>(null);
  const loading=useRef(false), sending=useRef(false);
  async function load(quiet=false) {
    if(loading.current)return;loading.current=true;if(!quiet)setBusy(true);
    try{setData(await api('/parent/updates'));setError('');}catch(e){setError((e as Error).message);}finally{loading.current=false;if(!quiet)setBusy(false);}
  }
  useEffect(()=>{if(!open)return;const timer=setInterval(()=>void load(true),3000);return()=>clearInterval(timer);},[open]);
  async function request(action:'check'|'install'|'set_mode',mode?:'auto'|'notify') {
    if(sending.current)return;sending.current=true;setBusy(true);setError('');
    try{const result=await api('/parent/updates','POST',{action,...(action==='set_mode'?{mode}:{})});setNotice(result.state==='pending'?'בקשה קודמת כבר ממתינה לטיפול. הבקשה החדשה לא נוספה; רעננו לאחר השלמתה.':action==='set_mode'?'הבקשה נשמרה. מחילים את הבחירה; המצב הפעיל מוצג בנפרד.':'הבקשה נשמרה. ההתקדמות תופיע כאן כשהבדיקה תתחיל.');setConfirm(false);setChoice(null);await load(true);}
    catch(e){setError((e as Error).message);}finally{sending.current=false;setBusy(false);}
  }
  const configured=data?.configured?data:null;
  const pending=configured?.queuedMode||configured?.lastModeRequest?.state==='pending';
  const disabled=busy||!configured?.enabled||configured?.stale||!!pending;
  return <section className="parent-card diagnostics-card">
    <button className="diagnostics-toggle" aria-expanded={open} onClick={()=>{setOpen(!open);if(!open)void load();}}><Download size={20}/><span><b>עדכוני האפליקציה</b><small>גרסאות מ־GitHub, גיבוי ושליטה שלכם</small></span><span>{open?'סגירה':'פתיחה'}</span></button>
    {open&&<div className="diagnostics-body" aria-busy={busy}>
      {error&&<p className="error" role="alert">{error}</p>}{busy&&!data&&<p role="status">בודקים את המצב…</p>}
      {data&&!configured&&<p>רכיב העדכון עדיין לא הוגדר במחשב השרת. המתקין החד־פעמי מגדיר אותו; אין צורך בחשבון GitHub נוסף.</p>}
      {configured&&<>
        <p role="status"><b>{phases[configured.phase]||'בודקים את המצב'}</b> · גרסה נוכחית: <span dir="ltr">{configured.currentVersion||'טרם דווחה'}</span>{configured.latestVersion&&<> · גרסה זמינה: <span dir="ltr">{configured.latestVersion}</span></>}</p>
        <div className="update-mode-status"><b>המצב הפעיל כעת</b><p>{!configured.enabled?'העדכונים מושהים בהגדרות השרת':configured.mode==='auto'?'עדכונים אוטומטיים פועלים':'הודעה על גרסה חדשה — ההתקנה לבחירתכם'}</p></div>
        <p>גרסאות יציבות מתפרסמות במאגר הקבוע ב־GitHub. לפני התקנה נשמר גיבוי מקומי מוצפן, ואחריה נבדקת תקינות האפליקציה. ייתכן ניתוק קצר בזמן עדכון.</p>
        {pending&&<p role="status" className="diagnostics-notice">מחילים את הבחירה: {(configured.queuedMode?.mode||configured.lastModeRequest?.mode)==='auto'?'עדכונים אוטומטיים':'הודעה בלבד'}. המצב הפעיל למעלה יתעדכן לאחר שהשינוי יושלם. אין צורך ללחוץ שוב.</p>}
        {!configured.queuedMode&&configured.lastModeRequest?.state==='applied'&&<p role="status">הבחירה נשמרה והופעלה.</p>}
        {!configured.queuedMode&&configured.lastModeRequest?.state==='rejected'&&<p role="alert">השינוי לא הוחל. המצב הפעיל למעלה נשמר. רעננו ונסו שוב; אם זה חוזר, נדרשת בדיקה במחשב השרת.</p>}
        {configured.lastModeRequest?.state==='pending'&&configured.lastModeRequest.error&&<p role="alert">לא ניתן לשמור את שינוי המצב בגלל תקלה בהגדרת השירות. נדרשת בדיקה במחשב השרת; בדיקות גרסה חדשות עשויות להמתין עד לתיקון.</p>}
        {!configured.modeControlAllowed&&<p>שינוי המצב זמין להורה המוגדר כאחראי לעדכונים, לאחר השלמת המתקין במחשב השרת.</p>}
        <button type="button" className="outline-button" disabled={disabled||!configured.modeControlAllowed} onClick={()=>setChoice(configured.mode==='auto'?'notify':'auto')}>{configured.mode==='auto'?'מעבר להודעה בלבד':'הפעלת עדכונים אוטומטיים'}</button>
        {choice&&<div className="diagnostics-notice" role="group" aria-label="אישור שינוי מצב העדכונים"><p>{choice==='auto'?'להתקין אוטומטית גרסאות יציבות חדשות? ייתכן ניתוק קצר מהשיחות בזמן התקנה.':'לעבור להודעה בלבד? גרסאות חדשות יוצגו כאן, ותבחרו מתי להתקין.'}</p><button className="outline-button" disabled={disabled} onClick={()=>void request('set_mode',choice)}>כן, {choice==='auto'?'הפעלת עדכונים אוטומטיים':'מעבר להודעה בלבד'}</button><button className="text-button" disabled={busy} onClick={()=>setChoice(null)}>ביטול</button></div>}
        {configured.checkedAt&&<p className="diagnostics-caption">בדיקה אחרונה: {new Date(configured.checkedAt).toLocaleString('he-IL')}</p>}
        {(configured.stale||configured.error)&&<p className="diagnostics-notice">{configured.stale?'המידע עדיין לא התעדכן. ':''}{configured.error?'הבדיקה או העדכון האחרון לא הושלמו. המצב אינו מעיד שהותקנה גרסה חדשה.':''}</p>}
        <div className="diagnostics-actions"><button className="outline-button" disabled={disabled} onClick={()=>void request('check')}><RefreshCw size={16}/>{configured.mode==='auto'?'בדיקה ועדכון אוטומטי':'בדיקת גרסה חדשה'}</button>{configured.phase==='available'&&<button className="outline-button" disabled={disabled} onClick={()=>setConfirm(true)}><Download size={16}/>התקנת הגרסה הזמינה</button>}</div>
        {confirm&&<div className="diagnostics-notice"><p>ההתקנה תשמור גיבוי ותנתק לרגע את האפליקציה. להתחיל כעת?</p><button className="outline-button" disabled={disabled} onClick={()=>void request('install')}>כן, התקנה עכשיו</button><button className="text-button" disabled={busy} onClick={()=>setConfirm(false)}>ביטול</button></div>}
      </>}
      {notice&&<p role="status">{notice}</p>}<div className="diagnostics-actions"><button className="text-button" disabled={busy} onClick={()=>void load()}><RefreshCw size={16}/>רענון המצב</button></div>
    </div>}
  </section>;
}

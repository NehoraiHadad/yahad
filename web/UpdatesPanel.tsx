import React, { useEffect, useRef, useState } from 'react';
import { Download, RefreshCw } from 'lucide-react';
import { api } from './api';
import type { updateStatus } from '../src/updates';
type Status = Awaited<ReturnType<typeof updateStatus>>;
const phases: Record<string,string> = {idle:'אין עדכון חדש',available:'יש גרסה חדשה',preparing:'מכינים את העדכון',stopping:'עוצרים את הגרסה הנוכחית',backing_up:'יוצרים גיבוי מוצפן',activating:'מפעילים את הגרסה החדשה',checking_health:'בודקים שהגרסה תקינה',healthy:'הגרסה פועלת כרגיל',rolled_back:'חזרנו לגרסה הקודמת',failed:'העדכון לא הושלם',incompatible:'הגרסה אינה מתאימה להתקנה',rolling_back:'חוזרים לגרסה הקודמת',recovery_required:'נדרשת בדיקה מקומית לפני עדכונים נוספים',rollback_pending_health:'הגרסה הקודמת נבחרה; ממתינים לבדיקת תקינות'};
export function UpdatesPanel() {
  const [open,setOpen]=useState(false),[data,setData]=useState<Status|null>(null),[busy,setBusy]=useState(false),[error,setError]=useState(''),[notice,setNotice]=useState(''),[confirm,setConfirm]=useState(false),[confirmAuto,setConfirmAuto]=useState(false);
  const loading=useRef(false);
  async function load(quiet=false) {
    if(loading.current)return;loading.current=true;if(!quiet)setBusy(true);
    try{setData(await api('/parent/updates'));setError('');}catch(e){setError((e as Error).message);}finally{loading.current=false;if(!quiet)setBusy(false);}
  }
  useEffect(()=>{if(!open)return;const timer=setInterval(()=>void load(true),3000);return()=>clearInterval(timer);},[open]);
  async function request(action:'check'|'install'|'set_mode',mode?:'auto'|'notify') {
    setBusy(true);setError('');
    try{const result=await api('/parent/updates','POST',{action,...(action==='set_mode'?{mode}:{})});setNotice(result.state==='pending'?'בקשה כבר ממתינה לטיפול.':'הבקשה נשלחה; רכיב העדכון יטפל בה בהפעלה הקרובה, בדרך כלל בתוך כחמש דקות. שליחה אינה אישור שהפעולה הושלמה.');setConfirm(false);setConfirmAuto(false);await load();}
    catch(e){setError((e as Error).message);}finally{setBusy(false);}
  }
  const configured=data?.configured?data:null;
  const pending=configured?.queuedMode||configured?.lastModeRequest?.state==='pending';
  const disabled=busy||!configured?.enabled||configured?.stale||!!pending;
  return <section className="parent-card diagnostics-card">
    <button className="diagnostics-toggle" aria-expanded={open} onClick={()=>{setOpen(!open);if(!open)void load();}}><Download size={20}/><span><b>עדכוני מערכת</b><small>גרסאות חתומות ובקרת עדכונים</small></span><span>{open?'סגירה':'פתיחה'}</span></button>
    {open&&<div className="diagnostics-body">
      {error&&<p className="error" role="alert">{error}</p>}{busy&&!data&&<p role="status">טוענים…</p>}
      {data&&!configured&&<p>רכיב העדכון עדיין לא הוגדר לחשבון הזה. בעל ה־Pi משלים פעם אחת את הגדרת הרכיב והמפתח הציבורי; אין צורך בפרטי GitHub בדפדפן.</p>}
      {configured&&<>
        <p role="status"><b>{phases[configured.phase]}</b> · גרסה נוכחית: <span dir="ltr">{configured.currentVersion||'לא ידועה'}</span>{configured.latestVersion&&<> · גרסה זמינה: <span dir="ltr">{configured.latestVersion}</span></>}</p>
        <p>המצב שאושר ברכיב העדכון: {!configured.enabled?'העדכונים מושהים בהגדרה המקומית':configured.mode==='auto'?'עדכונים אוטומטיים':'התראה בלבד; התקנה לפי בקשתך'}.</p>
        <button type="button" role="switch" aria-label="עדכונים אוטומטיים" aria-checked={configured.enabled&&configured.mode==='auto'} className="outline-button" disabled={disabled||!configured.modeControlAllowed} onClick={()=>configured.mode==='auto'?void request('set_mode','notify'):setConfirmAuto(true)}>עדכונים אוטומטיים: {!configured.enabled?'מושהים':configured.mode==='auto'?'פעילים':'כבויים'}</button>
        {!configured.modeControlAllowed&&<p className="diagnostics-caption">שליטה במצב דורשת השלמת ההגדרה החד־פעמית ועדכון רכיב הניהול המקומי ב־Pi.</p>}
        {pending&&<p role="status">התבקש מצב {(configured.queuedMode?.mode||configured.lastModeRequest?.mode)==='auto'?'אוטומטי':'התראה בלבד'}; השינוי ממתין לאישור רכיב העדכון. המצב המוצג עדיין בתוקף. פעולה שכבר התחילה יכולה להסתיים.</p>}
        {!configured.queuedMode&&configured.lastModeRequest?.state==='applied'&&<p role="status">רכיב העדכון אישר את שינוי המצב.</p>}
        {!configured.queuedMode&&configured.lastModeRequest?.state==='rejected'&&<p role="alert">{configured.lastModeRequest.error==='operator_changed'?'לא ניתן לאשר את הבקשה הקודמת אחרי שינוי ההורה המנהל.':configured.lastModeRequest.error==='local_setup'?'הבקשה נדחתה; נדרשת השלמת ההגדרה המקומית.':'הבקשה נדחתה; אפשר לרענן ולשלוח בקשה חדשה.'}</p>}
        {configured.lastModeRequest?.state==='pending'&&configured.lastModeRequest.error&&<p role="alert">עדיין אין אישור לשינוי. הבקשה נשמרה להמשך טיפול אחרי שגיאת הגדרה.</p>}
        {confirmAuto&&<div className="diagnostics-notice"><p>גרסאות יציבות חתומות יותקנו אחרי גיבוי ועשויות לעצור את האפליקציה לזמן קצר. השינוי ייכנס לתוקף אחרי אישור רכיב העדכון.</p><button className="outline-button" disabled={disabled} onClick={()=>void request('set_mode','auto')}>הפעלת עדכונים אוטומטיים</button><button className="text-button" onClick={()=>setConfirmAuto(false)}>ביטול</button></div>}
        {configured.checkedAt&&<p className="diagnostics-caption">בדיקה אחרונה: {new Date(configured.checkedAt).toLocaleString('he-IL')}</p>}
        {(configured.stale||configured.error)&&<p className="diagnostics-notice">{configured.stale?'המצב לא עודכן לאחרונה. ':''}{configured.error?'רכיב העדכון דיווח על תקלה; אפשר לבדוק את המצב המקומי ב־Pi.':''}</p>}
        <p className="diagnostics-caption">שינוי להתראה בלבד עוצר התקנות אוטומטיות עתידיות לאחר אישורו; הוא אינו מבטל פעולה שכבר התחילה. בזמן שחזור או כשל אין לעקוף את הבדיקות המקומיות.</p>
        <p className="diagnostics-caption">גיבויי העדכון מוצפנים; נשמרים עד שבעה עותקים ועד שבעה ימים. עותק התאוששות מחוץ ל־Pi נשמר בנפרד.</p>
        <div className="diagnostics-actions"><button className="outline-button" disabled={disabled} onClick={()=>void request('check')}><RefreshCw size={16}/>בדיקה ב־GitHub</button>{configured.phase==='available'&&<button className="outline-button" disabled={disabled} onClick={()=>setConfirm(true)}><Download size={16}/>התקנת העדכון</button>}</div>
        {confirm&&<div className="diagnostics-notice"><p>התקנה כוללת גיבוי ועצירה זמנית של האפליקציה. להמשיך?</p><button className="outline-button" disabled={disabled} onClick={()=>void request('install')}>שליחת בקשת התקנה</button><button className="text-button" onClick={()=>setConfirm(false)}>ביטול</button></div>}
      </>}
      {notice&&<p role="status">{notice}</p>}<div className="diagnostics-actions"><button className="text-button" disabled={busy} onClick={()=>void load()}><RefreshCw size={16}/>רענון מצב</button></div>
    </div>}
  </section>;
}

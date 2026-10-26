import React, { useEffect, useState } from 'react';
import { api } from './api';
import type { HistoryTracker } from '../src/history';
type Status=ReturnType<HistoryTracker['view']>;
const phases: Record<string,string>={partial:'ההיסטוריה המקומית חלקית',requesting:'שולחים בקשה לטלפון',waiting:'הבקשה נשלחה; ממתינים לנתונים',receiving:'מעבדים נתונים מהטלפון',timeout:'לא אומתה תשובה בזמן',failed:'חלק מהעיבוד או הבקשה נכשלו',overloaded:'עומס קליטה: חלק מהנתונים לא נקלטו'};
export function HistoryPanel({groupId}:{groupId:string}) {
  const [status,setStatus]=useState<Status|null>(null),[error,setError]=useState(''),[busy,setBusy]=useState(false);
  useEffect(()=>{
    let closed=false,inFlight=false;
    const load=async()=>{if(inFlight)return;inFlight=true;try{const data=await api<Status>('/groups/'+encodeURIComponent(groupId)+'/history');if(!closed){setStatus(data);setError('');}}
      catch{if(!closed){setStatus(null);setError('לא ניתן לבדוק את מצב ההיסטוריה.');}}finally{inFlight=false;}};
    void load();const timer=setInterval(()=>void load(),3000);
    return()=>{closed=true;clearInterval(timer);};
  },[groupId]);
  return <section className="history-status" aria-label="מצב קליטת היסטוריה">
    <p role="status" aria-live="polite">{status?phases[status.phase]:'בודקים מצב היסטוריה…'}</p>
    {status && <>
      <p>שמורות בקבוצה: {status.totalStored}. נשמרו מהיסטוריה במדידה הנוכחית: {status.stored}.</p>
      <p>נבדקו בעיבוד: {status.processed} מתוך {status.received} הודעות מורשות שנמסרו לקליטה. מספר זה אינו מספר ההודעות שנשמרו.</p>
      {status.correlated && <p>נתונים עם מזהה תשובה תואם: {status.requestStored} הודעות חדשות נשמרו. זו אינה הוכחה להיסטוריה מלאה.</p>}
      {!status.correlated && status.stored>0 && <p>הגיעו נתוני היסטוריה, אך לא אומת שהם תשובה לבקשה הנוכחית.</p>}
      {status.reason==='no_anchor' && <p>אין עדיין הודעת WhatsApp מקורית מתאימה לבקשת הודעות ישנות. בקבוצה שאושרה אחרי החיבור לא שמרנו הודעות שלא היו מורשות. הודעות חדשות או סנכרון עתידי מהטלפון עשויים לספק נקודת התחלה; אין הבטחה לשחזור מלא.</p>}
      {status.reason==='anchor_scan_limit' && <p>לא נמצאה נקודת התחלה בטווח הבדיקה המקומי. אין בקשת שחזור זמינה כרגע.</p>}
      {status.reason==='capacity' && <p>מעקב ההיסטוריה עמוס. הנתונים שכבר נשמרו נשארים זמינים; המתינו לפני בקשה נוספת.</p>}
      {status.reason==='offline' && <p>נדרש חיבור פעיל כדי לבקש מידע מהטלפון.</p>}
      {status.reason==='unsupported' && <p>בקשת היסטוריה אינה זמינה במתאם הזה.</p>}
      {status.incomplete && <p role="alert">יש מידע חסר או עיבוד שלא הושלם. שומרים את מה שנקלט; אין סימון שהכול שוחזר.</p>}
      <button type="button" className="older-button" disabled={busy||!status.requestable} onClick={async()=>{
        setBusy(true);setError('');try{setStatus(await api('/groups/'+encodeURIComponent(groupId)+'/history','POST',{}));}
        catch{setError('הבקשה לא הושלמה. המתינו מעט ובדקו את החיבור.');}finally{setBusy(false);}
      }}>{busy?'שולחים…':'בקשת הודעות ישנות מהטלפון'}</button>
    </>}
    {error&&<p className="error" role="alert">{error}</p>}
  </section>;
}

import React, { useState } from 'react';

export function DisconnectPanel({ active, busy, mock, result, history, onDisconnect }: {
  active: boolean; busy: boolean; mock: boolean; result?: string | null; history?: string | null;
  onDisconnect: () => Promise<unknown>;
}) {
  const [confirming, setConfirming] = useState(false), [error, setError] = useState('');
  return <div className="disconnect-panel" id="whatsapp-disconnect">
    {!active && <p role="status">WhatsApp מנותק. חיבור מחדש ייעשה רק בלחיצה מפורשת על כפתור החיבור.</p>}
    {result && <p role="status">הקישור המקומי נותק, ולא נתחבר שוב אחרי הפעלה מחדש. ניתוק רגיל שומר שיחות והרשאות; מחיקה מתבצעת רק בפעולת המחיקה הנפרדת.</p>}
    {history === 'retained' && <p>השיחות, המדיה וההרשאות נשמרו.</p>}
    {history === 'deleted' && <p>נתוני השיחות המקומיים נמחקו בפעולת המחיקה הנפרדת שאושרה.</p>}
    {history === 'deleting' && <p role="alert">פעולת המחיקה נקטעה או לא הושלמה. בדקו את המצב לפני שתאשרו שוב מחיקה נפרדת.</p>}
    {result === 'unconfirmed' && !mock && <p className="disconnect-warning" role="alert">לא התקבל אישור לביטול המכשיר המקושר ב־WhatsApp. בטלפון ההורה פתחו WhatsApp ← מכשירים מקושרים, ונתקו את המכשיר הזה אם הוא עדיין מופיע. החיבור המקומי נשאר מנותק.</p>}
    {result === 'confirmed' && <p>התקבל אישור מהמתאם לניתוק המכשיר המקושר. אפשר לבדוק זאת גם ברשימת המכשירים בטלפון ההורה.</p>}
    {active && <button type="button" className="outline-button" disabled={busy} onClick={() => { setError(''); setConfirming(true); }}>{mock ? 'ניתוק המתאם המדומה' : 'ניתוק מ־WhatsApp'}</button>}
    {confirming && <div className="modal-backdrop"><section className="modal" role="dialog" aria-modal="true" aria-labelledby="disconnect-title">
      <h2 id="disconnect-title">לנתק את WhatsApp?</h2>
      <p>השליחה והקבלה ייפסקו, נתוני הקישור המקומיים יימחקו ונבקש לבטל את המכשיר המקושר ב־WhatsApp. אם לא יתקבל אישור, תופיע הנחיה לניתוק בטלפון ההורה.</p>
      <p><strong>השיחות, המדיה, חשבונות הילדים וההרשאות לקבוצות יישמרו.</strong> מחיקת שיחות היא פעולה נפרדת. חיבור מחדש ידרוש פעולה מפורשת וקישור מחדש.</p>
      {mock && <p>זו סביבת דוגמה בלבד; לא נוגעים בחשבון WhatsApp אמיתי.</p>}
      {error && <p className="error" role="alert">{error}</p>}
      <button type="button" className="primary" disabled={busy} onClick={async () => {
        try { await onDisconnect(); setConfirming(false); } catch (e) { setError((e as Error).message); }
      }}>{busy ? 'מנתקים…' : 'ניתוק ושמירת השיחות'}</button>
      <button type="button" className="text-button" disabled={busy} onClick={() => setConfirming(false)}>ביטול</button>
    </section></div>}
  </div>;
}

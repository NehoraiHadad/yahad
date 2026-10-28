import React from 'react';
import { ArrowRight, ShieldCheck } from 'lucide-react';

/** Fail-closed fallback for older or inconsistent server snapshots. */
export function CatalogConversation({ title, onBack, onRetry }: {
  title: string; onBack: () => void; onRetry: () => void;
}) {
  return <div className="catalog-conversation">
    <header className="chat-header">
      <button type="button" className="icon-button back" aria-label="חזרה לשיחות" onClick={onBack}><ArrowRight /></button>
      <div className="chat-title"><h2>{title}</h2><span>הגישה לשיחה אינה זמינה</span></div>
    </header>
    <div className="catalog-explanation" role="status">
      <ShieldCheck size={36} aria-hidden="true" />
      <h2>לא ניתן לפתוח את השיחה כרגע</h2>
      <p>השרת לא אישר גישה לתוכן השיחה. ההודעות והשליחה מושהות עד לאימות הגישה.</p>
      <p>חשבון ההורה אינו תלוי באישור קבוצה לילדים. אין צורך לשנות הרשאות של ילד כדי לפתוח שיחה בחשבון ההורה.</p>
      <button type="button" className="small-button" onClick={onRetry}>לנסות שוב</button>
    </div>
  </div>;
}

export function ConversationVerification({ title, checking, onBack, onRetry }: {
  title: string; checking: boolean; onBack: () => void; onRetry: () => void;
}) {
  return <div className="catalog-conversation">
    <header className="chat-header">
      <button type="button" className="icon-button back" aria-label="חזרה לשיחות" onClick={onBack}><ArrowRight /></button>
      <div className="chat-title"><h2>{title}</h2><span>בודקים את הגישה לשיחה</span></div>
    </header>
    <div className="catalog-explanation" role="status">
      <ShieldCheck size={36} aria-hidden="true" />
      <h2>{checking ? 'מאמתים את הגישה לשיחה' : 'לא הצלחנו לאמת את הגישה לשיחה'}</h2>
      <p>ההודעות והשליחה מושהות עד לקבלת הרשאות עדכניות מהשרת. שם הקבוצה נשאר ברשימה.</p>
      <p>טיוטת הודעה חדשה נשמרת זמנית במסך הזה ותופיע שוב אחרי אימות הגישה. יציאה מהשיחה או ביטול הגישה מוחקים אותה.</p>
      <button type="button" className="small-button" disabled={checking} onClick={onRetry}>{checking ? 'מאמתים…' : 'לנסות שוב'}</button>
    </div>
  </div>;
}

import React from 'react';
import { ArrowRight, ShieldCheck } from 'lucide-react';

/** Metadata only: intentionally never mounts message, history or send controls. */
export function CatalogConversation({ title, onBack, onManage }: {
  title: string; onBack: () => void; onManage: () => void;
}) {
  return <div className="catalog-conversation">
    <header className="chat-header">
      <button type="button" className="icon-button back" aria-label="חזרה לשיחות" onClick={onBack}><ArrowRight /></button>
      <div className="chat-title"><h2>{title}</h2><span>שם הקבוצה בלבד · ללא תוכן שמור</span></div>
    </header>
    <div className="catalog-explanation" role="status">
      <ShieldCheck size={36} aria-hidden="true" />
      <h2>הקבוצה לא פתוחה ב״יחד״</h2>
      <p>הקבוצה מופיעה ברשימת ההורה, אבל אינה מאושרת לאף ילד. הודעות והיסטוריה אינן נשמרות כאן, ואי אפשר לשלוח מכאן.</p>
      <p>לשיחה פרטית של ההורה ממשיכים ב־WhatsApp. אישור קבוצה לילד ייתן גם לו גישה אליה; אין צורך לאשר כדי לראות את שם הקבוצה.</p>
      <button type="button" className="small-button" onClick={onManage}>לניהול הקבוצות וההרשאות</button>
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
      <button type="button" className="small-button" disabled={checking} onClick={onRetry}>{checking ? 'מאמתים…' : 'לנסות שוב'}</button>
    </div>
  </div>;
}

import React, { useRef, useState } from 'react';
import { ShieldCheck, X } from 'lucide-react';
import { api, isTransientApiError } from './api';
import { useDialogFocus } from './useDialogFocus';

export const passphraseHelp = 'בחרו כמה מילים שקל לזכור, עם רווחים — לפחות 14 תווים. אפשר להדביק ממנהל הסיסמאות. לכל חשבון משפט נפרד.';
export function suggestUsername(name: string, taken: readonly string[] = []) {
  const latin = name.toLowerCase().replace(/[^a-z0-9._-]/g, '').slice(0, 50);
  const base = latin.length >= 3 ? latin : 'child';
  const used = new Set(taken.map(value => value.toLowerCase()));
  let candidate = base, suffix = 2;
  while (used.has(candidate)) candidate = `${base}${suffix++}`;
  return candidate;
}
export function FamilySetup({ setupToken, onDone, onClose, existingUsernames = [] }: {
  setupToken?: string; existingUsernames?: readonly string[]; onDone: (childId?: string) => void; onClose?: () => void;
}) {
  const setup = setupToken !== undefined;
  const [username, setUsername] = useState(() => setup ? 'parent' : suggestUsername('', existingUsernames)), [name, setName] = useState(setup ? 'המשפחה שלנו' : ''),
    [password, setPassword] = useState(''), [confirmation, setConfirmation] = useState(''),
    [uncertain, setUncertain] = useState(false),
    [show, setShow] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState('');
  const submitting = useRef(false), editedUsername = useRef(false);
  const form = useRef<HTMLFormElement>(null);
  useDialogFocus(form, !setup);
  const submit = async (e: React.FormEvent) => {
    e.preventDefault(); if (submitting.current || uncertain) return; setError('');
    if (password !== confirmation) { setError('הסיסמאות אינן זהות. נסו שוב בשדה האישור.'); return; }
    submitting.current = true; setBusy(true);
    try {
      let childId: string | undefined;
      if (setup) {
        const res = await fetch('/api/setup', { method: 'POST', credentials: 'same-origin',
          headers: { 'Content-Type': 'application/json', 'X-Setup-Token': setupToken },
          body: JSON.stringify({ name: name.trim() || 'המשפחה שלנו', username, password, confirmation }) });
        if (res.status >= 500) throw new TypeError('Setup response unconfirmed');
        if (!res.ok) {
          const body = await res.json().catch(() => ({}));
          throw new Error(body.error === 'SETUP_CLOSED' ? 'קישור ההכנה פג או שכבר נוצר חשבון. אם נוצר, עברו לכניסה; אחרת פתחו קישור חדש מהמתקין.' : 'ההכנה לא הושלמה. בדקו את הפרטים ונסו שוב.');
        }
        localStorage.removeItem('yachad-locked');
      } else childId = (await api<{id: string}>('/parent/children', 'POST', { display: name, username, password, confirmation })).id;
      setPassword(''); setConfirmation(''); onDone(childId);
    } catch (e) {
      if (e instanceof TypeError || e instanceof SyntaxError || isTransientApiError(e)) {
        setUncertain(true);
        setError(setup
          ? 'לא התקבל אישור מהשרת. ייתכן שהחשבון נוצר. עברו לכניסה ונסו את הפרטים שבחרתם לפני פתיחת הכנה חדשה.'
          : 'לא התקבל אישור מהשרת. ייתכן שהחשבון נוצר. סגרו את החלון ובדקו ברשימת החשבונות לפני יצירה נוספת.');
      } else setError((e as Error).message);
    }
    finally { submitting.current = false; setBusy(false); }
  };
  return <div className={setup ? 'family-setup-page' : 'modal-backdrop'} dir="rtl">
    <form ref={form} className="family-form modal" role={setup ? undefined : 'dialog'} aria-modal={setup ? undefined : true}
      aria-labelledby="family-form-title" aria-busy={busy} onSubmit={submit}
      onKeyDown={e => { if (e.key === 'Escape' && !busy) onClose?.(); }}>
      <div className="modal-top"><span className="eyebrow"><ShieldCheck size={18}/> {setup ? 'מתחילים יחד · חשבון ההורה' : 'חשבון אישי · בלי גישה אוטומטית לקבוצות'}</span>
        {onClose && <button type="button" className="icon-button" aria-label={setup ? 'מעבר לכניסה' : 'סגירה'} disabled={busy} onClick={onClose}><X/></button>}</div>
      <h1 id="family-form-title">{setup ? 'ברוכים הבאים למשפחה' : 'מוסיפים ילד או ילדה'}</h1>
      <p>{setup ? 'יוצרים חשבון הורה, ואחר כך מוסיפים ילדים ובוחרים קבוצות. הכול כאן בדפדפן.' : 'אחרי יצירת החשבון נבחר יחד אילו קבוצות להציג.'}</p>
      <label htmlFor="family-name">{setup ? 'שם המשפחה באפליקציה (אפשר להשאיר כך)' : 'שם לתצוגה'}<input disabled={busy || uncertain} id="family-name" autoFocus={setup} required={!setup} autoComplete="off" maxLength={setup ? 80 : 40} value={name} onChange={e => { setName(e.target.value); if (!setup && !editedUsername.current) setUsername(suggestUsername(e.target.value, existingUsernames)); }}/></label>
      <label htmlFor="family-username">שם משתמש<input disabled={busy || uncertain} id="family-username" dir="ltr" spellCheck={false} autoCapitalize="none" autoCorrect="off" autoComplete="username" required pattern="[a-z0-9._-]{3,60}" minLength={3} maxLength={60} aria-describedby="username-help" value={username} onChange={e => { editedUsername.current = true; setUsername(e.target.value.toLowerCase()); }}/></label>
      <small id="username-help">3–60 אותיות באנגלית, מספרים, נקודה או מקף. אפשר לערוך את ההצעה.</small>
      <label htmlFor="family-password">משפט סודי<input disabled={busy || uncertain} id="family-password" dir="auto" spellCheck={false} autoCapitalize="none" autoCorrect="off" type={show ? 'text' : 'password'} autoComplete="new-password" required minLength={14} maxLength={256} aria-describedby="passphrase-help family-error" value={password} onChange={e => setPassword(e.target.value)}/></label>
      <label htmlFor="family-confirmation">שוב את המשפט הסודי<input disabled={busy || uncertain} id="family-confirmation" dir="auto" spellCheck={false} autoCapitalize="none" autoCorrect="off" type={show ? 'text' : 'password'} autoComplete="new-password" required minLength={14} maxLength={256} aria-describedby="family-error" value={confirmation} onChange={e => setConfirmation(e.target.value)}/></label>
      <label className="secret-toggle"><input type="checkbox" checked={show} onChange={e => setShow(e.target.checked)}/> הצגת המשפטים</label>
      <p id="passphrase-help" className="muted">{passphraseHelp}</p>
      <p id="family-error" role={error ? 'alert' : undefined} className="error">{error}</p>
      <div className="family-form-actions"><button className="primary" disabled={busy || uncertain}>{busy ? 'יוצרים את החשבון…' : setup ? 'יצירת חשבון והמשך' : 'יצירה ובחירת קבוצות'}</button>
        {onClose && <button type="button" disabled={busy} onClick={onClose}>{setup ? 'מעבר לכניסה' : 'אחר כך'}</button>}</div>
    </form>
  </div>;
}

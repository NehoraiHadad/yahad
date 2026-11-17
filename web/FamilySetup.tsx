import React, { useRef, useState } from 'react';
import { ShieldCheck, X } from 'lucide-react';
import { ApiError, ApiNetworkError, api, isTransientApiError, shownError, type ShownError } from './api';
import { ErrorReference } from './ErrorReference';
import { normalizeAccountName } from '../src/account-name';
import { useDialogFocus } from './useDialogFocus';

export const passphraseHelp = 'אפשר לבחור קוד קצר של 4 תווים לפחות — מספרים, אותיות או שילוב. סיסמה ארוכה עדיין נתמכת ובטוחה יותר. בחרו קוד שונה לכל חשבון; אחרי ניסיונות שגויים הכניסה תושהה זמנית.';
export function FamilySetup({ setupToken, onDone, onClose }: {
  setupToken?: string; onDone: (childId?: string) => void; onClose?: () => void;
}) {
  const setup = setupToken !== undefined;
  const [name, setName] = useState(''), [familyName, setFamilyName] = useState('המשפחה שלנו'),
    [password, setPassword] = useState(''), [confirmation, setConfirmation] = useState(''),
    [uncertain, setUncertain] = useState(false),
    [nameInvalid, setNameInvalid] = useState(false),
    [show, setShow] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState<ShownError | null>(null);
  const submitting = useRef(false);
  const form = useRef<HTMLFormElement>(null);
  useDialogFocus(form, !setup);
  const submit = async (e: React.FormEvent) => {
    e.preventDefault(); if (submitting.current || uncertain) return; setError(null); setNameInvalid(false);
    if (password !== confirmation) { setError({ text: 'הסיסמאות אינן זהות. נסו שוב בשדה האישור.' }); return; }
    let accountName: string;
    try { accountName = normalizeAccountName(name); }
    catch { setNameInvalid(true); setError({ text: 'כתבו שם באורך 1–60 תווים: אותיות, מספרים, רווח, נקודה או מקף.' }); return; }
    submitting.current = true; setBusy(true);
    try {
      let childId: string | undefined;
      if (setup) {
        await api('/setup', 'POST', { familyName: familyName.trim() || 'המשפחה שלנו', name: accountName, password, confirmation },
          { 'X-Setup-Token': setupToken });
        localStorage.removeItem('yachad-locked');
      } else childId = (await api<{id: string}>('/parent/children', 'POST', { name: accountName, password, confirmation })).id;
      setPassword(''); setConfirmation(''); onDone(childId);
    } catch (e) {
      if (e instanceof ApiError && e.code === 'NAME_UNAVAILABLE') setNameInvalid(true);
      // A lost or failed response may still have created the account: lock the form and send
      // the person to check, keeping the code and reference of what actually came back.
      const unconfirmed = e instanceof ApiNetworkError || e instanceof SyntaxError || isTransientApiError(e);
      if (unconfirmed) setUncertain(true);
      setError(!unconfirmed ? shownError(e) : { ...shownError(e), text: setup
        ? 'לא התקבל אישור מהשרת. ייתכן שהחשבון נוצר. עברו לכניסה ונסו את הפרטים שבחרתם לפני פתיחת הכנה חדשה.'
        : 'לא התקבל אישור מהשרת. ייתכן שהחשבון נוצר. סגרו את החלון ובדקו ברשימת החשבונות לפני יצירה נוספת.' });
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
      {setup && <label htmlFor="family-label">שם המשפחה באפליקציה<input disabled={busy || uncertain} id="family-label" autoComplete="off" maxLength={80} value={familyName} onChange={e => setFamilyName(e.target.value)} /></label>}
      <label htmlFor="account-name">{setup ? 'השם שלך' : 'שם הילד או הילדה'}<input disabled={busy || uncertain} id="account-name" dir="auto" autoFocus={setup} required autoComplete="username" spellCheck={false} autoCapitalize="none" autoCorrect="off" maxLength={120} aria-invalid={nameInvalid} aria-describedby="account-name-help family-error" value={name} onChange={e => { setName(e.target.value); setNameInvalid(false); setError(null); }} /></label>
      <small id="account-name-help">שם אחד שמופיע באפליקציה וגם משמש לכניסה. אפשר בעברית או באנגלית, עד 60 תווים.</small>
      <label htmlFor="family-password">סיסמה<input disabled={busy || uncertain} id="family-password" dir="auto" spellCheck={false} autoCapitalize="none" autoCorrect="off" type={show ? 'text' : 'password'} autoComplete="new-password" required minLength={4} maxLength={256} aria-describedby="passphrase-help family-error" value={password} onChange={e => setPassword(e.target.value)}/></label>
      <label htmlFor="family-confirmation">אישור הסיסמה<input disabled={busy || uncertain} id="family-confirmation" dir="auto" spellCheck={false} autoCapitalize="none" autoCorrect="off" type={show ? 'text' : 'password'} autoComplete="new-password" required minLength={4} maxLength={256} aria-describedby="family-error" value={confirmation} onChange={e => setConfirmation(e.target.value)}/></label>
      <label className="secret-toggle"><input type="checkbox" checked={show} onChange={e => setShow(e.target.checked)}/> הצגת הסיסמה</label>
      <p id="passphrase-help" className="muted">{passphraseHelp}</p>
      <p id="family-error" role={error ? 'alert' : undefined} className="error">{error?.text}</p>
      {error && <ErrorReference code={error.code} refId={error.ref}/>}
      <div className="family-form-actions"><button className="primary" disabled={busy || uncertain}>{busy ? 'יוצרים את החשבון…' : setup ? 'יצירת חשבון והמשך' : 'יצירה ובחירת קבוצות'}</button>
        {onClose && <button type="button" disabled={busy} onClick={onClose}>{setup ? 'מעבר לכניסה' : 'אחר כך'}</button>}</div>
    </form>
  </div>;
}

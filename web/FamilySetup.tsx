import React, { useState } from 'react';
import { api } from './api';

export const passphraseHelp = 'אפשר לבחור כמה מילים אקראיות שקל לזכור, עם רווחים — לפחות 14 תווים. בחרו משפט שונה לכל משתמש ושמרו במנהל הסיסמאות במכשיר האישי.';

export function FamilySetup({ setupToken, onDone, onClose }: {
  setupToken?: string; onDone: () => void; onClose?: () => void;
}) {
  const [username, setUsername] = useState(''), [name, setName] = useState(''),
    [password, setPassword] = useState(''), [confirmation, setConfirmation] = useState(''),
    [show, setShow] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState('');
  const setup = setupToken !== undefined;
  return <main className={setup ? 'family-setup-page' : 'modal-backdrop'} dir="rtl">
    <form className={setup ? 'login-card' : 'modal'} role={setup ? undefined : 'dialog'} aria-modal={setup ? undefined : true} aria-label={setup ? 'הגדרת משפחה' : 'הוספת ילד או ילדה'} onSubmit={async e => {
      e.preventDefault(); setError('');
      if (password !== confirmation) { setError('הסיסמאות אינן זהות.'); return; }
      setBusy(true);
      try {
        if (setup) {
          const res = await fetch('/api/setup', { method: 'POST', credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', 'X-Setup-Token': setupToken },
            body: JSON.stringify({ name, username, password, confirmation }) });
          if (!res.ok) throw new Error('ההגדרה לא הושלמה. בדקו את הפרטים ואת תוקף קישור ההגדרה המקומי.');
        } else await api('/parent/children', 'POST', { display: name, username, password, confirmation });
        setPassword(''); setConfirmation(''); onDone();
      } catch (e) { setError((e as Error).message); }
      finally { setBusy(false); }
    }}>
      <h1>{setup ? 'ברוכים הבאים ליחד' : 'הוספת ילד או ילדה'}</h1>
      <p>{setup ? 'יוצרים חשבון הורה. אחר כך אפשר להוסיף ילדים ולבחור בנפרד אילו קבוצות יראו.' : 'לכל ילד חשבון נפרד. החשבון החדש מתחיל ללא גישה לקבוצות; בחרו את הקבוצות במסך ההורה.'}</p>
      <label>{setup ? 'שם המשפחה במערכת' : 'שם לתצוגה'}<input required maxLength={setup ? 80 : 40} value={name} onChange={e => setName(e.target.value)} /></label>
      <label>{setup ? 'שם משתמש של ההורה' : 'שם משתמש של הילד'}<input dir="ltr" autoComplete="username" required pattern="[a-z0-9._-]{3,60}" minLength={3} maxLength={60} value={username} onChange={e => setUsername(e.target.value.toLowerCase())} /></label>
      <p className="muted">שם המשתמש: 3–60 אותיות באנגלית קטנה, מספרים, נקודה, מקף או קו תחתון.</p>
      <label>סיסמה או משפט סודי<input dir="auto" type={show ? 'text' : 'password'} autoComplete="new-password" required minLength={14} maxLength={256} value={password} onChange={e => setPassword(e.target.value)} /></label>
      <label>אישור הסיסמה<input dir="auto" type={show ? 'text' : 'password'} autoComplete="new-password" required minLength={14} maxLength={256} value={confirmation} onChange={e => setConfirmation(e.target.value)} /></label>
      <label className="secret-toggle"><input type="checkbox" checked={show} onChange={e => setShow(e.target.checked)} /> הצגת הסיסמה</label>
      <p className="muted">{passphraseHelp}</p>
      {error && <p role="alert" className="error">{error}</p>}
      <button className="primary" disabled={busy}>{busy ? 'שומרים…' : setup ? 'יצירת חשבון הורה' : 'יצירת חשבון ילד'}</button>
      {onClose && <button type="button" onClick={onClose}>ביטול</button>}
    </form>
  </main>;
}

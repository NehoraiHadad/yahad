import React, { useRef, useState } from 'react';
import { X, Check } from 'lucide-react';
import { ApiError, api, isTransientApiError } from './api';
import { normalizeAccountName, accountNameKey } from '../src/account-name';
import { passphraseHelp } from './FamilySetup';
import { useDialogFocus } from './useDialogFocus';
import { childLabel, type ChildIdentity } from './childLabels';

export function AccountSettings({
  onClose,
  children: users,
  initialChildId,
  initialMode,
  onProfileChanged,
}: {
  onClose: () => void;
  children?: ChildIdentity[];
  initialChildId?: string;
  initialMode?: 'profile' | 'password';
  onProfileChanged?: () => void;
}) {
  const initialChild = users?.find(user => user.id === initialChildId);
  const [current, setCurrent] = useState(''),
    [next, setNext] = useState(''),
    [confirmation, setConfirmation] = useState(''),
    [show, setShow] = useState(false),
    [child, setChild] = useState(initialChildId || ''),
    [mode, setMode] = useState<'profile' | 'password'>(initialChild ? initialMode || 'profile' : 'password'),
    [name, setName] = useState(initialChild?.name || ''),
    [confirmedNames, setConfirmedNames] = useState<Record<string, string>>({}),
    [busy, setBusy] = useState(false),
    [message, setMessage] = useState(''),
    [error, setError] = useState('');
  const [nameInvalid, setNameInvalid] = useState(false);
  const [uncertain, setUncertain] = useState(false);
  const submitting = useRef(false), dialog = useRef<HTMLDivElement>(null);
  useDialogFocus(dialog, true);
  const accounts = (users || []).map(user => ({ ...user, name: confirmedNames[user.id] ?? user.name }));
  const selected = accounts.find(user => user.id === child);
  const unavailable = !!child && !selected;
  const editingProfile = !!selected && mode === 'profile';
  const resetFeedback = () => { setCurrent(''); setNext(''); setConfirmation(''); setShow(false); setMessage(''); setError(''); setNameInvalid(false); };
  const changeMode = (value: 'profile' | 'password') => {
    if (submitting.current || uncertain) return;
    setMode(value); setName(selected?.name || ''); resetFeedback();
  };
  const saveProfile = async (e: React.FormEvent) => {
    e.preventDefault();
    if (submitting.current || uncertain || !selected) return;
    let value: string;
    setError(''); setMessage(''); setNameInvalid(false);
    try { value = normalizeAccountName(name); }
    catch { setNameInvalid(true); setError('כתבו שם באורך 1–60 תווים: אותיות בעברית או באנגלית, מספרים, רווח, נקודה או מקף.'); return; }
    if (value === selected.name) return;
    submitting.current = true; setBusy(true);
    try {
      const result = await api<{ child: ChildIdentity }>('/parent/child-profile', 'PUT', { childId: selected.id, name: value });
      setConfirmedNames(previous => ({ ...previous, [selected.id]: result.child.name }));
      setName(result.child.name);
      setMessage(`השם עודכן ל־${result.child.name}. בכניסה הבאה משתמשים בשם הזה.${accountNameKey(result.child.name) !== accountNameKey(selected.name) ? ' השם הקודם כבר לא משמש לכניסה.' : ''} הסיסמה והקבוצות לא השתנו.`);
      onProfileChanged?.();
    } catch (e) {
      if (e instanceof ApiError && e.code === 'NAME_UNAVAILABLE') setNameInvalid(true);
      const unconfirmed = isTransientApiError(e) || e instanceof SyntaxError;
      if (unconfirmed) setUncertain(true);
      setError(unconfirmed
        ? 'לא התקבל אישור לשמירה. ייתכן שהשם עודכן. סגרו את החלון ובדקו את השם ברשימה לפני ניסיון נוסף.'
        : (e as Error).message);
      onProfileChanged?.();
    } finally {
      submitting.current = false; setBusy(false);
    }
  };
  const savePassword = async (e: React.FormEvent) => {
    e.preventDefault();
    if (submitting.current || uncertain || unavailable) return;
    setError('');
    if (next !== confirmation) { setError('הסיסמאות אינן זהות.'); return; }
    submitting.current = true; setBusy(true); setMessage('');
    try {
      if (child) {
        await api('/parent/child-password', 'POST', { childId: child, password: next, confirmation });
        setMessage(`הסיסמה של ${selected ? childLabel(selected) : 'החשבון'} עודכנה וכל חיבורי החשבון נותקו.`);
        setNext(''); setConfirmation(''); setShow(false);
      } else {
        await api('/password', 'POST', { current, next, confirmation });
        window.dispatchEvent(new Event('session-expired'));
      }
    } catch (e) {
      if (isTransientApiError(e) || e instanceof SyntaxError) {
        setUncertain(true);
        setError('לא התקבל אישור לעדכון הסיסמה. ייתכן שהיא השתנתה. סגרו את החלון ובדקו כניסה עם הסיסמה החדשה לפני ניסיון נוסף.');
      } else setError((e as Error).message);
    } finally {
      submitting.current = false; setBusy(false);
    }
  };
  return (
    <div className="modal-backdrop">
      <div
        ref={dialog}
        className="modal account-settings"
        aria-busy={busy}
        onKeyDown={e => { if (e.key === 'Escape' && !submitting.current) onClose(); }}
        role="dialog"
        aria-modal="true"
        aria-label="הגדרות חשבון"
      >
        <div className="modal-top">
          <h2>{selected ? <>החשבון של <bdi>{childLabel(selected)}</bdi></> : child ? 'הגדרות חשבון' : 'הגדרות החשבון שלי'}</h2>
          <button type="button" className="icon-button" aria-label="סגירה" disabled={busy} onClick={() => { if (!submitting.current) onClose(); }}>
            <X />
          </button>
        </div>
        {users && (
          <label>
            איזה חשבון מעדכנים?
            <select disabled={busy || uncertain} value={child} onChange={e => {
              if (submitting.current || uncertain) return;
              const account = accounts.find(user => user.id === e.target.value);
              setChild(account?.id || ''); setMode(account ? 'profile' : 'password');
              setName(account?.name || ''); resetFeedback();
            }}>
              <option value="">החשבון שלי</option>
              {accounts.map(user => <option key={user.id} value={user.id}>{childLabel(user)}</option>)}
            </select>
          </label>
        )}
        {selected && <>
          <div className="account-settings-actions" role="group" aria-label="מה מעדכנים?">
            <button type="button" aria-pressed={editingProfile} disabled={busy || uncertain} onClick={() => changeMode('profile')}>שם</button>
            <button type="button" aria-pressed={!editingProfile} disabled={busy || uncertain} onClick={() => changeMode('password')}>סיסמה</button>
          </div>
        </>}
        {unavailable && <p role="alert">החשבון הזה כבר אינו זמין. סגרו את החלון ובחרו חשבון מהרשימה המעודכנת.</p>}
        <form className="account-settings-form" onSubmit={editingProfile ? saveProfile : savePassword}>
          {editingProfile ? <>
            <label htmlFor="account-name">שם הילד או הילדה
              <input id="account-name" dir="auto" autoComplete="off" required maxLength={120} disabled={busy || uncertain || unavailable}
                aria-invalid={nameInvalid} aria-describedby="account-name-help account-settings-error" value={name}
                onChange={e => { setName(e.target.value); setNameInvalid(false); setError(''); setMessage(''); }} />
            </label>
            <p id="account-name-help" className="muted">שם אחד שמופיע באפליקציה וגם משמש לכניסה. אפשר בעברית. אחרי השמירה נכנסים בשם החדש, עם אותה סיסמה.</p>
            <p className="muted">השינוי לא משנה את החתימה בהודעות WhatsApp ולא מנתק מכשירים שכבר מחוברים.</p>
          </> : <>
            {!child && <label>סיסמה נוכחית
              <input disabled={busy || uncertain || unavailable} type="password" autoComplete="current-password" aria-describedby="account-settings-error" required value={current} onChange={e => setCurrent(e.target.value)} />
            </label>}
            <label>סיסמה חדשה · לפחות 4 תווים
              <input disabled={busy || uncertain || unavailable} type={show ? 'text' : 'password'} autoComplete="new-password" aria-describedby="account-password-help account-settings-error" required minLength={4} maxLength={256} value={next} onChange={e => setNext(e.target.value)} />
            </label>
            <label>אישור הסיסמה החדשה<input disabled={busy || uncertain || unavailable} type={show ? 'text' : 'password'} autoComplete="new-password" aria-describedby="account-password-help account-settings-error" required minLength={4} maxLength={256} value={confirmation} onChange={e => setConfirmation(e.target.value)} /></label>
            <label className="secret-toggle"><input type="checkbox" disabled={busy || uncertain || unavailable} checked={show} onChange={e => setShow(e.target.checked)} /> הצגת הסיסמה</label>
            <p id="account-password-help" className="muted">{passphraseHelp}</p>
            <p className="muted">שינוי סיסמה מנתק את כל החיבורים של החשבון. אפשר להשתמש במשפט סודי שקל לזכור.</p>
          </>}
          {error && <p id="account-settings-error" className="error" role="alert">{error}</p>}
          {!error && <span id="account-settings-error" />}
          {message && <p role="status">{message}</p>}
          <button className="primary" disabled={busy || uncertain || unavailable || editingProfile && (!name.trim() || name === selected?.name)}>
            {busy ? 'שומרים…' : editingProfile ? 'שמירת השם' : 'עדכון סיסמה'} <Check size={17} />
          </button>
        </form>
      </div>
    </div>
  );
}

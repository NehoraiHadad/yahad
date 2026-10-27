import React, { useRef, useState } from 'react';
import { X, Check } from 'lucide-react';
import { api } from './api';
import { passphraseHelp } from './FamilySetup';
import { useDialogFocus } from './useDialogFocus';
import { childLabel, type ChildIdentity } from './childLabels';

export function AccountSettings({
  onClose,
  children: users,
}: {
  onClose: () => void;
  children?: ChildIdentity[];
}) {
  const [current, setCurrent] = useState(''),
    [next, setNext] = useState(''),
    [confirmation, setConfirmation] = useState(''),
    [show, setShow] = useState(false),
    [child, setChild] = useState(''),
    [busy, setBusy] = useState(false),
    [message, setMessage] = useState(''),
    [error, setError] = useState('');
  const submitting = useRef(false), form = useRef<HTMLFormElement>(null);
  useDialogFocus(form, true);
  return (
    <div className="modal-backdrop">
      <form
        ref={form}
        className="modal"
        aria-busy={busy}
        onKeyDown={e => { if (e.key === "Escape" && !submitting.current) onClose(); }}
        role="dialog"
        aria-modal="true"
        aria-label="הגדרות חשבון"
        onSubmit={async (e) => {
          e.preventDefault();
          if (submitting.current) return;
          setError('');
          if (next !== confirmation) { setError('הסיסמאות אינן זהות.'); return; }
          submitting.current = true; setBusy(true); setMessage('');
          try {
            if (child) {
              await api('/parent/child-password', 'POST', { childId: child, password: next, confirmation });
              const selected = users?.find(u => u.id === child);
              setMessage(`הסיסמה של ${selected ? childLabel(selected, users || []) : 'החשבון'} עודכנה וכל חיבורי החשבון נותקו.`);
              setNext(''); setConfirmation('');
            } else {
              await api('/password', 'POST', { current, next, confirmation });
              window.dispatchEvent(new Event('session-expired'));
            }
          } catch (e) {
            setError((e as Error).message);
          } finally {
            submitting.current = false; setBusy(false);
          }
        }}
      >
        <div className="modal-top">
          <h2>הגדרות חשבון</h2>
          <button type="button" className="icon-button" aria-label="סגירה" disabled={busy} onClick={() => { if (!submitting.current) onClose(); }}>
            <X />
          </button>
        </div>
        {users && (
          <label>
            איזה חשבון מעדכנים?
            <select disabled={busy} value={child} onChange={(e) => { if (submitting.current) return; setChild(e.target.value); setCurrent(''); setNext(''); setConfirmation(''); setMessage(''); setError(''); }}>
              <option value="">החשבון שלי</option>
              {users.map((u) => (
                <option key={u.id} value={u.id}>
                  {childLabel(u, users)}
                </option>
              ))}
            </select>
          </label>
        )}
        {!child && (
          <label>
            סיסמה נוכחית
            <input
              disabled={busy}
              type="password"
              autoComplete="current-password"
              aria-describedby="account-password-error"
              required
              value={current}
              onChange={(e) => setCurrent(e.target.value)}
            />
          </label>
        )}
        <label>
          סיסמה חדשה · לפחות 14 תווים
          <input
              disabled={busy}
            type={show ? "text" : "password"}
            autoComplete="new-password" aria-describedby="account-password-help account-password-error"
            required
            minLength={14}
            maxLength={256}
            value={next}
            onChange={(e) => setNext(e.target.value)}
          />
        </label>
        <label>אישור הסיסמה החדשה<input disabled={busy} type={show ? 'text' : 'password'} autoComplete="new-password" aria-describedby="account-password-help account-password-error" required minLength={14} maxLength={256} value={confirmation} onChange={e => setConfirmation(e.target.value)} /></label>
        <label className="secret-toggle"><input type="checkbox" checked={show} onChange={e => setShow(e.target.checked)} /> הצגת הסיסמה</label>
        <p id="account-password-help" className="muted">{passphraseHelp}</p>
        <p id="account-password-error" className="error" role={error ? "alert" : undefined}>{error}</p>
        <p className="muted">
          שינוי סיסמה מנתק את כל החיבורים של החשבון. אפשר להשתמש במשפט סודי שקל לזכור.
        </p>
        {message && <p role="status">{message}</p>}
        <button className="primary" disabled={busy}>
          עדכון סיסמה <Check size={17} />
        </button>
      </form>
    </div>
  );
}

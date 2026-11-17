import React, { useEffect, useRef } from 'react';
import { X } from 'lucide-react';
import { validNotificationReturnTarget, restoreNotificationFocus } from './notificationFocus';
import { ErrorReference } from './ErrorReference';
import type { ShownError } from './api';

/** Only an error that passes by itself may disappear unread; one that needs a step stays. */
export function toastExpires(error: ShownError) {
  return error.category === 'TRANSIENT';
}
export function ErrorToast({ error, onDismiss }: { error: ShownError; onDismiss: () => void }) {
  const dismiss = useRef(onDismiss);
  const notice = useRef<HTMLDivElement>(null), previousControl = useRef<HTMLElement | null>(null);
  dismiss.current = onDismiss;
  useEffect(() => {
    const remember = () => {
      const active = document.activeElement as HTMLElement | null;
      if (active && !notice.current?.contains(active) && validNotificationReturnTarget(active)) previousControl.current = active;
    };
    remember();
    document.addEventListener('focusin', remember);
    return () => document.removeEventListener('focusin', remember);
  }, []);
  const expires = toastExpires(error);
  useEffect(() => {
    if (!expires) return;
    const timer = setTimeout(() => dismiss.current(), 8000);
    return () => clearTimeout(timer);
  }, [error, expires]);
  return <div ref={notice} role="alert" className="toast">
    <div className="toast-body">
      <span>{error.text}</span>
      <ErrorReference code={error.code} refId={error.ref} />
    </div>
    <button type="button" className="icon-button" aria-label="סגירת הודעה" onClick={event => {
      const closeButton = event.currentTarget, target = previousControl.current;
      onDismiss();
      queueMicrotask(() => restoreNotificationFocus(target, closeButton));
    }}><X size={18}/></button>
  </div>;
}

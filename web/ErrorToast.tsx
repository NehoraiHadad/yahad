import React, { useEffect, useRef } from 'react';
import { X } from 'lucide-react';
import { validNotificationReturnTarget, restoreNotificationFocus } from './notificationFocus';

export function ErrorToast({ message, onDismiss }: { message: string; onDismiss: () => void }) {
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
  useEffect(() => {
    const timer = setTimeout(() => dismiss.current(), 8000);
    return () => clearTimeout(timer);
  }, [message]);
  return <div ref={notice} role="alert" className="toast">
    <span>{message}</span>
    <button type="button" className="icon-button" aria-label="סגירת הודעה" onClick={event => {
      const closeButton = event.currentTarget, target = previousControl.current;
      onDismiss();
      queueMicrotask(() => restoreNotificationFocus(target, closeButton));
    }}><X size={18}/></button>
  </div>;
}

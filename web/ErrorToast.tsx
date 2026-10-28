import React, { useEffect, useRef } from 'react';
import { X } from 'lucide-react';

export function ErrorToast({ message, onDismiss }: { message: string; onDismiss: () => void }) {
  const dismiss = useRef(onDismiss);
  dismiss.current = onDismiss;
  useEffect(() => {
    const timer = setTimeout(() => dismiss.current(), 8000);
    return () => clearTimeout(timer);
  }, [message]);
  return <div role="alert" className="toast">
    <span>{message}</span>
    <button type="button" className="icon-button" aria-label="סגירת הודעה" onClick={onDismiss}><X size={18}/></button>
  </div>;
}

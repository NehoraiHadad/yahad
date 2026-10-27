import { useEffect, type RefObject } from 'react';
/** Keep keyboard focus inside a modal and return it to the opener on close. */
export function useDialogFocus(ref: RefObject<HTMLElement | null>, enabled: boolean) {
  useEffect(() => {
    if (!enabled || !ref.current) return;
    const opener = document.activeElement as HTMLElement | null, dialog = ref.current;
    const controls = () => Array.from(dialog.querySelectorAll<HTMLElement>('button:not(:disabled),input:not(:disabled),select:not(:disabled),textarea:not(:disabled),a[href]'));
    (dialog.querySelector<HTMLElement>('input:not(:disabled)') || controls()[0])?.focus();
    const trap = (e: KeyboardEvent) => {
      if (e.key !== 'Tab') return;
      const items = controls(), first = items[0], last = items[items.length - 1];
      if (!items.includes(document.activeElement as HTMLElement)) { e.preventDefault(); (e.shiftKey ? last : first)?.focus(); }
      else if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last?.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first?.focus(); }
    };
    dialog.addEventListener('keydown', trap);
    return () => { dialog.removeEventListener('keydown', trap); if (opener?.isConnected) opener.focus(); };
  }, [enabled, ref]);
}

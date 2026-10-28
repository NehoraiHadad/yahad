import { useEffect, type RefObject } from 'react';

const focusable = 'button:not(:disabled),input:not(:disabled),select:not(:disabled),textarea:not(:disabled),a[href]';
/** Disabling a submitting control can drop focus onto body. Recover inside the
 * dialog so Escape and keyboard navigation still work after either outcome. */
export function recoverDialogFocus(dialog: HTMLElement, active: Element | null) {
  if (active && active !== document.body && dialog.contains(active) && !(active as HTMLButtonElement).disabled) return;
  if (active && active !== document.body && active !== dialog && !dialog.contains(active)) return;
  (dialog.querySelector<HTMLElement>('input:not(:disabled)') || dialog.querySelector<HTMLElement>(focusable) || dialog).focus();
}

/** Keep keyboard focus inside a modal and return it to the opener on close. */
export function useDialogFocus(ref: RefObject<HTMLElement | null>, enabled: boolean) {
  useEffect(() => {
    if (!enabled || !ref.current) return;
    const opener = document.activeElement as HTMLElement | null, dialog = ref.current;
    // The parent may temporarily disable the opener while refreshing after save.
    const fallback = opener?.closest('section')?.querySelector<HTMLElement>('h2[tabindex]');
    dialog.tabIndex = -1;
    const controls = () => Array.from(dialog.querySelectorAll<HTMLElement>(focusable));
    (dialog.querySelector<HTMLElement>('input:not(:disabled)') || controls()[0] || dialog).focus();
    const trap = (e: KeyboardEvent) => {
      if (e.key !== 'Tab') return;
      const items = controls(), first = items[0] || dialog, last = items[items.length - 1] || dialog;
      if (!items.includes(document.activeElement as HTMLElement)) { e.preventDefault(); (e.shiftKey ? last : first).focus(); }
      else if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
    };
    const observer = new MutationObserver(() => recoverDialogFocus(dialog, document.activeElement));
    observer.observe(dialog, { subtree: true, childList: true, attributes: true, attributeFilter: ['disabled'] });
    dialog.addEventListener('keydown', trap);
    return () => {
      observer.disconnect(); dialog.removeEventListener('keydown', trap);
      // React finishes the closing/refresh render before returning focus.
      queueMicrotask(() => {
        if (opener?.isConnected && !(opener as HTMLButtonElement).disabled) opener.focus();
        else if (fallback?.isConnected) fallback.focus();
      });
    };
  }, [enabled, ref]);
}

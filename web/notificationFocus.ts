const controls = 'button,input,select,textarea,a[href],[tabindex]';

/** Revalidate at dismissal: permissions/navigation may have hidden or removed the old control. */
export function validNotificationReturnTarget(target: HTMLElement | null): target is HTMLElement {
  if (!target?.isConnected || !target.matches(controls) || target.tabIndex < 0 ||
      target.matches(':disabled') || target.closest('[hidden],[inert],[aria-disabled="true"]') ||
      !target.getClientRects().length) return false;
  const style = getComputedStyle(target);
  if (style.visibility === 'hidden' || style.visibility === 'collapse' || style.display === 'none') return false;
  const modal = document.querySelector('[role="dialog"][aria-modal="true"]');
  return !modal || modal.contains(target);
}

/** Only explicit dismissal restores focus; a timeout must never call this helper. */
export function restoreNotificationFocus(target: HTMLElement | null, closeButton: HTMLElement) {
  const active = document.activeElement;
  if (active !== closeButton && active !== document.body) return;
  if (validNotificationReturnTarget(target)) target.focus({ preventScroll: true });
}

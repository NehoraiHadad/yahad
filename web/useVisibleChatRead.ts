import { useEffect, type RefObject } from 'react';
import { api } from './api';

export function visibleMessageIds(container: HTMLElement, visible: boolean, focused: boolean): string[] {
  if (!visible || !focused || container.getClientRects().length === 0 ||
      container.closest('[inert], [hidden], [aria-hidden="true"]') ||
      window.getComputedStyle(container).visibility !== 'visible') return [];
  if (Array.from(container.ownerDocument.querySelectorAll<HTMLElement>('[aria-modal="true"]'))
    .some(dialog => dialog.getClientRects().length > 0)) return [];
  const bounds = container.getBoundingClientRect();
  return Array.from(container.querySelectorAll<HTMLElement>('[data-local-unread-id]')).filter(row => {
    const rect = row.getBoundingClientRect();
    return rect.height > 0 && rect.width > 0 && rect.bottom > Math.max(bounds.top, 0) &&
      rect.top < Math.min(bounds.bottom, window.innerHeight) && rect.right > Math.max(bounds.left, 0) &&
      rect.left < Math.min(bounds.right, window.innerWidth);
  }).map(row => row.dataset.localUnreadId!).filter(Boolean);
}


/** Preserve the existing capability-gated provider receipt, but only for the same
 * rendered rows acknowledged locally. Local counters never depend on provider success. */
export async function acknowledgeVisibleMessages(group: string, ids: string[], providerRead: boolean, active: () => boolean) {
  await api('/groups/' + encodeURIComponent(group) + '/viewed', 'POST', { ids });
  if (providerRead && active())
    await api('/groups/' + encodeURIComponent(group) + '/read', 'POST', { ids }).catch(() => {});
}

/** A GET, prefetch, hidden tab, search result or background SSE is not a view. */
export function useVisibleChatRead(container: RefObject<HTMLDivElement | null>, group: string | null,
  enabled: boolean, providerRead: boolean, revision: unknown, onRead: () => Promise<void>) {
  useEffect(() => {
    if (!enabled || !group || !container.current) return;
    const element = container.current, seen = new Set<string>();
    let cancelled = false, frame = 0, pending = false;
    const check = async () => {
      if (cancelled || pending) return;
      const ids = visibleMessageIds(element, document.visibilityState === 'visible', document.hasFocus())
        .filter(id => !seen.has(id)).slice(0, 80);
      if (!ids.length) return;
      pending = true;
      try {
        await acknowledgeVisibleMessages(group, ids, providerRead, () => !cancelled &&
          ids.every(id => visibleMessageIds(element, document.visibilityState === 'visible', document.hasFocus()).includes(id)));
        if (cancelled) return;
        ids.forEach(id => seen.add(id));
        await onRead();
      } catch { /* Fail closed; a later scroll/focus/render retries without losing unread state. */ }
      finally { pending = false; }
    };
    const schedule = () => { cancelAnimationFrame(frame); frame = requestAnimationFrame(() => void check()); };
    // Rendering has committed before this effect; intersection and scroll handle
    // images resizing, initial scroll-to-bottom, and newly arriving messages.
    const observer = new IntersectionObserver(schedule, { root: element });
    element.querySelectorAll('[data-local-unread-id]').forEach(row => observer.observe(row));
    element.addEventListener('scroll', schedule);
    document.addEventListener('visibilitychange', schedule);
    window.addEventListener('focus', schedule);
    schedule();
    return () => {
      cancelled = true; cancelAnimationFrame(frame); observer.disconnect();
      element.removeEventListener('scroll', schedule);
      document.removeEventListener('visibilitychange', schedule);
      window.removeEventListener('focus', schedule);
    };
  }, [container, group, enabled, providerRead, revision, onRead]);
}

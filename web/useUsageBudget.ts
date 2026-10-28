import { useCallback, useEffect, useRef, useState } from 'react';
import { api, csrf, ApiError } from './api';
import type { UsageStatus } from '../src/usage';

export type UsageReason = 'checking' | 'hidden' | 'idle' | 'offline' | 'exhausted' | 'setup' | 'tabs' | 'unavailable' | '';
export function useUsageBudget(child: boolean, identity: string) {
  const [status, setStatus] = useState<UsageStatus | null>(null);
  const [reason, setReason] = useState<UsageReason>(child ? 'checking' : '');
  const suspended = child && reason !== '';
  const suspendedRef = useRef(suspended); suspendedRef.current = suspended;
  const activation = useRef(0);
  const trigger = useRef<(resume?: boolean) => void>(() => {});
  const resume = useCallback(() => trigger.current(true), []);
  const reconcile = useCallback(() => trigger.current(), []);
  useEffect(() => {
    if (!child) return;
    let alive = true, seq = 0, revision = 0, last = performance.now(), deadline = 0;
    let idle = false, exhausted = false, idleMs = 300000, latest: UsageStatus | null = null;
    let tab: string | null = null, opening = false;
    const visible = () => document.visibilityState === 'visible' && document.hasFocus();
    const block = (why: UsageReason) => {
      if (!why && suspendedRef.current) activation.current++;
      suspendedRef.current = !!why; setReason(why);
    };
    const failed = (error: unknown) => {
      deadline = 0;
      if (error instanceof ApiError && error.code === 'USAGE_TAB_EXPIRED') { tab = null; block('checking'); }
      else if (error instanceof ApiError) { setStatus(null); block(error.code === 'USAGE_TAB_LIMIT' ? 'tabs' : 'unavailable'); }
      else block('offline');
    };
    const beat = (resume = false) => {
      if (!alive) return;
      if (resume) { idle = false; last = performance.now(); }
      if (performance.now() - last >= idleMs) idle = true;
      const active = visible() && !idle && navigator.onLine;
      const request = ++revision, sent = performance.now();
      const why: UsageReason = !navigator.onLine ? 'offline' : !visible() ? 'hidden' : idle ? 'idle' : exhausted ? 'exhausted' : 'checking';
      // A response, never focus/online alone, authorizes revealing content.
      if (!active || deadline <= performance.now() || resume) block(why);
      if (!tab) {
        if (!active || opening) return;
        opening = true;
        // Admission cannot bill time. A lost/unmounted response reserves at most
        // one slot for 15s; late responses cannot activate an unmounted client.
        void api<{ tab: string }>('/usage/open', 'POST', {}).then(value => {
          opening = false; if (!alive) return;
          tab = value.tab; seq = 0; beat();
        }).catch(error => { opening = false; if (alive) failed(error); });
        return;
      }
      void api<UsageStatus>('/usage/heartbeat', 'POST', { tab, seq: ++seq, active }).then(value => {
        if (!alive || request !== revision) return;
        latest = value; setStatus(value); idleMs = value.idleMs; exhausted = value.exhausted;
        const lease = value.leaseUntil - value.serverNow;
        // Subtract full request latency conservatively; use monotonic browser
        // time only to expire permission, never to bill time or reset a day.
        deadline = sent + Math.max(0, lease);
        block(value.timezoneConfirmed === false && value.minutes !== null ? 'setup' : value.exhausted ? 'exhausted' : !active ? why : deadline > performance.now() ? '' : 'offline');
      }).catch(error => { if (alive && request === revision) failed(error); });
    };
    trigger.current = beat;
    const activity = () => { if (visible() && !idle) last = performance.now(); };
    const pause = () => { deadline = 0; block('hidden'); beat(); };
    const focus = () => { deadline = 0; block('checking'); beat(); };
    const change = () => visible() ? focus() : pause();
    const failure = () => { deadline = 0; block('checking'); beat(); };
    const pagehide = () => {
      deadline = 0; block('hidden'); revision++;
      if (!tab) return;
      // Best effort only. The server expires the last lease within 15 seconds.
      void fetch('/api/usage/heartbeat', { method: 'POST', credentials: 'same-origin', keepalive: true,
        headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
        body: JSON.stringify({ tab, seq: ++seq, active: false }) }).catch(() => {});
    };
    beat();
    const timer = setInterval(beat, 5000);
    const expiry = setInterval(() => {
      if (deadline && deadline <= performance.now()) { deadline = 0; block(exhausted || latest?.remainingMs === 0 ? 'exhausted' : 'checking'); beat(); }
    }, 250);
    for (const event of ['pointerdown', 'keydown', 'touchstart', 'scroll']) window.addEventListener(event, activity, { passive: true, capture: true });
    window.addEventListener('focus', focus); window.addEventListener('blur', pause);
    window.addEventListener('online', focus); window.addEventListener('offline', pause);
    window.addEventListener('pagehide', pagehide); window.addEventListener('pageshow', focus);
    window.addEventListener('usage-required', failure);
    document.addEventListener('visibilitychange', change);
    return () => {
      pagehide(); alive = false; trigger.current = () => {};
      clearInterval(timer); clearInterval(expiry);
      for (const event of ['pointerdown', 'keydown', 'touchstart', 'scroll']) window.removeEventListener(event, activity, true);
      window.removeEventListener('focus', focus); window.removeEventListener('blur', pause);
      window.removeEventListener('online', focus); window.removeEventListener('offline', pause);
      window.removeEventListener('pagehide', pagehide); window.removeEventListener('pageshow', focus);
      window.removeEventListener('usage-required', failure); document.removeEventListener('visibilitychange', change);
    };
  }, [child, identity]);
  return { status, suspended, suspendedRef, activation, reason, resume, reconcile };
}

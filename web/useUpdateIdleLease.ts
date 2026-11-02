import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { api, ApiError, csrf } from './api';
import { newClientId } from './clientId';

type Lease = { id: string; generation: string; previousGeneration?: string; unsafe: boolean;
  phase: 'opening' | 'open' | 'releasing' };
type Saved = { format: 1; tab: string; current: string | null; leases: Lease[] };
type LeaseIdentity = Pick<Lease, 'id' | 'generation'>;
function settlementSnapshot(leases: LeaseIdentity[], currentId: string): LeaseIdentity[] {
  return leases.filter(lease => lease.id !== currentId).slice(0, 128)
    .map(lease => ({ id: lease.id, generation: lease.generation }));
}
const uuid = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/;
const storageKey = (identity: string) => `yahad-update-idle:${identity}`;
function readSaved(identity: string): Saved {
  const raw = sessionStorage.getItem(storageKey(identity));
  if (!raw) return { format: 1, tab: newClientId(), current: null, leases: [] };
  if (raw.length > 1024 * 1024) throw new Error('Browser admission state refused');
  const value = JSON.parse(raw) as Saved;
  if (value.format !== 1 || !uuid.test(value.tab) || value.current !== null && !uuid.test(value.current) ||
      !Array.isArray(value.leases) || value.leases.length > 4096) throw new Error('Browser admission state refused');
  const ids = new Set<string>();
  for (const lease of value.leases) {
    if (!uuid.test(lease.id) || !uuid.test(lease.generation) || ids.has(lease.id) ||
        lease.previousGeneration !== undefined && !uuid.test(lease.previousGeneration) ||
        typeof lease.unsafe !== 'boolean' || !['opening', 'open', 'releasing'].includes(lease.phase))
      throw new Error('Browser admission state refused');
    ids.add(lease.id);
  }
  if (value.current !== null && !ids.has(value.current)) throw new Error('Browser admission state refused');
  return value;
}
function save(identity: string, value: Saved) {
  if (value.leases.length > 4096) throw new Error('Browser admission state full');
  sessionStorage.setItem(storageKey(identity), JSON.stringify(value));
}
function put(identity: string, lease: Lease) {
  const value = readSaved(identity), index = value.leases.findIndex(entry => entry.id === lease.id);
  if (index < 0) value.leases.push(lease); else value.leases[index] = lease;
  value.current = lease.id; save(identity, value);
}
function remove(identity: string, lease: LeaseIdentity) {
  const value = readSaved(identity);
  // An old keepalive response cannot erase a newly admitted generation.
  value.leases = value.leases.filter(entry => entry.id !== lease.id || entry.generation !== lease.generation);
  if (value.current === lease.id && !value.leases.some(entry => entry.id === lease.id)) value.current = null;
  save(identity, value);
}
async function claim(tab: string): Promise<(() => void) | null | undefined> {
  if (!navigator.locks) return undefined;
  return new Promise(resolve => {
    void navigator.locks.request(`yahad-update-idle:${tab}`, { mode: 'exclusive', ifAvailable: true }, lock => {
      if (!lock) { resolve(null); return; }
      return new Promise<void>(release => resolve(release));
    }).catch(() => resolve(undefined));
  });
}

// Keep only Booleans, never message text or media. An unsafe unmount is not
// evidence that a draft, recording or unknown send was safely settled.
type Blocker = { identity: string; serial: number; unsafe: boolean; mounted: boolean };
const blockers = new Map<symbol, Blocker>(), observers = new Set<() => void>();
let blockerSerial = 0;
const safe = () => ![...blockers.values()].some(blocker => blocker.unsafe);
const liveSafe = () => ![...blockers.values()].some(blocker => blocker.unsafe && blocker.mounted);
const orphanKeys = (identity: string) => [...blockers].filter(([, blocker]) =>
  blocker.identity === identity && blocker.unsafe && !blocker.mounted).map(([key]) => key);
const orphanSnapshot = (identity: string) => orphanKeys(identity).map(key => ({ key, serial: blockers.get(key)!.serial }));
const orphanRevision = (identity: string) => orphanKeys(identity).map(key => blockers.get(key)!.serial).join(',');
const changed = () => observers.forEach(observer => observer());
function clearUnmountedSnapshot(identity: string, snapshot: Array<{ key: symbol; serial: number }>) {
  for (const { key, serial } of snapshot) {
    const blocker = blockers.get(key);
    if (blocker?.identity === identity && blocker.serial === serial && blocker.unsafe && !blocker.mounted) blockers.delete(key);
  }
  changed();
}
export function useUpdateIdleBlocker(identity: string, unsafe: boolean) {
  const key = useRef(Symbol());
  const previous = blockers.get(key.current);
  const serial = previous?.identity === identity && previous.mounted && previous.unsafe === unsafe ? previous.serial : ++blockerSerial;
  blockers.set(key.current, { identity, serial, unsafe, mounted: true }); changed();
  useEffect(() => () => {
    const blocker = blockers.get(key.current);
    if (blocker?.unsafe) { blocker.mounted = false; blocker.serial = ++blockerSerial; } else blockers.delete(key.current);
    changed();
  }, []);
  return useCallback(() => {
    const blocker = blockers.get(key.current);
    if (blocker && !blocker.unsafe) { blocker.unsafe = true; blocker.serial = ++blockerSerial; }
    changed();
  }, []);
}
const freeze = () => document.querySelectorAll('.app-shell').forEach(element => element.setAttribute('inert', ''));
const thaw = () => document.querySelectorAll('.app-shell').forEach(element => element.removeAttribute('inert'));
// These baselines exist only in this document's memory. Field contents are never
// serialized; durable admission records contain only the resulting unsafe bit.
const editorBaselines = new WeakMap<HTMLElement, { value: string | null; edited: boolean }>();
const dirtyEditors = new Set<HTMLElement>();
function editorFrom(event: Event): HTMLElement | null {
  if (!(event.target instanceof Element)) return null;
  let element = event.target.closest<HTMLElement>('input, textarea, select, [contenteditable="true"]');
  const label = event.target.closest('label');
  if (!element && label instanceof HTMLLabelElement) element = label.control;
  if (!element || element.closest('.composer-wrap, .update-idle-repair') ||
      element instanceof HTMLInputElement && ['search', 'hidden', 'button', 'submit'].includes(element.type)) return null;
  return element;
}
function editorValue(element: HTMLElement) {
  if (element instanceof HTMLInputElement) return JSON.stringify([element.value, element.checked]);
  if (element instanceof HTMLTextAreaElement) return element.value;
  if (element instanceof HTMLSelectElement) return JSON.stringify([...element.options].filter(option => option.selected).map(option => option.value));
  return element.innerHTML;
}
function captureEditor(event: Event) {
  const element = editorFrom(event); if (!element) return;
  const baseline = editorBaselines.get(element);
  // Server-driven renders before the first actual edit are not unsaved work.
  if (!baseline?.edited) editorBaselines.set(element, { value: editorValue(element), edited: false });
}
function changedEditor(event: Event) {
  const element = editorFrom(event); if (!element) return;
  const baseline = editorBaselines.get(element) ?? { value: null, edited: false };
  baseline.edited = true; editorBaselines.set(element, baseline);
  if (baseline.value === null || editorValue(element) !== baseline.value) dirtyEditors.add(element);
  else dirtyEditors.delete(element);
  changed();
}
function untrackedEditor() {
  for (const element of dirtyEditors) {
    const baseline = editorBaselines.get(element);
    if (!element.isConnected || baseline?.value !== null && editorValue(element) === baseline?.value) dirtyEditors.delete(element);
  }
  return dirtyEditors.size > 0;
}

/** Admission precedes interaction. Lost acknowledgements and unsafe prior pages
 * remain durable blockers; elapsed time and BroadcastChannel silence prove none. */
export function useUpdateIdleLease(identity: string | null) {
  const [admission, setAdmission] = useState<{ identity: string; ready: boolean } | null>(null);
  const [settlementRequired, setSettlementRequired] = useState(false);
  const [otherLeases, setOtherLeases] = useState<LeaseIdentity[]>([]);
  const [repairBusy, setRepairBusy] = useState(false), [repairError, setRepairError] = useState('');
  const [repairSafe, setRepairSafe] = useState(false);
  const [orphanWork, setOrphanWork] = useState('');
  const repairing = useRef(false);
  const current = useRef<{ identity: string; lease: Lease } | null>(null);
  const tracked = useRef<{ identity: string; lease: Lease } | null>(null);
  const refreshPrevious = useCallback(async (preserveError = false) => {
    const active = current.current;
    if (!identity || active?.identity !== identity) return;
    try {
      const result = await api<{ leases: LeaseIdentity[] }>('/update-idle/leases');
      if (!Array.isArray(result.leases) || result.leases.length > 4096) throw new Error();
      const ids = new Set<string>();
      for (const lease of result.leases) {
        if (!lease || !uuid.test(lease.id) || !uuid.test(lease.generation) || ids.has(lease.id)) throw new Error();
        ids.add(lease.id);
      }
      if (current.current?.identity !== identity || current.current.lease.id !== active.lease.id ||
          current.current.lease.generation !== active.lease.generation) return;
      // Never offer the current ID, even if its observed generation changed.
      setOtherLeases(result.leases.filter(lease => lease.id !== active.lease.id));
      if (!preserveError) setRepairError('');
    } catch {
      if (current.current?.identity === identity) setRepairError('לא ניתן לבדוק כרגע את החלונות הקודמים. אפשר לנסות שוב.');
    }
  }, [identity]);
  const settlePrevious = useCallback(async (confirmed: boolean, expectedOrphanRevision: string) => {
    const active = current.current;
    if (!confirmed || !identity || active?.identity !== identity || repairing.current) return;
    if (!liveSafe() || untrackedEditor()) {
      setRepairError('סיימו את הטיוטה, ההקלטה או השליחה הפתוחה בחלון הזה לפני האישור.'); return;
    }
    if (orphanRevision(identity) !== expectedOrphanRevision) {
      setRepairError('נוספה פעילות שצריך לבדוק. בדקו שוב לפני האישור.'); return;
    }
    const selected = settlementSnapshot(otherLeases, active.lease.id);
    const unmounted = orphanSnapshot(identity);
    if (!selected.length && !unmounted.length) return;
    repairing.current = true; setRepairBusy(true); setRepairError('');
    try {
      // Only this explicitly reviewed snapshot is submitted. A fresh inventory
      // must not silently add another window to the user's confirmation.
      await api('/update-idle/settle', 'POST', { leases: selected, confirmClosed: true });
      for (const lease of selected) remove(identity, lease);
      // No mounted work, newer unmounted work or another account is cleared.
      // This touches only safety metadata; it cannot submit or delete a message.
      clearUnmountedSnapshot(identity, unmounted);
      if (current.current?.identity === identity) {
        const value = readSaved(identity);
        setSettlementRequired(value.leases.some(lease => lease.id !== current.current?.lease.id));
      }
    } catch {
      if (current.current?.identity === identity)
        setRepairError('לא התקבל אישור לסיום הפעילות הקודמת. בדקו את המצב לפני ניסיון נוסף.');
    } finally {
      repairing.current = false; setRepairBusy(false); await refreshPrevious(true);
    }
  }, [identity, otherLeases, refreshPrevious]);
  useEffect(() => {
    if (!identity || admission?.identity !== identity || !admission.ready) {
      setOtherLeases([]); setRepairSafe(false); setOrphanWork(''); return;
    }
    let alive = true, queued = false;
    const safety = () => {
      if (queued) return; queued = true;
      queueMicrotask(() => {
        queued = false;
        if (alive) { setRepairSafe(liveSafe() && !untrackedEditor()); setOrphanWork(orphanRevision(identity)); }
      });
    };
    observers.add(safety); safety(); void refreshPrevious();
    document.addEventListener('input', safety, true); document.addEventListener('change', safety, true);
    document.addEventListener('focusin', safety, true);
    return () => {
      alive = false; observers.delete(safety);
      document.removeEventListener('input', safety, true); document.removeEventListener('change', safety, true);
      document.removeEventListener('focusin', safety, true);
    };
  }, [identity, admission?.identity, admission?.ready, refreshPrevious]);
  useLayoutEffect(() => {
    if (identity && admission?.identity === identity && admission.ready && current.current?.identity === identity) thaw();
  }, [identity, admission]);
  const releaseIfSafe = useCallback(async () => {
    const active = current.current;
    if (!active || !safe()) return;
    freeze(); current.current = null;
    setAdmission({ identity: active.identity, ready: false });
    const lease: Lease = { ...active.lease, unsafe: false, phase: 'releasing' };
    try {
      put(active.identity, lease);
      const response = await fetch('/api/update-idle/lease', {
        method: 'POST', credentials: 'same-origin', keepalive: true,
        headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
        body: JSON.stringify({ id: lease.id, generation: lease.generation, action: 'release' }),
        signal: AbortSignal.timeout(1500),
      });
      if (response.ok) {
        remove(active.identity, lease);
        if (tracked.current?.identity === active.identity)
          setSettlementRequired(readSaved(active.identity).leases.some(entry => entry.id !== tracked.current?.lease.id));
      }
    } catch { /* Keep the persisted ID for explicit reconciliation, never expire it. */ }
  }, []);
  useEffect(() => {
    if (!identity) { setAdmission(null); return; }
    let alive = true, timer: ReturnType<typeof setTimeout> | undefined, unlock: (() => void) | undefined;
    let request = 0, delay = 1000, stateRefused = false;
    const persistSafety = () => {
      const active = tracked.current;
      if (stateRefused || active?.identity !== identity) return;
      const unsafe = untrackedEditor() || !safe();
      if (active.lease.unsafe === unsafe) return;
      active.lease.unsafe = unsafe;
      try {
        const value = readSaved(identity), stored = value.leases.find(entry => entry.id === active.lease.id);
        if (stored?.generation === active.lease.generation && stored.phase !== 'releasing') {
          stored.unsafe = active.lease.unsafe; save(identity, value);
        }
      } catch {
        stateRefused = true; freeze();
        queueMicrotask(() => { if (alive) setAdmission({ identity, ready: false }); });
      }
    };
    observers.add(persistSafety);
    const open = async (retainedPage = false) => {
      const generation = ++request;
      freeze(); setAdmission({ identity, ready: false });
      try {
        const saved = readSaved(identity);
        save(identity, saved); // The name being locked must equal the persisted tab name.
        unlock?.(); unlock = undefined;
        let ownership = await claim(saved.tab), unique = typeof ownership === 'function';
        if (!alive || generation !== request) { ownership?.(); return; }
        if (!unique) {
          // A duplicated tab can inherit sessionStorage. Neither lock contention
          // nor lack of Web Locks permits it to take over copied lease IDs.
          saved.tab = newClientId(); save(identity, saved);
          ownership = await claim(saved.tab);
          if (!alive || generation !== request) { ownership?.(); return; }
          if (ownership === null) throw new Error('Browser admission collision');
        }
        unlock = ownership ?? undefined;
        const prior = saved.leases.find(entry => entry.id === saved.current);
        const retained = retainedPage && prior && tracked.current?.identity === identity &&
          tracked.current.lease.id === prior.id && tracked.current.lease.generation === prior.generation;
        const reclaim = unique && prior && prior.phase !== 'releasing' && (!prior.unsafe || retained);
        let lease: Lease = { id: newClientId(), generation: newClientId(), unsafe: untrackedEditor() || !safe(), phase: 'opening' };
        if (reclaim && prior) lease = prior.phase === 'opening' ? { ...prior }
          : { id: prior.id, generation: newClientId(), previousGeneration: prior.generation, unsafe: prior.unsafe, phase: 'opening' };
        const start = () => {
          put(identity, lease); tracked.current = { identity, lease };
          setSettlementRequired(readSaved(identity).leases.some(entry => entry.id !== lease.id));
        };
        start();
        let fallback = !!reclaim;
        const attempt = () => {
          void api('/update-idle/lease', 'POST', { id: lease.id, generation: lease.generation,
            ...(lease.previousGeneration ? { previousGeneration: lease.previousGeneration } : {}), action: 'open' }).then(() => {
            if (!alive || generation !== request) return;
            lease.phase = 'open'; put(identity, lease);
            current.current = { identity, lease }; delay = 1000;
            setAdmission({ identity, ready: true });
          }).catch(error => {
            if (!alive || generation !== request) return;
            if (fallback && error instanceof ApiError && error.status === 409) {
              // Preserve the conflicting generation for owner settlement. A
              // fresh ID cannot clear another active tab or a late old request.
              fallback = false;
              lease = { id: newClientId(), generation: newClientId(), unsafe: untrackedEditor() || !safe(), phase: 'opening' };
              try { start(); } catch { return; }
            }
            timer = setTimeout(attempt, delay); delay = Math.min(30000, delay * 2);
          });
        };
        attempt();
      } catch { setSettlementRequired(true); }
    };
    const hide = () => {
      request++; freeze(); setAdmission({ identity, ready: false });
      if (timer) clearTimeout(timer);
      const editor = untrackedEditor();
      if (safe() && !editor) void releaseIfSafe();
      else {
        const active = tracked.current;
        if (active?.identity === identity) {
          active.lease.unsafe = true;
          try { put(identity, active.lease); } catch { /* Retain the server fence. */ }
        }
      }
      unlock?.(); unlock = undefined;
    };
    const show = (event: PageTransitionEvent) => { if (event.persisted) void open(true); };
    void open();
    for (const type of ['focusin', 'pointerdown', 'keydown', 'beforeinput']) document.addEventListener(type, captureEditor, true);
    for (const type of ['input', 'change']) document.addEventListener(type, changedEditor, true);
    window.addEventListener('pagehide', hide); window.addEventListener('pageshow', show);
    return () => {
      alive = false; request++; if (timer) clearTimeout(timer);
      observers.delete(persistSafety); unlock?.();
      for (const type of ['focusin', 'pointerdown', 'keydown', 'beforeinput']) document.removeEventListener(type, captureEditor, true);
      for (const type of ['input', 'change']) document.removeEventListener(type, changedEditor, true);
      window.removeEventListener('pagehide', hide); window.removeEventListener('pageshow', show);
    };
  }, [identity, releaseIfSafe]);
  return { ready: !!identity && admission?.identity === identity && admission.ready,
    releaseIfSafe, settlementRequired, otherLeases, repairBusy, repairError,
    repairAllowed: repairSafe && !repairBusy, orphanRevision: orphanWork, refreshPrevious, settlePrevious };
}

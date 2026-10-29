import type { UpdateTarget, updateStatus } from '../src/updates';

export type { UpdateTarget };
export type UpdateStatus = Awaited<ReturnType<typeof updateStatus>>;
export type ConfiguredUpdate = Extract<UpdateStatus, { configured: true }>;
export type UpdateAction = 'check' | 'install' | 'set_mode';
export type LocalUpdateRequest = {
  action: UpdateAction;
  mode?: 'auto' | 'notify';
  target?: UpdateTarget;
  state: 'sending' | 'queued' | 'uncertain' | 'following';
  queuedId?: string;
  previousRequestId?: string;
  previousModeId?: string;
};

const activePhases = new Set(['awaiting_local_approval','checking', 'downloading', 'verifying', 'building', 'preparing',
  'stopping', 'backing_up', 'activating', 'checking_health', 'rolling_back']);
const repairPhases = new Set(['recovery_required', 'rollback_pending_health']);
const failedPhases = new Set(['failed', 'rolled_back', 'incompatible', 'recovery_required', 'rollback_pending_health']);
export const updatePhases: Record<string, string> = {
  awaiting_local_approval: 'ממתינים לאישור במחשב הביתי',
  idle: 'אין עדכון ממתין', available: 'גרסה חדשה זמינה', checking: 'בודקים גרסה חדשה ב־GitHub',
  downloading: 'מורידים את קובצי הגרסה', verifying: 'מאמתים את קובצי הגרסה',
  building: 'מכינים ובודקים את האפליקציה החדשה', preparing: 'מכינים את העדכון',
  stopping: 'עוצרים לרגע את האפליקציה', backing_up: 'שומרים גיבוי מוצפן',
  activating: 'מפעילים את הגרסה החדשה', checking_health: 'בודקים שהאפליקציה תקינה',
  healthy: 'העדכון הושלם בהצלחה', rolled_back: 'חזרנו לגרסה הקודמת', failed: 'הפעולה לא הושלמה',
  incompatible: 'הגרסה אינה מתאימה למערכת', rolling_back: 'חוזרים לגרסה הקודמת',
  recovery_required: 'העדכון נעצר — נדרש תיקון',
  rollback_pending_health: 'החזרה לגרסה הקודמת עדיין לא אושרה כתקינה',
};

/** Native authority supplies the one meaningful confirmation; legacy web workers keep theirs. */
export function requiresWebUpdateConfirmation(data: ConfiguredUpdate) { return data.confirmation !== 'native'; }

export function sameUpdateTarget(a: UpdateTarget | null | undefined, b: UpdateTarget | null | undefined) {
  return !!a && !!b && a.version === b.version && a.fingerprint === b.fingerprint;
}

/** Missing/invalid targets from an older worker never enable an unbound install. */
export function availableUpdateTarget(data: ConfiguredUpdate | null): UpdateTarget | null {
  const target = data?.installTarget;
  if (!data || data.phase !== 'available' || !target || target.version !== data.latestVersion ||
    !/^\d{1,5}\.\d{1,5}\.\d{1,5}$/.test(target.version) || !/^[a-f0-9]{64}$/.test(target.fingerprint)) return null;
  return { version: target.version, fingerprint: target.fingerprint };
}

export function updateActionLabel(action: UpdateAction, mode?: 'auto' | 'notify', target?: UpdateTarget | null) {
  if (action === 'install') return target ? `התקנת גרסה ${target.version}` : 'התקנת הגרסה הזמינה';
  if (action === 'set_mode') return mode === 'auto' ? 'הפעלת עדכונים אוטומטיים' : 'מעבר להודעה בלבד';
  return mode === 'auto' ? 'בדיקה ועדכון אוטומטי אם תימצא גרסה' : 'בדיקת גרסה חדשה';
}

/** Worker phases also describe scheduled work; only the relevant journal acknowledges a request. */
export function observesRequest(data: UpdateStatus, request: LocalUpdateRequest) {
  if (!data.configured) return false;
  if (data.queuedRequest || data.queuedMode) return false;
  if (request.action === 'set_mode') {
    const journal = data.lastModeRequest;
    if (!journal || journal.mode !== request.mode) return false;
    return request.queuedId ? journal.id === request.queuedId : journal.id !== request.previousModeId;
  }
  const journal = data.lastRequest;
  if (!journal || journal.action !== request.action) return false;
  if (request.target && !sameUpdateTarget(request.target, journal.target)) return false;
  return request.queuedId ? journal.id === request.queuedId : journal.id !== request.previousRequestId;
}

export function reconcileUpdateRequest(data: UpdateStatus, local: LocalUpdateRequest | null) {
  if (!data.configured) return local;
  const queued = data.queuedRequest ?? (data.queuedMode ? { ...data.queuedMode, action: 'set_mode' as const } : null);
  if (queued) return { action: queued.action, mode: queued.mode, target: 'target' in queued ? queued.target : undefined, state: 'queued' as const, queuedId: queued.id,
    previousRequestId: local?.previousRequestId ?? data.lastRequest?.id,
    previousModeId: local?.previousModeId ?? data.lastModeRequest?.id };
  // A definite pending response means this POST did not publish its own work.
  // Follow the actual competing operation, which may have another action/target.
  if (local?.state === 'following' && (
    data.lastRequest && (data.lastRequest.state === 'running' || data.lastRequest.id !== local.previousRequestId) ||
    data.lastModeRequest && (data.lastModeRequest.state === 'pending' || data.lastModeRequest.id !== local.previousModeId)
  )) return null;
  if (local && observesRequest(data, local)) return null;
  // Older workers may consume a queue without any request journal. Do not
  // recycle an old phase as acknowledgement of this particular request.
  return local?.queuedId ? { ...local, state: 'uncertain' as const } : local;
}

export function updateNeedsRepair(data: ConfiguredUpdate) {
  return repairPhases.has(data.phase);
}

export function updateProgress(data: ConfiguredUpdate, local: LocalUpdateRequest | null = null) {
  // A repair barrier is authoritative even if an older queue/journal still says running.
  if (updateNeedsRepair(data)) return { kind: 'failure', active: false, title: updatePhases[data.phase],
    action: undefined, detail: data.phase === 'recovery_required'
      ? 'לא ניתן להמשיך אוטומטית בבטחה. נדרש תיקון במחשב שבו מותקנת האפליקציה. אין כאן אישור שהאפליקציה חזרה לפעול כראוי. רענון המצב רק בודק אם התקבל דיווח חדש; הוא לא מתחיל עדכון נוסף.'
      : 'שירות העדכון עדיין לא אישר שהגרסה הקודמת פועלת כראוי. נדרשת בדיקה במחשב שבו מותקנת האפליקציה לפני המשך עדכונים; אין לשלוח התקנה נוספת.' } as const;
  if (!Object.hasOwn(updatePhases, data.phase)) return { kind: 'unknown', active: true,
    title: 'אין דיווח מצב מוכר מהשירות', action: undefined,
    detail: 'אין כרגע אישור להתקדמות, להצלחה או לכשל. נבדוק שוב את המצב בלבד, בלי לשלוח בקשת עדכון נוספת.' } as const;

  if (data.phase === 'awaiting_local_approval') return { kind: 'queued', active: true,
    title: updatePhases.awaiting_local_approval, action: undefined,
    detail: 'פתחו את כלי האישור של יחד במחשב הביתי כדי לבדוק ולאשר את הפעולה. הבקשה נשמרה; אין צורך לשלוח אותה שוב.' } as const;

  // A timer-driven run may be newer than the retained manual-request journal.
  const last = data.lastRequest && (data.lastRequest.state === 'running' || !data.checkedAt ||
    (data.lastRequest.finishedAt !== null && data.checkedAt <= data.lastRequest.finishedAt)) ? data.lastRequest : null;
  const queued = data.queuedRequest;
  if (queued) return { kind: 'queued', active: true, title: 'הבקשה בתור — ממתינים לשירות העדכון',
    action: updateActionLabel(queued.action, queued.mode ?? data.mode, queued.target),
    detail: 'השירות עדיין לא אישר שהתחיל לטפל בבקשה הזאת. המצב מתעדכן אוטומטית; אין צורך ללחוץ שוב.' } as const;
  if (local?.state === 'following') return { kind: 'queued', active: true,
    title: 'כבר יש בקשה בטיפול — ממתינים לדיווח מהשירות', action: undefined,
    detail: 'הבקשה הזאת לא נוספה לתור. בודקים את הבקשה שכבר קיימת; הגרסה והשלב יוצגו לפי דיווח השירות, בלי לשלוח שוב.' } as const;
  if (local) return { kind: local.state === 'queued' ? 'queued' : 'unknown', active: true,
    title: local.state === 'sending' ? 'שולחים את הבקשה…' : local.state === 'queued' ? 'הבקשה נקלטה — ממתינים לדיווח מהשירות' : 'ממתינים לאישור מצב הבקשה',
    action: updateActionLabel(local.action, local.mode ?? data.mode, local.target),
    detail: local.state === 'sending' ? 'עדיין לא התקבל אישור מהשרת.' : local.state === 'queued'
      ? 'זהו אישור קבלה, לא אישור שההתקנה התחילה. ההתקדמות תופיע כאן אוטומטית.'
      : 'לא התקבל אישור חד־משמעי. בודקים שוב אוטומטית; אין להסיק שהפעולה נכשלה או לשלוח בקשה נוספת.' } as const;
  if (data.queuedMode || data.lastModeRequest?.state === 'pending') return { kind: 'queued', active: true,
    title: 'ממתינים להחלת שינוי המצב', action: updateActionLabel('set_mode', data.queuedMode?.mode ?? data.lastModeRequest?.mode),
    detail: 'המצב הפעיל מוצג בנפרד ויתחלף רק לאחר אישור מהשירות. אין צורך ללחוץ שוב.' } as const;
  if (last?.state === 'running' || activePhases.has(data.phase)) return { kind: 'running', active: true,
    title: activePhases.has(data.phase) ? updatePhases[data.phase] : 'שירות העדכון מטפל בבקשה',
    action: last?.state === 'running' ? updateActionLabel(last.action, data.mode, last.target) : undefined,
    detail: 'זהו השלב האחרון שדווח בפועל. שירות העדכון ממשיך במחשב גם אם סוגרים את הדפדפן. השאירו את המחשב פועל; בפתיחה מחדש בודקים את המצב מהשירות, בלי להתחיל שוב.' } as const;
  if (last?.state === 'failed' || failedPhases.has(data.phase)) return { kind: 'failure', active: false,
    title: last?.error === 'target_changed' ? 'יעד העדכון השתנה — נדרשת בדיקה חדשה' : last?.error === 'expired' ? 'תוקף הבקשה פג לפני הטיפול' : failedPhases.has(data.phase) ? updatePhases[data.phase] : 'הפעולה לא הושלמה',
    action: last?.state === 'failed' ? updateActionLabel(last.action, data.mode, last.target) : undefined,
    detail: last?.error === 'target_changed' ? 'ההתקנה שהתבקשה לא בוצעה: הגרסה או קובצי העדכון אינם זהים לאישור שנשלח. בדקו שוב ובחרו את הגרסה מחדש; לא נשלחת התקנה נוספת אוטומטית.' : last?.error === 'expired' ? 'הבקשה המתינה מעבר לתוקף שלה ולא בוצעה עבורה התקנה. אפשר לשלוח בקשה חדשה אם עדיין תרצו; הבקשה הזאת לא תישלח שוב אוטומטית.'
      : data.phase === 'rolled_back' ? 'הגרסה החדשה לא התקבלה. שירות העדכון דיווח שחזר לגרסה הקודמת.'
      : 'שירות העדכון דיווח על כשל. אין כאן אישור שהגרסה החדשה הותקנה.' } as const;
  if (last?.state === 'succeeded') return { kind: 'success', active: false,
    title: data.phase === 'healthy' ? updatePhases.healthy : data.phase === 'available' ? 'הבדיקה הסתיימה — נמצאה גרסה חדשה' : 'הבדיקה הסתיימה — אין עדכון ממתין',
    action: updateActionLabel(last.action, data.mode, last.target),
    detail: data.phase === 'healthy' ? 'שירות העדכון אישר שהגרסה החדשה פעילה ועברה בדיקת תקינות.'
      : data.phase === 'available' ? 'הגרסה זמינה, אך לא התקבלה הודעה שהותקנה.' : 'זהו המצב שאישר שירות העדכון בסיום הפעולה.' } as const;
  return { kind: data.error ? 'failure' : data.phase === 'healthy' ? 'success' : 'idle', active: false, title: updatePhases[data.phase] || 'מצב שירות העדכון',
    action: undefined, detail: 'מצב השרת האחרון שדווח. בקשה שנשלחה אינה הוכחה שהחלה התקנה.' } as const;
}

export function updateActionsDisabled(data: ConfiguredUpdate | null, local: LocalUpdateRequest | null,
  unavailable: boolean, busy: boolean) {
  return !data || busy || unavailable || !data.enabled || data.stale || updateNeedsRepair(data) || !!local || updateProgress(data).active;
}

export function updatePollDelay(failures: number, active: boolean) {
  return failures ? Math.min(30_000, 3000 * 2 ** Math.min(failures - 1, 4)) : active ? 3000 : 15_000;
}

/** Serialized reads, capped reconnect backoff, and abort/ignore on cleanup. Never retries a POST. */
export function createUpdatePoller<T>({ read, onValue, onError, active, schedule = setTimeout, cancel = clearTimeout }: {
  read: (signal: AbortSignal) => Promise<T>;
  onValue: (value: T) => void;
  onError: (error: unknown) => void;
  active: () => boolean;
  schedule?: typeof setTimeout;
  cancel?: typeof clearTimeout;
}) {
  let stopped = false, inFlight = false, again = false, failures = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let controller: AbortController | undefined;
  async function tick() {
    if (stopped || inFlight) return;
    inFlight = true;
    const current = new AbortController(); controller = current;
    const timeout = schedule(() => current.abort(), 15_000);
    try {
      const value = await read(current.signal);
      if (!stopped) { failures = 0; onValue(value); }
    } catch (error) {
      if (!stopped) { failures++; onError(error); }
    } finally {
      cancel(timeout); inFlight = false; controller = undefined;
      if (!stopped) { timer = schedule(() => void tick(), again ? 0 : updatePollDelay(failures, active())); again = false; }
    }
  }
  return {
    refresh() { if (stopped) return; if (timer) cancel(timer); if (inFlight) again = true; else void tick(); },
    stop() { stopped = true; if (timer) cancel(timer); controller?.abort(); },
  };
}

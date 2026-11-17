import { catalogEntry, errorText, type ErrorCategory, type ViewerRole } from './errorCatalog';

export let csrf = '';
export function setCsrf(value: string) {
  csrf = value;
}
let viewerRole: ViewerRole | undefined;
/** The signed-in role chooses the wording (a child never sees admin detail). */
export function setViewerRole(role: ViewerRole | undefined) {
  viewerRole = role;
}
let viewerId: string | undefined;
/** The signed-in account this page acts for; a token refresh never switches accounts silently. */
export function setViewerAccount(id: string | undefined) {
  viewerId = id;
}
export class ApiError extends Error {
  constructor(message: string, readonly status: number, readonly code: string, readonly sendOutcome?: 'not_submitted',
    readonly ref?: string) {
    super(message); this.name = 'ApiError';
  }
}
export class ApiNetworkError extends Error {
  readonly code = 'NETWORK_UNAVAILABLE';
}
/** Only background reads may quietly retry these failures; auth errors remain explicit. */
export function isTransientApiError(error: unknown) {
  return error instanceof ApiNetworkError || error instanceof ApiError &&
    error.code !== 'LOGIN_WAIT' && (error.status === 408 || error.status === 429 || error.status >= 500 && error.status < 600);
}

/** What a screen shows for an error: the text, and a reference for support when there is one. */
export interface ShownError {
  text: string;
  code?: string;
  ref?: string;
  category?: ErrorCategory;
}
/** Normalize anything a catch block holds. A plain string or local Error keeps its text. */
export function shownError(error: unknown): ShownError {
  if (typeof error === 'string') return { text: error };
  if (error instanceof ApiError || error instanceof ApiNetworkError) {
    const code = error.code;
    return { text: error.message, code, ref: error instanceof ApiError ? error.ref : undefined, category: catalogEntry(code).category };
  }
  if (error instanceof Error && error.message) return { text: error.message };
  return { text: errorText('', viewerRole) };
}

const refPattern = /^[a-z0-9]{6,16}$/;
const codePattern = /^[A-Z][A-Z0-9_]{0,63}$/;
async function request(path: string, method: string, body: unknown, headers: Record<string, string>) {
  const form = body instanceof FormData;
  return fetch('/api' + path, {
    method,
    headers: {
      ...(method === 'GET' ? {} : { 'X-CSRF-Token': csrf }),
      ...(body && !form ? { 'Content-Type': 'application/json' } : {}),
      ...headers,
    },
    body: body ? (form ? body : JSON.stringify(body)) : undefined,
    credentials: 'same-origin',
    cache: 'no-store',
  }).catch(error => {
    if (error instanceof TypeError) throw new ApiNetworkError(errorText('NETWORK_UNAVAILABLE', viewerRole), { cause: error });
    throw error;
  });
}
/** CSRF_DENIED is refused before any handler runs, so nothing was done. It means this page
 * holds the token of a replaced session (a newer sign-in in another window). Recover the
 * current token once, and only when the cookie still belongs to the same account. */
async function refreshToken() {
  const res = await request('/me', 'GET', undefined, {}).catch(() => null);
  if (!res?.ok) return false;
  const me = await res.json().catch(() => null);
  if (!me?.user?.id || typeof me.csrf !== 'string') return false;
  if (me.user.id !== viewerId) {
    window.dispatchEvent(new Event('session-changed'));
    return false;
  }
  setCsrf(me.csrf);
  return true;
}
export async function api<T = any>(path: string, method = 'GET', body?: unknown, headers: Record<string, string> = {}): Promise<T> {
  let res = await request(path, method, body, headers);
  if (res.status === 403 && method !== 'GET' && viewerId) {
    const denied = await res.clone().json().catch(() => null);
    if (denied?.error === 'CSRF_DENIED' && await refreshToken()) res = await request(path, method, body, headers);
  }
  if (!res.ok) {
    let error = 'UNREADABLE_RESPONSE', sendOutcome: 'not_submitted' | undefined;
    let ref = res.headers.get('X-Request-Id') ?? undefined;
    try {
      const result = await res.json();
      if (typeof result.error === 'string' && codePattern.test(result.error)) error = result.error;
      if (typeof result.ref === 'string') ref = result.ref;
      if (result.sendOutcome === 'not_submitted') sendOutcome = result.sendOutcome;
    } catch {}
    if (ref !== undefined && !refPattern.test(ref)) ref = undefined;
    if (res.status === 401 && path !== '/login') window.dispatchEvent(new Event('session-expired'));
    if (error.startsWith('USAGE_') && path !== '/usage/heartbeat') window.dispatchEvent(new Event('usage-required'));
    throw new ApiError(errorText(error, viewerRole), res.status, error, sendOutcome, ref);
  }
  return res.json();
}

export let csrf = '';
export function setCsrf(value: string) {
  csrf = value;
}
export class ApiError extends Error {
  constructor(message: string, readonly status: number, readonly code: string) {
    super(message); this.name = 'ApiError';
  }
}
export class ApiNetworkError extends Error {}
/** Only background reads may quietly retry these failures; auth errors remain explicit. */
export function isTransientApiError(error: unknown) {
  return error instanceof ApiNetworkError || error instanceof ApiError &&
    error.code !== 'LOGIN_WAIT' && (error.status === 408 || error.status === 429 || error.status >= 500 && error.status < 600);
}
const errors: Record<string, string> = {
  USAGE_EXHAUSTED: 'מכסת הזמן להיום הסתיימה. ההורה יכול להוסיף זמן.',
  USAGE_PAUSED: 'זמן השימוש מושהה. יש לחזור לחלון פעיל ולבדוק את המכסה.',
  USAGE_CLOCK: 'שעון השרת דורש בדיקה. זמן השימוש מושהה.',
  USAGE_TIMEZONE_REQUIRED: 'על ההורה לבחור אזור זמן משפחתי לפני הפעלת המכסה.',
  BONUS_DAY_CHANGED: 'היום התחלף. יש לרענן לפני הוספת זמן.',
  UPDATES_MODE_LOCKED: 'שליטה במצב העדכון טרם אושרה בהגדרה המקומית של רכיב העדכון.',
  HISTORY_WAIT: 'יש בקשות היסטוריה פעילות או שבוצעו לאחרונה. המתינו מעט לפני בקשה נוספת.',
  DELETION_INCOMPLETE: 'מחיקת שיחות קודמת לא הושלמה. בדקו את המצב ואשרו בנפרד את השלמת המחיקה לפני חיבור מחדש.',
  CONNECTION_BUSY: 'פעולת חיבור או ניתוק כבר מתבצעת. המתינו לסיום.',
  PASSWORD_MISMATCH: 'הסיסמאות אינן זהות.',
  NAME_UNAVAILABLE: 'השם כבר בשימוש. בחרו שם אחר, למשל שם פרטי ושם משפחה.',
  CHILD_LIMIT: 'אפשר ליצור עד 20 חשבונות ילדים במשפחה.',
  UPDATES_UNAVAILABLE: 'שירות העדכון אינו זמין או שהחשבון אינו מורשה לנהל אותו.',
  SIGN_IN_REQUIRED: 'החיבור הסתיים. צריך להיכנס שוב.',
  INVALID_CREDENTIALS: 'השם או הסיסמה אינם נכונים.',
  LOGIN_WAIT: 'היו יותר מדי ניסיונות כניסה. המתינו עד 15 דקות ונסו שוב. תקלה בחשבון אחד אינה מחייבת איפוס של שאר החשבונות.',
  SLOW_DOWN: 'רגע קטן, ננסה שוב בעוד כמה דקות.',
  NOT_FOUND: 'השיחה או ההודעה כבר אינן זמינות.',
  PARENT_ONLY: 'הפעולה זמינה להורה בלבד.',
  NOT_CONNECTED: 'אין כרגע חיבור. אפשר לנסות שוב כשיחזור.',
  SEND_UNCERTAIN: 'לא התקבל אישור שליחה. לפני ניסיון נוסף, כדאי לבדוק אם ההודעה הגיעה.',
  FILE_TOO_LARGE: 'הקובץ גדול מדי. אפשר לבחור קובץ קטן יותר.',
  EDIT_WINDOW_CLOSED: 'חלון הזמן לעריכה הסתיים.',
  AUDIO_ENCODING_FAILED: 'לא הצלחנו לעבד את ההקלטה. אפשר לנסות שוב.',
  INVALID_AUDIO: 'קובץ הקול אינו נתמך.',
  WRONG_PASSWORD: 'הסיסמה הנוכחית אינה נכונה.',
  WEBP_REQUIRED: 'מדבקה צריכה להיות קובץ WebP.',
  UNSUPPORTED: 'הפעולה אינה נתמכת בחיבור הנוכחי.',
};
export async function api<T = any>(path: string, method = 'GET', body?: unknown): Promise<T> {
  const form = body instanceof FormData;
  const res = await fetch('/api' + path, {
    method,
    headers: {
      ...(method === 'GET' ? {} : { 'X-CSRF-Token': csrf }),
      ...(body && !form ? { 'Content-Type': 'application/json' } : {}),
    },
    body: body ? (form ? body : JSON.stringify(body)) : undefined,
    credentials: 'same-origin',
    cache: 'no-store',
  }).catch(error => {
    if (error instanceof TypeError) throw new ApiNetworkError('החיבור לשרת אינו זמין כרגע.', { cause: error });
    throw error;
  });
  if (!res.ok) {
    let error = 'REQUEST_FAILED';
    try {
      error = (await res.json()).error;
    } catch {}
    if (res.status === 401 && path !== '/login') window.dispatchEvent(new Event('session-expired'));
    if (error.startsWith('USAGE_') && path !== '/usage/heartbeat') window.dispatchEvent(new Event('usage-required'));
    throw new ApiError(errors[error] || 'הפעולה לא הושלמה. אפשר לנסות שוב.', res.status, error);
  }
  return res.json();
}

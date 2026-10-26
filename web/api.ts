export let csrf = '';
export function setCsrf(value: string) {
  csrf = value;
}
const errors: Record<string, string> = {
  DELETION_INCOMPLETE: 'מחיקת שיחות קודמת לא הושלמה. בדקו את המצב ואשרו בנפרד את השלמת המחיקה לפני חיבור מחדש.',
  CONNECTION_BUSY: 'פעולת חיבור או ניתוק כבר מתבצעת. המתינו לסיום.',
  PASSWORD_MISMATCH: 'הסיסמאות אינן זהות.',
  USERNAME_UNAVAILABLE: 'שם המשתמש אינו פנוי. בחרו שם אחר.',
  CHILD_LIMIT: 'אפשר ליצור עד 20 חשבונות ילדים במשפחה.',
  UPDATES_UNAVAILABLE: 'שירות העדכון אינו זמין או שהחשבון אינו מורשה לנהל אותו.',
  SIGN_IN_REQUIRED: 'החיבור הסתיים. צריך להיכנס שוב.',
  INVALID_CREDENTIALS: 'שם המשתמש או הסיסמה אינם נכונים.',
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
  });
  if (!res.ok) {
    let error = 'REQUEST_FAILED';
    try {
      error = (await res.json()).error;
    } catch {}
    if (res.status === 401 && path !== '/login') window.dispatchEvent(new Event('session-expired'));
    throw new Error(errors[error] || 'הפעולה לא הושלמה. אפשר לנסות שוב.');
  }
  return res.json();
}

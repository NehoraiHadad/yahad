// One catalog for every error code the server (or this web app) can produce.
// Each entry answers: what happened (for the person's role), what to do now, and
// whether trying again is honest. The code itself is never replaced on the way up:
// a code without an entry still reaches the screen as its own reference.

/** USER_ACTION: the person can fix it. TRANSIENT: it passes with time. ENVIRONMENT:
 * the setup around Yahad (address, clock, connection, storage). DEFECT: needs a fix in Yahad. */
export type ErrorCategory = 'USER_ACTION' | 'TRANSIENT' | 'ENVIRONMENT' | 'DEFECT';
export type ViewerRole = 'parent' | 'child';

export interface CatalogEntry {
  category: ErrorCategory;
  /** Shown to anyone, including a child or someone not signed in yet: no admin jargon. */
  text: string;
  /** Shown instead to a signed-in parent, when the parent needs more detail. */
  parent?: string;
  /** Repeating the same action later can succeed without changing anything first. */
  retry: boolean;
}

const support = 'אם זה חוזר, שלחו להורה את קוד התמיכה שמופיע כאן.';
const supportParent = 'אם זה חוזר, הורידו את הדוח ב"מצב המערכת" ושלחו אותו יחד עם קוד התמיכה.';

export const errorCatalog: Record<string, CatalogEntry> = {
  // Address, session and request integrity
  ORIGIN_DENIED: { category: 'ENVIRONMENT', retry: false,
    text: 'הדף נפתח מכתובת שיחד לא מכירה, ולכן הפעולה נחסמה. בקשו מההורה את הכתובת הנכונה של יחד ופתחו אותה.',
    parent: 'הדף נפתח מכתובת שיחד לא מכירה, ולכן הפעולה נחסמה. פתחו את יחד מהכתובת הרגילה שלו בבית (זו שהמתקין הציג) ונסו שם. כתובת נוספת צריך להוסיף בהגדרות ההתקנה.' },
  CSRF_DENIED: { category: 'USER_ACTION', retry: false,
    text: 'בחלון אחר נכנסו לחשבון מחדש, ולכן הפעולה בחלון הזה נחסמה. רעננו את הדף כדי להמשיך.' },
  SIGN_IN_REQUIRED: { category: 'USER_ACTION', retry: false, text: 'החיבור הסתיים. צריך להיכנס שוב.' },
  INVALID_CREDENTIALS: { category: 'USER_ACTION', retry: false, text: 'השם או הסיסמה אינם נכונים.' },
  WRONG_PASSWORD: { category: 'USER_ACTION', retry: false, text: 'הסיסמה הנוכחית אינה נכונה.' },
  LOGIN_WAIT: { category: 'USER_ACTION', retry: false,
    text: 'היו הרבה ניסיונות כניסה שגויים. חכו רבע שעה ונסו שוב. אם שכחתם את הסיסמה, בקשו עזרה מההורה.',
    parent: 'היו יותר מדי ניסיונות כניסה שגויים לחשבון הזה, ולכן הוא מושהה לעד 15 דקות. החשבונות האחרים לא מושפעים, ואין צורך לאפס אותם.' },
  LOGIN_BUSY: { category: 'TRANSIENT', retry: true, text: 'כמה כניסות נבדקות ממש עכשיו. חכו כמה שניות ולחצו שוב.' },
  PASSWORD_MISMATCH: { category: 'USER_ACTION', retry: false, text: 'הסיסמאות אינן זהות.' },
  INVALID_INPUT: { category: 'DEFECT', retry: false,
    text: 'הבקשה לא התקבלה כי חלק מהפרטים אינם תקינים. בדקו את מה שהוזן. ' + support,
    parent: 'הבקשה לא התקבלה כי חלק מהפרטים אינם תקינים. בדקו את מה שהוזן. ' + supportParent },
  REQUEST_FAILED: { category: 'DEFECT', retry: true,
    text: 'השרת של יחד נתקל בתקלה ולא השלים את הפעולה. אפשר לנסות שוב. ' + support,
    parent: 'השרת של יחד נתקל בתקלה ולא השלים את הפעולה. אפשר לנסות שוב. ' + supportParent },
  SERVER_SHUTTING_DOWN: { category: 'TRANSIENT', retry: true, text: 'השרת של יחד מופעל מחדש. החיבור יחזור בעוד רגע.' },
  TOO_MANY_STREAMS: { category: 'ENVIRONMENT', retry: true,
    text: 'יותר מדי חלונות של יחד פתוחים מול השרת. סגרו לשוניות של יחד שאינן בשימוש.' },
  NOT_FOUND: { category: 'USER_ACTION', retry: false, text: 'השיחה או ההודעה כבר אינן זמינות.' },
  PARENT_ONLY: { category: 'USER_ACTION', retry: false, text: 'הפעולה זמינה להורה בלבד.' },
  CHILD_ONLY: { category: 'DEFECT', retry: false, text: 'הפעולה הזאת שייכת לחשבון של ילד או ילדה. ' + supportParent },

  // Setup
  SETUP_CLOSED: { category: 'USER_ACTION', retry: false,
    text: 'קישור ההכנה פג או שכבר נוצר חשבון. אם נוצר, עברו לכניסה; אחרת פתחו קישור חדש מהמתקין.' },
  SETUP_BUSY: { category: 'TRANSIENT', retry: false,
    text: 'יצירת החשבון כבר מתבצעת מחלון אחר. חכו רגע ועברו לכניסה כדי לבדוק אם החשבון נוצר.' },

  // Children and access
  NAME_UNAVAILABLE: { category: 'USER_ACTION', retry: false, text: 'השם כבר בשימוש. בחרו שם אחר, למשל שם פרטי ושם משפחה.' },
  CHILD_LIMIT: { category: 'USER_ACTION', retry: false, text: 'אפשר ליצור עד 20 חשבונות ילדים במשפחה.' },
  CHILD_CREATE_BUSY: { category: 'TRANSIENT', retry: true, text: 'חשבון אחר נוצר ממש עכשיו, והחשבון הזה לא נוצר. חכו רגע ונסו שוב.' },
  CHILD_NOT_FOUND: { category: 'USER_ACTION', retry: false, text: 'החשבון הזה כבר אינו קיים. רעננו את המסך ובחרו חשבון מהרשימה.' },
  CHILD_ACCESS_PAUSED: { category: 'USER_ACTION', retry: false, text: 'ההורה השהה את הגישה לחשבון. אפשר לבקש ממנו להפעיל אותה מחדש.' },
  CHILD_ACCESS_CHANGED: { category: 'USER_ACTION', retry: true, text: 'מצב הגישה השתנה בינתיים. בדקו את המצב המעודכן ונסו שוב.' },
  INVALID_GRANT: { category: 'USER_ACTION', retry: false,
    text: 'אי אפשר לשנות את האישור לשיחה הזאת. ייתכן שהשיחה או החשבון כבר אינם קיימים. רעננו את המסך ובדקו.' },

  // Usage time
  USAGE_TAB_LIMIT: { category: 'USER_ACTION', retry: true, text: 'יותר מדי חלונות פעילים בחשבון הזה. סגרו חלון אחר ונסו שוב בעוד כמה שניות.' },
  USAGE_TAB_EXPIRED: { category: 'TRANSIENT', retry: true, text: 'החלון הזה עמד בלי שימוש, ולכן בודקים מחדש את זמן השימוש לפני שממשיכים.' },
  USAGE_EXHAUSTED: { category: 'USER_ACTION', retry: false, text: 'הזמן להיום נגמר. אפשר לבקש מההורה להוסיף זמן.' },
  USAGE_PAUSED: { category: 'TRANSIENT', retry: true, text: 'זמן השימוש מושהה. חזרו לחלון הפעיל כדי להמשיך.' },
  USAGE_CLOCK: { category: 'ENVIRONMENT', retry: false,
    text: 'השעון של יחד לא מסונכרן, ולכן זמן השימוש נעצר. ספרו להורה.',
    parent: 'שעון המחשב שמריץ את יחד חזר אחורה, ולכן ספירת זמן השימוש מושהית. בדקו את התאריך והשעה במחשב הזה; הספירה תמשיך כשהשעון יתוקן.' },
  USAGE_TIMEZONE_REQUIRED: { category: 'USER_ACTION', retry: false,
    text: 'ההורה עוד לא סיים להגדיר את זמן השימוש. ספרו לו.',
    parent: 'בחרו אזור זמן משפחתי לפני הפעלת מכסת הזמן.' },
  INVALID_TIMEZONE: { category: 'USER_ACTION', retry: false, text: 'אזור הזמן שנבחר אינו מוכר. בחרו אזור זמן מהרשימה.' },
  BONUS_DAY_CHANGED: { category: 'USER_ACTION', retry: true, text: 'היום התחלף בזמן ההוספה, והזמן לא נוסף. רעננו את המסך והוסיפו שוב.' },
  BONUS_CONFLICT: { category: 'USER_ACTION', retry: false,
    text: 'הוספת הזמן הזאת כבר נרשמה עם ערך אחר. רעננו את המסך ובדקו כמה זמן נוסף לפני הוספה נוספת.' },

  // Updates (server answers for /parent/updates)
  UPDATES_MODE_LOCKED: { category: 'ENVIRONMENT', retry: false, text: 'שליטה במצב העדכון טרם אושרה בהגדרה המקומית של רכיב העדכון.' },
  UPDATES_UNAVAILABLE: { category: 'ENVIRONMENT', retry: false, text: 'שירות העדכון אינו זמין או שהחשבון אינו מורשה לנהל אותו.' },
  UPDATES_TARGET_CHANGED: { category: 'USER_ACTION', retry: false,
    text: 'בזמן הבקשה התפרסמה גרסה אחרת. בדקו שוב את פרטי העדכון ואשרו מחדש.' },

  // WhatsApp connection
  NOT_CONNECTED: { category: 'ENVIRONMENT', retry: true,
    text: 'יחד לא מחובר עכשיו ל־WhatsApp. אפשר לנסות שוב כשהחיבור יחזור.',
    parent: 'WhatsApp לא מחובר כרגע. בדקו את מצב החיבור במרחב ההורה.' },
  CONNECTION_BUSY: { category: 'TRANSIENT', retry: true, text: 'פעולת חיבור או ניתוק כבר מתבצעת. המתינו לסיום.' },
  CONNECT_FAILED: { category: 'ENVIRONMENT', retry: true,
    text: 'החיבור ל־WhatsApp לא התחיל בגלל תקלה בשרת של יחד. ' + support,
    parent: 'החיבור ל־WhatsApp לא התחיל בגלל תקלה בשרת של יחד, לא בטלפון. פרטי הקישור נשמרו, ואין צורך בקוד QR חדש. נסו שוב בעוד דקה. ' + supportParent },
  PAIRING_UNREADABLE: { category: 'ENVIRONMENT', retry: false,
    text: 'החיבור ל־WhatsApp צריך קישור מחדש. ספרו להורה.',
    parent: 'השרת לא מצליח לקרוא את נתוני הקישור השמורים (למשל אחרי שחזור מגיבוי). צריך לקשר את WhatsApp מחדש עם קוד QR. חשבונות הילדים נשמרים.' },
  GROUP_SYNC_FAILED: { category: 'TRANSIENT', retry: true,
    text: 'רשימת השיחות לא נטענה עכשיו מ־WhatsApp. החיבור עצמו לא השתנה. נסו לרענן בעוד רגע.' },
  DELETION_INCOMPLETE: { category: 'USER_ACTION', retry: false,
    text: 'מחיקת שיחות קודמת לא הושלמה. בדקו את המצב ואשרו בנפרד את השלמת המחיקה לפני חיבור מחדש.' },
  HISTORY_WAIT: { category: 'TRANSIENT', retry: true, text: 'יש בקשות היסטוריה פעילות או שבוצעו לאחרונה. המתינו מעט לפני בקשה נוספת.' },
  UNSUPPORTED: { category: 'USER_ACTION', retry: false, text: 'הפעולה אינה נתמכת בחיבור הנוכחי.' },

  // Messages
  SEND_RATE_LIMIT: { category: 'TRANSIENT', retry: true,
    text: 'נשלחו או שונו הרבה הודעות בדקה האחרונה, והפעולה הזאת לא בוצעה. חכו דקה ונסו שוב.' },
  SEND_PENDING: { category: 'TRANSIENT', retry: false, text: 'השליחה עדיין מתבצעת. אפשר לבדוק את מצבה בלי לשלוח שוב.' },
  SEND_ID_CONFLICT: { category: 'DEFECT', retry: false,
    text: 'ההודעה הזאת כבר נשלחה בעבר בנוסח אחר, ולכן לא נשלחה שוב. כתבו אותה מחדש כדי לשלוח.',
    parent: 'מזהה השליחה כבר שייך להודעה אחרת, ולכן לא נשלחה הודעה נוספת. כתבו את ההודעה מחדש. ' + supportParent },
  SEND_NOT_SUBMITTED: { category: 'TRANSIENT', retry: true, text: 'ההודעה לא הוגשה לשליחה. אפשר לנסות שוב.' },
  SEND_UNCERTAIN: { category: 'TRANSIENT', retry: false,
    text: 'לא התקבל אישור שליחה. אפשר לבדוק את המצב; לא נשלח שוב כל עוד התוצאה לא ידועה.' },
  EMPTY_MESSAGE: { category: 'USER_ACTION', retry: false, text: 'אי אפשר לשלוח הודעה ריקה. כתבו משהו או צרפו קובץ.' },
  ORIGINAL_UNAVAILABLE: { category: 'USER_ACTION', retry: false, text: 'ההודעה המקורית אינה זמינה לפעולה הזאת. אפשר לפתוח אותה ב־WhatsApp.' },
  OWN_MESSAGE_ONLY: { category: 'USER_ACTION', retry: false, text: 'אפשר לערוך או למחוק רק הודעה שלך שכבר נשלחה.' },
  EDIT_WINDOW_CLOSED: { category: 'USER_ACTION', retry: false, text: 'חלון הזמן לעריכה הסתיים.' },
  BAD_CURSOR: { category: 'DEFECT', retry: false, text: 'טעינת הודעות קודמות נכשלה. רעננו את השיחה. ' + support },

  // Files and recordings
  UPLOAD_BUSY: { category: 'TRANSIENT', retry: true,
    text: 'קבצים אחרים נשלחים ממש עכשיו, והקובץ הזה לא נשלח. חכו שהשליחה הקודמת תסתיים ונסו שוב.' },
  NO_FILE: { category: 'USER_ACTION', retry: false, text: 'לא התקבל קובץ. בחרו קובץ ונסו שוב.' },
  FILE_TOO_LARGE: { category: 'USER_ACTION', retry: false, text: 'הקובץ גדול מדי. אפשר לבחור קובץ קטן יותר.' },
  UNEXPECTED_FILE: { category: 'DEFECT', retry: false, text: 'הבקשה כללה קובץ לא צפוי. ' + support },
  WEBP_REQUIRED: { category: 'USER_ACTION', retry: false, text: 'מדבקה צריכה להיות קובץ WebP.' },
  PREVIEW_REQUIRED: { category: 'TRANSIENT', retry: true, text: 'לתמונה ולסרטון צריך תצוגה מקדימה, והדפדפן לא הכין אותה. אפשר לנסות שוב.' },
  INVALID_PREVIEW: { category: 'TRANSIENT', retry: true, text: 'התצוגה המקדימה שהדפדפן הכין אינה תקינה. אפשר לנסות שוב.' },
  PREVIEW_UNEXPECTED: { category: 'DEFECT', retry: false, text: 'נשלחה תצוגה מקדימה לקובץ שאינו תמונה או סרטון. ' + support },
  AUDIO_NOT_OGG: { category: 'USER_ACTION', retry: false, text: 'ההקלטה אינה בפורמט של הודעה קולית. אפשר להקליט שוב.' },
  AUDIO_NOT_OPUS: { category: 'USER_ACTION', retry: false, text: 'ההקלטה אינה בפורמט של הודעה קולית. אפשר להקליט שוב.' },
  AUDIO_NOT_MONO: { category: 'USER_ACTION', retry: false, text: 'אפשר לשלוח רק הקלטה בערוץ אחד (מונו).' },
  AUDIO_TOO_LONG: { category: 'USER_ACTION', retry: false, text: 'הודעה קולית יכולה להיות באורך של עד שתי דקות.' },
  AUDIO_TOO_LARGE: { category: 'USER_ACTION', retry: false, text: 'ההקלטה גדולה מדי לשליחה.' },
  AUDIO_EMPTY: { category: 'USER_ACTION', retry: false, text: 'ההקלטה ריקה. אפשר להקליט שוב.' },

  // Produced by this web app, not by the server
  NETWORK_UNAVAILABLE: { category: 'TRANSIENT', retry: true, text: 'החיבור לשרת של יחד אינו זמין כרגע. בדקו את הרשת ונסו שוב.' },
  UNREADABLE_RESPONSE: { category: 'ENVIRONMENT', retry: true,
    text: 'התקבלה תשובה לא צפויה בדרך לשרת של יחד, והפעולה לא אושרה. רעננו את הדף ונסו שוב. ' + support,
    parent: 'התקבלה תשובה לא צפויה בדרך לשרת של יחד (ייתכן שרכיב רשת ביניים ענה במקומו), והפעולה לא אושרה. רעננו את הדף ונסו שוב. ' + supportParent },
  CLIENT_RENDER_FAILED: { category: 'DEFECT', retry: true,
    text: 'המסך נתקל בתקלה. רעננו את הדף כדי להמשיך. ' + support,
    parent: 'המסך נתקל בתקלה. רעננו את הדף כדי להמשיך. ' + supportParent },
};

/** Codes this web app produces itself; every other catalog entry must be a server code. */
export const clientErrorCodes = ['NETWORK_UNAVAILABLE', 'UNREADABLE_RESPONSE', 'CLIENT_RENDER_FAILED'] as const;

const unknownEntry: CatalogEntry = { category: 'DEFECT', retry: false,
  text: 'הפעולה לא הושלמה. ' + support, parent: 'הפעולה לא הושלמה. ' + supportParent };

export function catalogEntry(code: string): CatalogEntry {
  return Object.hasOwn(errorCatalog, code) ? errorCatalog[code] : unknownEntry;
}
export function errorText(code: string, role?: ViewerRole) {
  const entry = catalogEntry(code);
  return role === 'parent' && entry.parent ? entry.parent : entry.text;
}

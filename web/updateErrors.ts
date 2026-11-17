import type { modeRequestErrorWords, updateErrorWords } from '../src/updates';

// The one catalog of what the updates screen says about a failure. Each entry answers
// what happened, what to do now, and whether asking again can help. The precise code
// (Windows Updater, UpdaterRules.Code) refines the coarse word; a code this catalog
// does not know still shows its word's text, and the code itself is always shown for
// support. tests/update-errors.test.ts proves that every word src/updates.ts accepts and
// every code the Updater can emit resolves to an entry here.

export type UpdateErrorWord = typeof updateErrorWords[number] | 'expired';
export type ModeRequestErrorWord = typeof modeRequestErrorWords[number];
export type UpdateErrorCategory = 'user_action' | 'transient' | 'environment' | 'defect';
export type UpdateErrorText = { category: UpdateErrorCategory; title: string; what: string; todo: string; retry: boolean };
export type UpdateFailure = UpdateErrorText & { support: string };

const sendCode = 'אם זה חוזר, שלחו את קוד התמיכה שמופיע כאן.';
const rerunSetup = 'הריצו שוב את תוכנית ההתקנה של Yahad (Yahad Setup) במחשב השרת; היא מתקנת את ההתקנה ושומרת את הנתונים.';

export const updateErrorTexts: Record<UpdateErrorWord, UpdateErrorText> = {
  configuration: { category: 'environment', retry: false, title: 'הגדרת שירות העדכון אינה תקינה',
    what: 'שירות העדכון לא הצליח לקרוא את ההגדרות שלו במחשב השרת, ולכן לא בדק ולא התקין דבר.',
    todo: `צריך לתקן את ההגדרה במחשב השרת: ${rerunSetup}` },
  network: { category: 'transient', retry: true, title: 'אין כרגע חיבור תקין ל־GitHub',
    what: 'שירות העדכון לא הצליח לקבל מ־GitHub את פרטי הגרסה או את קובצי העדכון. לא הותקן דבר.',
    todo: 'זו בדרך כלל תקלה זמנית ברשת או ב־GitHub. השירות יבדוק שוב בעצמו בבדיקה התקופתית הבאה; אפשר גם לנסות שוב מאוחר יותר.' },
  signature: { category: 'defect', retry: false, title: 'הגרסה נחסמה מטעמי בטיחות',
    what: 'קובצי הגרסה החדשה לא עברו את בדיקת החתימה, ולכן שירות העדכון חסם אותה כדי להגן על המחשב. לא הותקן דבר, והגרסה הנוכחית ממשיכה לפעול.',
    todo: 'אין צורך לעשות דבר. הגרסה הזאת לא תותקן; כשתצא הגרסה הבאה, היא תיבדק ותוצע כרגיל.' },
  archive: { category: 'transient', retry: true, title: 'קובץ העדכון שהורד פגום',
    what: 'קובץ העדכון שהתקבל אינו תואם לגרסה החתומה (הגודל או התוכן שונים), ולכן לא נשמר ולא הותקן.',
    todo: 'בדרך כלל זו הורדה שנקטעה או השתבשה. הבדיקה הבאה תוריד אותו מחדש בעצמה; אפשר גם לנסות שוב עכשיו.' },
  build: { category: 'defect', retry: false, title: 'הכנת הגרסה החדשה נכשלה',
    what: 'בניית הגרסה החדשה במחשב השרת לא הצליחה. לא הותקן דבר, והגרסה הנוכחית ממשיכה לפעול.',
    todo: `אין צורך לעשות דבר כרגע; הגרסה הבאה תיבדק כרגיל. ${sendCode}` },
  backup: { category: 'environment', retry: true, title: 'לא הצלחנו לשמור גיבוי לפני העדכון',
    what: 'לפני כל התקנה נשמר גיבוי של הנתונים. הגיבוי לא הצליח, ולכן העדכון לא התחיל והנתונים לא השתנו.',
    todo: 'בדקו שבמחשב השרת יש מקום פנוי ושתיקיית הגיבויים זמינה, ואז נסו שוב.' },
  health: { category: 'defect', retry: false, title: 'הגרסה החדשה לא עלתה — חזרנו לגרסה הקודמת',
    what: 'הגרסה החדשה לא עברה את בדיקת התקינות אחרי ההתקנה, ולכן שירות העדכון החזיר בעצמו את הגרסה הקודמת. הנתונים שלכם שמורים.',
    todo: 'אין צורך לעשות דבר. הגרסה הזאת לא תותקן שוב; המתינו לגרסה הבאה.' },
  rollback: { category: 'environment', retry: false, title: 'העדכון נכשל וגם החזרה לגרסה הקודמת לא הושלמה',
    what: 'ייתכן שהאפליקציה לא פועלת כרגע. העדכון אינו מוחק את הנתונים.',
    todo: `במחשב שבו מותקנת Yahad: 1. הפעילו את המחשב מחדש; בעלייה רצה בדיקת תיקון אוטומטית. 2. אם האפליקציה עדיין לא נפתחת, ${rerunSetup} 3. אם גם זה לא עוזר, שלחו את קוד התמיכה.` },
  storage: { category: 'user_action', retry: true, title: 'אין מספיק מקום פנוי בדיסק',
    what: 'לא היה מספיק מקום פנוי במחשב השרת, ולכן העדכון לא הורד או לא הותקן. שום דבר לא השתנה.',
    todo: 'פנו מקום בדיסק של מחשב השרת, ואז נסו שוב.' },
  maintenance: { category: 'transient', retry: true, title: 'העדכון נדחה בגלל תחזוקה',
    what: 'שירות העדכון לא התחיל את הפעולה כי המערכת הייתה באמצע תחזוקה.',
    todo: 'אין צורך לעשות דבר מיוחד; אפשר לנסות שוב מאוחר יותר.' },
  incompatible: { category: 'user_action', retry: false, title: 'הגרסה החדשה דורשת התקנה מלאה',
    what: 'הגרסה החדשה אינה מתאימה להתקנה הנוכחית, ולכן אי אפשר להתקין אותה דרך העדכון. הגרסה הנוכחית ממשיכה לפעול.',
    todo: 'כדי לעבור לגרסה החדשה, הורידו את תוכנית ההתקנה העדכנית של Yahad (Yahad Setup) והריצו אותה במחשב השרת. היא מעדכנת את ההתקנה ושומרת את הנתונים.' },
  internal: { category: 'defect', retry: true, title: 'תקלה פנימית בשירות העדכון',
    what: 'שירות העדכון נתקל בתקלה לא צפויה. אין כאן אישור שהגרסה החדשה הותקנה.',
    todo: `אפשר לנסות שוב. ${sendCode} ב־Windows הפרטים נרשמים גם ביומן האירועים (Application, מקור Yahad).` },
  target_changed: { category: 'user_action', retry: false, title: 'יעד העדכון השתנה — נדרשת בדיקה חדשה',
    what: 'ההתקנה שהתבקשה לא בוצעה: הגרסה או קובצי העדכון אינם זהים לאישור שנשלח.',
    todo: 'בדקו שוב ובחרו את הגרסה מחדש; לא נשלחת התקנה נוספת אוטומטית.' },
  expired: { category: 'user_action', retry: true, title: 'תוקף הבקשה פג לפני הטיפול',
    what: 'הבקשה המתינה מעבר לתוקף שלה ולא בוצעה עבורה התקנה.',
    todo: 'אפשר לשלוח בקשה חדשה אם עדיין תרצו; הבקשה הזאת לא תישלח שוב אוטומטית.' },
};

// The repair barriers: the worker stopped every update until someone fixes the
// computer, so nothing here invites a retry from the browser.
export const updateRepairTexts: Record<'recovery_required' | 'rollback_pending_health', { what: string; todo: string }> = {
  recovery_required: {
    what: 'לא ניתן להמשיך אוטומטית בבטחה. נדרש תיקון במחשב שבו מותקנת האפליקציה. אין כאן אישור שהאפליקציה חזרה לפעול כראוי. רענון המצב רק בודק אם התקבל דיווח חדש; הוא לא מתחיל עדכון נוסף.',
    // docs/AUTOMATIC-UPDATES-SETUP.md, the recovery_required procedure (Linux worker).
    todo: 'במחשב השרת: אל תמחקו ואל תשחזרו דבר ידנית — שמרו את קובץ המצב, תיקיות הקוד, הנתונים, המפתח והגיבויים. בדקו את הגרסה הקודמת על עותק נפרד, תקנו את הבעיה עצמה, ורק אז הריצו את פקודת recover של שירות העדכון (המדריך AUTOMATIC-UPDATES-SETUP מפרט את השלבים). שלחו את קוד התמיכה אם צריך עזרה.' },
  rollback_pending_health: {
    what: 'שירות העדכון עדיין לא אישר שהגרסה הקודמת פועלת כראוי. נדרשת בדיקה במחשב שבו מותקנת האפליקציה לפני המשך עדכונים; אין לשלוח התקנה נוספת.',
    todo: 'במחשב השרת: בדקו שהאפליקציה נפתחת. אם לא, הפעילו את המחשב מחדש; אם גם אז היא לא נפתחת, פעלו לפי סעיף השחזור במדריך העדכונים האוטומטיים ושלחו את קוד התמיכה.' },
};

// A failed phase the worker reported without an error word.
const phaseFallback: Record<string, UpdateErrorWord> = { rolled_back: 'health', incompatible: 'incompatible' };
const unexplained: UpdateErrorText = { category: 'defect', retry: true, title: 'הפעולה לא הושלמה',
  what: 'שירות העדכון דיווח על כשל בלי לציין את הסיבה. אין כאן אישור שהגרסה החדשה הותקנה.', todo: `אפשר לבדוק שוב. ${sendCode}` };

// Precise codes: the word each one belongs to (as the Updater maps it, UpdaterEngine.cs
// UpdaterRules) and, where the code says more than its word, its own text.
type CodeEntry = { word: UpdateErrorWord; text?: Partial<UpdateErrorText> | ((action?: 'check' | 'install') => Partial<UpdateErrorText>) };
const launchFailed: Partial<UpdateErrorText> = { title: 'לא הצלחנו להפעיל את התקנת הגרסה',
  what: 'שירות העדכון לא הצליח להפעיל את התקנת הגרסה החדשה. לא הותקן דבר, והגרסה הנוכחית ממשיכה לפעול.',
  todo: `אפשר לנסות שוב. אם זה חוזר, ${rerunSetup} ושלחו את קוד התמיכה.` };
const offline: Partial<UpdateErrorText> = { title: 'אין חיבור לאינטרנט במחשב השרת',
  what: 'מחשב השרת לא הצליח להגיע ל־GitHub כדי לבדוק אם יש גרסה חדשה. לא הותקן דבר.',
  todo: 'בדקו שלמחשב השרת יש חיבור לאינטרנט. השירות יבדוק שוב בעצמו בבדיקה התקופתית הבאה; אפשר גם לנסות שוב עכשיו.' };
const rateLimited: Partial<UpdateErrorText> = { title: 'GitHub הגביל זמנית את הבדיקות',
  what: 'GitHub סירב זמנית לבקשת הבדיקה, בדרך כלל בגלל מגבלת מספר בקשות מהרשת שלכם. לא הותקן דבר.',
  todo: 'אין צורך לעשות דבר. השירות יבדוק שוב בעצמו בבדיקה התקופתית הבאה; אם לוחצים שוב מיד, סביר שתתקבל אותה תשובה.' };
const resultLost = (what: string): Partial<UpdateErrorText> => ({ title: 'ההתקנה לא דיווחה בבירור על סיום', what,
  todo: `בדקו שהאפליקציה נפתחת. אם כן, אפשר לנסות להתקין שוב; אם לא, הפעילו את המחשב מחדש. ${sendCode}` });

export const updateCodes: Record<string, CodeEntry> = {
  // Network: GitHub's answer, or the connection itself.
  GITHUB_HTTP: { word: 'network' }, 'GITHUB_HTTP:403': { word: 'network', text: rateLimited }, 'GITHUB_HTTP:429': { word: 'network', text: rateLimited },
  GITHUB_URL: { word: 'network' }, GITHUB_REDIRECT_LIMIT: { word: 'network' }, GITHUB_CONTENT_ENCODING: { word: 'network' },
  GITHUB_LIST_INVALID: { word: 'network' }, GITHUB_RESPONSE_BOUND: { word: 'network' },
  HttpRequestException: { word: 'network' }, TaskCanceledException: { word: 'network' },
  'HttpRequestException:NameResolutionError': { word: 'network', text: offline }, 'HttpRequestException:ConnectionError': { word: 'network', text: offline },
  // The downloaded bytes are not the signed ones.
  RELEASE_SETUP_SIZE: { word: 'archive' }, RELEASE_SETUP_HASH: { word: 'archive' }, RELEASE_APP_SIZE: { word: 'archive' }, RELEASE_APP_HASH: { word: 'archive' },
  // Refused for good: the signature, the key or the signed manifest.
  RELEASE_SIGNATURE_INVALID: { word: 'signature' }, RELEASE_KEY_UNTRUSTED: { word: 'signature' }, RELEASE_MANIFEST_INVALID: { word: 'signature' },
  RELEASE_NOT_NEWER: { word: 'signature' }, RELEASE_SETUP_MISMATCH: { word: 'signature' },
  RELEASE_UNSUPPORTED_FROM: { word: 'incompatible' }, APP_NEEDS_INFRASTRUCTURE: { word: 'incompatible' },
  DISK_SPACE: { word: 'storage', text: action => action === 'install'
    ? { todo: 'פנו מקום בכונן המערכת (בדרך כלל C:): ההתקנה צריכה מקום לקובצי הגרסה החדשה ולגיבוי מלא של הנתונים, ועוד 512 MB פנויים לפחות. אחרי שמפנים מקום, נסו להתקין שוב.' }
    : { todo: 'פנו מקום בכונן המערכת (בדרך כלל C:): ההורדה צריכה את גודל קובץ העדכון ועוד 1 GB פנוי לפחות, ובעדכון של האפליקציה בלבד — פי שלושה מגודל הקובץ ועוד 1 GB. אחרי שמפנים מקום, הבדיקה התקופתית הבאה תנסה שוב בעצמה, או בדקו שוב עכשיו.' } },
  ROLLED_BACK_UNHEALTHY: { word: 'health' }, ROLLBACK_FAILED: { word: 'rollback' },
  // The Updater's own words for a failure that threw nothing.
  CHECK_INTERRUPTED: { word: 'internal', text: { category: 'transient', title: 'הבדיקה נקטעה',
    what: 'שירות העדכון הופעל מחדש באמצע בדיקת הגרסה, ולכן הבדיקה לא הסתיימה. לא הותקן דבר.',
    todo: 'אין צורך לעשות דבר; הבדיקה התקופתית הבאה תרוץ בעצמה, או בדקו שוב עכשיו.' } },
  UPGRADE_RESULT_MISSING: { word: 'internal', text: resultLost('תוכנית ההתקנה לא דיווחה תוך 30 דקות אם העדכון הצליח. אין כאן אישור שהגרסה החדשה הותקנה.') },
  UPGRADE_RESULT_INVALID: { word: 'internal', text: resultLost('הדיווח של תוכנית ההתקנה לא היה קריא. אין כאן אישור שהגרסה החדשה הותקנה.') },
  UPDATER_LAUNCH_PATH: { word: 'internal', text: launchFailed },
  INFRA_IDENTITY_MISMATCH: { word: 'internal' }, ACTIVE_POINTER_INVALID: { word: 'internal' },
};
// Families the Updater classifies by prefix, as UpdaterRules does.
export const updateCodePrefixes: [prefix: string, entry: CodeEntry][] = [
  ['APP_PACKAGE_', { word: 'archive', text: { category: 'defect', retry: false, title: 'חבילת האפליקציה בגרסה הזאת לא עברה בדיקה',
    what: 'בדיקת התקינות של חבילת האפליקציה בגרסה החדשה נכשלה, ולכן לא הותקן דבר. הגרסה הנוכחית ממשיכה לפעול.',
    todo: `אין צורך לעשות דבר; המתינו לגרסה הבאה. ${sendCode}` } }],
  ['GITHUB_', { word: 'network' }],
  ['RELEASE_', { word: 'signature' }],
];

/** The entry a precise code resolves to: the exact code, then the code without its
 * detail (GITHUB_HTTP:500 is GITHUB_HTTP), then its prefix family. */
export function resolveUpdateCode(code: string | null | undefined): CodeEntry | undefined {
  if (!code) return undefined;
  const base = code.split(':')[0];
  return updateCodes[code] ?? updateCodes[base] ?? updateCodePrefixes.find(([prefix]) => base.startsWith(prefix))?.[1];
}

export function describeUpdateFailure({ error, code, permanent, action, phase }: {
  error?: UpdateErrorWord | null; code?: string | null; permanent?: boolean; action?: 'check' | 'install'; phase?: string;
}): UpdateFailure {
  const word = error ?? (phase ? phaseFallback[phase] : undefined);
  let text = word ? updateErrorTexts[word] : unexplained;
  const known = resolveUpdateCode(code);
  // A code refines only its own word; a code of another word keeps the word's text.
  if (known && known.word === word && known.text) text = { ...text, ...typeof known.text === 'function' ? known.text(action) : known.text };
  // The Updater's own answer wins: a version refused for good is never worth retrying.
  return { ...text, retry: permanent === true ? false : text.retry, support: code || word || phase || 'unknown' };
}

export const modeRequestErrorTexts: Record<ModeRequestErrorWord, string> = {
  configuration: 'לא ניתן לשמור את שינוי המצב בגלל תקלה בהגדרת שירות העדכון. השינוי ממתין ולא הוחל; נדרשת בדיקה במחשב השרת, ובדיקות גרסה חדשות עשויות להמתין עד לתיקון.',
  expired: 'הבקשה לשינוי המצב המתינה יותר מעשר דקות ולכן לא בוצעה. המצב הפעיל למעלה נשמר; אפשר לבקש שוב.',
  replayed: 'הבקשה הזאת כבר טופלה בעבר ולכן לא בוצעה שוב (הגנה מפני שליחה כפולה). אם המצב הפעיל אינו מה שרציתם, בקשו שוב.',
  local_setup: 'שינוי המצב מהדפדפן אינו מותר בהגדרת שירות העדכון במחשב השרת, או שהעדכונים מושהים שם. המצב הפעיל למעלה נשמר; אפשר לשנות אותו במחשב השרת.',
  operator_changed: 'ההורה האחראי לעדכונים השתנה מאז שנשלחה הבקשה, ולכן היא לא בוצעה. המצב הפעיל למעלה נשמר; ההורה האחראי יכול לבקש שוב.',
  busy: 'שירות העדכון קיבל יותר מדי בקשות לשינוי מצב בזמן קצר, ולכן הבקשה לא בוצעה. המתינו כמה דקות ונסו שוב.',
};
export const modeRequestRejectedText = 'שינוי המצב האחרון לא הוחל. המצב הפעיל למעלה נשמר. רעננו ונסו שוב; אם זה חוזר, נדרשת בדיקה במחשב השרת.';

// The server's 4xx answer to an update request (src/server.ts), by its code.
export const updateRejectionTexts: Record<string, string> = {
  UPDATES_TARGET_CHANGED: 'יעד העדכון השתנה או טרם אושר. לא נשלחה התקנה; בדקו שוב את הגרסה ובחרו מחדש לפני אישור.',
  UPDATES_UNAVAILABLE: 'שירות העדכון לא קיבל את הבקשה: הוא אינו זמין כרגע, העדכונים מושהים, הדיווח שלו ישן, או שעדכון קודם מחכה לתיקון. רעננו את המצב כדי לראות את הסיבה; לא נשלחה בקשה.',
  UPDATES_MODE_LOCKED: 'שינוי מצב העדכונים מהדפדפן עדיין לא הותר בהגדרת רכיב העדכון במחשב השרת. המצב הנוכחי לא השתנה; אפשר לשנות אותו בהרצה חוזרת של תוכנית ההתקנה.',
};
export const updateRejectionFallback = 'השרת לא אישר את הבקשה. רעננו את המצב ובדקו שהעדכונים זמינים ושהחשבון מורשה.';

// configured:false. The reason is a fixed stage and at most a Windows error number
// (Yahad.Platform UpdaterClient, src/windows-update-transport.ts, src/updates.ts); the
// plain text names what the admin can do, and the reason itself goes under technical
// details. tests/update-errors.test.ts proves every stage those sources emit is here.
const unavailableText = {
  operator: 'מצב העדכונים מוצג רק להורה שהוגדר כאחראי לעדכונים בזמן ההתקנה. היכנסו עם החשבון הזה כדי לראות ולנהל עדכונים.',
  noKey: 'העדכונים אינם פעילים בהתקנה הזאת, כי חסר בה מפתח החתימה של הגרסאות. הריצו שוב את תוכנית ההתקנה של Yahad (Yahad Setup) כדי להפעיל אותם.',
  serviceDown: 'שירות העדכון של Yahad אינו פועל או אינו עונה במחשב השרת. הפעילו את המחשב מחדש; אם זה נמשך, הריצו שוב את תוכנית ההתקנה של Yahad (Yahad Setup).',
  mismatch: 'רכיבי העדכון במחשב השרת אינם תואמים זה לזה (למשל אחרי התקנה שלא הושלמה), ולכן המצב לא נקרא. הריצו שוב את תוכנית ההתקנה של Yahad (Yahad Setup); היא מתקנת את ההתקנה ושומרת את הנתונים.',
  internal: 'שירות העדכון נתקל בתקלה פנימית בזמן קריאת המצב. הפעילו את המחשב מחדש; אם זה נמשך, שלחו את קוד התמיכה. הפרטים נרשמים גם ביומן האירועים של Windows (Application, מקור Yahad).',
};
export const unavailableStages: Record<string, keyof typeof unavailableText> = {
  OPERATOR: 'operator', UPDATER_NOT_AVAILABLE: 'noKey', UPDATER_INTERNAL: 'internal',
  // The service is stopped, starting or unreachable.
  PIPE_CONNECT: 'serviceDown', EXCHANGE: 'serviceDown', UPDATER: 'serviceDown', SERVER_PID: 'serviceDown', SERVER_STATE: 'serviceDown',
  SERVER_PROCESS: 'serviceDown', SCM_MANAGER: 'serviceDown', SCM_SERVICE: 'serviceDown', SCM_STATUS: 'serviceDown',
  // The pieces answer, but not as this release expects them to.
  SERVER_ACCOUNT: 'mismatch', SERVER_IMAGE: 'mismatch', SCM_CONFIG: 'mismatch', SCM_CONFIG_SIZE: 'mismatch', OWNER: 'mismatch', REQUEST: 'mismatch',
  UPDATER_BAD_REQUEST: 'mismatch', UPDATER_NO_STATUS: 'mismatch', PLATFORM_REPLY: 'mismatch', PLATFORM_HELPER: 'mismatch', STATUS_SCHEMA: 'mismatch',
};
export function unavailableUpdatesText(reason?: string) {
  const stage = unavailableStages[reason?.split(':')[0] ?? ''];
  return stage ? unavailableText[stage]
    : 'מצב העדכונים אינו זמין לחשבון הזה. ייתכן שרכיב העדכון עוד לא הוגדר, שאין כרגע דיווח תקין או שהחשבון אינו מוגדר כאחראי לעדכונים. אפשר לרענן את המצב; אין כאן אישור שעדכון התחיל או הסתיים.';
}

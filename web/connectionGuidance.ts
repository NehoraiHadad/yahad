// Parent-facing explanation of the connection state. Pure and descriptive:
// it never starts a reconnect, relink, unlink or reset by itself.
export type ConnectionCode =
  | 'ok' | 'device_offline' | 'server_no_answer' | 'scan_qr' | 'qr_pending' | 'relink_required'
  | 'replaced' | 'connecting' | 'reconnecting' | 'not_linked' | 'disconnected_by_parent' | 'unknown';

export interface ConnectionInput {
  /** true when the latest read of /parent/overview failed (the screen may be stale). */
  overviewFailed: boolean;
  /** navigator.onLine: this browser reports no network. */
  browserOnline: boolean;
  /** Connector state from the last successful overview read. */
  state?: string;
  /** A QR image from the current screen is actually available to scan. */
  qrVisible?: boolean;
  /** data.disconnect: the parent explicitly disconnected. */
  disconnected?: boolean;
}

export interface ConnectionGuidance {
  code: ConnectionCode;
  tone: 'ok' | 'wait' | 'action';
  title: string;
  detail: string;
  steps: string[];
  /** The app keeps retrying by itself; nothing needs to be pressed. */
  recoversAlone: boolean;
  /** Fixing this needs the parent to scan a WhatsApp QR code. */
  needsQr: boolean;
}

export function explainConnection(i: ConnectionInput): ConnectionGuidance {
  if (!i.browserOnline)
    return { code: 'device_offline', tone: 'wait', recoversAlone: true, needsQr: false,
      title: 'אין חיבור לאינטרנט במכשיר הזה',
      detail: 'הדפדפן מדווח שאין רשת. זו לא בהכרח בעיה ב־WhatsApp או בשרת, והמסך עשוי להיות לא מעודכן.',
      steps: ['בדקו Wi‑Fi או נתונים סלולריים במכשיר הזה', 'אל תקשרו מחדש את WhatsApp בגלל זה'] };
  if (i.overviewFailed)
    return { code: 'server_no_answer', tone: 'wait', recoversAlone: true, needsQr: false,
      title: 'לא התקבל מענה תקין מהשרת של יחד',
      detail: 'ייתכן שהשרת כבוי או עמוס, או שיש תקלת רשת בדרך אליו. מכאן אי אפשר לדעת מה מצב החיבור ל־WhatsApp, והמידע שמוצג במסך עשוי להיות לא מעודכן. ננסה שוב אוטומטית.',
      steps: ['המתינו כדקה', 'אם זה נמשך: ודאו שהמחשב או ה־Raspberry Pi שמריצים את יחד דולקים ומחוברים לרשת', 'אל תקשרו מחדש את WhatsApp לפני שהמסך מתעדכן'] };
  if (i.disconnected)
    return { code: 'disconnected_by_parent', tone: 'action', recoversAlone: false, needsQr: true,
      title: 'החשבון נותק',
      detail: 'חיבור ה־WhatsApp נותק, ולא ייווצר שוב עד שתלחצו על "קישור החשבון". בינתיים אין קבלה ושליחה של הודעות.',
      steps: ['לחצו "קישור החשבון" וסרקו קוד QR'] };
  switch (i.state) {
    case 'connected':
      return { code: 'ok', tone: 'ok', recoversAlone: false, needsQr: false, title: 'מחובר', detail: '', steps: [] };
    case 'connecting':
      return { code: 'connecting', tone: 'wait', recoversAlone: true, needsQr: false,
        title: 'מתחברים ל־WhatsApp',
        detail: 'השרת עונה ומקים את החיבור הראשון. זה אמור לקחת רגע. אם יידרש קוד QR, המסך יציג אותו.',
        steps: ['המתינו כמה דקות. אם המצב לא משתנה, רעננו את המסך'] };
    case 'reconnecting':
      return { code: 'reconnecting', tone: 'wait', recoversAlone: true, needsQr: false,
        title: 'החיבור ל־WhatsApp נקטע, מנסים שוב',
        detail: 'השרת עונה ומנסה להתחבר מחדש בעצמו, והניסיונות ממשיכים. אין כאן הבטחה שזה יצליח או מתי. אם WhatsApp ינתק את המכשיר המקושר, המצב ישתנה ל"נדרש קישור מחדש". עד אז אין צורך בקוד QR.',
        steps: ['המתינו כמה דקות', 'אם זה נמשך: ודאו שהטלפון הראשי מחובר לאינטרנט ושיחד מופיע תחת "מכשירים מקושרים"'] };
    case 'qr':
      if (!i.qrVisible)
        return { code: 'qr_pending', tone: 'wait', recoversAlone: true, needsQr: false,
          title: 'ממתינים לקוד ה־QR',
          detail: 'קוד ה־QR עדיין לא זמין. המסך ינסה לטעון אותו שוב אוטומטית.',
          steps: [] };
      return { code: 'scan_qr', tone: 'action', recoversAlone: false, needsQr: true,
        title: 'ממתין לסריקת קוד QR',
        detail: 'סרקו את הקוד מהטלפון הראשי כדי להשלים את הקישור.',
        steps: ['WhatsApp בטלפון ← מכשירים מקושרים ← קישור מכשיר', 'סרקו את הקוד שמוצג כאן'] };
    case 'needs_pairing':
      return { code: 'relink_required', tone: 'action', recoversAlone: false, needsQr: true,
        title: 'נדרש קישור מחדש ל־WhatsApp',
        detail: 'WhatsApp ניתק את המכשיר המקושר, או שההתחברות מחדש נכשלה. נתוני הקישור הישנים נמחקו מהשרת ונדרש קוד QR חדש. חשבונות הילדים לא נמחקים. בסנכרון הקבוצות אחרי הקישור, קבוצות שהחשבון המקושר כבר לא רואה יוסרו יחד עם ההרשאות שלהן.',
        steps: ['לחצו "קישור החשבון"', 'WhatsApp בטלפון ← מכשירים מקושרים ← קישור מכשיר, וסרקו את הקוד', 'קשרו את אותו חשבון WhatsApp שחובר עד עכשיו'] };
    case 'conflict':
      return { code: 'replaced', tone: 'action', recoversAlone: false, needsQr: false,
        title: 'החיבור הוחלף במקום אחר',
        detail: 'WhatsApp דיווח שהחיבור הזה הוחלף, וההתחברות נעצרה כדי לא להתנגש. לא מתבצע איפוס אוטומטי. ייתכן ששרת נוסף של יחד רץ עם אותו חשבון.',
        steps: ['ב־WhatsApp, תחת "מכשירים מקושרים", בדקו מה מחובר', 'אם רץ שרת יחד נוסף, כבו אותו', 'לחצו "קישור החשבון" רק כשתרצו שהשרת הזה יתחבר שוב'] };
    case 'idle':
      return { code: 'not_linked', tone: 'action', recoversAlone: false, needsQr: true,
        title: 'WhatsApp עדיין לא קושר',
        detail: 'אין חיבור פעיל. כדי להתחיל צריך לקשר את החשבון עם קוד QR.',
        steps: ['לחצו "קישור החשבון" וסרקו את הקוד'] };
    default:
      return { code: 'unknown', tone: 'wait', recoversAlone: false, needsQr: false,
        title: 'מצב החיבור לא ברור',
        detail: 'השרת דיווח על מצב שהמסך לא מכיר. לא נעשה שום שינוי אוטומטי.',
        steps: ['רעננו את המסך. אם זה נמשך, בדקו את מצב המערכת למטה'] };
  }
}

using System.Globalization;
using System.Text.RegularExpressions;
namespace Yahad.Setup;

// What a failure is to the person at the window (docs: one error model). Every
// code the engine produces resolves here to a category, what happened, what to do
// now and, where one exists, a step this window can take for them. A code with no
// entry still reaches the screen, the log and the copied details as itself; only
// the words fall back to the generic text (SetupErrorCatalogTests keeps that empty).
internal enum SetupErrorCategory { UserAction, Transient, Environment, Defect }
internal enum SetupErrorAction { None, OpenNetworkSettings, OpenStorageSettings, DownloadAgain, ContinueRemoval, ReportProblem }
internal sealed record SetupErrorEntry(SetupErrorCategory Category,string What,string Next,SetupErrorAction Action=SetupErrorAction.None);

internal static class SetupErrorCatalog
{
    // A deliberate code: fixed UPPER_SNAKE text the engine wrote itself. Anything
    // else in an exception message (Windows text, a path) is never a code.
    private static readonly Regex Shape=new(@"\A[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+\z",RegexOptions.CultureInvariant);
    internal static bool IsCode(string? text)=>text is {Length:<=64}&&Shape.IsMatch(text);

    internal const string NetworkSettingsUri="ms-settings:network-status";
    internal const string StorageSettingsUri="ms-settings:storagesense";
    // The project's own release and issue pages (the Updater reads the same repository).
    internal const string DownloadPage="https://github.com/NehoraiHadad/yahad/releases/latest";
    internal const string ReportPage="https://github.com/NehoraiHadad/yahad/issues/new";
    internal static string? Target(SetupErrorAction action)=>action switch
    {
        SetupErrorAction.OpenNetworkSettings=>NetworkSettingsUri,SetupErrorAction.OpenStorageSettings=>StorageSettingsUri,
        SetupErrorAction.DownloadAgain=>DownloadPage,SetupErrorAction.ReportProblem=>ReportPage,_=>null
    };
    internal static string? Label(SetupErrorAction action)=>action switch
    {
        SetupErrorAction.OpenNetworkSettings=>"פתיחת הגדרות הרשת",SetupErrorAction.OpenStorageSettings=>"פתיחת הגדרות האחסון",
        SetupErrorAction.DownloadAgain=>"לעמוד ההורדה של יחד",SetupErrorAction.ContinueRemoval=>"אפשרויות הסרה",
        SetupErrorAction.ReportProblem=>"דיווח על התקלה",_=>null
    };

    private const string SendDetails="לחצו על העתקת פרטים ושלחו אותם לצוות של יחד בעמוד הדיווח.";
    private const string DefectNext="זו תקלה שצריך לתקן בתוכנה של יחד, ולא משהו שעשיתם לא נכון. "+SendDetails;
    private const string RetryThenSend="לחצו על ניסיון נוסף. אם זה חוזר, הפעילו מחדש את המחשב ונסו שוב. אם גם אז זה לא עובד, "+SendDetails;
    private static SetupErrorEntry Defect(string what)=>new(SetupErrorCategory.Defect,what,DefectNext,SetupErrorAction.ReportProblem);

    // Never an entry for a code the engine names: what an unlisted code resolves to.
    internal static readonly SetupErrorEntry Fallback=Defect("הפעולה נעצרה בגלל תקלה שיחד עדיין לא מזהה בשם.");

    private static readonly SetupErrorEntry CorruptDownload=new(SetupErrorCategory.Environment,
        "קובץ ההתקנה פגום, או שההורדה שלו לא הסתיימה.",
        "מחקו את הקובץ שהורדתם, הורידו את ההתקנה מחדש מהעמוד הרשמי של יחד והפעילו את הקובץ החדש.",SetupErrorAction.DownloadAgain);
    private static readonly SetupErrorEntry CorruptUpdate=new(SetupErrorCategory.Environment,
        "קובץ הגרסה החדשה פגום, או שההורדה שלו לא הסתיימה. הגרסה הנוכחית לא שונתה.",
        "יחד ממשיכה לעבוד בגרסה הנוכחית. אפשר לנסות את העדכון שוב מאוחר יותר. אם זה חוזר, הורידו את ההתקנה המלאה מהעמוד הרשמי של יחד.",SetupErrorAction.DownloadAgain);
    private static readonly SetupErrorEntry ReleaseMismatch=new(SetupErrorCategory.Environment,
        "הגרסה בקובץ הזה אינה מתאימה להתקנה שבמחשב. לא שינינו דבר.",
        "הורידו את ההתקנה העדכנית מהעמוד הרשמי של יחד והפעילו אותה.",SetupErrorAction.DownloadAgain);
    private static readonly SetupErrorEntry DamagedRecord=new(SetupErrorCategory.Environment,
        "קובץ רישום של ההתקנה במחשב פגום או אינו תואם לקבצים. ההתקנה נעצרה כדי לא לפגוע בנתונים.",
        "אל תמחקו קבצים של יחד בעצמכם. "+SendDetails,SetupErrorAction.ReportProblem);
    private static readonly SetupErrorEntry OtherSetup=new(SetupErrorCategory.UserAction,
        "חלון אחר של התקנה או הסרה של יחד פתוח כרגע, או שעדכון של יחד רץ ברקע.",
        "סגרו את החלון האחר של יחד (בדקו גם בשורת המשימות), המתינו דקה ולחצו על ניסיון נוסף.");
    private static readonly SetupErrorEntry ServiceDidNotStart=new(SetupErrorCategory.Transient,
        "השירות של יחד הותקן, אבל לא הצליח לעלות.",RetryThenSend,SetupErrorAction.ReportProblem);
    private static readonly SetupErrorEntry ServerNotReady=new(SetupErrorCategory.Transient,
        "יחד הופעלה, אבל לא ענתה בזמן לבדיקה שהיא מוכנה.",RetryThenSend,SetupErrorAction.ReportProblem);
    private static readonly SetupErrorEntry StopUnconfirmed=new(SetupErrorCategory.Transient,
        "לא הצלחנו לוודא שיחד נסגרה עד הסוף. לא נמחק דבר.",
        "המתינו דקה ולחצו על ניסיון נוסף. אם זה חוזר, הפעילו מחדש את המחשב ונסו שוב.");
    private static readonly SetupErrorEntry UpgradeRolledBack=new(SetupErrorCategory.Transient,
        "הגרסה החדשה לא עלתה, ולכן הגרסה הקודמת חזרה לפעול. הנתונים נשמרו.",
        "אין צורך לעשות דבר עכשיו. אפשר לנסות שוב מאוחר יותר. אם זה חוזר, "+SendDetails);
    private static readonly SetupErrorEntry UpgradeLeftForRepair=new(SetupErrorCategory.Transient,
        "העדכון לא הסתיים. הנתונים נשמרו.",
        "התיקון ימשיך לבד בהפעלה הבאה של המחשב. אם יחד לא חוזרת לעבוד אחרי הפעלה מחדש, "+SendDetails);
    private static readonly SetupErrorEntry FirewallMismatch=new(SetupErrorCategory.Environment,
        "כללי חומת האש של Windows אינם תואמים להגדרה של יחד. ההתקנה לא מחליפה כללים שהיא לא יצרה.",
        "אפשר לבחור רק במחשב הזה ולהמשיך בלי גישה מהטלפון. כדי לתקן את הגישה מהטלפון, "+SendDetails,SetupErrorAction.ReportProblem);
    private static readonly SetupErrorEntry CertificateMismatch=new(SetupErrorCategory.Environment,
        "תעודת האבטחה של הגישה הביתית אינה תואמת להתקנה. היא נשמרה ולא הוחלפה.",
        "אפשר לבחור רק במחשב הזה ולהמשיך בלי גישה מהטלפון. כדי לתקן את הגישה מהטלפון, "+SendDetails,SetupErrorAction.ReportProblem);
    private static readonly SetupErrorEntry HttpsUnconfirmed=new(SetupErrorCategory.Transient,
        "הגישה הביתית הוגדרה, אבל בדיקת החיבור המאובטח ממחשב זה לא הצליחה.",
        "ודאו שהמחשב עדיין מחובר לאותה רשת ביתית, ולחצו על ניסיון נוסף. אם זה חוזר, בחרו רק במחשב הזה, ו"+SendDetails);
    private static readonly SetupErrorEntry WindowsFolders=new(SetupErrorCategory.Environment,
        "Windows לא מסר מיקום תקין של תיקיית מערכת שההתקנה צריכה.",RetryThenSend,SetupErrorAction.ReportProblem);
    private static readonly SetupErrorEntry UnverifiedFile=new(SetupErrorCategory.Environment,
        "נמצא קובץ או קישור שיחד לא יצרה. הפעולה נעצרה כדי לא לפגוע בקבצים שאינם של יחד.",
        "לא נמחק דבר שאינו של יחד. "+SendDetails,SetupErrorAction.ReportProblem);

    private static readonly Dictionary<string,SetupErrorEntry> Exact=new(StringComparer.Ordinal)
    {
        // Choices on this screen.
        ["LAN_CONSENT_REQUIRED"]=new(SetupErrorCategory.UserAction,"עדיין לא אישרתם את הגישה מהטלפון ומהטאבלט.",
            "בחרו את החיבור הביתי, סמנו את תיבת האישור ולחצו על ניסיון נוסף. אפשר גם לבחור רק במחשב הזה."),
        ["LAN_EXISTING_CONFIGURATION_REQUIRED"]=new(SetupErrorCategory.UserAction,"במחשב הזה אין גישה קיימת מהטלפון שאפשר לבטל.",
            "בחרו רק במחשב הזה או גישה מהטלפון, ולחצו על ניסיון נוסף."),
        ["UPDATE_CONSENT_REQUIRED"]=new(SetupErrorCategory.UserAction,"עדיין לא בחרתם איך יחד תתעדכן.",
            "בחרו איך לקבל עדכונים, סמנו את תיבת האישור של מנגנון העדכון ולחצו על ניסיון נוסף."),
        ["UPDATE_CONSENT_ALREADY_CAPTURED"]=new(SetupErrorCategory.UserAction,"הבחירה בעדכונים כבר נשמרה בניסיון הקודם.",
            "לחצו על ניסיון נוסף. הבחירה הקודמת תישמר, ואפשר לשנות אותה בהגדרות של יחד אחרי ההתקנה."),
        ["UNINSTALL_CONFIRMATION_REQUIRED"]=new(SetupErrorCategory.UserAction,"מחיקה מלאה צריכה אישור מפורש.",
            "הקלידו את המילה מחיקה בשדה האישור, או בחרו בהסרה ששומרת את הנתונים."),
        ["UNINSTALL_MODE_CHANGED"]=new(SetupErrorCategory.UserAction,"ניסיון הסרה קודם נעצר עם בחירה אחרת לגבי הנתונים.",
            "בחרו באותה אפשרות שבחרתם בפעם הקודמת ולחצו על ניסיון נוסף, כדי להמשיך את אותה הסרה."),
        ["LAN_PRIVATE_PROFILE_REQUIRED"]=new(SetupErrorCategory.UserAction,"Windows מגדיר את הרשת שבחרתם כרשת ציבורית, ולכן לא נפתחה אליה גישה מהטלפון.",
            "אם זו הרשת של הבית, סמנו אותה כרשת פרטית בהגדרות הרשת ולחצו על ניסיון נוסף. ברשת שאינה של הבית בחרו רק במחשב הזה.",SetupErrorAction.OpenNetworkSettings),
        ["LAN_NETWORK_PROFILE_UNAVAILABLE"]=new(SetupErrorCategory.Environment,"Windows לא מסר מה סוג הרשת שבחרתם.",
            "ודאו שהמחשב מחובר לרשת הביתית ולחצו על ניסיון נוסף. אפשר גם לבחור רק במחשב הזה.",SetupErrorAction.OpenNetworkSettings),
        ["NETWORK_SELECTION_CHANGED"]=new(SetupErrorCategory.UserAction,"החיבור לרשת השתנה מאז שבחרתם אותו.",
            "בחרו שוב את החיבור הביתי ולחצו על ניסיון נוסף. יחד לא בוחרת רשת אחרת לבד."),
        ["INSTALL_UPGRADE_REQUIRED"]=new(SetupErrorCategory.UserAction,"במחשב מותקנת גרסה אחרת של יחד, שהמתקין הזה לא מעדכן ישירות.",
            "הסירו את יחד ובחרו לשמור את הנתונים, ואז הפעילו שוב את המתקין הזה. המשפחה והנתונים יישמרו.",SetupErrorAction.ContinueRemoval),
        ["RELEASE_KEY_UNTRUSTED"]=new(SetupErrorCategory.UserAction,"המתקין הזה לא נחתם במפתח שההתקנה הקיימת מכירה. לא שינינו דבר.",
            "כדי לעבור אליו, הסירו את יחד ובחרו לשמור את הנתונים, ואז הפעילו שוב את המתקין.",SetupErrorAction.ContinueRemoval),
        ["RELEASE_NOT_NEWER"]=new(SetupErrorCategory.UserAction,"הגרסה בקובץ הזה כבר מותקנת, או שמותקנת גרסה חדשה ממנה.",
            "אין צורך לעשות דבר. אם חיפשתם גרסה אחרת, הורידו את האחרונה מהעמוד הרשמי של יחד.",SetupErrorAction.DownloadAgain),
        ["INSTALL_DOWNGRADE_REFUSED"]=new(SetupErrorCategory.UserAction,"במחשב מותקנת או נשמרה גרסה חדשה יותר של יחד. המתקין הזה ישן יותר, ולכן לא שינינו דבר.",
            "הורידו את הגרסה האחרונה מהעמוד הרשמי של יחד והפעילו אותה.",SetupErrorAction.DownloadAgain),
        ["RELEASE_UNSUPPORTED_FROM"]=new(SetupErrorCategory.UserAction,"הנתונים במחשב חדשים יותר ממה שהמתקין הזה מכיר, ולכן לא שינינו דבר.",
            "הורידו את הגרסה האחרונה מהעמוד הרשמי של יחד והפעילו אותה.",SetupErrorAction.DownloadAgain),
        ["EXISTING_DATA_PRESERVED"]=new(SetupErrorCategory.UserAction,"ניסיון התקנה קודם של גרסה אחרת לא הסתיים. מה שכבר נוצר בו נשמר.",
            "הפעילו את אותו קובץ התקנה שאיתו התחלתם, או הסירו את יחד ובחרו לשמור את הנתונים ואז התקינו את הגרסה הזו.",SetupErrorAction.ContinueRemoval),
        ["UNINSTALL_INCOMPLETE"]=new(SetupErrorCategory.UserAction,"הסרה קודמת של יחד לא הסתיימה.",
            "פתחו את אפשרויות ההסרה, השלימו את ההסרה באותה בחירה, ואז התקינו מחדש.",SetupErrorAction.ContinueRemoval),
        ["NOT_INSTALLED"]=new(SetupErrorCategory.UserAction,"יחד אינה מותקנת במחשב הזה, ולכן אין מה לעדכן או לתקן.",
            "הורידו את ההתקנה המלאה מהעמוד הרשמי של יחד והפעילו אותה.",SetupErrorAction.DownloadAgain),
        ["INSTALL_INCOMPLETE"]=new(SetupErrorCategory.UserAction,"ההתקנה של יחד במחשב לא הסתיימה, ולכן אי אפשר לעדכן אותה.",
            "הפעילו שוב את קובץ ההתקנה המלא כדי להשלים את ההתקנה."),
        ["ADMIN_REQUIRED"]=new(SetupErrorCategory.UserAction,"ההתקנה צריכה הרשאות של מנהל המחשב.",
            "סגרו את החלון, הפעילו שוב את קובץ ההתקנה ואשרו את חלון ההרשאות של Windows. אם Windows מבקש סיסמה, צריך את החשבון של מנהל המחשב."),
        ["INSTALL_OWNER_IDENTITY"]=new(SetupErrorCategory.UserAction,"לא הצלחנו לזהות את חשבון המשתמש שמפעיל את ההתקנה.",
            "התחברו ל-Windows עם החשבון האישי שלכם במחשב והפעילו שוב את ההתקנה."),
        // Another Setup, or the Updater, holds the machine-wide install lock.
        ["INSTALL_BUSY"]=OtherSetup,["UPGRADE_LOCKED"]=OtherSetup,
        ["UPGRADE_INCOMPLETE"]=new(SetupErrorCategory.Transient,"עדכון קודם של יחד עדיין לא הסתיים.",
            "הפעילו מחדש את המחשב. התיקון ימשיך לבד, ואחר כך אפשר ללחוץ על ניסיון נוסף."),
        // The machine.
        ["DISK_SPACE"]=new(SetupErrorCategory.Environment,"אין מספיק מקום פנוי בכונן.",
            "פנו מקום בכונן שבו מותקן Windows (בדרך כלל C:), למשל בהגדרות האחסון, ולחצו על ניסיון נוסף.",SetupErrorAction.OpenStorageSettings),
        ["ACCESS_DENIED"]=new(SetupErrorCategory.Environment,"Windows או תוכנת אבטחה חסמו גישה לקובץ שההתקנה צריכה.",
            "ודאו שאישרתם את חלון ההרשאות של Windows. אם במחשב יש אנטי־וירוס, בדקו אם הוא חסם את יחד ואשרו אותה, ולחצו על ניסיון נוסף. אם זה חוזר, "+SendDetails),
        ["FILE_LOCKED"]=new(SetupErrorCategory.UserAction,"קובץ של יחד פתוח כרגע בתוכנה אחרת.",
            "סגרו חלונות של יחד, חלונות של סייר הקבצים שפתוחים בתיקיות של יחד ותוכנות גיבוי או סנכרון, ולחצו על ניסיון נוסף. אם זה חוזר, הפעילו מחדש את המחשב ונסו שוב."),
        ["NO_LOCAL_PORT"]=new(SetupErrorCategory.Environment,"כל הכתובות המקומיות שיחד יכולה להשתמש בהן תפוסות בידי תוכנות אחרות.",
            "הפעילו מחדש את המחשב ולחצו על ניסיון נוסף. אם זה חוזר, "+SendDetails),
        ["INSTALL_TIMEOUT"]=new(SetupErrorCategory.Transient,"הפעולה נמשכה הרבה יותר מהרגיל ונעצרה. מה שכבר נוצר נשמר.",
            "הפעילו מחדש את המחשב ולחצו על ניסיון נוסף. הפעולה תמשיך מהמקום שבו נעצרה."),
        ["INSTALL_COLLISION"]=new(SetupErrorCategory.Environment,"נמצאו במחשב שאריות של התקנה קודמת של יחד, שאינן שייכות להתקנה הזו.",
            "אם יחד מופיעה ברשימת האפליקציות של Windows, הסירו אותה משם ובחרו לשמור את הנתונים, ואז נסו שוב. אם לא, "+SendDetails,SetupErrorAction.ReportProblem),
        ["LAN_INSTALL_COLLISION"]=new(SetupErrorCategory.Environment,"נמצאו במחשב רכיבים של גישה ביתית שאינם שייכים להתקנה הזו.",
            "בחרו רק במחשב הזה כדי להמשיך בלי גישה מהטלפון. כדי לתקן את הגישה מהטלפון, "+SendDetails,SetupErrorAction.ReportProblem),
        ["INSTALL_TARGET_CHANGED"]=new(SetupErrorCategory.Environment,"קבצי יחד במחשב השתנו ואינם תואמים לחבילה. ההתקנה נעצרה ולא שינתה אותם.",
            "ייתכן שתוכנה אחרת או אנטי־וירוס שינו קבצים. הסירו את יחד ובחרו לשמור את הנתונים, ואז התקינו שוב. אם זה חוזר, "+SendDetails,SetupErrorAction.ContinueRemoval),
        ["INSTALL_ROOT_OWNER"]=new(SetupErrorCategory.Environment,"תיקיית הנתונים של יחד לא נוצרה על ידי ההתקנה, ולכן אי אפשר לסמוך עליה.",
            "אל תשתמשו בתיקייה הזו. "+SendDetails,SetupErrorAction.ReportProblem),
        ["SERVICE_NOT_OWNED"]=new(SetupErrorCategory.Environment,"במחשב יש שירות בשם של יחד שההתקנה לא יצרה.",
            "ההתקנה לא נוגעת בשירות שאינו שלה. "+SendDetails,SetupErrorAction.ReportProblem),
        ["INSTALL_SERVICE_GENERATION_CHANGED"]=new(SetupErrorCategory.Environment,"השירות של יחד שונה מבחוץ בזמן ההתקנה.",RetryThenSend,SetupErrorAction.ReportProblem),
        ["LEGACY_INSTALL_UNSUPPORTED"]=new(SetupErrorCategory.Environment,"במחשב מותקנת גרסה ישנה מאוד של יחד, שהמתקין הזה אינו יודע לעדכן. לא שינינו דבר.",
            "כדי לא לאבד נתונים, אל תסירו אותה עדיין. "+SendDetails,SetupErrorAction.ReportProblem),
        ["PRIVATE_TEMP_REQUIRED"]=WindowsFolders,["WINDOWS_DRIVE_REQUIRED"]=WindowsFolders,["MACHINE_FOLDER_UNRESOLVED"]=WindowsFolders,
        // The services.
        ["SERVICE_START_FAILED"]=ServiceDidNotStart,["SERVICE_CHILD_EXITED"]=ServiceDidNotStart,
        ["SERVER_NOT_READY"]=ServerNotReady,["REPAIR_UNHEALTHY"]=new(SetupErrorCategory.Transient,"התיקון הסתיים, אבל יחד עדיין לא ענתה לבדיקה שהיא מוכנה.",RetryThenSend,SetupErrorAction.ReportProblem),
        ["INSTALL_SERVICE_STOP_STALLED"]=new(SetupErrorCategory.Transient,"השירות של יחד לא נסגר בזמן.",
            "המתינו דקה ולחצו על ניסיון נוסף. אם זה חוזר, הפעילו מחדש את המחשב ונסו שוב."),
        ["UNINSTALL_SERVICE_REMOVAL_PENDING"]=new(SetupErrorCategory.Transient,"Windows עדיין משחרר את השירות של יחד.",
            "סגרו חלונות של ניהול שירותים או של מנהל המשימות, המתינו דקה ולחצו על ניסיון נוסף."),
        ["UNINSTALL_SERVICE_REAPPEARED"]=new(SetupErrorCategory.Transient,"שירות של יחד הופיע שוב באמצע ההסרה.",
            "הפעילו מחדש את המחשב ולחצו על ניסיון נוסף באותה בחירה."),
        ["UNINSTALL_QUIESCENCE_UNCONFIRMED"]=StopUnconfirmed,["LIFECYCLE_QUIESCENCE_UNCONFIRMED"]=StopUnconfirmed,["LAN_QUIESCENCE_UNCONFIRMED"]=StopUnconfirmed,
        // Upgrades and repair (8.x).
        ["ROLLED_BACK_UNHEALTHY"]=UpgradeRolledBack,["UPGRADE_PREPARE_FAILED"]=UpgradeRolledBack,["UPGRADE_INTERRUPTED"]=UpgradeRolledBack,
        ["ROLLBACK_FAILED"]=UpgradeLeftForRepair,["UPGRADE_COMMIT_INCOMPLETE"]=UpgradeLeftForRepair,
        ["RELEASE_SIGNATURE_INVALID"]=new(SetupErrorCategory.Environment,"החתימה של הגרסה החדשה לא אומתה, ולכן היא לא הותקנה. הגרסה הנוכחית לא שונתה.",
            "יחד ממשיכה לעבוד בגרסה הנוכחית. הורידו את יחד רק מהעמוד הרשמי.",SetupErrorAction.DownloadAgain),
        ["RELEASE_MANIFEST_INVALID"]=CorruptUpdate,["RELEASE_APP_SIZE"]=CorruptUpdate,["RELEASE_APP_HASH"]=CorruptUpdate,
        ["RELEASE_SETUP_MISMATCH"]=ReleaseMismatch,["RELEASE_TRACK_MISMATCH"]=ReleaseMismatch,["APP_NEEDS_INFRASTRUCTURE"]=ReleaseMismatch,
        ["PAYLOAD_VERSION"]=CorruptDownload,["RELEASE_KEY_EMBEDDED_INVALID"]=CorruptDownload,
        ["PACKAGE_NOT_EMBEDDED"]=new(SetupErrorCategory.Environment,"הקובץ שהופעל הוא רכיב פנימי של יחד, ולא קובץ ההתקנה.",
            "הורידו את קובץ ההתקנה מהעמוד הרשמי של יחד והפעילו אותו.",SetupErrorAction.DownloadAgain),
        ["LAN_PAYLOAD_NOT_SUPPORTED"]=new(SetupErrorCategory.Environment,"קובץ ההתקנה הזה אינו כולל את רכיב הגישה הביתית.",
            "בחרו רק במחשב הזה, או הורידו את ההתקנה העדכנית מהעמוד הרשמי של יחד.",SetupErrorAction.DownloadAgain),
        // Records the engine wrote and no longer reads as its own.
        ["INSTALL_STATE_INVALID"]=DamagedRecord,["UNINSTALL_INTENT_INVALID"]=DamagedRecord,["ACTIVE_POINTER_INVALID"]=DamagedRecord,
        ["TREE_RECORD_INVALID"]=DamagedRecord,["UPGRADE_BACKUP_INVALID"]=DamagedRecord,["UPDATER_STATE_INVALID"]=DamagedRecord,
        ["INFRA_IDENTITY_MISMATCH"]=DamagedRecord,["APP_IDENTITY_MISMATCH"]=DamagedRecord,["LAN_OWNERSHIP_MISSING"]=DamagedRecord,
        ["UPDATE_OWNER_BINDING"]=DamagedRecord,
        // Removal.
        ["UNINSTALL_LINK_PRESERVED"]=UnverifiedFile,["UNINSTALL_UNOWNED_FILE_PRESERVED"]=UnverifiedFile,
        ["UNINSTALL_ROOT_NOT_EMPTY"]=new(SetupErrorCategory.Transient,"אחרי המחיקה נשארו קבצים בתיקייה של יחד.",
            "סגרו תוכנות שאולי פתחו קבצים בתיקייה, ולחצו על ניסיון נוסף באותה בחירה."),
        ["UNINSTALL_TREE_LIMIT"]=new(SetupErrorCategory.Environment,"בתיקייה של יחד יש יותר קבצים או תיקיות מקוננות ממה שההסרה מטפלת בהם. לא נמחק דבר נוסף.",
            SendDetails,SetupErrorAction.ReportProblem),
        // Home network access.
        ["LAN_FIREWALL_CHANGED"]=FirewallMismatch,["LAN_FIREWALL_DUPLICATE"]=FirewallMismatch,["LAN_FIREWALL_REPAIR_REQUIRED"]=FirewallMismatch,
        ["LAN_FIREWALL_UNAVAILABLE"]=new(SetupErrorCategory.Environment,"חומת האש של Windows אינה זמינה, ולכן אי אפשר לפתוח גישה מהטלפון.",
            "ודאו שחומת האש של Windows פועלת ולחצו על ניסיון נוסף, או בחרו רק במחשב הזה."),
        ["LAN_CA_CHANGED"]=CertificateMismatch,["LAN_CA_STORAGE_DAMAGED"]=CertificateMismatch,
        ["LAN_SETUP_PAUSED"]=HttpsUnconfirmed,["LAN_HTTPS_CONNECTION_FAILED"]=HttpsUnconfirmed,
        ["LAN_PAYLOAD_CHANGED"]=new(SetupErrorCategory.UserAction,"קובץ ההתקנה השתנה מאז הניסיון הקודם להגדיר גישה ביתית.",
            "הפעילו את אותו קובץ התקנה שאיתו התחלתם, או בחרו רק במחשב הזה."),
        ["LAN_PREPARATION_CHANGED"]=new(SetupErrorCategory.UserAction,"הגדרת הגישה הביתית השתנתה מאז הניסיון הקודם.",
            "בחרו שוב את החיבור הביתי ולחצו על ניסיון נוסף, או בחרו רק במחשב הזה."),
        ["LAN_UNINSTALL_INCOMPLETE"]=new(SetupErrorCategory.Transient,"ביטול הגישה הביתית לא הסתיים.",
            "לחצו על ניסיון נוסף באותה בחירה. אם זה חוזר, הפעילו מחדש את המחשב ונסו שוב."),
        // The engine refused itself: a fix belongs in Yahad.
        ["INSTALL_FAILED"]=Defect("הפעולה נעצרה בגלל תקלה לא צפויה."),
        ["UNINSTALL_OPERATION_FAILED"]=Defect("הפעולה נעצרה בגלל תקלה לא צפויה."),
        ["UNINSTALL_MODE_INVALID"]=Defect("בחירת ההסרה שהתקבלה אינה מוכרת."),
        ["LAN_ACTION_INVALID"]=Defect("בחירת הגישה הביתית שהתקבלה אינה מוכרת."),
        ["LAN_CERTIFICATE_INVALID"]=Defect("תעודת הגישה הביתית לא נקראה כמו שצריך."),
        ["SETUP_ARGUMENTS"]=Defect("ההתקנה הופעלה עם פרמטרים שאינם מוכרים."),["SETUP_COMMAND"]=Defect("ההתקנה הופעלה עם פעולה שאינה מוכרת."),
        ["SETUP_IMAGE"]=Defect("ההתקנה לא מצאה את הקובץ של עצמה."),
        ["SETUP_NOT_STARTED"]=new(SetupErrorCategory.Transient,"חלון ההסרה לא נפתח.",RetryThenSend,SetupErrorAction.ReportProblem),
        ["INSTALL_SERVICE_CONFIG_BOUND"]=Defect("הגדרות השירות של יחד חורגות מהגבולות הצפויים."),
        ["INSTALL_SERVICE_FAILURE_ACTIONS"]=Defect("הגדרת ההפעלה מחדש של השירות לא נשמרה כצפוי."),
        ["INSTALL_SERVICE_PROVISIONING_UNCONFIRMED"]=Defect("לא הצלחנו לאמת שהשירות של יחד הוגדר כצפוי."),
        ["INFRASTRUCTURE_IDENTITY_MISSING"]=Defect("לקובץ ההתקנה חסר זיהוי גרסה."),["INFRASTRUCTURE_SOURCE_IDENTITY_MISSING"]=Defect("לקובץ ההתקנה חסר זיהוי גרסה."),
        ["IPC_IDENTITY"]=Defect("ההתקנה לא זיהתה את החשבון שלה."),
        ["EXECUTABLE_REQUIRED"]=Defect("רכיב של יחד שצריך להפעיל לא נמצא."),
        ["CHILD_ENVIRONMENT_BOUNDS"]=Defect("הסביבה שהועברה לרכיב של יחד חורגת מהגבולות."),["INVALID_CHILD_ENVIRONMENT"]=Defect("הסביבה שהועברה לרכיב של יחד אינה תקינה."),
        ["NUL_ARGUMENT"]=Defect("פרמטר שהועבר לרכיב של יחד אינו תקין."),
        ["LIFECYCLE_STATE"]=Defect("רכיב של יחד הופעל בסדר שגוי."),["SESSION_REQUIRED"]=Defect("רכיב של יחד הופעל בסדר שגוי."),
        ["UNCLEAN_APP_EXIT"]=Defect("רכיב של יחד נסגר עם שגיאה."),
        ["UPDATER_FRAME"]=Defect("הודעה פנימית של מנגנון העדכון אינה תקינה."),["UPDATER_JSON_OPTIONS"]=Defect("הגדרה פנימית של מנגנון העדכון אינה תקינה."),
    };
    // Code families: the archive validators compose their codes from a prefix
    // (ArchiveExtraction, AppMemberPolicy) and the native boundaries share one each.
    private static readonly (string Prefix,SetupErrorEntry Entry)[] Families=
    [
        ("APP_PACKAGE_",CorruptUpdate),("PACKAGE_",CorruptDownload),
        ("LAN_PUBLIC_",Defect("תעודת הגישה הביתית לא נקראה כמו שצריך.")),
        ("NATIVE_",Defect("פעולה פנימית על קבצי המערכת נעצרה.")),("SCM_",Defect("פעולה פנימית מול שירותי Windows נעצרה.")),
    ];
    internal static SetupErrorEntry Resolve(string code)
    {
        if(Exact.TryGetValue(code,out var entry))return entry;
        foreach(var(prefix,family) in Families)if(code.StartsWith(prefix,StringComparison.Ordinal))return family;
        return Fallback;
    }
    // The space the install check requires (InstallPackageManifest.RequiredFreeBytes),
    // as Explorer counts it, rounded up to a tenth.
    internal static string Space(long bytes)=>(Math.Ceiling(bytes/(1024d*1024*1024)*10)/10).ToString("0.0",CultureInfo.InvariantCulture)+" GB";
    internal static string Next(string code,SetupErrorEntry entry,long? requiredFreeBytes)=>code=="DISK_SPACE"&&requiredFreeBytes is {} bytes
        ?"צריך לפחות "+Space(bytes)+" פנויים בכונן שבו מותקן Windows (בדרך כלל C:). פנו מקום, למשל בהגדרות האחסון, ולחצו על ניסיון נוסף."
        :entry.Next;
}

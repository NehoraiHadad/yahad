using System.Windows;
using Yahad.Authority;
namespace Yahad.Owner;
public partial class ApprovalWindow:Window
{
    private readonly OwnerConfirmationState state;
    private readonly Func<AuthorityFrame,CancellationToken,Task<EndpointReply>> send;
    internal ApprovalWindow(OwnerConfirmationState state,Func<AuthorityFrame,CancellationToken,Task<EndpointReply>> send)
    {
        InitializeComponent();this.state=state;this.send=send;var c=state.Snapshot;
        if(c.Scope=="delegate-system"&&c.SystemConsent is { } full)
        {
            Heading.Text="אישור עדכונים אוטומטיים מלאים";Approve.Content="מאשרים עדכונים אוטומטיים";Versions.Text="האפליקציה, מסד הנתונים ורכיבי העדכון";
            Summary.Text=$"עדכונים יציבים מהמאגר {full.Repository} ב־GitHub יותקנו אוטומטית, כולל שינויי מסד נתונים ועדכון השירותים ורכיבי העדכון. האישור נשאר פעיל עד שתכבו אותו; אין צורך באישור נוסף לכל עדכון תואם. אפשר לעבור להתראה בלבד בכל עת.";
        }
        else if(c.Scope=="activate-system")
        {
            Heading.Text="אישור עדכון מלא חד־פעמי";Approve.Content="מאשרים את העדכון הזה";Versions.Text=$"מגרסה {c.FromVersion} לגרסה {c.ToVersion}";
            Summary.Text="האישור כולל את האפליקציה, שינויי מסד הנתונים הנדרשים ורכיבי העדכון של הגרסה המוצגת. זהו אישור לגרסה הזאת בלבד; הוא לא מפעיל עדכונים אוטומטיים. העדכון ימתין לסיום פעילות לפני שינוי הנתונים.";
        }
        else if(c.Scope=="reconcile-system")
        {
            Heading.Text="בדיקה ותיקון של עדכון שנקטע";Approve.Content="לבדוק ולתקן";Versions.Text=$"עדכון מגרסה {c.FromVersion} לגרסה {c.ToVersion}";
            Summary.Text="יחד תבדוק את מצב העדכון שנקטע ותנסה להחזיר את האפליקציה לפעולה תוך שמירה על הנתונים. נתונים שכבר השתנו לא יוחלפו בגיבוי ישן. האישור מיועד לתיקון הזה בלבד ואינו מפעיל עדכונים אוטומטיים.";
        }
        else if(c.Delegation is { } d)
        {
            Heading.Text="אישור עדכונים אוטומטיים";Approve.Content="אישור עדכונים אוטומטיים";Versions.Text=$"גרסאות {d.MinimumVersion} עד {d.MaximumVersion}";
            Summary.Text=$"ערוץ {(d.Channel=="stable"?"יציב":"לא מוכר")}. עד {d.MaximumOperations} עדכונים, עד {DateTimeOffset.FromUnixTimeSeconds(d.ExpiresUnix).LocalDateTime:d}. העדכונים מוגבלים לנתונים ולמדיניות המוצגים כאן. הרחבת ההרשאה או חידושה ידרשו אישור נוסף.";
        }
        else
        {
            Versions.Text=$"מגרסה {c.FromVersion} לגרסה {c.ToVersion}";
            if(c.Recovery=="new_copy_preserve_current")
            {
                Heading.Text="אישור התאוששות מעדכון";Approve.Content="אישור שחזור לעותק חדש";
                Summary.Text="גיבוי מאומת ישוחזר לעותק נתונים חדש ותופעל הגרסה הקודמת. הנתונים הקיימים יישמרו. שינויים שנעשו לאחר הגיבוי יישארו בעותק הקיים ולא יועברו אוטומטית לעותק המשוחזר. ההשלמה תדווח רק אחרי בדיקת הפעלת היישום.";
            }
            else if(c.Recovery=="previous_unchanged")
            {
                Heading.Text="הפעלת הגרסה הקודמת";Approve.Content="אישור הפעלה מחדש";
                Summary.Text="המועמד לעדכון טרם הופעל. התאמת הנתונים לגרסה הקודמת תיבדק שוב לפני הפעלתה. הגרסה הקודמת תופעל עם הנתונים הקיימים, בלי שחזור גיבוי. ההשלמה תדווח אחרי בדיקת הפעלת היישום.";
            }
            else Summary.Text="הבקשה מוגבלת לעדכון המדויק שמוצג כאן. ההרשאה נבדקת שוב לפני הפעלתו. אם הפרטים אינם מוכרים, אפשר לדחות את הבקשה.";
        }
        Closing+=(_,_)=>state.Close();
    }
    private async void Approve_Click(object sender,RoutedEventArgs e)=>await AnswerAsync(true);
    private async void Deny_Click(object sender,RoutedEventArgs e)=>await AnswerAsync(false);
    private async Task AnswerAsync(bool approve)
    {
        Approve.IsEnabled=false;Deny.IsEnabled=false;Status.Text="שולחים את ההחלטה…";
        try{await state.AnswerAsync(approve,send);if(state.Snapshot.Scope is "delegate-system" or "activate-system" or "reconcile-system")Close();Status.Text=approve?"האישור נשלח. אפשר לבדוק את ההתקדמות בחלון המצב.":"הבקשה נדחתה.";}
        catch{Status.Text="לא ידוע אם ההחלטה נקלטה. יש לבדוק את המצב לפני פעולה נוספת; אין לשלוח את אותו אישור שוב.";}
    }
}

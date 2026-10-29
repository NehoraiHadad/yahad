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
        if(c.Delegation is { } d)
        {
            Heading.Text="אישור עדכונים אוטומטיים";Approve.Content="אישור עדכונים אוטומטיים";Versions.Text=$"מגרסה {d.MinimumVersion} עד {d.MaximumVersion}";
            Summary.Text=$"ערוץ {(d.Channel=="stable"?"רגיל":"ניסיוני")}. עד {d.MaximumOperations} עדכונים, עד {DateTimeOffset.FromUnixTimeSeconds(d.ExpiresUnix).LocalDateTime:d}. העדכונים יישארו בגבולות ההרשאה הקיימת. שינוי בגישה לרשת או במדיניות ידרוש אישור נוסף.";
        }
        else{Versions.Text=$"מגרסה {c.FromVersion} לגרסה {c.ToVersion}";Summary.Text="האפליקציה תיסגר לזמן קצר ותיפתח שוב לאחר העדכון. הנתונים והגדרות המשפחה יישמרו. אם העדכון לא יושלם, יוצג מצב השחזור.";}
        Closing+=(_,_)=>state.Close();
    }
    private async void Approve_Click(object sender,RoutedEventArgs e)=>await AnswerAsync(true);
    private async void Deny_Click(object sender,RoutedEventArgs e)=>await AnswerAsync(false);
    private async Task AnswerAsync(bool approve)
    {
        Approve.IsEnabled=false;Deny.IsEnabled=false;Status.Text="שולחים את הבחירה.";
        try{await state.AnswerAsync(approve,send);Status.Text=approve?"האישור התקבל. מצב העדכון יוצג באפליקציה לאחר בדיקה.":"הפעולה בוטלה.";}
        catch{Status.Text="לא התקבלה תשובה ברורה. בדקו את מצב העדכון לפני פעולה נוספת; האישור לא יישלח שוב.";}
    }
}

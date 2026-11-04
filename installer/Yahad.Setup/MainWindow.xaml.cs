using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Yahad.Install;
namespace Yahad.Setup;
public partial class MainWindow:Window
{
    private bool running,completed;private readonly bool uninstall;private SetupResult? result;private readonly UninstallChoice choice=new();
    internal MainWindow(bool uninstall=false,IReadOnlyList<LanChoice>? lanChoices=null,bool? existingAccess=null,bool? existingUpdate=null)
    {
        InitializeComponent();VersionBadge.Text=ApplicationRelease.Version+" · Windows";this.uninstall=uninstall;Closing+=OnClosing;InitializeLan(lanChoices,existingAccess??(lanChoices is null&&HasExistingAccess()));
        InitializeUpdate(existingUpdate??ExistingInstallation.Present());
        if(!uninstall&&existingUpdateProfile)RemovalEntry.Visibility=Visibility.Visible;
        if(uninstall)
        {
            Title="יחד | הסרה";Eyebrow.Text="הסרת יחד";Heading.Text="מסירים את יחד.\nמה עושים עם הנתונים?";
            Description.Text="ההסרה תסגור את השירות במחשב הזה. בחרו מה יישאר לאחריה.";
            InstallSteps.Visibility=Visibility.Collapsed;UninstallChoices.Visibility=Visibility.Visible;
            SideHeading.Text="בוחרים איך\nלהיפרד.";SideIllustration.Visibility=Visibility.Collapsed;InstallSideDetails.Visibility=Visibility.Collapsed;UninstallSideDetails.Visibility=Visibility.Visible;
            NoticeText.Text="תוכנות ושירותים אחרים במחשב לא יוסרו.";UpdateRemovalUi();
        }
    }
    private void RemovalOptions_Click(object sender,RoutedEventArgs e)
    {
        if(running||completed)return;
        try{SetupOperation.OpenRemoval();Close();}
        catch{NoticeText.Text="לא ניתן לפתוח את אפשרויות ההסרה. נסו שוב; לא בוצע שינוי בהתקנה.";}
    }
    private void RemovalChanged(object sender,RoutedEventArgs e)
    {
        if(!uninstall||running||completed)return;var next=DeleteOption.IsChecked==true?UninstallMode.DeleteData:UninstallMode.KeepData;
        if(next!=choice.Mode){choice.Select(next);ConfirmationInput.Text="";}UpdateRemovalUi();
    }
    private void ConfirmationChanged(object sender,TextChangedEventArgs e){if(!uninstall||running||completed)return;choice.Confirmation=ConfirmationInput.Text;UpdateRemovalUi();}
    private void UpdateRemovalUi()
    {
        if(!uninstall||running||completed)return;var full=choice.Mode==UninstallMode.DeleteData;
        DeleteConfirmation.Visibility=full?Visibility.Visible:Visibility.Collapsed;
        Primary.Content=full?"הסרה ומחיקת הנתונים":"הסרה ושמירת הנתונים";Primary.Background=new SolidColorBrush(full?Color.FromRgb(166,61,50):Color.FromRgb(21,124,105));
        Primary.IsEnabled=choice.CanBegin;Primary.ToolTip=full&&!choice.CanBegin?"הקלידו מחיקה בשדה האישור כדי להמשיך.":null;
        Status.Text=full?(choice.CanBegin?"נבחרה מחיקה מלאה מהמחשב הזה.":"להמשך מחיקה מלאה נדרשת הקלדת אישור."):"נתוני המשפחה יישמרו להתקנה חוזרת.";
    }
    private async void Start_Click(object sender,RoutedEventArgs e)
    {
        if(running)return;if(completed){if(!uninstall&&result?.Url is not null)await OpenCurrentLink(result);else Close();return;}
        var mode=uninstall?choice.Confirmed():UninstallMode.KeepData;var network=SelectedLanRequest();var update=SelectedUpdateRequest();UpdatePanel.IsEnabled=false;
        running=true;Primary.IsEnabled=false;Secondary.IsEnabled=false;UninstallChoices.IsEnabled=false;LanPanel.IsEnabled=false;ProgressBar.Visibility=Visibility.Visible;ProgressBar.IsIndeterminate=true;
        Notice.Background=new SolidColorBrush(Color.FromRgb(234,241,234));Status.Text="בודקים מקום פנוי והרשאות.";
        Heading.Text=uninstall?(mode==UninstallMode.DeleteData?"מסירים את יחד\nואת הנתונים.":"מסירים את התוכנה.\nהנתונים יישמרו."):"מתקינים את יחד.\nעוד רגע בבית.";
        Description.Text=uninstall?"אל תכבו את המחשב במהלך הפעולה.":"מכינים את הרכיבים, מפעילים את השירות ובודקים שיחד זמינה.";
        try
        {
            result=await SetupOperation.RunAsync(uninstall,p=>Dispatcher.Invoke(()=>ShowProgress(p)),CancellationToken.None,mode,network,update:update);
            if(!result.Ok){Failure(result.Code,result.Failure);return;}
            completed=true;ProgressBar.IsIndeterminate=false;ProgressBar.Value=100;
            Heading.Text=uninstall?(result.DataPreserved?"יחד הוסרה.\nהנתונים נשמרו.":"יחד והנתונים\nהוסרו מהמחשב."):"הכול מוכן.\nממשיכים ליחד.";
            Description.Text=uninstall?(result.DataPreserved?"השירות וקובצי התוכנה הוסרו. אפשר להתקין מחדש ולהמשיך עם הנתונים השמורים.":"תיקיות התוכנה והנתונים הוסרו, כולל המפתחות והגיבויים שבתיקיית יחד. גיבויים שייצאתם למיקום אחר וקובצי הורדה נשארו במקומם."):"השירות פועל ברקע. ההגדרה המשפחתית נפתחת כעת בדפדפן.";
            UninstallChoices.Visibility=Visibility.Collapsed;Primary.Content=uninstall?"סיום":"פתיחת יחד";
            NoticeText.Text=uninstall?(result.DataPreserved?"נבחרה שמירת נתונים. הנתונים המקומיים לא נמחקו.":"המחיקה המקומית הושלמה. לא בוצע שינוי במכשירים אחרים או ברשת."):"אפשר לסגור את המתקין. השירות ימשיך לפעול במחשב.";
            ShowLanResult(result.Lan);
            UpdatePanel.Visibility=Visibility.Collapsed;
            if(uninstall)ShortcutCleanup.TryRemove(result.Url);
            else try{OpenBrowser(result);}catch{Status.Text="הדפדפן לא נפתח. אפשר לנסות שוב בלחיצה על פתיחת יחד.";}
        }
        catch(Exception error){Failure(SetupOperation.Code(error),SetupFailureDetail.From(error));}
        finally{UpdatePanel.IsEnabled=true;running=false;Primary.IsEnabled=true;Secondary.IsEnabled=true;UninstallChoices.IsEnabled=true;LanPanel.IsEnabled=true;if(uninstall&&!completed)Primary.IsEnabled=choice.CanBegin;else if(!completed)UpdateLanUi();}
    }
    // The window may stay open long past the link's 15 minutes. Only this elevated process can issue a new one.
    private async Task OpenCurrentLink(SetupResult current)
    {
        if(current.SetupToken is null){OpenBrowser(current);return;}
        running=true;Primary.IsEnabled=false;Secondary.IsEnabled=false;
        try
        {
            var state=await SetupOperation.SetupStateAsync(current.Url!);
            if(state=="ready"){OpenBrowser(current);return;}
            if(state=="complete"){OpenBrowser(result=current with{SetupToken=null});return;}
            ProgressBar.Visibility=Visibility.Visible;ProgressBar.IsIndeterminate=true;Status.Text="הקישור פג. מכינים קישור חדש.";
            var renewed=await SetupOperation.RunAsync(false,p=>Dispatcher.Invoke(()=>ShowProgress(p)),CancellationToken.None);
            if(!renewed.Ok){Failure(renewed.Code,renewed.Failure);return;}
            ProgressBar.IsIndeterminate=false;ProgressBar.Value=100;Status.Text="הקישור חודש.";Primary.Content="פתיחת יחד";
            result=current with{Url=renewed.Url,SetupToken=renewed.SetupToken};OpenBrowser(result);
        }
        catch(Exception error){Failure(SetupOperation.Code(error),SetupFailureDetail.From(error));}
        finally{running=false;Primary.IsEnabled=true;Secondary.IsEnabled=true;}
    }
    private void ShowProgress(SetupProgress p)
    {
        ProgressBar.IsIndeterminate=p.Phase is not("extracting" or "installing_service" or "cleaning_installation" or "deleting_data" or "ready" or "removed");ProgressBar.Value=p.Percent;
        Status.Text=p.Phase switch{"checking"=>"בודקים מקום פנוי והרשאות.","extracting"=>"מכינים את הרכיבים של יחד.","installing_service"=>"מתקינים את שירות Windows.","cleaning_installation"=>"מסיימים את ההתקנה.","starting_server"=>"מפעילים את יחד ומוודאים שהיא זמינה.","stopping_server"=>"סוגרים את יחד לפני ההסרה.","deleting_data"=>"מוחקים את הנתונים המקומיים שבחרתם להסיר.","rolling_back"=>"מחזירים את הגרסה הקודמת.","ready"=>"יחד זמינה לשימוש","removed"=>"ההסרה הושלמה",_=>"מתקדמים בהתקנה."};
    }
    private void Failure(string code,SetupFailureDetail? detail=null)
    {
        ProgressBar.IsIndeterminate=false;Heading.Text=uninstall?"ההסרה נעצרה.":"נעצרנו כאן.\nההתקנה לא הושלמה.";Notice.Background=new SolidColorBrush(Color.FromRgb(249,237,224));
        Description.Text=code switch
        {
            "INSTALL_TIMEOUT"=>"שלב ההתקנה חרג מהזמן המותר. ההתקנה לא הושלמה; הקבצים שנוצרו נשמרו לבדיקה לפני המשך.",
            "UPDATE_CONSENT_ALREADY_CAPTURED"=>"הבחירה בעדכונים כבר נשמרה בניסיון הקודם. בהמשך ההתקנה היא תישמר; אפשר לשנות אותה בהגדרות של יחד אחרי ההתקנה.",
            "UPDATE_CONSENT_REQUIRED"=>"יש לבחור איך לקבל עדכונים ולאשר את מנגנון העדכון לפני ההתקנה.",
            "INSTALL_UPGRADE_REQUIRED"=>"במחשב מותקנת גרסה אחרת של יחד. כדי לעבור לגרסה הזו הסירו את יחד ובחרו לשמור את הנתונים, ואז הפעילו שוב את המתקין הזה. המשפחה והנתונים יישמרו.",
            "ROLLED_BACK_UNHEALTHY" or "UPGRADE_PREPARE_FAILED"=>"הגרסה החדשה לא עלתה. הגרסה הקודמת חזרה לפעול והנתונים נשמרו.",
            "ROLLBACK_FAILED" or "UPGRADE_COMMIT_INCOMPLETE"=>"השדרוג לא הסתיים. הנתונים נשמרו. התיקון ימשיך לבד בהפעלה הבאה של המחשב.",
            "RELEASE_KEY_UNTRUSTED"=>"המתקין הזה לא נחתם במפתח שההתקנה הקיימת מכירה. כדי לעבור אליו הסירו את יחד ובחרו לשמור את הנתונים, ואז הפעילו שוב את המתקין.",
            "RELEASE_UNSUPPORTED_FROM"=>"הנתונים במחשב חדשים יותר ממה שהמתקין הזה מכיר, ולכן לא שינינו דבר. הורידו את הגרסה האחרונה.",
            "INSTALL_DOWNGRADE_REFUSED"=>"במחשב מותקנת או נשמרה גרסה חדשה יותר של יחד. המתקין הזה ישן יותר ולכן לא שינינו דבר.",
            "INSTALL_TARGET_CHANGED"=>"קובצי ההתקנה בתיקייה אינם תואמים לחבילה. ההתקנה נעצרה ולא שינתה אותם.",
            "LAN_CONSENT_REQUIRED"=>"בחרו חיבור ביתי ואשרו במפורש את פתיחת הגישה לפני ניסיון נוסף.",
            "LAN_PRIVATE_PROFILE_REQUIRED"=>"החיבור שנבחר אינו מוגדר ב-Windows כרשת ביתית פרטית. רק אם זו הרשת הביתית שלכם, אפשר לאשר ב-Windows שזו רשת פרטית. המתקין אינו משנה את סוג הרשת.",
            "NETWORK_SELECTION_CHANGED"=>"כתובת הרשת או המתאם השתנו. בחרו מחדש את החיבור הביתי; לא תיבחר רשת אחרת אוטומטית.",
            "LAN_CA_CHANGED" or "LAN_CA_STORAGE_DAMAGED"=>"חומר התעודות אינו תואם להתקנה. הוא נשמר ולא הוחלף אוטומטית. נדרש תיקון מקומי לפני חידוש הגישה.",
            "LAN_FIREWALL_CHANGED" or "LAN_FIREWALL_DUPLICATE" or "LAN_FIREWALL_REPAIR_REQUIRED"=>"כללי חומת האש אינם תואמים להגדרה של יחד. ההתקנה לא מחליפה כללים לא מוכרים. הגישה דורשת תיקון ואישור מחדש.",
            "LAN_PAYLOAD_NOT_SUPPORTED"=>"חבילת ההתקנה אינה כוללת את תמיכת הרשת הביתית הנדרשת. השתמשו בחבילת התקנה תואמת של יחד ונסו שוב.",
            "LAN_SETUP_PAUSED" or "LAN_HTTPS_CONNECTION_FAILED"=>"חיבור HTTPS לא הושלם. זו אינה אבחנה שחסרה תעודה: בדקו במסך המחשב כתובת, שירות ורשת, ונסו תיקון מפורש.",
            "UNINSTALL_MODE_CHANGED"=>"ניסיון הסרה קודם נעצר עם בחירה אחרת. בחרו באותו מסלול כדי להמשיך אותו.",
            "UNINSTALL_QUIESCENCE_UNCONFIRMED"=>"לא ניתן לאמת שסגירת יחד הסתיימה. מחיקת הנתונים לא התחילה. נסו שוב לאחר סגירה תקינה של השירות.",
            "UNINSTALL_SERVICE_REMOVAL_PENDING"=>"Windows עדיין משחרר את השירות. סגרו חלונות ניהול שירותים ונסו שוב.",
            "FILE_LOCKED"=>"קובץ נמצא בשימוש. סגרו תוכנות שמשתמשות בקובצי יחד ונסו שוב.",
            "UNINSTALL_LINK_PRESERVED" or "UNINSTALL_UNOWNED_FILE_PRESERVED"=>"נמצא קובץ או קישור שלא ניתן לאמת. ההסרה נעצרה כדי לא לפגוע בקבצים אחרים.",
            "UNINSTALL_INCOMPLETE"=>"יש ניסיון הסרה שלא הושלם. המשיכו אותו לפני התקנה חוזרת.",
            _=>uninstall&&choice.Mode==UninstallMode.DeleteData?"המחיקה לא הושלמה. ייתכן שחלק מהנתונים כבר הוסרו. אפשר להמשיך באותה בחירה; אין לבצע התקנה חדשה עד לסיום.":"הפעולה לא הושלמה. שמרו את קוד התקלה ונסו שוב או פנו לעזרה."
        };
        NoticeText.Text="קוד תקלה: "+code+(detail?.Display()??"");Status.Text="הפעולה טרם הושלמה";Primary.Content=uninstall&&choice.Mode==UninstallMode.DeleteData?"המשך המחיקה":"ניסיון נוסף";
    }
    private static void OpenBrowser(SetupResult result)
    {
        if(result.Url is null)return;var url=result.Url+(result.SetupToken is null?"":"#setup="+result.SetupToken);Process.Start(new ProcessStartInfo(url){UseShellExecute=true});
        try{var path=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"Yahad.url");if(!System.IO.File.Exists(path))System.IO.File.WriteAllText(path,"[InternetShortcut]\r\nURL="+result.Url+"\r\n");}catch{}
    }
    private void Close_Click(object sender,RoutedEventArgs e){if(!running)Close();}
    private void OnClosing(object? sender,CancelEventArgs e){if(running)e.Cancel=true;}
}

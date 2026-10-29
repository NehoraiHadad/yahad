using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace Yahad.Setup;
public partial class MainWindow:Window
{
    private bool running,completed;private readonly bool uninstall;private SetupResult? result;private readonly UninstallChoice choice=new();
    internal MainWindow(bool uninstall=false)
    {
        InitializeComponent();this.uninstall=uninstall;Closing+=OnClosing;
        if(!uninstall&&(System.IO.File.Exists(InstallLocations.InstallerImage)||PendingRemoval()))RemovalEntry.Visibility=Visibility.Visible;
        if(uninstall)
        {
            Title="יחד | הסרה";Eyebrow.Text="הסרת יחד";Heading.Text="מסירים את יחד.\nמה עושים עם הנתונים?";
            Description.Text="ההסרה תסגור את השירות במחשב הזה. בחרו מה יישאר לאחריה.";
            InstallSteps.Visibility=Visibility.Collapsed;UninstallChoices.Visibility=Visibility.Visible;
            SideHeading.Text="בוחרים איך\nלהיפרד.";SideIllustration.Visibility=Visibility.Collapsed;InstallSideDetails.Visibility=Visibility.Collapsed;UninstallSideDetails.Visibility=Visibility.Visible;
            NoticeText.Text="Windows יבקש אישור מנהל לפני ההסרה. תוכנות ושירותים אחרים במחשב לא יוסרו.";UpdateRemovalUi();
        }
    }
    private static bool PendingRemoval()
    {
        // Discover the fixed receipt name through its public parent directory;
        // do not grant the unelevated UI read access to protected control records.
        try{return System.IO.Directory.EnumerateFileSystemEntries(System.IO.Path.GetDirectoryName(InstallLocations.RemovalStateFile)!,System.IO.Path.GetFileName(InstallLocations.RemovalStateFile),System.IO.SearchOption.TopDirectoryOnly).Any();}catch{return false;}
    }
    private void RemovalOptions_Click(object sender,RoutedEventArgs e)
    {
        if(running||completed)return;
        try{var copy=SetupElevation.RelocateForUninstall();Process.Start(new ProcessStartInfo(copy){UseShellExecute=true,ArgumentList={"--uninstall-ui"}});Close();}
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
        if(running)return;if(completed){if(!uninstall&&result?.Url is not null)OpenBrowser(result);else Close();return;}
        var mode=uninstall?choice.Confirmed():UninstallMode.KeepData;
        running=true;Primary.IsEnabled=false;Secondary.IsEnabled=false;UninstallChoices.IsEnabled=false;ProgressBar.Visibility=Visibility.Visible;ProgressBar.IsIndeterminate=true;
        Notice.Background=new SolidColorBrush(Color.FromRgb(234,241,234));Status.Text="ממתינים לאישור של Windows.";
        Heading.Text=uninstall?(mode==UninstallMode.DeleteData?"מסירים את יחד\nואת הנתונים.":"מסירים את התוכנה.\nהנתונים יישמרו."):"מתקינים את יחד.\nעוד רגע בבית.";
        Description.Text=uninstall?"הפעולה תתחיל לאחר אישור Windows. אל תכבו את המחשב במהלכה.":"מכינים את הרכיבים, מפעילים את השירות ובודקים שיחד זמינה.";
        try
        {
            result=await SetupElevation.RunAsync(uninstall,p=>Dispatcher.Invoke(()=>ShowProgress(p)),CancellationToken.None,mode);
            if(!result.Ok){Failure(result.Code);return;}
            completed=true;ProgressBar.IsIndeterminate=false;ProgressBar.Value=100;
            Heading.Text=uninstall?(result.DataPreserved?"יחד הוסרה.\nהנתונים נשמרו.":"יחד והנתונים\nהוסרו מהמחשב."):"הכול מוכן.\nממשיכים ליחד.";
            Description.Text=uninstall?(result.DataPreserved?"השירות וקובצי התוכנה הוסרו. אפשר להתקין מחדש ולהמשיך עם הנתונים השמורים.":"תיקיות התוכנה והנתונים הוסרו, כולל המפתחות והגיבויים שבתיקיית יחד. גיבויים שייצאתם למיקום אחר וקובצי הורדה נשארו במקומם."):"השירות פועל ברקע. ההגדרה המשפחתית נפתחת כעת בדפדפן.";
            UninstallChoices.Visibility=Visibility.Collapsed;Primary.Content=uninstall?"סיום":"פתיחת יחד";
            NoticeText.Text=uninstall?(result.DataPreserved?"נבחרה שמירת נתונים. הנתונים המקומיים לא נמחקו.":"המחיקה המקומית הושלמה. לא בוצע שינוי במכשירים אחרים או ברשת."):"אפשר לסגור את המתקין. השירות ימשיך לפעול במחשב.";
            if(uninstall)ShortcutCleanup.TryRemove(result.Url);
            else try{OpenBrowser(result);}catch{Status.Text="הדפדפן לא נפתח. אפשר לנסות שוב בלחיצה על פתיחת יחד.";}
        }
        catch(Exception error){Failure(SetupElevation.Code(error));}
        finally{running=false;Primary.IsEnabled=true;Secondary.IsEnabled=true;UninstallChoices.IsEnabled=true;if(uninstall&&!completed)Primary.IsEnabled=choice.CanBegin;}
    }
    private void ShowProgress(SetupProgress p)
    {
        ProgressBar.IsIndeterminate=p.Phase is not("extracting" or "deleting_data" or "ready" or "removed");ProgressBar.Value=p.Percent;
        Status.Text=p.Phase switch{"checking"=>"בודקים מקום פנוי והרשאות.","extracting"=>"מכינים את הרכיבים של יחד.","installing_service"=>"מתקינים את שירות Windows.","starting_server"=>"מפעילים את יחד ומוודאים שהיא זמינה.","stopping_server"=>"סוגרים את יחד לפני ההסרה.","deleting_data"=>"מוחקים את הנתונים המקומיים שבחרתם להסיר.","ready"=>"יחד זמינה לשימוש","removed"=>"ההסרה הושלמה",_=>"מתקדמים בהתקנה."};
    }
    private void Failure(string code)
    {
        ProgressBar.IsIndeterminate=false;Heading.Text=uninstall?"ההסרה נעצרה.":"נעצרנו כאן.\nההתקנה לא הושלמה.";Notice.Background=new SolidColorBrush(Color.FromRgb(249,237,224));
        Description.Text=code switch
        {
            "UAC_CANCELLED"=>"בקשת האישור של Windows בוטלה. לא התחילה הסרה או מחיקה בניסיון הזה.",
            "UNINSTALL_MODE_CHANGED"=>"ניסיון הסרה קודם נעצר עם בחירה אחרת. בחרו באותו מסלול כדי להמשיך אותו.",
            "UNINSTALL_QUIESCENCE_UNCONFIRMED"=>"לא ניתן לאמת שסגירת יחד הסתיימה. מחיקת הנתונים לא התחילה. נסו שוב לאחר סגירה תקינה של השירות.",
            "UNINSTALL_SERVICE_REMOVAL_PENDING"=>"Windows עדיין משחרר את השירות. סגרו חלונות ניהול שירותים ונסו שוב.",
            "FILE_LOCKED"=>"קובץ נמצא בשימוש. סגרו תוכנות שמשתמשות בקובצי יחד ונסו שוב.",
            "UNINSTALL_LINK_PRESERVED" or "UNINSTALL_UNOWNED_FILE_PRESERVED"=>"נמצא קובץ או קישור שלא ניתן לאמת. ההסרה נעצרה כדי לא לפגוע בקבצים אחרים.",
            "UNINSTALL_INCOMPLETE"=>"יש ניסיון הסרה שלא הושלם. המשיכו אותו לפני התקנה חוזרת.",
            _=>uninstall&&choice.Mode==UninstallMode.DeleteData?"המחיקה לא הושלמה. ייתכן שחלק מהנתונים כבר הוסרו. אפשר להמשיך באותה בחירה; אין לבצע התקנה חדשה עד לסיום.":"הפעולה לא הושלמה. שמרו את קוד התקלה ונסו שוב או פנו לעזרה."
        };
        NoticeText.Text="קוד תקלה: "+code;Status.Text="הפעולה טרם הושלמה";Primary.Content=uninstall&&choice.Mode==UninstallMode.DeleteData?"המשך המחיקה":"ניסיון נוסף";
    }
    private static void OpenBrowser(SetupResult result)
    {
        if(result.Url is null)return;var url=result.Url+(result.SetupToken is null?"":"#setup="+result.SetupToken);Process.Start(new ProcessStartInfo(url){UseShellExecute=true});
        try{var path=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"Yahad.url");if(!System.IO.File.Exists(path))System.IO.File.WriteAllText(path,"[InternetShortcut]\r\nURL="+result.Url+"\r\n");}catch{}
    }
    private void Close_Click(object sender,RoutedEventArgs e){if(!running)Close();}
    private void OnClosing(object? sender,CancelEventArgs e){if(running)e.Cancel=true;}
}

using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
namespace Yahad.Setup;
public partial class MainWindow:Window
{
    private bool running,completed;private readonly bool uninstall;private SetupResult? result;
    internal MainWindow(bool uninstall=false)
    {
        InitializeComponent();this.uninstall=uninstall;Closing+=OnClosing;
        if(uninstall){Eyebrow.Text="הסרת יחד";Heading.Text="מסירים את התוכנה,\nשומרים את הנתונים.";Description.Text="נעצור את השירות ונסיר את קובצי התוכנה. נתוני המשפחה, המפתחות והגיבויים יישמרו במחשב.";Primary.Content="הסרת יחד";NoticeText.Text="Windows יבקש אישור להסרת השירות. תוכנות ושירותים אחרים במחשב לא יוסרו.";}
    }
    private async void Start_Click(object sender,RoutedEventArgs e)
    {
        if(running)return;if(completed){if(!uninstall&&result?.Url is not null)OpenBrowser(result);else Close();return;}
        running=true;Primary.IsEnabled=false;Secondary.IsEnabled=false;ProgressBar.Visibility=Visibility.Visible;ProgressBar.IsIndeterminate=true;
        try
        {
            result=await SetupElevation.RunAsync(uninstall,p=>Dispatcher.Invoke(()=>ShowProgress(p)),CancellationToken.None);
            if(!result.Ok){Failure(result.Code);return;}
            completed=true;ProgressBar.IsIndeterminate=false;ProgressBar.Value=100;Heading.Text=uninstall?"יחד הוסר.\nהנתונים שלכם נשמרו.":"הכול מוכן.\nממשיכים ליחד.";
            Description.Text=uninstall?"השירות וקובצי התוכנה הוסרו. נתוני המשפחה נשארו מוגנים במחשב.":"השירות פועל ברקע. ההגדרה המשפחתית נפתחת כעת בדפדפן.";
            Primary.Content=uninstall?"סיום":"פתיחת יחד";NoticeText.Text=uninstall?"לא נמחקו נתוני המשפחה, מפתחות או גיבויים.":"אפשר לסגור את המתקין. השירות ימשיך לפעול במחשב.";
            if(!uninstall)try{OpenBrowser(result);}catch{Status.Text="הדפדפן לא נפתח. אפשר לנסות שוב בלחיצה על פתיחת יחד.";}
        }
        catch(Exception error){Failure(SetupElevation.Code(error));}
        finally{running=false;Primary.IsEnabled=true;Secondary.IsEnabled=true;}
    }
    private void ShowProgress(SetupProgress progress)
    {
        ProgressBar.IsIndeterminate=progress.Phase is not("extracting" or "ready" or "removed");ProgressBar.Value=progress.Percent;
        Status.Text=progress.Phase switch{"checking"=>"בודקים שהכול מוכן להתקנה…","extracting"=>"מכינים את הרכיבים של יחד…","installing_service"=>"מתקינים את שירות Windows…","starting_server"=>"מפעילים את יחד ומוודאים שהשרת מוכן…","stopping_server"=>"סוגרים את יחד בבטחה…","ready"=>"יחד מוכן לשימוש","removed"=>"ההסרה הושלמה",_=>"ממשיכים בהתקנה…"};
    }
    private void Failure(string code)
    {
        ProgressBar.IsIndeterminate=false;Heading.Text="נעצרנו כאן.\nהנתונים נשמרו.";Notice.Background=new SolidColorBrush(Color.FromRgb(249,237,224));
        Description.Text=code switch{"UAC_CANCELLED"=>"האישור של Windows בוטל. אפשר לנסות שוב כשנוח לכם.","DISK_SPACE"=>"אין מספיק מקום פנוי. פנו מקום במחשב ונסו שוב.","FILE_LOCKED"=>"קובץ של יחד נמצא בשימוש. המתינו לסגירתו ונסו שוב.","INSTALL_COLLISION" or "EXISTING_DATA_PRESERVED"=>"מצאנו התקנה או נתונים קיימים. שמרנו עליהם ולא החלפנו אותם.","NO_LOCAL_PORT"=>"לא נמצאה כתובת מקומית פנויה. לא סגרנו תוכנות אחרות במחשב.","SERVICE_START_FAILED" or "SERVER_NOT_READY"=>"השירות עדיין לא מוכן. קובצי ההתקנה נשמרו, בלי למחוק נתונים.",_=>"ההתקנה לא הושלמה. לא מחקנו נתונים קיימים. אפשר לנסות שוב או לשמור את קוד התקלה לפנייה לעזרה."};
        NoticeText.Text="קוד תקלה: "+code;Status.Text="ההתקנה טרם הושלמה";Primary.Content="ניסיון נוסף";
    }
    private static void OpenBrowser(SetupResult result)
    {
        if(result.Url is null)return;var url=result.Url+(result.SetupToken is null?"":"#setup="+result.SetupToken);
        Process.Start(new ProcessStartInfo(url){UseShellExecute=true});
        // Shortcut deliberately contains no one-use capability.
        try{var folder=Environment.GetFolderPath(Environment.SpecialFolder.Programs);var path=System.IO.Path.Combine(folder,"Yahad.url");if(!System.IO.File.Exists(path))System.IO.File.WriteAllText(path,"[InternetShortcut]\r\nURL="+result.Url+"\r\n");}catch{}
    }
    private void Close_Click(object sender,RoutedEventArgs e){if(!running)Close();}
    private void OnClosing(object? sender,CancelEventArgs e){if(running)e.Cancel=true;}
}

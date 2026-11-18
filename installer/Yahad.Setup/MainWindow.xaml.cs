using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Yahad.Install;
namespace Yahad.Setup;
public partial class MainWindow:Window
{
    private bool running,completed;private readonly bool uninstall;private SetupResult? result;private readonly UninstallChoice choice=new();
    // The failure on screen: what "copy details" copies and the action button takes.
    private SetupFailureReport? failureReport;private string? failureLog;private SetupErrorAction failureAction;
    private readonly Func<SetupFailureReport,string?> writeFailureLog=SetupFailureLog.Write;
    internal MainWindow(bool uninstall=false,IReadOnlyList<LanChoice>? lanChoices=null,bool? existingAccess=null,bool? existingUpdate=null,Func<string,int>? lanCategory=null,Func<SetupFailureReport,string?>? failureLog=null)
    {
        InitializeComponent();VersionBadge.Text=(SetupOperation.DisplayVersion() is {} version?version+" · ":"")+"Windows";this.uninstall=uninstall;Closing+=OnClosing;InitializeLan(lanChoices,existingAccess??(lanChoices is null&&HasExistingAccess()),lanCategory);
        if(failureLog is not null)writeFailureLog=failureLog;
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
        catch{Status.Text="לא ניתן לפתוח את אפשרויות ההסרה. נסו שוב; לא בוצע שינוי בהתקנה.";}
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
        try
        {
            // A refused choice (consent, confirmation) reaches the same failure screen as
            // the operation; nothing is thrown out of this async void.
            var mode=uninstall?choice.Confirmed():UninstallMode.KeepData;var network=SelectedLanRequest();var homeName=network is not null&&SelectedHomeName();var update=SelectedUpdateRequest();
            running=true;UpdatePanel.IsEnabled=false;Primary.IsEnabled=false;Secondary.IsEnabled=false;UninstallChoices.IsEnabled=false;LanPanel.IsEnabled=false;ProgressBar.Visibility=Visibility.Visible;ProgressBar.IsIndeterminate=true;
            ClearFailure();Status.Text="בודקים מקום פנוי והרשאות.";
            Heading.Text=uninstall?(mode==UninstallMode.DeleteData?"מסירים את יחד\nואת הנתונים.":"מסירים את התוכנה.\nהנתונים יישמרו."):"מתקינים את יחד.\nעוד רגע בבית.";
            Description.Text=uninstall?"אל תכבו את המחשב במהלך הפעולה.":"מכינים את הרכיבים, מפעילים את השירות ובודקים שיחד זמינה.";
            result=await SetupOperation.RunAsync(uninstall,p=>Dispatcher.Invoke(()=>ShowProgress(p)),CancellationToken.None,mode,network,update:update,homeName:homeName);
            if(!result.Ok){Failure(result.Code,result.Failure);return;}
            completed=true;ProgressBar.IsIndeterminate=false;ProgressBar.Value=100;
            Heading.Text=uninstall?(result.DataPreserved?"יחד הוסרה.\nהנתונים נשמרו.":"יחד והנתונים\nהוסרו מהמחשב."):"הכול מוכן.\nממשיכים ליחד.";
            Description.Text=uninstall?(result.DataPreserved?"השירות וקובצי התוכנה הוסרו. אפשר להתקין מחדש ולהמשיך עם הנתונים השמורים.":"תיקיות התוכנה והנתונים הוסרו, כולל המפתחות והגיבויים שבתיקיית יחד. גיבויים שייצאתם למיקום אחר וקובצי הורדה נשארו במקומם."):"השירות פועל ברקע. ההגדרה המשפחתית נפתחת כעת בדפדפן.";
            UninstallChoices.Visibility=Visibility.Collapsed;Primary.Content=uninstall?"סיום":"פתיחת יחד";
            NoticeText.Text=uninstall?(result.DataPreserved?"נבחרה שמירת נתונים. הנתונים המקומיים לא נמחקו.":"המחיקה המקומית הושלמה. לא בוצע שינוי במכשירים אחרים או ברשת."):"אפשר לסגור את המתקין. השירות ימשיך לפעול במחשב.";
            ShowLanResult(result.Lan);
            UpdatePanel.Visibility=Visibility.Collapsed;
            if(uninstall)ShortcutCleanup.TryRemove(result.Url);
            else TryOpenBrowser(result);
        }
        catch(Exception error){Failure(SetupOperation.Code(error),SetupFailureDetail.From(error));}
        finally{UpdatePanel.IsEnabled=true;running=false;Primary.IsEnabled=true;Secondary.IsEnabled=true;UninstallChoices.IsEnabled=true;LanPanel.IsEnabled=true;if(uninstall&&!completed)Primary.IsEnabled=choice.CanBegin;else if(!completed){if(HomeLan.IsChecked==true)RefreshLanProfile();else UpdateLanUi();}}
    }
    // The window may stay open long past the link's 15 minutes. Only this elevated process can issue a new one.
    private async Task OpenCurrentLink(SetupResult current)
    {
        running=true;Primary.IsEnabled=false;Secondary.IsEnabled=false;
        try
        {
            if(current.SetupToken is null){TryOpenBrowser(current);return;}
            var state=await SetupOperation.SetupStateAsync(current.Url!);
            if(state=="ready"){TryOpenBrowser(current);return;}
            if(state=="complete"){TryOpenBrowser(result=current with{SetupToken=null});return;}
            ClearFailure();ProgressBar.Visibility=Visibility.Visible;ProgressBar.IsIndeterminate=true;Status.Text="הקישור פג. מכינים קישור חדש.";
            var renewed=await SetupOperation.RunAsync(false,p=>Dispatcher.Invoke(()=>ShowProgress(p)),CancellationToken.None);
            if(!renewed.Ok){Failure(renewed.Code,renewed.Failure);return;}
            ProgressBar.IsIndeterminate=false;ProgressBar.Value=100;Status.Text="הקישור חודש.";Primary.Content="פתיחת יחד";
            result=current with{Url=renewed.Url,SetupToken=renewed.SetupToken};TryOpenBrowser(result);
        }
        catch(Exception error){Failure(SetupOperation.Code(error),SetupFailureDetail.From(error));}
        finally{running=false;Primary.IsEnabled=true;Secondary.IsEnabled=true;}
    }
    private void ShowProgress(SetupProgress p)
    {
        ProgressBar.IsIndeterminate=p.Phase is not("extracting" or "installing_service" or "cleaning_installation" or "deleting_data" or "ready" or "removed");ProgressBar.Value=p.Percent;
        Status.Text=p.Phase switch{"checking"=>"בודקים מקום פנוי והרשאות.","extracting"=>"מכינים את הרכיבים של יחד.","installing_service"=>"מתקינים את שירות Windows.","cleaning_installation"=>"מסיימים את ההתקנה.","starting_server"=>"מפעילים את יחד ומוודאים שהיא זמינה.","stopping_server"=>"סוגרים את יחד לפני ההסרה.","deleting_data"=>"מוחקים את הנתונים המקומיים שבחרתם להסיר.","rolling_back"=>"מחזירים את הגרסה הקודמת.","ready"=>"יחד זמינה לשימוש","removed"=>"ההסרה הושלמה",_=>"מתקדמים בהתקנה."};
    }
    // Every failure the window shows: the catalog's words, the support code, and the
    // developer's record written once and offered to copy (SetupFailureLog).
    private void Failure(string code,SetupFailureDetail? detail=null)
    {
        var report=SetupFailureReport.Create(code,detail,uninstall?"uninstall":completed?"renew-link":"install",DateTimeOffset.UtcNow,SetupOperation.DisplayVersion());
        ShowFailure(report,writeFailureLog(report),code=="DISK_SPACE"&&!uninstall?RequiredFreeBytes():null);
    }
    private void ShowFailure(SetupFailureReport report,string? log,long? requiredFreeBytes)
    {
        var entry=SetupErrorCatalog.Resolve(report.Code);failureReport=report;failureLog=log;
        ProgressBar.IsIndeterminate=false;
        Heading.Text=uninstall?"ההסרה נעצרה.":completed?"יחד מותקנת.\nהקישור להגדרה לא נפתח.":"נעצרנו כאן.\nההתקנה לא הושלמה.";
        // The card is self-contained: the choices above stay in place for a retry, so the
        // heading may be out of view while the card is what the user reads.
        FailureTitle.Text=uninstall?"ההסרה נעצרה":completed?"הקישור להגדרה לא נפתח":"ההתקנה לא הושלמה";
        Description.Text=FailureWhat.Text=entry.What;FailureNext.Text=SetupErrorCatalog.Next(report.Code,entry,requiredFreeBytes);
        // A removal that deletes data may already have deleted some of it, whatever stopped it.
        var partial=uninstall&&choice.Mode==UninstallMode.DeleteData;
        FailureContext.Text=partial?"ייתכן שחלק מהנתונים כבר נמחקו. אפשר להמשיך את המחיקה באותה בחירה; אל תתקינו את יחד מחדש לפני שהיא מסתיימת.":"";
        FailureContext.Visibility=partial?Visibility.Visible:Visibility.Collapsed;
        // Removal options open from an install window only; the removal window is already one.
        failureAction=entry.Action==SetupErrorAction.ContinueRemoval&&(uninstall||completed)?SetupErrorAction.None:entry.Action;
        FailureAction.Content=SetupErrorCatalog.Label(failureAction);FailureAction.Visibility=failureAction==SetupErrorAction.None?Visibility.Collapsed:Visibility.Visible;
        SupportCode.Text="קוד לתמיכה: "+report.Code;FailureTechnicalText.Text=report.Text(log);FailureTechnical.IsExpanded=false;
        Notice.Visibility=Visibility.Collapsed;FailurePanel.Visibility=Visibility.Visible;
        Status.Text="הפעולה טרם הושלמה";Primary.Content=partial?"המשך המחיקה":"ניסיון נוסף";
        // Once the card has its size, scroll it into view.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(RevealFailure));
    }
    private void RevealFailure()
    {
        if(FailurePanel.Visibility!=Visibility.Visible)return;
        UpdateLayout();FailurePanel.BringIntoView();
    }
    private void ClearFailure(){failureReport=null;failureLog=null;failureAction=SetupErrorAction.None;FailurePanel.Visibility=Visibility.Collapsed;Notice.Visibility=Visibility.Visible;}
    private static long? RequiredFreeBytes()
    {
        try{return InstallPackage.RequiredFreeBytes();}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException){return null;}
    }
    private void FailureAction_Click(object sender,RoutedEventArgs e)
    {
        if(running)return;
        if(failureAction==SetupErrorAction.ContinueRemoval){RemovalOptions_Click(sender,e);return;}
        if(SetupErrorCatalog.Target(failureAction) is not {} target)return;
        try{Process.Start(new ProcessStartInfo(target){UseShellExecute=true});}
        catch(Exception error)when(error is Win32Exception or InvalidOperationException)
        {Status.Text=target.StartsWith("https://",StringComparison.Ordinal)?"הדפדפן לא נפתח. פתחו בדפדפן את הכתובת "+target:"הגדרות Windows לא נפתחו. פתחו את התפריט התחל וחפשו הגדרות.";}
    }
    private void CopyDetails_Click(object sender,RoutedEventArgs e)
    {
        if(failureReport is null)return;
        try{Clipboard.SetText(failureReport.Text(failureLog));Status.Text="הפרטים הועתקו. אפשר להדביק אותם בהודעה לצוות של יחד.";}
        catch(ExternalException){FailureTechnical.IsExpanded=true;Status.Text="ההעתקה לא הצליחה. סמנו את הטקסט בפרטים הטכניים והעתיקו אותו.";}
    }
    private void TryOpenBrowser(SetupResult current)
    {
        try{OpenBrowser(current);}
        catch(Exception error)when(error is Win32Exception or InvalidOperationException){Status.Text="הדפדפן לא נפתח. אפשר לנסות שוב בלחיצה על פתיחת יחד.";}
    }
    private static void OpenBrowser(SetupResult result)
    {
        if(result.Url is null)return;var url=result.Url+(result.SetupToken is null?"":"#setup="+result.SetupToken);Process.Start(new ProcessStartInfo(url){UseShellExecute=true});
        try{var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"Yahad.url");if(!File.Exists(path))File.WriteAllText(path,"[InternetShortcut]\r\nURL="+result.Url+"\r\n");}catch{}
    }
    private void Close_Click(object sender,RoutedEventArgs e){if(!running)Close();}
    private void OnClosing(object? sender,CancelEventArgs e){if(running)e.Cancel=true;}
}

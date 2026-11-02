using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Yahad.Install;

namespace Yahad.Setup;

// Deliberately separate from Continue Removal: this is one explicit receipt ACL
// repair, then return. No service stop, data deletion or automatic continuation.
internal sealed class ReceiptRepairWindow:Window
{
    private readonly Button primary=new(){Content="תיקון הקבלה בלבד",IsEnabled=false};
    private readonly CheckBox consent=new(){Content="אני מאשר/ת את הידוק ההרשאות של הקבלה היחידה שמוצגת כאן.",Margin=new Thickness(0,18,0,12)};
    private readonly TextBlock status=new(){Text="טרם בוצע שינוי.",Margin=new Thickness(0,16,0,0)};
    private readonly TextBox proof=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,FlowDirection=FlowDirection.LeftToRight,Margin=new Thickness(0,12,0,0),Visibility=Visibility.Collapsed};
    private bool running,completed;
    internal ReceiptRepairWindow(string? previewServiceSid=null)
    {
        Title="יחד | תיקון קבלת הסרה";Width=800;Height=700;MinWidth=760;MinHeight=620;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;FlowDirection=FlowDirection.RightToLeft;
        Background=new SolidColorBrush(Color.FromRgb(247,248,244));Foreground=new SolidColorBrush(Color.FromRgb(38,62,58));
        var body=new StackPanel{Margin=new Thickness(32)};
        void Text(string value,double size=15){body.Children.Add(new TextBlock{Text=value,FontSize=size,Margin=new Thickness(0,0,0,12),TextWrapping=TextWrapping.Wrap});}
        Text("יחד · "+ApplicationRelease.Version,13);Text("תיקון קבלת ההסרה",29);
        Text("הפעולה מתקנת הרשאות של קבלה מקומית אחת שנשארה מהסרה שנכשלה. היא לא מוחקת נתונים, לא מסירה או מפעילה שירות, ולא ממשיכה להסרה אוטומטית.");
        Text("הקובץ היחיד שהרשאותיו יתוקנו:");
        body.Children.Add(new TextBox{Text=InstallLocations.RemovalStateFile,IsReadOnly=true,FlowDirection=FlowDirection.LeftToRight,Margin=new Thickness(0,0,0,12)});
        Text("אחרי התיקון: SYSTEM ומנהלי המחשב יקבלו גישה מלאה; שירות יחד יקבל קריאה והרצה בלבד. הרשאות הביניים האחרות יוסרו. מדיניות הביקורת ותוכן הקובץ לא ישתנו.");
        try
        {
            var sid=previewServiceSid??((SecurityIdentifier)new NTAccount("NT SERVICE",InstallLocations.ServiceName).Translate(typeof(SecurityIdentifier))).Value;
            body.Children.Add(new TextBox{Text=NativeInstallFiles.Descriptor(sid).Replace("OICI",""),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,FlowDirection=FlowDirection.LeftToRight,FontFamily=new FontFamily("Consolas"),Margin=new Thickness(0,0,0,12)});
            consent.Checked+=(_,_)=>{if(!running)primary.IsEnabled=true;};consent.Unchecked+=(_,_)=>{if(!running)primary.IsEnabled=false;};
        }
        catch{status.Text="השירות הקבוע לא זוהה. לא ניתן לבצע תיקון.";consent.IsEnabled=false;}
        body.Children.Add(consent);body.Children.Add(primary);body.Children.Add(status);body.Children.Add(proof);
        Content=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        Closing+=(_,e)=>{if(running)e.Cancel=true;};primary.Click+=Start;
    }
    private async void Start(object sender,RoutedEventArgs e)
    {
        if(completed){Close();return;}if(running||consent.IsChecked!=true)return;
        running=true;primary.IsEnabled=false;consent.IsEnabled=false;status.Text="ממתינים לאישור Windows לתיקון הקבלה בלבד.";
        try
        {
            var result=await SetupElevation.RunAsync(true,p=>Dispatcher.Invoke(()=>status.Text=p.Phase=="protecting_receipt"?"מעדכנים את הרשאות הקבלה בלבד.":"בודקים את הבעלים, ההתקנה והקבלה."),CancellationToken.None,UninstallMode.DeleteData,repairReceipt:true);
            if(!result.Ok){status.Text="התיקון לא הושלם. קוד תקלה: "+result.Code+(result.Failure?.Display()??"");return;}
            completed=true;primary.Content="סיום";status.Text="הקבלה תוקנה ואומתה. לא בוצעה הסרה. יש לסגור מסך זה ולפתוח בנפרד את מסך ההסרה להמשך באותה בחירה.";
            if(result.ReceiptSha256 is{} digest){proof.Text="SHA-256 before = after: "+digest;proof.Visibility=Visibility.Visible;}
        }
        catch(Exception error){status.Text="התיקון לא הושלם. קוד תקלה: "+SetupElevation.Code(error)+SetupFailureDetail.From(error).Display();}
        finally{running=false;primary.IsEnabled=completed||consent.IsChecked==true;consent.IsEnabled=!completed;}
    }
}

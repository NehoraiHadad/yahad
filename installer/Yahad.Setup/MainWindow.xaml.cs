using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Yahad.Install;

namespace Yahad.Setup;

public partial class MainWindow : Window
{
    private CancellationTokenSource? running;
    private string? session;
    private string? package;
    private bool completed;
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        var screen = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--preview="))?.Split('=')[1];
        if (screen == "error") ShowFailure("PACKAGE_HASH", true);
        else if (screen == "consent") ShowConsent();
        else if (screen == "ready") ShowReady(true);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (running is not null) return;
        if (completed) { Close(); return; }
        using var cancellation = new CancellationTokenSource(); running = cancellation;
        Primary.IsEnabled = false; Secondary.Content = "עצירת הבדיקה";
        Notice.Background = new SolidColorBrush(Color.FromRgb(234, 241, 234));
        Status.FlowDirection = FlowDirection.RightToLeft;
        Heading.Text = "מכינים מקום,\nצעד אחר צעד.";
        Description.Text = "בודקים את שלמות החבילה ומכינים קבצים לדוגמה בתיקייה פרטית וזמנית.";
        ProgressBar.Visibility = Visibility.Visible;
        try
        {
            if (session is null)
            {
                session = SafeDirectory.CreatePrivateSession(Path.GetTempPath());
                package = Path.Combine(session, "fixture.zip");
                FixtureBundle.Create(package);
            }
            var progress = new Progress<Yahad.Install.Progress>(p =>
            {
                ProgressBar.Value = p.Stage == Stage.Ready ? 100 : p.Stage == Stage.Staging ? 65 : 20;
                Status.Text = p.Stage == Stage.Ready ? "הקבצים נבדקו בהצלחה" : p.Stage == Stage.Staging ? "מכינים את קבצי הדוגמה" : "בודקים שלמות ותאימות";
            });
            await Task.Run(() => FixtureInstaller.StageAsync(package!, session, cancellation.Token, progress));
            ShowReady(false);
        }
        catch (OperationCanceledException)
        {
            Heading.Text = "עצרנו כאן.\nאפשר להמשיך אחר כך.";
            Description.Text = "הבדיקה נעצרה. קבצי הדוגמה נשמרו בתיקייה הזמנית, ושום התקנת מערכת לא בוצעה.";
            Primary.Content = "המשך הבדיקה  ←"; Status.Text = "מושהה";
        }
        catch (Exception ex)
        {
            // Only fixed diagnostic codes; never paths, tokens, payloads or exception strings.
            var code = ex is InvalidDataException ? "PACKAGE_INVALID" : ex is UnauthorizedAccessException ? "ACCESS_DENIED" : "LOCAL_IO";
            ShowFailure(code, false);
        }
        finally { running = null; Primary.IsEnabled = true; Secondary.Content = "סגירה"; }
    }

    private void ShowReady(bool preview)
    {
        completed = true;
        Notice.Background = new SolidColorBrush(Color.FromRgb(234, 241, 234));
        Status.FlowDirection = FlowDirection.RightToLeft;
        Eyebrow.Text = preview ? "תצוגת מסך לדוגמה" : "בדיקת ההיתכנות הושלמה";
        Heading.Text = "יש בסיס טוב.\nממשיכים לבנות.";
        Description.Text = "המסלול המקומי מוכן לבדיקה: חבילה מאומתת, תיקייה פרטית ושמירה בטוחה של התקדמות.";
        NoticeText.Text = "זו עדיין אינה התקנת יחד. השירות, חיבור הרשת ואשף המשפחה בדפדפן יופעלו רק לאחר השלמת שכבות האבטחה ובדיקות במכונה נפרדת.";
        Primary.Content = "סיום הבדיקה";
        System.Windows.Automation.AutomationProperties.SetName(Primary, "סיום הבדיקה");
        Step1.Text = "✓  שלמות חבילת הדוגמה"; Step2.Text = "✓  תיקייה פרטית עם הרשאות Windows"; Step3.Text = "✓  קבצים ויומן התקדמות מאומתים";
        if (preview) Status.Text = "תצוגה בלבד · לא הורצה בדיקה";
    }

    private void ShowFailure(string code, bool preview)
    {
        Eyebrow.Text = preview ? "תצוגת מסך שגיאה" : "הבדיקה נעצרה בבטחה";
        Heading.Text = "לא ממשיכים\nעם חבילה לא תקינה.";
        Description.Text = "לא הצלחנו להשלים את הבדיקה. הקבצים הקיימים נשמרו; אפשר לנסות שוב או לסגור ולחזור בהמשך.";
        Notice.Background = new SolidColorBrush(Color.FromRgb(249, 237, 224));
        NoticeText.Text = "אם התקלה חוזרת, מוסרים לתמיכה רק את קוד התקלה שמופיע כאן. אין צורך לשלוח מפתחות, סיסמאות או שיחות.";
        Status.Text = (preview ? "תצוגה בלבד · " : "קוד תקלה: ") + code;
        Status.FlowDirection = FlowDirection.LeftToRight;
        Primary.Content = "ניסיון נוסף  ←";
        System.Windows.Automation.AutomationProperties.SetName(Primary, "ניסיון נוסף");
    }

    private void ShowConsent()
    {
        Eyebrow.Text = "תצוגת מסך הסבר לפני הרשאה";
        Heading.Text = "אישור אחד,\nעם הסבר ברור.";
        Description.Text = "בהתקנה המלאה, Windows יבקש הרשאת מנהל כדי להפעיל את יחד גם כשההורה לא מחובר למחשב.";
        NoticeText.Text = "הבקשה המתוכננת: התקנת שירות יחד בלבד. פתיחת גישה לרשת הביתית ואמון בתעודה יוסברו בנפרד. לא תופיע בקשת הרשאה בגרסת ההדגמה הזו.";
        Primary.Content = "חזרה לבדיקה";
    }
    private void LanPlan_Click(object sender, RoutedEventArgs e)
    { new LanPlanWindow { Owner = this }.ShowDialog(); }

    private void Close_Click(object sender, RoutedEventArgs e)
    { if (running is not null) running.Cancel(); else Close(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    { if (running is not null) { e.Cancel = true; running.Cancel(); } }
}

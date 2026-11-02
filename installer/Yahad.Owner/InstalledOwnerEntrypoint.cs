using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using Yahad.Authority;

namespace Yahad.Owner;

internal sealed record InstalledOwnerLaunch(string Mode,string InstallationId);

// Used by the integrated candidate App. The sole installer must
// create a shortcut containing only this canonical installation address. The ID
// is not an authority claim: the existing fixed SCM/process/token-authenticated
// endpoint independently authenticates the actual original Windows Owner.
internal static class InstalledOwnerEntrypoint
{
    internal static InstalledOwnerLaunch Parse(string[] arguments)
    {
        if(arguments.Length!=2||arguments[0] is not("--confirm" or "--recover")||
            !Guid.TryParseExact(arguments[1],"N",out var installation)||installation.ToString("N")!=arguments[1])
            throw new IOException("OWNER_LAUNCH_ARGUMENTS");
        return new(arguments[0]=="--confirm"?"confirm":"recover",arguments[1]);
    }
    internal static async Task<int> RunAsync(Application application,string[] arguments,CancellationToken cancellation=default)
    {
        if(!application.Dispatcher.CheckAccess())throw new InvalidOperationException("OWNER_LAUNCH_DISPATCHER");
        try
        {
            var request=Parse(arguments);RequireInteractiveUser();
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(20));
            Window window;
            if(request.Mode=="confirm")window=await OwnerWindowFactory.ReadAsync(request.InstallationId,deadline.Token);
            else
            {
                // Authenticate the existing server/actual Owner before opening a
                // control window. This read cannot approve, restart or repair.
                var frame=new AuthorityFrame(1,Guid.NewGuid().ToString("N"),"recovery",JsonSerializer.SerializeToElement(new{}));
                var reply=await WindowsAuthorityEndpoint.SendOwnerAsync(request.InstallationId,frame,deadline.Token);
                if(reply.Format!=1||reply.Id!=frame.Id||reply.LastRequest is not {} body||body.Deserialize<OwnerRecoverySummary>() is null)
                    throw new IOException("OWNER_LAUNCH_RECOVERY_REPLY");
                window=OwnerWindowFactory.Recovery(request.InstallationId);
            }
            cancellation.ThrowIfCancellationRequested();application.MainWindow=window;window.ShowDialog();return 0;
        }
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested){return 1;}
        catch(IOException error)when(error.Message=="OWNER_NO_PENDING_CONFIRMATION")
        {Show("אין כרגע בקשת אישור ממתינה. אפשר לפתוח את כלי בדיקת העדכונים וההתאוששות מקיצור הדרך של יחד.",false);return 0;}
        catch(IOException error)when(error.Message=="OWNER_LAUNCH_ARGUMENTS")
        {Show("הבקשה לפתיחת כלי העדכון אינה מוכרת. פתחו אותו מקיצור הדרך של יחד.",true);return 2;}
        catch(UnauthorizedAccessException)
        {Show("לא ניתן לפתוח את כלי העדכון בחשבון זה. היכנסו לחשבון Windows שבו הותקנה יחד ופתחו את קיצור הדרך שוב.",true);return 3;}
        catch(Exception error)when(error is IOException or Win32Exception or OperationCanceledException or JsonException)
        {Show("לא הצלחנו לקבל תשובה משירות העדכון. בדקו שוב את המצב בעוד רגע לפני ניסיון נוסף. אפשר להשתמש בכלי ההתקנה לתיקון אם הבעיה נמשכת.",true);return 4;}
    }
    private static void RequireInteractiveUser()
    {
        if(!OperatingSystem.IsWindows()||!Environment.UserInteractive)throw new UnauthorizedAccessException("OWNER_LAUNCH_INTERACTIVE");
        using var process=Process.GetCurrentProcess();if(process.SessionId<1)throw new UnauthorizedAccessException("OWNER_LAUNCH_SESSION");
        using var impersonated=WindowsIdentity.GetCurrent(true);if(impersonated is not null)throw new UnauthorizedAccessException("OWNER_LAUNCH_IMPERSONATION");
        using var identity=WindowsIdentity.GetCurrent();
        if(!identity.IsAuthenticated||identity.User is not {} user||!user.IsAccountSid()||!user.Value.StartsWith("S-1-5-21-",StringComparison.Ordinal))
            throw new UnauthorizedAccessException("OWNER_LAUNCH_USER");
    }
    private static void Show(string message,bool failure)
        =>MessageBox.Show(message,"יחד",MessageBoxButton.OK,failure?MessageBoxImage.Warning:MessageBoxImage.Information,
            MessageBoxResult.OK,MessageBoxOptions.RightAlign|MessageBoxOptions.RtlReading);
}

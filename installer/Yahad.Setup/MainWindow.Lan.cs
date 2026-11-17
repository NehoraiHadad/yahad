using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Setup;

public partial class MainWindow
{
    private sealed record LanDisplay(LanChoice Choice, string Label);
    private bool existingNetworkAccess;
    // Windows' own category for the selected adapter, read before anything is
    // installed so a Public network is explained here, not as a failure code.
    // Read-only: the user changes the category in Windows; Setup never does.
    private enum LanProfile { Unknown, Private, Public, Domain }
    private Func<string,int> readLanCategory=NativeNetworkProfile.ReadCategory;
    private LanProfile selectedProfile=LanProfile.Unknown;
    private static bool HasExistingAccess()=>new[]{InstallLocations.Binaries,InstallLocations.RemovalStateFile,LanProxyPaths.State}.Any(Path.Exists);
    private void InitializeLan(IReadOnlyList<LanChoice>? choices=null,bool existingAccess=false,Func<string,int>? lanCategory=null)
    {
        if(uninstall){LanPanel.Visibility=Visibility.Collapsed;return;}
        if(lanCategory is not null)readLanCategory=lanCategory;
        Activated+=(_,_)=>{if(!running&&!completed&&HomeLan.IsChecked==true&&LanAddress.SelectedItem is LanDisplay)RefreshLanProfile();};
        try{LanAddress.ItemsSource=(choices??LanSetupRequest.Choices()).Select(c=>new LanDisplay(c,c.DisplayName+" · "+c.Address)).ToArray();}
        catch{LanAddress.ItemsSource=Array.Empty<LanDisplay>();}
        LanProgram.Text=LanProxyPaths.Caddy(InfrastructureRelease.Version);
        existingNetworkAccess=existingAccess;
        KeepLan.Content=existingAccess?"להשאיר את אפשרויות הגישה הקיימות":"רק במחשב הזה";
        LanKeepHint.Text=existingAccess?"הגישה שכבר הוגדרה תישמר. הבחירה הזאת אינה מכבה גישה קיימת מהטלפון.":"יחד תהיה זמינה במחשב הזה בלבד. אפשר להוסיף גישה מהטלפון בהמשך.";
        DisableLan.Visibility=DisableLanHint.Visibility=existingAccess?Visibility.Visible:Visibility.Collapsed;
        UpdateLanUi();
    }
    private void LanAddressChanged(object sender,SelectionChangedEventArgs e)
    {if(LanConsent is null)return;LanConsent.IsChecked=false;RefreshLanProfile();}
    private void LanChanged(object sender,RoutedEventArgs e)
    {if(LanConsent is null||LanSelection is null||Primary is null)return;if(HomeLan.IsChecked==true)RefreshLanProfile();else UpdateLanUi();}
    private void RefreshLanProfile()
    {
        selectedProfile=LanAddress.SelectedItem is LanDisplay selected?ReadProfile(selected.Choice.AdapterId):LanProfile.Unknown;
        UpdateLanUi();
    }
    private LanProfile ReadProfile(string adapter)
    {
        try{return readLanCategory(adapter) switch{1=>LanProfile.Private,0=>LanProfile.Public,2=>LanProfile.Domain,_=>LanProfile.Unknown};}
        catch{return LanProfile.Unknown;}
    }
    private void UpdateLanUi()
    {
        if(uninstall||running||completed)return;
        var home=HomeLan.IsChecked==true;LanSelection.Visibility=home?Visibility.Visible:Visibility.Collapsed;
        var chosen=home&&LanAddress.SelectedItem is LanDisplay;var ready=chosen&&selectedProfile==LanProfile.Private;
        LanProfileHelp.Visibility=chosen&&!ready?Visibility.Visible:Visibility.Collapsed;
        LanConsentArea.Visibility=!chosen||ready?Visibility.Visible:Visibility.Collapsed;
        if(chosen&&!ready)ShowProfileHelp(selectedProfile);
        Primary.IsEnabled=UpdateChoiceReady()&&(!home||(ready&&LanConsent.IsChecked==true));
        Primary.ToolTip=chosen&&!ready?"הרשת שנבחרה אינה מוגדרת ב-Windows כרשת פרטית. אפשר לסמן אותה כפרטית או לבחור רק במחשב הזה.":null;
    }
    private void ShowProfileHelp(LanProfile profile)
    {
        var steps=profile is LanProfile.Public;
        LanProfileHeading.Text=profile switch
        {
            LanProfile.Public=>"Windows מגדיר את הרשת הזו כרשת ציבורית",
            LanProfile.Domain=>"זו רשת של עבודה או ארגון",
            _=>"לא הצלחנו לבדוק את סוג הרשת"
        };
        LanProfileText.Text=profile switch
        {
            LanProfile.Public=>"ברשת ציבורית Windows חוסם חיבורים ממכשירים אחרים, ולכן הטלפון לא יוכל להגיע ליחד. אם זו הרשת של הבית, אפשר לסמן אותה כרשת פרטית. ברשת של בית קפה, עבודה או מקום ציבורי אל תשנו את ההגדרה; בחרו רק במחשב הזה.",
            LanProfile.Domain=>"גישה מהטלפון אפשרית רק ברשת הביתית. במחשב שמחובר לרשת של עבודה בחרו רק במחשב הזה.",
            _=>"Windows לא מסר את סוג הרשת שנבחרה. ודאו שהמחשב מחובר לרשת הביתית ולחצו על בדיקה מחדש."
        };
        LanProfileSteps.Visibility=OpenNetworkSettings.Visibility=steps?Visibility.Visible:Visibility.Collapsed;
    }
    private void OpenNetworkSettings_Click(object sender,RoutedEventArgs e)
    {
        try{Process.Start(new ProcessStartInfo(SetupErrorCatalog.NetworkSettingsUri){UseShellExecute=true});}
        catch{LanProfileText.Text="הגדרות Windows לא נפתחו. פתחו את התפריט התחל, חפשו הגדרות, ובחרו רשת ואינטרנט.";}
    }
    private void RecheckNetwork_Click(object sender,RoutedEventArgs e)=>RefreshLanProfile();
    private LanSetupRequest? SelectedLanRequest()
    {
        if(uninstall)return null;
        if(HomeLan.IsChecked==true)
        {
            if(LanAddress.SelectedItem is not LanDisplay selected||LanConsent.IsChecked!=true)throw new IOException("LAN_CONSENT_REQUIRED");
            var request=new LanSetupRequest(LanSetupAction.Enable,selected.Choice.AdapterId,selected.Choice.Address,true,LanHttpHelp.IsChecked==true);
            request.Require();return request;
        }
        if(DisableLan.IsChecked==true)
        {
            if(!existingNetworkAccess)throw new IOException("LAN_EXISTING_CONFIGURATION_REQUIRED");
            return new(LanSetupAction.Disable);
        }
        return null;
    }
    private void ShowLanResult(LanSetupResult? lan)
    {
        LanPanel.Visibility=Visibility.Collapsed;
        if(lan is null)return;
        LanResultPanel.Visibility=Visibility.Visible;LanRootFingerprint.Text=lan.RootSha256;
        SaveLanCertificate.Visibility=lan.CertificateDerBase64 is null?Visibility.Collapsed:Visibility.Visible;
        LanResultText.Text="כתובת יחד בבית: "+lan.Origin+"\n"+
            "דף ההצטרפות: "+(lan.HelpOrigin??lan.Origin+LanDeployment.HelpPath)+"\n"+
            "חיבור HTTPS מהמחשב נבדק. כניסה ממכשיר נוסף עדיין לא נבדקה.\n"+
            // The browser opens the loopback address: the owner's first sign-in happens there,
            // and this computer does not trust the home certificate.
            "במחשב הזה יחד נפתחת בכתובת 127.0.0.1, שפועלת רק כאן. אין צורך לשמור את הכתובת לבית: "+
            "היא מופיעה גם ביחד, במסך ההורה, תחת „כתובת יחד למכשירים אחרים”.";
    }
    private void SaveLanCertificate_Click(object sender,RoutedEventArgs e)
    {
        if(result?.Lan is not{CertificateDerBase64:{ } encoded}lan)return;
        try
        {
            if(encoded.Length>20000)throw new IOException("LAN_CERTIFICATE_INVALID");
            var root=LanCertificate.ExportPublicRoot(Convert.FromBase64String(encoded));
            if(root.Sha256Fingerprint!=lan.RootSha256)throw new IOException("LAN_CA_CHANGED");
            var dialog=new Microsoft.Win32.SaveFileDialog{FileName="yahad-home-root.crt",DefaultExt=".crt",Filter="Public certificate (*.crt)|*.crt",OverwritePrompt=true};
            if(dialog.ShowDialog(this)==true)File.WriteAllBytes(dialog.FileName,root.Der);
        }
        catch{Status.Text="התעודה לא נשמרה. לא שונתה הגדרת אמון במחשב.";}
    }
}

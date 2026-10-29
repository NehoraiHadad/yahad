using System.IO;
using System.Windows;
using System.Windows.Controls;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Setup;

public partial class MainWindow
{
    private sealed record LanDisplay(LanChoice Choice, string Label);
    private void InitializeLan(IReadOnlyList<LanChoice>? choices=null)
    {
        if(uninstall){LanPanel.Visibility=Visibility.Collapsed;return;}
        try{LanAddress.ItemsSource=(choices??LanSetupRequest.Choices()).Select(c=>new LanDisplay(c,c.DisplayName+" · "+c.Address)).ToArray();}
        catch{LanAddress.ItemsSource=Array.Empty<LanDisplay>();}
        LanProgram.Text=LanProxyPaths.Caddy;
        UpdateLanUi();
    }
    private void LanAddressChanged(object sender,SelectionChangedEventArgs e)
    {if(LanConsent is null)return;LanConsent.IsChecked=false;UpdateLanUi();}
    private void LanChanged(object sender,RoutedEventArgs e)
    {if(LanConsent is null||LanSelection is null||Primary is null)return;UpdateLanUi();}
    private void UpdateLanUi()
    {
        if(uninstall||running||completed)return;
        var home=HomeLan.IsChecked==true;LanSelection.Visibility=home?Visibility.Visible:Visibility.Collapsed;
        Primary.IsEnabled=!home||(LanAddress.SelectedItem is LanDisplay&&LanConsent.IsChecked==true);
    }
    private LanSetupRequest? SelectedLanRequest()
    {
        if(uninstall)return null;
        if(HomeLan.IsChecked==true)
        {
            if(LanAddress.SelectedItem is not LanDisplay selected||LanConsent.IsChecked!=true)throw new IOException("LAN_CONSENT_REQUIRED");
            var request=new LanSetupRequest(LanSetupAction.Enable,selected.Choice.AdapterId,selected.Choice.Address,true,LanHttpHelp.IsChecked==true);
            request.Require();return request;
        }
        return DisableLan.IsChecked==true?new(LanSetupAction.Disable):null;
    }
    private void ShowLanResult(LanSetupResult? lan)
    {
        LanPanel.Visibility=Visibility.Collapsed;
        if(lan is null)return;
        LanResultPanel.Visibility=Visibility.Visible;LanRootFingerprint.Text=lan.RootSha256;
        SaveLanCertificate.Visibility=lan.CertificateDerBase64 is null?Visibility.Collapsed:Visibility.Visible;
        LanResultText.Text="כתובת יחד בבית: "+lan.Origin+"\n"+
            (lan.HelpOrigin is null?"דף HTTP אינו מופעל. ניתן להעביר את התעודה הציבורית מהמחשב באמצעות ערוץ עצמאי.":"דף ההצטרפות: "+lan.HelpOrigin)+"\n"+
            (lan.HostConnectionVerified?"חיבור HTTPS מהמחשב נבדק. כניסה ממכשיר נוסף עדיין לא נבדקה.":"הגישה הביתית נעצרה. לפני התקנה מחדש אפשר להסיר את האמון בתעודה הזו מכל מכשיר לפי מדריך ההצטרפות.");
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

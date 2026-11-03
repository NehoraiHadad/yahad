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
    private static bool HasExistingAccess()
    {
        // Presence only through public parent directories. The window never reads
        // installation records or CA contents; the operation verifies them.
        try
        {
            var root=LanProxyPaths.State;
            var binaries=InstallLocations.Binaries;var removal=InstallLocations.RemovalStateFile;
            return Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(binaries)!,Path.GetFileName(binaries),SearchOption.TopDirectoryOnly).Any()||
                Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(removal)!,Path.GetFileName(removal),SearchOption.TopDirectoryOnly).Any()||
                Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(root)!,Path.GetFileName(root),SearchOption.TopDirectoryOnly).Any();
        }
        catch{return true;} // Unknown existing state must not be called local-only.
    }
    private void InitializeLan(IReadOnlyList<LanChoice>? choices=null,bool existingAccess=false)
    {
        if(uninstall){LanPanel.Visibility=Visibility.Collapsed;return;}
        try{LanAddress.ItemsSource=(choices??LanSetupRequest.Choices()).Select(c=>new LanDisplay(c,c.DisplayName+" · "+c.Address)).ToArray();}
        catch{LanAddress.ItemsSource=Array.Empty<LanDisplay>();}
        LanProgram.Text=LanProxyPaths.Caddy;
        existingNetworkAccess=existingAccess;
        KeepLan.Content=existingAccess?"להשאיר את אפשרויות הגישה הקיימות":"רק במחשב הזה";
        LanKeepHint.Text=existingAccess?"הגישה שכבר הוגדרה תישמר. הבחירה הזאת אינה מכבה גישה קיימת מהטלפון.":"יחד תהיה זמינה במחשב הזה בלבד. אפשר להוסיף גישה מהטלפון בהמשך.";
        DisableLan.Visibility=DisableLanHint.Visibility=existingAccess?Visibility.Visible:Visibility.Collapsed;
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
        Primary.IsEnabled=UpdateChoiceReady()&&(!home||(LanAddress.SelectedItem is LanDisplay&&LanConsent.IsChecked==true));
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

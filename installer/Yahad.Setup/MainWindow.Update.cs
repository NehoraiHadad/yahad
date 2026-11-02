using System.IO;
using System.Windows;
namespace Yahad.Setup;
public partial class MainWindow
{
    private bool fullUpdateProfile,existingUpdateProfile;
    private static bool HasExistingUpdateInstallation()
    {
        // Presence through public parents only. A preserved computer-only
        // installation has no binaries/LAN state but still has enrollment.
        try
        {
            return HasExistingUpdateInstallation(path=>Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!,Path.GetFileName(path),SearchOption.TopDirectoryOnly).Any());
        }
        catch{return true;}
    }
    internal static bool HasExistingUpdateInstallation(Func<string,bool> present)
        =>new[]{InstallLocations.State,InstallLocations.Binaries,InstallLocations.RemovalStateFile}.Any(present);
    private void InitializeUpdate(bool full,bool existing)
    {
        fullUpdateProfile=full;existingUpdateProfile=existing;
        UpdatePanel.Visibility=!uninstall&&full?Visibility.Visible:Visibility.Collapsed;
        if(full&&existing)
        {
            UpdateChoices.Visibility=Visibility.Collapsed;
            UpdateExplanation.Text="בהתקנה קיימת נשמרים אישור העדכון והבחירה הקיימים. אפשר לשנות אותם בהגדרות של יחד. אם זו התקנה ישנה ללא המנגנון, המתקין יסביר איזה צעד נדרש.";
        }
        UpdateLanUi();
    }
    private bool UpdateChoiceReady()=>!fullUpdateProfile||existingUpdateProfile||UpdateConsent.IsChecked==true;
    private void UpdateChanged(object sender,RoutedEventArgs e)
    {if(UpdatePanel is not null&&Primary is not null&&LanPanel is not null)UpdateLanUi();}
    private UpdateSetupChoice? SelectedUpdateRequest()
    {
        if(uninstall||!fullUpdateProfile||existingUpdateProfile)return null;
        if(UpdateConsent.IsChecked!=true)throw new IOException("UPDATE_CONSENT_REQUIRED");
        return new(2,true,AutomaticUpdates.IsChecked==true,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
}

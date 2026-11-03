using System.IO;
using System.Windows;
namespace Yahad.Setup;
public partial class MainWindow
{
    private bool existingUpdateProfile;
    private void InitializeUpdate(bool existing)
    {
        existingUpdateProfile=existing;
        UpdatePanel.Visibility=!uninstall?Visibility.Visible:Visibility.Collapsed;
        if(existing)
        {
            UpdateChoices.Visibility=Visibility.Collapsed;
            UpdateExplanation.Text="בהתקנה קיימת נשמרים אישור העדכון והבחירה הקיימים. אפשר לשנות אותם בהגדרות של יחד. אם זו התקנה ישנה ללא המנגנון, המתקין יסביר איזה צעד נדרש.";
        }
        UpdateLanUi();
    }
    private bool UpdateChoiceReady()=>existingUpdateProfile||UpdateConsent.IsChecked==true;
    private void UpdateChanged(object sender,RoutedEventArgs e)
    {if(UpdatePanel is not null&&Primary is not null&&LanPanel is not null)UpdateLanUi();}
    private UpdateSetupChoice? SelectedUpdateRequest()
    {
        if(uninstall||existingUpdateProfile)return null;
        if(UpdateConsent.IsChecked!=true)throw new IOException("UPDATE_CONSENT_REQUIRED");
        return new(2,true,AutomaticUpdates.IsChecked==true,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
}

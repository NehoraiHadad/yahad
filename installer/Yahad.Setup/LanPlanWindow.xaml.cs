using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Yahad.Install;

namespace Yahad.Setup;
public partial class LanPlanWindow : Window
{
    private sealed record Choice(LanChoice Address, string Label);
    private readonly IReadOnlyList<LanChoice> addresses;
    public LanPlanWindow()
    {
        InitializeComponent();
        var found = new List<AdapterAddress>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                        found.Add(new(adapter.Id, adapter.Name, unicast.Address.ToString(), adapter.OperationalStatus == OperationalStatus.Up));
        }
        catch (NetworkInformationException) { }
        addresses = NetworkPlan.Candidates(found);
        AddressChoice.ItemsSource = addresses.Select(a => new Choice(a, a.DisplayName + " · " + a.Address)).ToArray();
        // No automatic first/VPN/default-adapter selection.
        AddressStatus.Text = addresses.Count == 0 ? "לא נמצאה כתובת פרטית פעילה. התחברו לרשת הביתית ובדקו שוב." : "בחרו כתובת כדי להציג תכנית. הקישור עדיין לא פעיל.";
    }
    private void AddressChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AddressChoice.SelectedItem is not Choice choice) return;
        var plan = AccessPlanner.Build(addresses, choice.Address.AdapterId, choice.Address.Address);
        PlannedLink.Text = plan.CurrentLanOrigin;
        AddressStatus.Text = "כתובת מתוכננת בלבד — טרם הוגדר HTTPS וטרם אומתה גישה מהטאבלט.";
        SavePlan.IsEnabled = true;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (AddressChoice.SelectedItem is not Choice choice) return;
        try
        {
            var plan = AccessPlanner.Build(addresses, choice.Address.AdapterId, choice.Address.Address);
            var session = SafeDirectory.CreatePrivateSession(Path.GetTempPath());
            File.WriteAllText(Path.Combine(session, "access-plan.json"), JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
            AddressStatus.Text = "התכנית נשמרה בתיקייה פרטית וזמנית. לא בוצע שינוי ברשת, בתעודות או בחומת האש.";
        }
        catch { AddressStatus.Text = "שמירת התכנית נכשלה. לא בוצע שינוי בהגדרות המחשב."; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HueStudio.App.Views;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

    public SettingsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        var b = App.Home.Bridge;
        BridgeInfo.Text = b is null
            ? (App.Settings.DemoMode ? "Using the demo house." : "Not connected.")
            : $"{b.Name ?? "Hue Bridge"} at {b.Address}\nBridge id {b.BridgeId} · paired {b.PairedAt.LocalDateTime:d MMM yyyy}\nEntertainment key: {(string.IsNullOrEmpty(b.ClientKey) ? "missing (pair again)" : "ready")}";
        DemoToggle.IsOn = App.Settings.DemoMode;
        ThemeChoice.SelectedItem = App.Settings.Theme;
        _loading = false;
    }

    private void Repair_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(SetupPage));

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        await App.Window.StartAsync();
    }

    private async void Demo_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.DemoMode = DemoToggle.IsOn;
        App.Settings.Save();
        await App.Window.StartAsync();
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeChoice.SelectedItem is not string theme) return;
        App.Settings.Theme = theme;
        App.Settings.Save();
        App.Window.ApplyTheme(theme);
    }
}

using HueStudio.App.Services;
using HueStudio.App.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HueStudio.App;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["home"] = typeof(HomePage),
        ["scenes"] = typeof(ScenesPage),
        ["lights"] = typeof(LightsPage),
        ["entertainment"] = typeof(EntertainmentPage),
        ["automations"] = typeof(AutomationsPage),
        ["settings"] = typeof(SettingsPage),
    };

    private bool _selectingInCode;

    public MainWindow()
    {
        InitializeComponent();
        ApplyTheme(App.Settings.Theme);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1360, 880));
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "HueStudio.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        App.Home.ConnectionChanged += (_, s) => ShowConnection(s);
        App.Home.Error += (_, msg) => ShowError(msg);
    }

    /// <summary>Reconnects to the last bridge, or shows first-run setup.</summary>
    public async Task StartAsync()
    {
        try
        {
            if (App.Settings.DemoMode)
            {
                await App.Home.ConnectDemoAsync();
                Navigate("home");
                return;
            }

            var bridges = await App.Credentials.LoadAsync();
            var bridge = bridges.FirstOrDefault(b => b.BridgeId == App.Settings.ActiveBridgeId) ?? bridges.FirstOrDefault();
            if (bridge is null)
            {
                ContentFrame.Navigate(typeof(SetupPage));
                return;
            }

            await App.Home.ConnectAsync(bridge);
            Navigate("home");
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't connect to your bridge: {ex.Message}");
            ContentFrame.Navigate(typeof(SetupPage));
        }
    }

    public void Navigate(string tag, object? parameter = null)
    {
        if (Pages.TryGetValue(tag, out var page) && ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page, parameter);

        // Keep the left menu highlight in step when navigation happens from code.
        _selectingInCode = true;
        Nav.SelectedItem = tag == "settings"
            ? Nav.SettingsItem
            : Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (i.Tag as string) == tag);
        _selectingInCode = false;
    }

    public void ApplyTheme(string theme)
    {
        if (Content is FrameworkElement root)
            root.RequestedTheme = theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    }

    public void NavigateToGroup(string groupId) => ContentFrame.Navigate(typeof(RoomPage), groupId);

    public void ShowError(string message)
    {
        ErrorBar.Title = "Something went wrong";
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void ShowConnection(ConnectionState s)
    {
        (StatusText.Text, var color) = s switch
        {
            ConnectionState.Live => ($"Live · {App.Home.Bridge?.Name ?? "Hue Bridge"}", Colors.LimeGreen),
            ConnectionState.Demo => ("Demo house", Colors.MediumPurple),
            ConnectionState.Connecting => ("Connecting…", Colors.Orange),
            ConnectionState.Reconnecting => ("Reconnecting…", Colors.Orange),
            _ => ("Not connected", Colors.Gray),
        };
        StatusDot.Fill = new SolidColorBrush(color);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_selectingInCode) return;
        if (!App.Home.IsConnected && !args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SetupPage));
            return;
        }
        var tag = args.IsSettingsSelected ? "settings" : (args.SelectedItemContainer?.Tag as string ?? "home");
        Navigate(tag);
    }

    private void Nav_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }
}

using System.Collections.ObjectModel;
using HueStudio.Core.Api;
using HueStudio.Core.Discovery;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HueStudio.App.Views;

/// <summary>First run: find the bridge (mDNS + cloud discovery or typed IP), then pair with the link button.</summary>
public sealed partial class SetupPage : Page
{
    public ObservableCollection<DiscoveredBridge> Bridges { get; } = [];
    private DiscoveredBridge? _selected;

    public SetupPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await SearchAsync();

    private async Task SearchAsync()
    {
        Searching.Visibility = Visibility.Visible;
        SearchButton.IsEnabled = false;
        NoneFound.Visibility = Visibility.Collapsed;
        Bridges.Clear();
        try
        {
            foreach (var b in await new BridgeDiscovery().DiscoverAsync())
                Bridges.Add(b with { Name = b.Name ?? "Hue Bridge" });
        }
        finally
        {
            Searching.Visibility = Visibility.Collapsed;
            SearchButton.IsEnabled = true;
            NoneFound.Visibility = Bridges.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (Bridges.Count == 1) BridgeList.SelectedIndex = 0;
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private void BridgeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = BridgeList.SelectedItem as DiscoveredBridge;
        PairButton.IsEnabled = _selected is not null;
    }

    private async void Manual_Click(object sender, RoutedEventArgs e)
    {
        var ip = ManualIp.Text.Trim();
        if (ip.Length == 0) return;
        PairStatus.Text = $"Checking {ip}…";
        var bridge = await BridgeDiscovery.ProbeAsync(ip);
        if (bridge is null)
        {
            PairStatus.Text = $"No Hue Bridge answered at {ip}.";
            return;
        }
        PairStatus.Text = "";
        Bridges.Add(bridge with { Name = bridge.Name ?? "Hue Bridge" });
        BridgeList.SelectedItem = Bridges[^1];
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        PairButton.IsEnabled = false;
        PairRing.IsActive = true;
        PairStatus.Text = "Press the link button on your bridge now…";
        try
        {
            var progress = new Progress<TimeSpan>(left => PairStatus.Text = $"Press the link button on your bridge… {left.Seconds}s");
            var creds = await BridgePairing.WaitForLinkButtonAsync(_selected, Environment.MachineName, remaining: progress);
            await App.Credentials.UpsertAsync(creds);
            App.Settings.ActiveBridgeId = creds.BridgeId;
            App.Settings.DemoMode = false;
            App.Settings.Save();

            PairStatus.Text = "Paired. Loading your home…";
            await App.Home.ConnectAsync(creds);
            App.Window.Navigate("home");
        }
        catch (LinkButtonNotPressedException)
        {
            PairStatus.Text = "Timed out. Click Pair and press the bridge button within 30 seconds.";
        }
        catch (Exception ex)
        {
            PairStatus.Text = ex.Message;
        }
        finally
        {
            PairRing.IsActive = false;
            PairButton.IsEnabled = _selected is not null;
        }
    }

    private async void Demo_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.DemoMode = true;
        App.Settings.Save();
        await App.Home.ConnectDemoAsync();
        App.Window.Navigate("home");
    }
}

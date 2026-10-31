using System.Collections.ObjectModel;
using HueStudio.App.ViewModels;
using HueStudio.Core.State;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HueStudio.App.Views;

public sealed partial class LightsPage : Page
{
    public ObservableCollection<LightViewModel> Visible { get; } = [];

    public LightsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        App.Home.HomeChanged += OnHomeChanged;
        Sync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => App.Home.HomeChanged -= OnHomeChanged;

    private void OnHomeChanged(object? sender, HomeChangedEventArgs e) => Sync();

    private void Sync()
    {
        var s = App.Home.State;
        var q = Search.Text?.Trim() ?? "";
        var lights = s.Lights
            .Select(l => (Light: l, Room: s.RoomOf(l)?.Name ?? "No room"))
            .Where(x => q.Length == 0 || x.Light.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || x.Room.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(x => x.Room).ThenBy(x => x.Light.Name)
            .Select(x => x.Light)
            .ToList();
        Collections.SyncById(Visible, lights, l => l.Id, l => new LightViewModel(App.Home, l.Id), (vm, l) => vm.Sync(l, s));
        var on = s.Lights.Count(l => l.IsOn);
        Summary.Text = $"{on} of {s.Lights.Count} on · {s.Lights.Count(l => l.SupportsColor)} colour capable";
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Sync();
}

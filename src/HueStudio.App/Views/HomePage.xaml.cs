using System.Collections.ObjectModel;
using HueStudio.App.ViewModels;
using HueStudio.Core.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HueStudio.App.Views;

public sealed partial class HomePage : Page
{
    private bool _syncing;

    public ObservableCollection<GroupViewModel> Rooms { get; } = [];
    public ObservableCollection<GroupViewModel> Zones { get; } = [];

    public HomePage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        App.Home.HomeChanged += OnHomeChanged;
        Greeting.Text = DateTime.Now.Hour switch { < 12 => "Good morning", < 18 => "Good afternoon", _ => "Good evening" };
        Sync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => App.Home.HomeChanged -= OnHomeChanged;

    private void OnHomeChanged(object? sender, HomeChangedEventArgs e) => Sync();

    private void Sync()
    {
        var s = App.Home.State;
        Collections.SyncById(Rooms, s.Rooms, r => r.Id, r => new GroupViewModel(App.Home, r.Id, false), (vm, r) => vm.Sync(r, s, includeChildren: false));
        Collections.SyncById(Zones, s.Zones, z => z.Id, z => new GroupViewModel(App.Home, z.Id, true), (vm, z) => vm.Sync(z, s, includeChildren: false));

        var lights = s.Lights;
        var on = lights.Count(l => l.IsOn);
        HomeSummary.Text = $"{on} of {lights.Count} lights on · {s.Rooms.Count} rooms · {s.Scenes.Count} scenes";

        _syncing = true;
        WholeHomeToggle.IsOn = s.WholeHome?.IsOn ?? on > 0;
        _syncing = false;
    }

    private void WholeHome_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing || App.Home.State.WholeHome is not { } home) return;
        App.Home.SetGroupOn(home.Id, WholeHomeToggle.IsOn);
    }

    private void Group_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GroupViewModel g) App.Window.NavigateToGroup(g.Id);
    }
}

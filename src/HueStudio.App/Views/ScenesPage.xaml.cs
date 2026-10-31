using System.Collections.ObjectModel;
using HueStudio.App.ViewModels;
using HueStudio.Core.Models;
using HueStudio.Core.State;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HueStudio.App.Views;

public sealed partial class ScenesPage : Page
{
    public ObservableCollection<SceneViewModel> Visible { get; } = [];

    public ScenesPage() => InitializeComponent();

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
        string GroupName(Scene sc) => sc.Group?.Rid is { } g ? (s.Get<Room>(g)?.Name ?? s.Get<Zone>(g)?.Name ?? "") : "";
        var scenes = s.Scenes
            .Where(sc => q.Length == 0 || sc.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || GroupName(sc).Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(GroupName).ThenBy(sc => sc.Name)
            .ToList();
        Collections.SyncById(Visible, scenes, sc => sc.Id, sc => new SceneViewModel(App.Home, sc.Id), (vm, sc) => vm.Sync(sc, s));
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Sync();

    private void Scene_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SceneViewModel s) s.RecallCommand.Execute(null);
    }
}

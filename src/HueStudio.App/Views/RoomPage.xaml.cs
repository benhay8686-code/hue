using HueStudio.App.ViewModels;
using HueStudio.Core.Models;
using HueStudio.Core.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HueStudio.App.Views;

/// <summary>One room or zone: master controls, its scenes and every light in it.</summary>
public sealed partial class RoomPage : Page
{
    public GroupViewModel Group { get; private set; } = null!;
    private string _id = "";

    public RoomPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _id = (string)e.Parameter;
        var isZone = App.Home.State.TypeOf(_id) == ResourceTypes.Zone;
        Group = new GroupViewModel(App.Home, _id, isZone);
        App.Home.HomeChanged += OnHomeChanged;
        Sync();
        Bindings.Update();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => App.Home.HomeChanged -= OnHomeChanged;

    private void OnHomeChanged(object? sender, HomeChangedEventArgs e) => Sync();

    private LightGroup? Model => (LightGroup?)App.Home.State.Get<Room>(_id) ?? App.Home.State.Get<Zone>(_id);

    private void Sync()
    {
        if (Model is { } g) Group.Sync(g, App.Home.State, includeChildren: true);
        else if (Frame.CanGoBack) Frame.GoBack(); // deleted elsewhere
    }

    private void Scene_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SceneViewModel s) s.RecallCommand.Execute(null);
    }

    private async void SaveScene_Click(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { PlaceholderText = "Scene name", Text = $"{Group.Name} {DateTime.Now:HH:mm}" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Save current lighting as a scene",
            Content = name,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || Model is not { } g) return;
        try { await App.Home.SaveCurrentAsSceneAsync(g, name.Text.Trim()); }
        catch (Exception ex) { App.Window.ShowError(ex.Message); }
    }
}

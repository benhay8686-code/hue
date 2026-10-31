using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HueStudio.App.Views;

public sealed partial class EntertainmentPage : Page
{
    public EntertainmentPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var areas = App.Home.State.EntertainmentAreas
            .Select(a => $"{a.Name}  ·  {a.ConfigurationType}  ·  {a.Channels.Count} channels  ·  {a.Status}")
            .ToList();
        Areas.ItemsSource = areas.Count > 0 ? areas : ["No entertainment areas yet. Create one in the Hue phone app for now."];
    }
}

using HueStudio.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace HueStudio.App.Views;

/// <summary>A light with its swatch (opens the colour studio), brightness, power and menu.</summary>
public sealed partial class LightRow : UserControl
{
    public static readonly DependencyProperty LightProperty = DependencyProperty.Register(
        nameof(Light), typeof(LightViewModel), typeof(LightRow),
        new PropertyMetadata(null, (d, _) => ((LightRow)d).Bindings.Update()));

    public LightViewModel Light
    {
        get => (LightViewModel)GetValue(LightProperty);
        set => SetValue(LightProperty, value);
    }

    public LightRow() => InitializeComponent();

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex } && Light is not null)
        {
            var rgb = HueStudio.Core.Color.Rgb.FromHex(hex);
            Light.PickPreset(Color.FromArgb(255, rgb.R, rgb.G, rgb.B));
        }
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HueStudio.App.Services;
using HueStudio.Core.Color;
using HueStudio.Core.Models;
using HueStudio.Core.State;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HueStudio.App.ViewModels;

internal static class UiColor
{
    public static Color From(Rgb c) => Color.FromArgb(255, c.R, c.G, c.B);
    public static SolidColorBrush Brush(Rgb c) => new(From(c));

    /// <summary>A soft left-to-right gradient of the given colours, used for room and scene cards.</summary>
    public static Brush Gradient(IReadOnlyList<Rgb> colors)
    {
        if (colors.Count == 0) colors = [new Rgb(0x2A, 0x2A, 0x2E)];
        if (colors.Count == 1) colors = [colors[0], ColorMath.Scale(colors[0], 70)];
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
        for (var i = 0; i < colors.Count; i++)
            brush.GradientStops.Add(new GradientStop { Color = From(colors[i]), Offset = (double)i / (colors.Count - 1) });
        return brush;
    }

    public static string Glyph(string? archetype) => archetype switch
    {
        "living_room" or "lounge" or "tv" => "",   // TV
        "kitchen" or "dining" => "",                // Cafe
        "bedroom" or "kids_bedroom" or "guest_room" => "", // Moon
        "office" or "computer" => "",              // Devices
        "bathroom" or "toilet" => "",
        "garden" or "terrace" or "balcony" => "", // Globe
        "hallway" or "staircase" or "front_door" => "",
        _ => "",                                      // Home
    };
}

/// <summary>One light: name, power, brightness, colour, all two-way and live.</summary>
public sealed partial class LightViewModel(HomeService home, string id) : ObservableObject, IHasId
{
    private bool _syncing;
    private string _name = "";
    private string _roomName = "";
    private bool _isOn;
    private double _brightness;
    private SolidColorBrush _swatch = new(Color.FromArgb(255, 42, 42, 46));
    private bool _supportsColor;
    private bool _supportsTemperature;
    private double _kelvin = 2700;
    private string _model = "";

    public string Id { get; } = id;
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string RoomName { get => _roomName; private set => SetProperty(ref _roomName, value); }
    public string Model { get => _model; private set => SetProperty(ref _model, value); }
    public SolidColorBrush Swatch { get => _swatch; private set => SetProperty(ref _swatch, value); }
    public bool SupportsColor { get => _supportsColor; private set => SetProperty(ref _supportsColor, value); }
    public bool SupportsTemperature { get => _supportsTemperature; private set => SetProperty(ref _supportsTemperature, value); }

    private readonly LocalEditGuard _guard = new();

    public bool IsOn
    {
        get => _isOn;
        set { if (SetProperty(ref _isOn, value) && !_syncing) { _guard.Touch(); home.SetLightOn(Id, value); } }
    }

    public double Brightness
    {
        get => _brightness;
        set { if (SetProperty(ref _brightness, value) && !_syncing) { _guard.Touch(); home.SetLightBrightness(Id, value); } }
    }

    public double Kelvin
    {
        get => _kelvin;
        set { if (SetProperty(ref _kelvin, value) && !_syncing) { _guard.Touch(); home.SetLightKelvin(Id, value); } }
    }

    public void Sync(Light light, HomeState state)
    {
        _syncing = true;
        try
        {
            Name = light.Name;
            RoomName = state.RoomOf(light)?.Name ?? "";
            SupportsColor = light.SupportsColor;
            SupportsTemperature = light.SupportsColorTemperature;
            if (!_guard.UserIsEditing)
            {
                IsOn = light.IsOn;
                Brightness = Math.Round(light.Dimming?.Brightness ?? (light.IsOn ? 100 : 0));
                if (light.ColorTemperature?.Mirek is int m && light.ColorTemperature.MirekValid) Kelvin = ColorMath.MirekToKelvin(m);
            }
            Swatch = UiColor.Brush(ColorMath.DisplayColor(light));
            var owner = light.Owner?.Rid is { } d ? state.Get<Device>(d) : null;
            Model = owner?.ProductData?.ProductName ?? "";
        }
        finally { _syncing = false; }
    }

    /// <summary>Bound to the colour wheel's ColorChanged event.</summary>
    public void OnColorPicked(ColorPicker sender, ColorChangedEventArgs args) =>
        home.SetLightColor(Id, new Rgb(args.NewColor.R, args.NewColor.G, args.NewColor.B));

    public void PickPreset(Color c) => home.SetLightColor(Id, new Rgb(c.R, c.G, c.B));

    [RelayCommand]
    private void Identify() => home.IdentifyLight(Id);
}

/// <summary>A scene card: name, palette preview, active state, tap to recall.</summary>
public sealed partial class SceneViewModel(HomeService home, string id) : ObservableObject, IHasId
{
    private string _name = "";
    private string _groupName = "";
    private bool _isActive;
    private Brush _preview = new SolidColorBrush(Color.FromArgb(255, 42, 42, 46));

    public string Id { get; } = id;
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string GroupName { get => _groupName; private set => SetProperty(ref _groupName, value); }
    public bool IsActive { get => _isActive; private set => SetProperty(ref _isActive, value); }
    public Brush Preview { get => _preview; private set => SetProperty(ref _preview, value); }

    public void Sync(Scene scene, HomeState state)
    {
        Name = scene.Name;
        GroupName = scene.Group?.Rid is { } g ? (state.Get<Room>(g)?.Name ?? state.Get<Zone>(g)?.Name ?? "") : "";
        IsActive = scene.IsActive;
        var colors = scene.Actions
            .Where(a => a.Action.On?.On != false)
            .Select(a => a.Action.Color?.Xy is { } xy ? ColorMath.XyToRgb(xy)
                       : a.Action.ColorTemperature?.Mirek is int m ? ColorMath.MirekToRgb(m)
                       : new Rgb(255, 214, 170))
            .Distinct().Take(5).ToList();
        Preview = UiColor.Gradient(colors);
    }

    [RelayCommand]
    private void Recall() => home.RecallScene(Id);

    [RelayCommand]
    private void RecallDynamic() => home.RecallScene(Id, dynamic: true);
}

/// <summary>A room or zone: power, brightness, live colour gradient, lights and scenes.</summary>
public sealed partial class GroupViewModel(HomeService home, string id, bool isZone) : ObservableObject, IHasId
{
    private bool _syncing;
    private string _name = "";
    private string _glyph = "";
    private string _summary = "";
    private bool _isOn;
    private double _brightness;
    private Brush _gradient = new SolidColorBrush(Color.FromArgb(255, 42, 42, 46));
    private string? _groupedLightId;

    public string Id { get; } = id;
    public bool IsZone { get; } = isZone;
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string Glyph { get => _glyph; private set => SetProperty(ref _glyph, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public Brush Gradient { get => _gradient; private set => SetProperty(ref _gradient, value); }

    public ObservableCollection<LightViewModel> Lights { get; } = [];
    public ObservableCollection<SceneViewModel> Scenes { get; } = [];

    private readonly LocalEditGuard _guard = new();

    public bool IsOn
    {
        get => _isOn;
        set { if (SetProperty(ref _isOn, value) && !_syncing && _groupedLightId is { } g) { _guard.Touch(); home.SetGroupOn(g, value); } }
    }

    public double Brightness
    {
        get => _brightness;
        set { if (SetProperty(ref _brightness, value) && !_syncing && _groupedLightId is { } g) { _guard.Touch(); home.SetGroupBrightness(g, value); } }
    }

    public void Sync(LightGroup group, HomeState state, bool includeChildren)
    {
        _syncing = true;
        try
        {
            _groupedLightId = group.GroupedLightId;
            Name = group.Name;
            Glyph = UiColor.Glyph(group.Metadata?.Archetype);
            var lights = state.LightsIn(group);
            var on = lights.Count(l => l.IsOn);
            Summary = on == 0 ? $"All off · {lights.Count} lights" : on == lights.Count ? $"All on · {lights.Count} lights" : $"{on} of {lights.Count} on";
            var gl = state.GroupedLightFor(group);
            if (!_guard.UserIsEditing)
            {
                IsOn = gl?.IsOn ?? on > 0;
                Brightness = Math.Round(gl?.Dimming?.Brightness ?? 0);
            }
            Gradient = UiColor.Gradient(lights.Where(l => l.IsOn).Select(ColorMath.DisplayColor).Distinct().Take(4).ToList());

            if (!includeChildren) return;
            Collections.SyncById(Lights, lights, l => l.Id, l => new LightViewModel(home, l.Id), (vm, l) => vm.Sync(l, state));
            Collections.SyncById(Scenes, state.ScenesFor(group), s => s.Id, s => new SceneViewModel(home, s.Id), (vm, s) => vm.Sync(s, state));
        }
        finally { _syncing = false; }
    }
}

public interface IHasId { string Id { get; } }

/// <summary>
/// While the user is dragging a slider, ignore incoming (slightly stale) bridge echoes for a
/// moment so the thumb doesn't jump back. After 1.5s of no input the bridge is the truth again.
/// </summary>
internal sealed class LocalEditGuard
{
    private long _lastTouch;
    public void Touch() => _lastTouch = Environment.TickCount64;
    public bool UserIsEditing => Environment.TickCount64 - _lastTouch < 1500;
}

internal static class Collections
{
    /// <summary>Updates an ObservableCollection in place (keeps scroll position and avoids flicker).</summary>
    public static void SyncById<TVm, TModel>(
        ObservableCollection<TVm> target,
        IReadOnlyList<TModel> source,
        Func<TModel, string> key,
        Func<TModel, TVm> create,
        Action<TVm, TModel> update) where TVm : class, IHasId
    {
        static string keyOf(TVm vm) => vm.Id;
        var wanted = source.Select(key).ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(keyOf(target[i]))) target.RemoveAt(i);

        for (var i = 0; i < source.Count; i++)
        {
            var k = key(source[i]);
            var existingIndex = -1;
            for (var j = i; j < target.Count; j++)
                if (keyOf(target[j]) == k) { existingIndex = j; break; }

            TVm vm;
            if (existingIndex < 0) { vm = create(source[i]); target.Insert(i, vm); }
            else
            {
                vm = target[existingIndex];
                if (existingIndex != i) target.Move(existingIndex, i);
            }
            update(vm, source[i]);
        }
    }
}

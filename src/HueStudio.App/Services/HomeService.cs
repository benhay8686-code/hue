using System.Text.Json.Nodes;
using HueStudio.Core.Api;
using HueStudio.Core.Color;
using HueStudio.Core.Demo;
using HueStudio.Core.Models;
using HueStudio.Core.State;
using HueStudio.Core.Storage;
using Microsoft.UI.Dispatching;

namespace HueStudio.App.Services;

public enum ConnectionState { Disconnected, Connecting, Live, Reconnecting, Demo }

/// <summary>
/// The app's single connection to the home: owns the bridge client, the live <see cref="HomeState"/>,
/// the event-stream pump and command throttling. Pages and view models only talk to this.
/// </summary>
public sealed class HomeService
{
    private readonly DispatcherQueue _ui;
    private readonly CommandCoalescer _lightQueue = new(TimeSpan.FromMilliseconds(110)); // ~9/s per light
    private readonly CommandCoalescer _groupQueue = new(TimeSpan.FromMilliseconds(350)); // groups are rate limited harder
    private IHueClient? _client;
    private CancellationTokenSource? _pump;

    public HomeService(DispatcherQueue ui)
    {
        _ui = ui;
        State.Changed += (_, e) => _ui.TryEnqueue(() => HomeChanged?.Invoke(this, e));
        _lightQueue.Failed += (_, ex) => RaiseError(ex);
        _groupQueue.Failed += (_, ex) => RaiseError(ex);
    }

    public HomeState State { get; } = new();
    public BridgeCredentials? Bridge { get; private set; }
    public ConnectionState Connection { get; private set; }
    public bool IsConnected => _client is not null;

    /// <summary>Raised on the UI thread whenever anything in the home changes.</summary>
    public event EventHandler<HomeChangedEventArgs>? HomeChanged;
    public event EventHandler<ConnectionState>? ConnectionChanged;
    public event EventHandler<string>? Error;

    public Task ConnectAsync(BridgeCredentials bridge, CancellationToken ct = default)
    {
        Bridge = bridge;
        return StartAsync(new HueClient(bridge.Address, bridge.ApplicationKey, bridge.CertificateSha256), ConnectionState.Live, ct);
    }

    public Task ConnectDemoAsync(CancellationToken ct = default)
    {
        Bridge = null;
        return StartAsync(new DemoHueClient(), ConnectionState.Demo, ct);
    }

    public void Disconnect()
    {
        _pump?.Cancel();
        _client?.Dispose();
        _client = null;
        SetConnection(ConnectionState.Disconnected);
    }

    private async Task StartAsync(IHueClient client, ConnectionState liveState, CancellationToken ct)
    {
        Disconnect();
        _client = client;
        SetConnection(ConnectionState.Connecting);
        await State.RefreshAsync(client, ct);
        SetConnection(liveState);

        _pump = new CancellationTokenSource();
        var token = _pump.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in client.EventsAsync(token))
                    State.Apply(evt);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { RaiseError(ex); }
        }, token);
    }

    // ---------------------------------------------------------------- light commands

    public void SetLightOn(string lightId, bool on)
    {
        Optimistic(lightId, new JsonObject { ["on"] = new JsonObject { ["on"] = on } });
        _lightQueue.Submit($"{lightId}:on", ct => Client.UpdateLightAsync(lightId, LightUpdate.Power(on), ct));
    }

    public void SetLightBrightness(string lightId, double pct)
    {
        Optimistic(lightId, new JsonObject
        {
            ["on"] = new JsonObject { ["on"] = pct > 0 },
            ["dimming"] = new JsonObject { ["brightness"] = pct },
        });
        _lightQueue.Submit($"{lightId}:bri", ct => Client.UpdateLightAsync(lightId, LightUpdate.Brightness(pct), ct));
    }

    public void SetLightColor(string lightId, Rgb rgb)
    {
        var light = State.Get<Light>(lightId);
        var (xy, _) = ColorMath.RgbToXy(rgb, ColorMath.GamutFor(light?.Color));
        Optimistic(lightId, new JsonObject
        {
            ["on"] = new JsonObject { ["on"] = true },
            ["color"] = new JsonObject { ["xy"] = new JsonObject { ["x"] = xy.X, ["y"] = xy.Y } },
            ["color_temperature"] = new JsonObject { ["mirek_valid"] = false },
        });
        _lightQueue.Submit($"{lightId}:color", ct => Client.UpdateLightAsync(lightId, LightUpdate.Xy(xy.X, xy.Y), ct));
    }

    public void SetLightKelvin(string lightId, double kelvin)
    {
        var light = State.Get<Light>(lightId);
        var schema = light?.ColorTemperature?.MirekSchema ?? new MirekSchema();
        var mirek = Math.Clamp(ColorMath.KelvinToMirek(kelvin), schema.MirekMinimum, schema.MirekMaximum);
        Optimistic(lightId, new JsonObject
        {
            ["on"] = new JsonObject { ["on"] = true },
            ["color_temperature"] = new JsonObject { ["mirek"] = mirek, ["mirek_valid"] = true },
        });
        _lightQueue.Submit($"{lightId}:color", ct => Client.UpdateLightAsync(lightId, LightUpdate.Mirek(mirek), ct));
    }

    public void IdentifyLight(string lightId) => Fire(ct => Client.UpdateLightAsync(lightId, LightUpdate.Identify(), ct));

    // ---------------------------------------------------------------- group commands

    public void SetGroupOn(string groupedLightId, bool on)
    {
        Optimistic(groupedLightId, new JsonObject { ["on"] = new JsonObject { ["on"] = on } });
        _groupQueue.Submit($"{groupedLightId}:on", ct => Client.UpdateGroupedLightAsync(groupedLightId, GroupedLightUpdate.Power(on), ct));
    }

    public void SetGroupBrightness(string groupedLightId, double pct)
    {
        Optimistic(groupedLightId, new JsonObject
        {
            ["on"] = new JsonObject { ["on"] = pct > 0 },
            ["dimming"] = new JsonObject { ["brightness"] = pct },
        });
        _groupQueue.Submit($"{groupedLightId}:bri", ct => Client.UpdateGroupedLightAsync(groupedLightId, GroupedLightUpdate.Brightness(pct), ct));
    }

    public void RecallScene(string sceneId, bool dynamic = false) =>
        Fire(ct => Client.RecallSceneAsync(sceneId, new SceneRecall { Action = dynamic ? "dynamic_palette" : "active" }, ct));

    public Task<string> SaveCurrentAsSceneAsync(LightGroup group, string name)
    {
        var actions = State.LightsIn(group).Select(l => new SceneAction
        {
            Target = new ResourceRef(l.Id, ResourceTypes.Light),
            Action = new SceneLightAction
            {
                On = new OnState { On = l.IsOn },
                Dimming = l.IsOn && l.Dimming is { } d ? new Dimming { Brightness = d.Brightness } : null,
                ColorTemperature = l.IsOn && l.ColorTemperature is { MirekValid: true, Mirek: { } m } ? new ColorTemperatureState { Mirek = m } : null,
                Color = l.IsOn && l.ColorTemperature?.MirekValid != true && l.Color?.Xy is { } xy ? new ColorState { Xy = xy } : null,
            },
        }).ToList();

        var groupType = group is Room ? ResourceTypes.Room : ResourceTypes.Zone;
        return Client.CreateSceneAsync(new SceneCreate
        {
            Metadata = new Metadata { Name = name },
            Group = new ResourceRef(group.Id, groupType),
            Actions = actions,
        });
    }

    public Task SetEntertainmentAsync(string configId, bool start) => Client.SetEntertainmentStreamingAsync(configId, start);

    // ---------------------------------------------------------------- helpers

    private IHueClient Client => _client ?? throw new InvalidOperationException("Not connected to a bridge.");

    private void Optimistic(string id, JsonObject patch) => State.ApplyLocal(id, patch);

    private void Fire(Func<CancellationToken, Task> action) => _ = Task.Run(async () =>
    {
        try { await action(CancellationToken.None); }
        catch (Exception ex) { RaiseError(ex); }
    });

    private void RaiseError(Exception ex) => _ui.TryEnqueue(() => Error?.Invoke(this, ex.Message));

    private void SetConnection(ConnectionState s)
    {
        Connection = s;
        _ui.TryEnqueue(() => ConnectionChanged?.Invoke(this, s));
    }
}

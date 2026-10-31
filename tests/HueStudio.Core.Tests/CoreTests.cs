using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HueStudio.Core.Api;
using HueStudio.Core.Color;
using HueStudio.Core.Demo;
using HueStudio.Core.Discovery;
using HueStudio.Core.Entertainment;
using HueStudio.Core.Models;
using HueStudio.Core.State;
using HueStudio.Core.Storage;

namespace HueStudio.Core.Tests;

public class ColorMathTests
{
    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(255, 160, 40)]
    public void RgbToXy_result_is_inside_gamut_C(byte r, byte g, byte b)
    {
        var (xy, _) = ColorMath.RgbToXy(new Rgb(r, g, b), ColorMath.GamutC);
        Assert.True(InOrOnGamut(xy, ColorMath.GamutC));
    }

    [Fact]
    public void White_maps_to_D65()
    {
        var (xy, lum) = ColorMath.RgbToXy(new Rgb(255, 255, 255));
        Assert.InRange(xy.X, 0.311, 0.315);
        Assert.InRange(xy.Y, 0.327, 0.331);
        Assert.InRange(lum, 0.99, 1.01);
    }

    [Fact]
    public void Xy_round_trip_keeps_dominant_channel()
    {
        var (xy, _) = ColorMath.RgbToXy(new Rgb(255, 0, 0));
        var back = ColorMath.XyToRgb(xy);
        Assert.Equal(255, back.R);
        Assert.True(back.G < 60 && back.B < 60, back.Hex);
    }

    [Fact]
    public void Out_of_gamut_point_is_clamped_onto_the_triangle()
    {
        var p = ColorMath.ClampToGamut(new XyPoint(0.8, 0.2), ColorMath.GamutA);
        Assert.True(InOrOnGamut(p, ColorMath.GamutA));
        Assert.True(p.X < 0.8);
    }

    [Theory]
    [InlineData(2700, 370)]
    [InlineData(6500, 154)]
    [InlineData(2000, 500)]
    public void Kelvin_mirek_conversion(int kelvin, int mirek) => Assert.Equal(mirek, ColorMath.KelvinToMirek(kelvin));

    [Fact]
    public void Warm_white_swatch_is_orange_ish_and_cool_is_blue_ish()
    {
        var warm = ColorMath.MirekToRgb(500);
        var cool = ColorMath.MirekToRgb(153);
        Assert.True(warm.R > warm.B);
        Assert.True(cool.B >= cool.R - 10);
    }

    [Fact]
    public void Hex_round_trip() => Assert.Equal("#FF8000", Rgb.FromHex("#ff8000").Hex);

    // Clamped points sit on an edge, where IsInGamut can flip on float noise (and RgbToXy rounds to 4dp).
    private static bool InOrOnGamut(XyPoint p, Gamut g)
    {
        var c = ColorMath.ClampToGamut(p, g);
        return Math.Abs(c.X - p.X) < 1e-3 && Math.Abs(c.Y - p.Y) < 1e-3;
    }
}

public class EventStreamTests
{
    [Fact]
    public async Task Parses_sse_frames_and_skips_keepalives()
    {
        const string wire = """
            : hi

            id: 1700000000:0
            data: [{"creationtime":"2026-10-31T09:00:00Z","data":[{"id":"abc","type":"light","on":{"on":true}}],"id":"e1","type":"update"}]

            id: 1700000001:0
            data: [{"creationtime":"2026-10-31T09:00:01Z","data":[{"id":"def","type":"light"}],"id":"e2","type":"delete"}]


            """;
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(wire));
        var events = new List<HueEvent>();
        await foreach (var e in EventStreamParser.ParseAsync(ms, TestContext.Current.CancellationToken)) events.Add(e);

        Assert.Equal(2, events.Count);
        Assert.Equal("update", events[0].Type);
        Assert.Equal("abc", events[0].Data[0]["id"]!.GetValue<string>());
        Assert.Equal("delete", events[1].Type);
    }

    [Fact]
    public void Garbage_payload_is_ignored() => Assert.Empty(EventStreamParser.ParsePayload("not json"));
}

public class HomeStateTests
{
    private static async Task<(DemoHueClient, HomeState)> LoadDemoAsync()
    {
        var client = new DemoHueClient();
        var state = new HomeState();
        await state.RefreshAsync(client);
        return (client, state);
    }

    [Fact]
    public async Task Demo_house_has_rooms_lights_scenes_and_zone()
    {
        var (_, s) = await LoadDemoAsync();
        Assert.Equal(5, s.Rooms.Count);
        Assert.Equal(15, s.Lights.Count);
        Assert.Single(s.Zones);
        Assert.True(s.Scenes.Count >= 30);
        Assert.NotNull(s.WholeHome);
        Assert.Single(s.EntertainmentAreas);
    }

    [Fact]
    public async Task Room_lights_resolve_via_devices_and_zone_lights_directly()
    {
        var (_, s) = await LoadDemoAsync();
        var living = s.Rooms.Single(r => r.Name == "Living room");
        Assert.Equal(5, s.LightsIn(living).Count);
        Assert.Equal(4, s.LightsIn(s.Zones[0]).Count);
        Assert.NotNull(s.GroupedLightFor(living));
        Assert.Equal(6, s.ScenesFor(living).Count);
        Assert.Equal("Living room", s.RoomOf(s.LightsIn(living)[0])!.Name);
    }

    [Fact]
    public async Task Partial_update_event_merges_without_losing_fields()
    {
        var (_, s) = await LoadDemoAsync();
        var light = s.Lights.First(l => l.SupportsColor);
        var name = light.Name;

        s.Apply(new HueEvent("update", null, [new JsonObject { ["id"] = light.Id, ["type"] = "light", ["dimming"] = new JsonObject { ["brightness"] = 12.5 } }]));

        var after = s.Get<Light>(light.Id)!;
        Assert.Equal(12.5, after.Dimming!.Brightness);
        Assert.Equal(name, after.Name);
        Assert.NotNull(after.Color?.Gamut);
    }

    [Fact]
    public async Task Event_stream_keeps_state_in_sync_with_commands()
    {
        var (client, s) = await LoadDemoAsync();
        var ct = TestContext.Current.CancellationToken;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var gotIt = new TaskCompletionSource();
        var light = s.Lights.First(l => l.SupportsColor);

        var pump = Task.Run(async () =>
        {
            await foreach (var e in client.EventsAsync(cts.Token))
            {
                s.Apply(e);
                if (s.Get<Light>(light.Id)!.Color!.Xy!.X == 0.2) gotIt.TrySetResult();
            }
        }, ct);

        await Task.Delay(50, ct);
        await client.UpdateLightAsync(light.Id, LightUpdate.Xy(0.2, 0.3), ct);
        await gotIt.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        cts.Cancel();

        var l = s.Get<Light>(light.Id)!;
        Assert.True(l.IsOn);
        Assert.False(l.ColorTemperature!.MirekValid);
    }

    [Fact]
    public async Task Recalling_a_scene_sets_lights_and_marks_it_active()
    {
        var (client, s) = await LoadDemoAsync();
        var bedroom = s.Rooms.Single(r => r.Name == "Bedroom");
        var relax = s.ScenesFor(bedroom).Single(x => x.Name == "Relax");

        await client.RecallSceneAsync(relax.Id);
        await s.RefreshAsync(client);

        Assert.All(s.LightsIn(bedroom), l => Assert.True(l.IsOn));
        Assert.True(s.Get<Scene>(relax.Id)!.IsActive);
        Assert.Single(s.ScenesFor(bedroom), x => x.IsActive);
    }

    [Fact]
    public async Task Grouped_light_turns_whole_room_off()
    {
        var (client, s) = await LoadDemoAsync();
        var living = s.Rooms.Single(r => r.Name == "Living room");
        await client.UpdateGroupedLightAsync(living.GroupedLightId!, GroupedLightUpdate.Power(false));
        await s.RefreshAsync(client);
        Assert.All(s.LightsIn(living), l => Assert.False(l.IsOn));
        Assert.False(s.GroupedLightFor(living)!.IsOn);
    }

    [Fact]
    public async Task Create_zone_adds_grouped_light()
    {
        var (client, s) = await LoadDemoAsync();
        var lights = s.Lights.Take(2).Select(l => new ResourceRef(l.Id, "light")).ToList();
        var id = await client.CreateZoneAsync(new GroupCreate { Metadata = new Metadata { Name = "Upstairs", Archetype = "upstairs" }, Children = lights });
        await s.RefreshAsync(client);
        var zone = s.Get<Zone>(id)!;
        Assert.Equal("Upstairs", zone.Name);
        Assert.NotNull(s.GroupedLightFor(zone));
        Assert.Equal(2, s.LightsIn(zone).Count);
    }
}

public class HueClientTests
{
    [Fact]
    public async Task Sends_application_key_and_snake_case_body()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"errors":[],"data":[{"rid":"x","rtype":"light"}]}"""));
        using var client = Create(handler);
        await client.UpdateLightAsync("abc", LightUpdate.Mirek(366), TestContext.Current.CancellationToken);

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.EndsWith("/clip/v2/resource/light/abc", req.Uri);
        Assert.Equal("KEY", req.AppKey);
        Assert.Contains("\"color_temperature\":{\"mirek\":366}", req.Body);
        Assert.DoesNotContain("null", req.Body);
    }

    [Fact]
    public async Task Typed_get_deserialises_lights()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """
            {"errors":[],"data":[{"id":"l1","type":"light","metadata":{"name":"Lamp","archetype":"sultan_bulb"},
              "on":{"on":true},"dimming":{"brightness":42.0},"color":{"xy":{"x":0.3,"y":0.3},"gamut_type":"C"},
              "color_temperature":{"mirek":null,"mirek_valid":false,"mirek_schema":{"mirek_minimum":153,"mirek_maximum":500}}}]}
            """));
        using var client = Create(handler);
        var lights = await client.GetAsync<Light>("light", TestContext.Current.CancellationToken);
        var l = Assert.Single(lights);
        Assert.Equal("Lamp", l.Name);
        Assert.True(l.IsOn);
        Assert.Equal(42, l.Dimming!.Brightness);
        Assert.Equal("C", l.Color!.GamutType);
        Assert.Equal(500, l.ColorTemperature!.MirekSchema!.MirekMaximum);
    }

    [Fact]
    public async Task Error_responses_become_friendly_exceptions()
    {
        using var client = Create(new FakeHandler(_ => Json(HttpStatusCode.Forbidden, """{"errors":[{"description":"unauthorized user"}],"data":[]}""")));
        var ex = await Assert.ThrowsAsync<HueApiException>(() => client.GetAllResourcesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(403, ex.StatusCode);
        Assert.Contains("Pair again", ex.Message);
    }

    [Fact]
    public async Task Create_returns_new_id()
    {
        using var client = Create(new FakeHandler(_ => Json(HttpStatusCode.OK, """{"errors":[],"data":[{"rid":"new-zone","rtype":"zone"}]}""")));
        var id = await client.CreateZoneAsync(new GroupCreate { Metadata = new Metadata { Name = "Z" } }, TestContext.Current.CancellationToken);
        Assert.Equal("new-zone", id);
    }

    private static HueClient Create(FakeHandler h) =>
        new(new HttpClient(h) { BaseAddress = new Uri("https://bridge/") }, new HttpClient(h) { BaseAddress = new Uri("https://bridge/") }, "KEY");

    internal static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

public class PairingTests
{
    [Fact]
    public async Task Link_button_not_pressed_maps_to_specific_exception()
    {
        var h = new FakeHandler(_ => HueClientTests.Json(HttpStatusCode.OK, """[{"error":{"type":101,"address":"","description":"link button not pressed"}}]"""));
        await Assert.ThrowsAsync<LinkButtonNotPressedException>(() =>
            BridgePairing.PairAsync(new DiscoveredBridge("b1", "10.0.0.2"), "Ben's PC", TestContext.Current.CancellationToken, h));
        Assert.Contains("\"generateclientkey\":true", h.Requests.Single().Body);
        Assert.Contains("huestudio#Bens PC", h.Requests.Single().Body);
    }

    [Fact]
    public async Task Successful_pairing_returns_keys()
    {
        var h = new FakeHandler(_ => HueClientTests.Json(HttpStatusCode.OK, """[{"success":{"username":"APPKEY","clientkey":"0123456789ABCDEF0123456789ABCDEF"}}]"""));
        var creds = await BridgePairing.PairAsync(new DiscoveredBridge("b1", "10.0.0.2") { Name = "Hue Bridge" }, "PC", TestContext.Current.CancellationToken, h);
        Assert.Equal("APPKEY", creds.ApplicationKey);
        Assert.Equal(32, creds.ClientKey!.Length);
        Assert.Equal("Hue Bridge", creds.Name);
    }

    [Fact]
    public async Task Wait_for_link_button_retries_until_pressed()
    {
        var calls = 0;
        var h = new FakeHandler(_ => ++calls < 3
            ? HueClientTests.Json(HttpStatusCode.OK, """[{"error":{"type":101,"description":"link button not pressed"}}]""")
            : HueClientTests.Json(HttpStatusCode.OK, """[{"success":{"username":"K","clientkey":"C"}}]"""));
        var creds = await BridgePairing.WaitForLinkButtonAsync(new DiscoveredBridge("b", "10.0.0.2"), "PC",
            TimeSpan.FromSeconds(10), ct: TestContext.Current.CancellationToken, handlerForTests: h, pollInterval: TimeSpan.FromMilliseconds(10));
        Assert.Equal("K", creds.ApplicationKey);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Certificate_pinning_rejects_a_different_certificate()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=001788fffe000000", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var thumb = BridgeHttp.Thumbprint(cert);

        Assert.True(BridgeHttp.ValidateCertificate(cert, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, null, null));
        Assert.True(BridgeHttp.ValidateCertificate(cert, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, thumb, null));
        Assert.False(BridgeHttp.ValidateCertificate(cert, System.Net.Security.SslPolicyErrors.None, new string('A', 64), null));
    }
}

public class MiscTests
{
    [Fact]
    public void HueStream_frame_layout()
    {
        var id = "1a8d99cc-967b-44f2-9202-43f976c0fa6b";
        var frame = HueStreamFrame.Build(id, [new ChannelColor(0, 1, 0, 0.5), new ChannelColor(1, 0, 1, 0)], sequence: 7);
        Assert.Equal(16 + 36 + 14, frame.Length);
        Assert.Equal("HueStream", Encoding.ASCII.GetString(frame, 0, 9));
        Assert.Equal(2, frame[9]);
        Assert.Equal(7, frame[11]);
        Assert.Equal(id, Encoding.ASCII.GetString(frame, 16, 36));
        Assert.Equal(0, frame[52]);
        Assert.Equal(0xFF, frame[53]); Assert.Equal(0xFF, frame[54]); // R = 1.0
        Assert.Equal(0x80, frame[57]);                                // B = 0.5 high byte
        Assert.Equal(1, frame[59]);
    }

    [Fact]
    public async Task Coalescer_sends_latest_value_and_drops_intermediate()
    {
        var sent = new List<int>();
        var c = new CommandCoalescer(TimeSpan.FromMilliseconds(100));
        var done = new TaskCompletionSource();
        for (var i = 1; i <= 20; i++)
        {
            var v = i;
            c.Submit("light-1", _ => { lock (sent) sent.Add(v); if (v == 20) done.TrySetResult(); return Task.CompletedTask; });
        }
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(20, sent[^1]);
        Assert.True(sent.Count <= 3, $"sent {sent.Count} commands");
    }

    [Fact]
    public async Task Credential_store_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"huestudio-{Guid.NewGuid():N}", "bridges.dat");
        var store = new CredentialStore(path, new PlainTextProtector());
        await store.UpsertAsync(new BridgeCredentials { BridgeId = "b1", Address = "10.0.0.2", ApplicationKey = "k" }, TestContext.Current.CancellationToken);
        await store.UpsertAsync(new BridgeCredentials { BridgeId = "B1", Address = "10.0.0.3", ApplicationKey = "k2" }, TestContext.Current.CancellationToken);
        var all = await store.LoadAsync(TestContext.Current.CancellationToken);
        var b = Assert.Single(all);
        Assert.Equal("10.0.0.3", b.Address);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Mdns_query_is_well_formed_and_response_parser_reads_bridge_id()
    {
        var q = MdnsDiscoveryAccessor.Query("_hue._tcp.local");
        Assert.Equal(12 + 1 + 4 + 1 + 4 + 1 + 5 + 1 + 4, q.Length);

        var fake = Encoding.ASCII.GetBytes("....._hue._tcp.local....bridgeid=001788fffe123456....modelid=BSB002");
        var b = MdnsDiscoveryAccessor.Parse(fake, IPAddress.Parse("192.168.1.20"))!;
        Assert.Equal("001788fffe123456", b.Id);
        Assert.Equal("192.168.1.20", b.Address);
    }
}

internal static class MdnsDiscoveryAccessor
{
    public static byte[] Query(string n) => MdnsDiscovery.BuildQuery(n);
    public static DiscoveredBridge? Parse(byte[] p, IPAddress a) => MdnsDiscovery.ParseResponse(p, a);
}

internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public sealed record Captured(HttpMethod Method, string Uri, string? AppKey, string Body);
    public List<Captured> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        request.Headers.TryGetValues("hue-application-key", out var keys);
        Requests.Add(new Captured(request.Method, request.RequestUri!.ToString(), keys?.FirstOrDefault(), body));
        return respond(request);
    }
}

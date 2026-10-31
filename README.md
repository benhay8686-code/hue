# Hue Studio

A fast, native Windows 11 app for controlling and configuring a Philips Hue home. It talks directly
to the Hue Bridge on your network (Hue API v2), so changes are instant and it works without internet.

## What works today (v0.1)

| Area | Status |
|---|---|
| Find bridge (mDNS + Philips discovery + typed IP) | ✅ |
| Pair with the link button, keys encrypted with Windows DPAPI | ✅ |
| Live sync (event stream): changes from the phone app or switches show instantly | ✅ |
| Home: every room and zone as a live-colour card with on/off and brightness | ✅ |
| Room/zone page: master controls, scenes, every light | ✅ |
| Colour studio per light: colour wheel (gamut-correct), presets, white temperature | ✅ |
| Scenes: browse/search all, play, play dynamically, save current lighting as a scene | ✅ |
| Lights: all lights with search, identify (blink) | ✅ |
| Demo house (no bridge needed) | ✅ |
| Entertainment: lists areas; HueStream packet builder done | 🟡 screen sync next |
| Automations | ⬜ planned |

## Run it

Requirements: Windows 10 19041+ or Windows 11, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0),
and Visual Studio 2022 17.14+ (or 2026) with the **WinUI application development** workload.

```powershell
git clone <this repo>
cd HueStudio
dotnet run --project src/HueStudio.App -p:Platform=x64
```

Or open `HueStudio.slnx` in Visual Studio, set **HueStudio.App** as startup project, platform **x64**, press F5.

No bridge to hand? On the setup screen, click **Explore a demo house**.

## Layout

```
src/HueStudio.Core      Hue API v2 client, discovery, pairing, live home state, colour maths, demo house
src/HueStudio.App       WinUI 3 app (Windows App SDK 1.8, unpackaged, self-contained)
tests/HueStudio.Core.Tests   xUnit tests for the core (run anywhere: dotnet test)
docs/PLAN.md            Roadmap and design decisions
```

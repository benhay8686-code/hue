# Hue Studio plan

## Goal
A Windows app that beats both the Hue Windows companion app and the official iPhone app for
controlling and configuring a Hue home: faster, richer colour control, full keyboard/mouse
ergonomics, and PC-only superpowers (screen sync, PC-event automations).

## Stack decision

**Chosen: WinUI 3 + .NET 10 (C#) + Hue API v2 (CLIP v2) + Entertainment API.**

| Option | Native feel | Performance | Entertainment (DTLS) | Effort | Verdict |
|---|---|---|---|---|---|
| WinUI 3 / .NET 10 | Best (Fluent, Mica, real Windows controls) | Excellent | Straightforward in C# (BouncyCastle) | Medium | ✅ Chosen |
| WPF / .NET 10 | Good, older look | Excellent | Same as above | Low-medium | Runner-up |
| Tauri (Rust + web UI) | Web look | Very good, small | Rust DTLS crates, more work | Medium-high | Third |
| Electron (web UI) | Web look | Heavy (150 MB+, RAM) | Node DTLS libs are weak | Low | Not recommended |

## Milestones

| # | Milestone | Contents | Status |
|---|---|---|---|
| 1 | Foundation | Core API client, discovery, pairing, live event stream, colour maths, demo house, tests | ✅ Done |
| 2 | Everyday control | Home, rooms/zones, lights, colour studio, scenes (play, dynamic, save) | ✅ First cut, needs a run on Windows |
| 3 | Immersive | Entertainment API over DTLS, screen sync (Windows.Graphics.Capture), music sync (WASAPI loopback), 2D/3D entertainment-area editor | Next |
| 4 | Configure | Create/edit rooms and zones, rename lights, scene editor with palette and per-light control, gradient lights, effects (candle, fire, sparkle) | Planned |
| 5 | Automations | Wake up / go to sleep / timers via bridge behaviours, plus PC triggers (unlock, focus, game launch), tray icon and global hotkeys | Planned |
| 6 | Polish | Installer (MSIX/winget), auto-update, multiple bridges, accessibility pass | Planned |

## Key design choices
- **Local only.** The app talks to the bridge on the LAN; no Hue cloud account needed.
- **Security.** Bridge cert is pinned at pairing (trust on first use); keys are DPAPI-encrypted per Windows user.
- **Live state.** One full load, then the bridge's event stream keeps everything in sync; partial updates are deep-merged so nothing is lost.
- **Bridge-friendly.** Slider drags are coalesced (latest value wins) to stay inside the bridge's rate limits, with optimistic UI so it feels instant.

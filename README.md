# Vulcano Control

A Windows desktop app for controlling a **Storz & Bickel Volcano** vaporizer over Bluetooth LE —
live temperature control, scripted temperature ramps with a live chart, device settings, and more.

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![License](https://img.shields.io/badge/license-unspecified-lightgrey)

## Features

- **Live control** — connect over Bluetooth LE, read the current temperature, and set a target
  temperature; toggle the heater and the pump.
- **Temperature ramps** — draw a ramp as a curve of as many points as you like, each segment with
  its own shape (linear, exponential, steep, or ease-in/out), plus an optional hold once the ramp
  finishes. Ramps are saved as named profiles. The app re-enables the heater if the Volcano's own
  auto-shutoff timer cuts it before the ramp completes, and says so when a segment asks for more
  than the device can physically do.
- **Live ramp chart** — plots the planned (Soll) curve against the measured (Ist) history on a
  sliding "now" timeline, with a locked temperature axis, a capped ±24h time zoom range, and a
  one-click view reset.
- **Device settings** — serial number, operating hours, firmware versions, LED display brightness,
  auto-shutoff timer, vibration alarm, "show temperature while cooling", and the Celsius/Fahrenheit
  display unit — all read from and written to the device live.
- **Auto-shutoff awareness** — shows a live countdown to the device's own auto-shutoff, and keeps
  the heater on through an active ramp even if that timer would otherwise cut it.
- **Persistent settings** — the last-used ramp shape, history retention, update threshold, and
  theme are remembered across restarts.
- **Light/Dark theme** — native Fluent theming on Windows 11+, with a hand-rolled fallback theme
  (including a dark title bar) for Windows 10, where the native theme renders incorrectly.
- **Built-in protocol log** — a dedicated log window with Debug/Info/Warning/Error severity levels
  and per-level filtering, covering both the BLE connection lifecycle and every command sent to the
  device.
Updating in place is not back yet: 1.x checked GitHub Releases on startup and installed updates
itself, and 2.0 does not do this. Until it does, a new version means downloading the installer and
running it — it keeps your settings and ramp profiles.

## Requirements

- Windows 10 (2004/build 19041 or later) or Windows 11
- A Bluetooth LE adapter
- A Storz & Bickel Volcano Hybrid/Classic with Bluetooth support

## Installation

Grab the latest installer from the [Releases page](https://github.com/Senifox/Vulcano-Control/releases) —
download and run `Vulcano-Control-win-Setup.exe`.

Settings and ramp profiles live in `%AppData%\Vulcano-Control`, which no installer touches, so
reinstalling or updating keeps them.

## Building from source

```
dotnet build
```

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). The app is built on
[Avalonia](https://avaloniaui.net) with [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
for MVVM, [LiveCharts](https://livecharts.dev) for the ramp chart, and
[Velopack](https://velopack.io) for packaging.

The solution is laid out as:

| | |
|---|---|
| `src/Vulcano.Core` | device protocol, ramp engine, settings, LAN relay — no UI, no Windows API |
| `src/Vulcano.App` | the Avalonia app: views, view models, theming |
| `src/Vulcano.Bluetooth.Windows` | the WinRT Bluetooth LE adapter |
| `tests/` | 223 tests across the core and the view models |
| `tools/Vulcano.Measure` | records heating and cooling from a real device; not shipped |

`Vulcano.Core` targets plain `net10.0` so the parts that are not Windows-specific stay that way; the
app and the Bluetooth adapter target `net10.0-windows10.0.19041.0`.

Versions 1.x were a WPF app. It was replaced wholesale in 2.0 and its source is no longer in the
tree — `git show v1.0.8` still has it.

### Cutting a release

`Release.ps1` wraps the whole publish → pack → publish workflow:

```powershell
.\Release.ps1 -Version x.y.z            # build and pack locally only
.\Release.ps1 -Version x.y.z -Publish   # also upload straight to a new GitHub Release
```

`-Publish` needs a GitHub [personal access token](https://github.com/settings/tokens) (repo
contents: read & write) available as the `GITHUB_TOKEN` environment variable.

## Acknowledgements

The Bluetooth LE protocol was reverse-engineered with reference to
[firsttris/reactive-volcano-app](https://github.com/firsttris/reactive-volcano-app).

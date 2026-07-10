# Vulcano Control

A Windows desktop app for controlling a **Storz & Bickel Volcano** vaporizer over Bluetooth LE —
live temperature control, scripted temperature ramps with a live chart, device settings, and more.

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![License](https://img.shields.io/badge/license-unspecified-lightgrey)

## Features

- **Live control** — connect over Bluetooth LE, read the current temperature, and set a target
  temperature; toggle the heater and the pump.
- **Temperature ramps** — define a start temperature, an end temperature, a duration, and an
  interpolation curve (linear, exponential, steep exponential, or ease-in/out), plus an optional
  hold ("Nachlaufzeit") once the ramp finishes. The app automatically re-enables the heater if the
  Volcano's own auto-shutoff timer cuts it before the ramp completes.
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
- **Automatic updates** — checks this repository's GitHub Releases on startup (and on demand via
  *Hilfe → Nach Updates suchen*) and installs updates via [Velopack](https://velopack.io).

## Requirements

- Windows 10 (2004/build 19041 or later) or Windows 11
- A Bluetooth LE adapter
- A Storz & Bickel Volcano Hybrid/Classic with Bluetooth support

## Installation

Grab the latest installer from the [Releases page](https://github.com/Senifox/Vulcano-Control/releases) —
download and run `Vulcano-Control-win-Setup.exe`. The app will keep itself up to date automatically
from there on.

## Building from source

```
dotnet build
```

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) with Windows desktop workloads.
The project targets `net10.0-windows10.0.19041.0` and uses WPF + [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
for MVVM, [OxyPlot](https://github.com/oxyplot/oxyplot) for the ramp chart, and
[Velopack](https://velopack.io) for packaging/updates.

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

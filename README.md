# Vulcano Control

A Windows desktop app for controlling a **Storz & Bickel Volcano** vaporizer over Bluetooth LE —
live temperature control, temperature ramps drawn as a curve, and a device that can be shared with
other machines on the network.

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
- **Running a ramp** — a tab that only exists while one is running: the segment underway, time left,
  the finishing time, and how far along the plan the device actually is.
- **Live chart** — the measured temperature against the planned curve. The time axis follows the
  run, shows the whole session, or stays at a fixed 15 or 60 minutes, and how much history is kept
  is yours to set.
- **Share one device over the LAN** — the machine with the Bluetooth connection hosts, the others
  join with an address, a port and a four-digit PIN. Joining machines either control the device or
  only watch, the host sees who is connected and can drop them, and everyone sees the same ramp,
  including one already underway when they arrive.
- **Sounds and desktop notifications** — for the start temperature being reached, a ramp finishing,
  and a connection lost. When Windows will not show a notification, the window says it instead
  rather than swallowing it.
- **Compact mode** — the same window shrunk to the temperature, the ramp progress and the essential
  controls, with a pin to keep it above everything else.
- **Device settings** — serial number, hours of heating, firmware versions, LED brightness,
  auto-shutoff timer, vibration alarm and "show temperature while cooling", read from and written to
  the device live. The device's own °C/°F display is deliberately left alone for now; the app works
  in °C throughout.
- **Auto-shutoff awareness** — a live countdown to the device's own auto-shutoff, and the heater is
  switched back on if that timer would cut an active ramp short.
- **Simulation mode** — `--simulate` runs the whole app against a simulated Volcano, with a chip in
  the title bar so simulated readings are never mistaken for real ones.
- **Protocol log** — the BLE connection lifecycle and every command sent, with severity levels,
  per-level filtering and export to a text file.
- **Light and dark theme**, following the system by default, and an **English or German** interface.

- **Updates that wait their turn** — a new version is fetched in the background and installed when
  you close the app, never while it is running. Restarting for it right away is a button, and that
  button is unavailable during a ramp: applying an update means stopping the app, and stopping the
  app mid-ramp leaves a device heating with nothing watching it. The automatic check can be
  switched off; the manual one stays.

Settings, ramp profiles and the quick-pick temperatures are remembered across restarts.

## Requirements

- Windows 10 (2004/build 19041 or later) or Windows 11
- A Bluetooth LE adapter
- A Storz & Bickel Volcano Hybrid/Classic with Bluetooth support

## Installation

Grab the latest installer from the [Releases page](https://github.com/Senifox/Vulcano-Control/releases) —
download and run `Vulcano-Control-win-Setup.exe`. It keeps itself up to date after that.

Settings and ramp profiles live in `%AppData%\Vulcano-Control`, which no installer touches, so
reinstalling or updating keeps them.

If a **preview build** from the rewrite is installed (`Vulcano Control (Preview)`), run
[`Cleanup.ps1`](Cleanup.ps1) *before* installing this — it removes the preview and carries its
settings across. The preview is a separate application to the installer, so it is neither replaced
nor updated by this one, and its data sits in the folder this installer clears.

## Building from source

```
dotnet build
```

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). The app is built on
[Avalonia](https://avaloniaui.net) with [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
for MVVM, [LiveCharts](https://livecharts.dev) for the ramp chart, and
[Velopack](https://velopack.io) for packaging and updates.

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

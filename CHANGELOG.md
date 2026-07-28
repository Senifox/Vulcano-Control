# Changelog

What changed in each released version, newest first. The app reads this file, so an entry is written
for whoever is using it — what is different for them, not what moved in the code.

Each version is a `## ` heading of `version — date`, and everything under it is a `- ` line.

## Unreleased

## 2.4.0 — 2026-07-27

- This list. Settings has a *What has changed* card, and after an update the app says once what the
  new version brought — worth having now that updates install themselves quietly.
- The compact window is now only as tall as what it shows. It used to leave about a third of itself
  empty.

## 2.3.0 — 2026-07-27

- Hosting a device now shows the round trip to each connected machine, next to it in the list.
  Machines running an older version show no number rather than a wrong one.

## 2.2.0 — 2026-07-27

- Joining another machine's device now shows the round trip to it, and says so plainly when the host
  has stopped answering. It is measured against a reply that does not touch the Volcano, so it stays
  readable when the device itself is busy or out of range.

## 2.1.1 — 2026-07-27

- Fixed: a downloaded update was not installed when the app was closed. It arrived, and then nothing
  happened with it.

## 2.1.0 — 2026-07-27

- The app keeps itself up to date again. A new version is fetched in the background and installed
  when you close the app — never while a ramp is running, because that would mean stopping the app
  with a device still heating. Restarting for it straight away is a button, and that button is
  unavailable during a ramp.
- Automatic checking can be switched off in Settings. Checking by hand stays.

## 2.0.1 — 2026-07-26

- Rewritten from the ground up. Multi-point temperature ramps drawn as a curve and saved as named
  profiles, a Run tab while one is running, sharing one device across the network, sounds and
  desktop notifications, a compact window, light and dark themes, and an English or German
  interface.
- Settings and ramp profiles now live in `%AppData%\Vulcano-Control`, which no installer touches.
  They used to sit in the folder the installer clears, and a reinstall took them with it. Anything
  found in the old place is carried across once.
- Ramps warn when a segment asks for more than the device can physically do — the Volcano has no
  cooling and sheds heat slowly.

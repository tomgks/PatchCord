# Vencord Auto-Updater

A small Windows desktop app that **keeps your Discord client mod injected**.
It runs quietly in the system tray and, whenever a Discord install is **running but
not patched** — which is exactly what happens after Discord auto-updates and
replaces its app folder — it shows a notification, re-injects your mod, and
restarts Discord automatically.

Pick your mod in the **Options** tab: **[Vencord](https://vencord.dev)**,
**[Equicord](https://github.com/Equicord/Equicord)** (a Vencord fork with 300+ extra
plugins), **[BetterDiscord](https://betterdiscord.app)**, or none. Only one client
mod is active at a time. **[OpenAsar](https://github.com/GooseMod/OpenAsar)** can be
kept installed independently (it stacks under any of them).

It reproduces what each mod's official installer does:

- **Vencord / Equicord** — backs up `resources\app.asar` to `_app.asar` and writes a
  tiny stub `app.asar` that does `require("<APPDATA>\<Mod>\dist\patcher.js")`.
- **BetterDiscord** — a different mechanism: it overwrites
  `modules\discord_desktop_core\index.js` to `require` the
  `%APPDATA%\BetterDiscord\data\betterdiscord.asar`.

No installer binary required — it reuses each mod's files already on disk. If the
chosen mod isn't installed, run that mod's installer once (the app shows a reminder
with a **Get** button) — it won't touch Discord until the mod is present.

## Features

- **Modern WPF UI** with custom window chrome (drag the title bar; minimize sends
  it to the notification area / "show hidden icons"; closing keeps it running in
  the tray).
- **Monitoring on/off** with a clear Active/Paused status (also shown in the tray
  tooltip).
- **Options tab** to choose your **client mod** — Vencord / Equicord / BetterDiscord /
  none — each with a short description. Switching mods removes the old one and injects
  the new. A **Get [mod]** button opens the installer page if the mod isn't installed,
  and a slider sets the **check interval**.
- **Managed installs** list with live status badges (which mod is active /
  `No Vencord` / `No Equicord` / `OpenAsar`) and a per-install Managed/Paused toggle.
- **Auto-detect** Discord and Discord PTB, or **add a custom path**.
- **Top-left notification** when a Discord/PTB instance is found needing re-patching.
- **Themes** that recolour the whole app *and* the notifications: **Discord**
  (default, signature dark blurple), **Dark** (neutral near-black), **Light**, and
  **High Contrast** (black background, white text, purple accents, high-contrast-blue
  highlights). Notifications are slightly transparent.
- **Notification options** (under a collapsible *Appearance & notifications*
  section): on/off, style (Bar / Solid / Minimal / Outline), size and duration
  sliders, live preview.
- **Runs at startup** (per-user, no admin needed) and lives in the tray.
- **Optional OpenAsar support** (off by default): a toggle in Options to also keep
  [OpenAsar](https://github.com/GooseMod/OpenAsar) installed, so it's restored after
  Discord updates too. Works alongside Vencord/Equicord or on its own; fetched from
  its official GitHub releases and cached locally.

## Install / run

Download **`VencordAutoUpdater.exe`** from the
[**Releases**](https://github.com/tomgks/vencord-auto-updater/releases/latest) page
and run it — the window opens and the monitor runs in the background.

To make it **start automatically at logon** (in the tray), download/clone this repo
(for `install.ps1`) next to the exe and run:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

To remove autostart and stop it:

```powershell
powershell -ExecutionPolicy Bypass -File uninstall.ps1
```

> Uninstalling does **not** unpatch Discord — Vencord stays installed. To unpatch,
> use the Vencord installer's *Uninstall* option.

## Building the exe yourself

The app is a C# / WPF project under [`src/`](src), targeting .NET 10. Building needs
the [.NET 10 SDK](https://dotnet.microsoft.com/download). From the repo root:

```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1
```

That produces a single, **self-contained** `publish\VencordAutoUpdater.exe` (~66 MB)
that bundles the .NET runtime, so end users need nothing else installed. Under the
hood it runs:

```powershell
dotnet publish src\VencordAutoUpdater.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -o publish
```

For day-to-day development, `dotnet build src` does a fast framework-dependent build
(uses the installed .NET 10 Desktop runtime). Run `VencordAutoUpdater.exe --selftest`
to build the UI and exit without arming the monitor.

> Earlier versions were a PowerShell script compiled with PS2EXE. The app was ported
> to C# so the release is a real .NET assembly — far less likely to trip antivirus
> than a PS2EXE wrapper. The old `VencordAutoUpdaterApp.ps1` is kept for reference.

## Files

| File | Purpose |
|------|---------|
| `src/` | C# / WPF source (App, MainWindow, PatchEngine, Theme, Alert, Config) |
| `publish.ps1` | Build the self-contained single-file release exe |
| `VencordAutoUpdaterApp.ps1` | Legacy PowerShell implementation (reference) |
| `install.ps1` / `uninstall.ps1` | Add / remove logon autostart |
| `app.ico` / `tray-on.ico` / `tray-off.ico` | App + tray-state icons |

The compiled `VencordAutoUpdater.exe` is published on the
[Releases](https://github.com/tomgks/vencord-auto-updater/releases) page.

Settings live in `config.json` and the activity log in `vencord-auto-updater.log`
(both created next to the exe at runtime, not tracked in git).

## Notes

- CPU impact is ~0 while idle (it sleeps between checks); it carries the usual WPF
  working set (~120–210 MB) while resident.
- Only **Windows** Discord installs are supported.

## Credits & license

The asar patch logic and the OpenAsar install logic are ported from the
[Vencord Installer](https://github.com/Vencord/Installer) (GPL-3.0). This project
is **not** affiliated with or endorsed by the Vencord project.

[Equicord](https://github.com/Equicord/Equicord) is a community fork of Vencord.
Equicord support here just points the injection at `%APPDATA%\Equicord\dist`
instead of Vencord's; this project is **not** affiliated with the Equicord project.

[BetterDiscord](https://github.com/BetterDiscord/BetterDiscord) is a separate client
mod (its injection is ported from BetterDiscord's `scripts/inject.ts`). This project
is **not** affiliated with the BetterDiscord project.

[OpenAsar](https://github.com/GooseMod/OpenAsar) is a separate open-source project
by GooseMod (**AGPL-3.0**) and is **not** affiliated with Vencord, Equicord, or this
app. OpenAsar support here is entirely optional and off by default; when enabled, the
app downloads OpenAsar from its official GitHub releases. Use it at your own risk.

Licensed under **GPL-3.0** — see [LICENSE](LICENSE).

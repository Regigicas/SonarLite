# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

SonarLite replaces the SteelSeries GG desktop app for controlling **Sonar**. It keeps the official
backend (`SteelSeriesSonar.exe`, plus its driver and APO, which must already be installed under
`C:\Program Files\SteelSeries\GG\apps\sonar`) and drops GGEZ / GG / Engine / Prism. SonarLite
pretends to be the GG core that Sonar talks to, then drives Sonar through Sonar's local REST API.
Don't reimplement mixing, the APO or the driver. Build on the REST API.

## Commands

Requires the .NET 10 SDK; the project is Windows-only (WPF).

```powershell
dotnet build SonarLite.slnx -c Release          # what CI runs (.github/workflows/build.yml)
scripts\publish.ps1 [-Start]                    # build, cleanly stop a running instance, publish to %LOCALAPPDATA%\Programs\SonarLite, restart it
scripts\start-headless.ps1 / stop-headless.ps1  # run Sonar against standalone FakeGG on port 6399, no UI (for API experiments)
scripts\restore-gg.ps1                          # go back to the official GG stack
```

- There are no tests and no linter. To check a change, build it, then publish and exercise the real app.
- `publish.ps1` stops a running SonarLite with `SonarLite.exe --exit`, which shuts Sonar down
  through its own sequence. Don't kill the processes: that leaves Sonar's virtual devices in a bad
  state, and the user loses audio while the app is down.
- Logs: `%LOCALAPPDATA%\SonarLite\logs\sonarlite.log` (the app) and `sonar.log` (Sonar's
  stdout/stderr). Settings: `%LOCALAPPDATA%\SonarLite\settings.json`. Sonar's REST address once
  it registers: `%LOCALAPPDATA%\SonarLite\sonar.json`.

## Architecture

Three projects in `SonarLite.slnx`:

- **`SonarLite.Core`** (net10.0, ASP.NET Core):
  - `GgHost` is a minimal fake GG server. Sonar calls `POST /subApps/register` and `/subApps/ready`,
    polls `GET /healthCheck` every 5 s (it exits after 3 failures) and keeps the `/ws` websocket open.
    If any of these fail, Sonar exits. Unknown GETs return 404 ("feature unavailable") and unknown
    writes return 200. `EVENT_SHUTDOWN` on the websocket triggers Sonar's clean shutdown.
  - `SonarProcess` kills the official GG processes (GGEZ first, because it respawns the others).
    It launches Sonar with the environment variables GGEZ normally sets (`GG_SERVER_ADDRESS`,
    `GG_WS_ENDPOINT`, `APP_DATA_PATH`, …) and with the working directory set to the Sonar install
    folder, which Sonar needs for its relative paths. It waits for "ready", then restarts Sonar if
    it crashes (at most 3 times in 2 minutes). Both output pipes must be drained continuously.
  - `SonarClient` is the typed client for Sonar's REST API and its `/sock` event websocket.
  - `VolumeCurve` holds a measured table of the Windows audio taper, used for the dB display. It
    is not 20·log10.
- **`SonarLite`** (net10.0-windows, WPF tray app; WinForms is used only for `NotifyIcon`):
  - `App.xaml.cs` owns the lifecycle: a single-instance mutex, plus named events
    `Local\SonarLite.Show` and `Local\SonarLite.Exit` that let a second launch forward
    `--exit` / show to the running instance. It runs `GgHost` on a random port, then `SonarProcess`.
    State changes call `MainViewModel.Attach(SonarClient)` or `Detach()`.
  - `MainViewModel` is split across `ViewModels.cs` (mixer, ChatMix, devices, presets, channel
    enable/disable) and `AppsViewModel.cs` (per-app routing). It polls every 2 s, but only while
    the window is visible. It also listens to Sonar's `/sock` events at all times.
    `LatestValueSender` sends only the latest slider value, with at most one request in flight.
  - `AppRouter` applies saved routing rules when Sonar raises
    `SONAR_EVENT_AUDIO_SESSION_OPENED_DATA`. Windows ignores per-app endpoint changes until the
    process has opened audio, and it stores the choice per executable path.
  - `Autostart` uses the HKCU Run key. Enabling it moves GG's own `SteelSeriesGG` Run entry to
    `HKCU\Software\SonarLite\ParkedRun` (the app re-parks it if a GG update adds it back).
    The "Volver a SteelSeries GG" option relaunches GG from that entry.
  - Startup shows the window unless `--minimized` is passed.
  - The app closes the window instead of hiding it, then forces a GC and trims the working set.
    A small memory footprint is an explicit goal (see the GC settings in the csproj).
- **`FakeGG`** is a console wrapper around `GgHost` on a fixed port, used by the headless scripts.

**`docs/sonar-api.md` is the reference** for how Sonar depends on GG, the REST endpoints, the
websocket events and the routing and channel-disable behaviour verified against the running
process. Check it before adding API calls, and update it with anything new you verify.
`_decompiled/` (git-ignored) holds decompiled SteelSeries code for local reference only. Never
commit, quote at length or redistribute it.

## Conventions

- UI text, README, docs and commit messages are in **Spanish**. Code identifiers and code
  comments are in English.
- Commit each distinct change on its own, matching the style of the existing commit history.
  Prefix commits that only touch docs, the README or CI with `docs:`, `README:` or `CI:`.

# FastApp

A Windows time tracker that lives in your tray and reports through a local web dashboard.

It records how long you actually use each application — separating focused time from time the
window merely sat open while you were away — and gives you daily limits, global hotkeys and a
managed startup list on top of that. Everything stays on your machine.

[![Latest release](https://img.shields.io/github/v/release/oHfok/FastApp?sort=semver)](https://github.com/oHfok/FastApp/releases/latest)

---

## What it does

**Tracking.** Samples the foreground window every five seconds and records three separate
figures per app: total time, focused time, and AFK time (open, but you were idle). Sessions are
logged individually, so the dashboard can show a real timeline of your day rather than only
daily totals.

**Daily limits.** Give an app a limit in minutes and FastApp warns you as you approach it and
again when you hit it. Optionally it force-closes the app instead. Limits can be extended for
the day, but only behind a PIN — so the limit means something even on the machine that set it.

**Hotkeys and macros.** Bind a global key combination to launch or focus an app, toggle system
mute, centre the active window, or paste a snippet of text. Combinations are matched exactly,
and a per-app switch decides whether the keystroke also reaches the focused application. Macros
are suppressed automatically while a game is in the foreground, so a stray combination cannot
fire mid-match.

**Startup apps.** Keep a list of applications to open when you log in, in the order you arrange
them, each with optional command-line arguments and its own delay. FastApp tells you what
actually opened, what was already running, and what failed.

**Notifications.** Real Windows toasts with buttons on them, so a limit warning can take you
straight to the extend dialog or the dashboard. There is a master switch and quiet hours.

**Dashboard.** A local web UI on `http://127.0.0.1:5050` with six views — Overview, Insights,
Periods, Activity, Leaderboard and All Applications — plus three themes. Cards can be reordered
and hidden. There is a "Wrapped" summary, and a full history of releases with their notes.

**Updates.** Updates install themselves in the background. Every version you have run stays
available to roll back to, and rolling back takes a database backup first.

---

## Installing

Download `FastApp-win-Setup.exe` from the [latest release](https://github.com/oHfok/FastApp/releases/latest)
and run it. There is no installer UI — it installs and launches.

> **SmartScreen will warn you.** The builds are not code-signed, so Windows shows
> "Windows protected your PC" on a fresh install. Choose *More info* → *Run anyway*.
> A signing certificate is a paid, identity-verified purchase; until there is one, this
> warning is expected.

A portable `.zip` is published alongside the installer if you would rather not install.

FastApp runs in the tray. Double-click the icon to open the manager, or right-click for the
dashboard, the extend-time dialog and exit.

### Starting with Windows

The **Launch on Startup** toggle registers a Task Scheduler logon task, falling back to the
`HKCU\...\Run` key if the task cannot be created. Registering it needs one UAC prompt; running
FastApp does not need elevation at any other time.

If a second copy of FastApp (a portable extract, or a build from source) claims the
registration, Settings says so and offers to point it back — it will not silently rewrite it,
because two copies correcting each other means a UAC prompt on every launch.

---

## Your data

Everything is local. There is no account, no telemetry and no server.

| | |
|---|---|
| Database | `%LocalAppData%\FastAppData\appmanager.db` (SQLite) |
| Dashboard | bound to `127.0.0.1:5050` — not reachable from your network |
| Window titles | **off by default**; opt in from the dashboard's Settings |
| Retention | configurable; older rows are pruned on startup |

Window-title capture is opt-in deliberately: a title can contain the page you are reading or the
document you are editing, which is far more revealing than a process name.

The database is plain SQLite. You can open it, query it, back it up, or delete it — and
FastApp will rebuild an empty one.

---

## Building from source

Requires the **.NET 10 SDK** and **Visual Studio 2022 or newer** (for MSBuild).

```bash
git clone https://github.com/oHfok/FastApp.git
```

Build with the full Visual Studio MSBuild rather than `dotnet build` — the project has a
`COMReference` to `IWshRuntimeLibrary`, used for the startup shortcut, that the .NET CLI cannot
resolve:

```bash
MSBuild.exe FastApp.csproj -t:Build -p:Configuration=Debug
```

### Cutting a release

`scripts/release.ps1` stamps the version, publishes a self-contained `win-x64` build, packs it
with Velopack and, with `-Publish`, uploads it to GitHub Releases:

```powershell
.\scripts\release.ps1 -Version 1.3.0 -Publish -NotesFile notes.md
```

Without `-Publish` it only builds locally into `.\Releases`, so you can test the installer first.
`-NotesFile` sets the release body, which is also what the in-app patch notes display.

---

## How it is put together

| | |
|---|---|
| Desktop app | WPF on .NET 10, with [WPF-UI](https://github.com/lepoco/wpfui) for Fluent controls |
| MVVM | CommunityToolkit.Mvvm source generators |
| Storage | EF Core 10 + SQLite, migrations applied on startup |
| Dashboard server | ASP.NET Core minimal APIs hosted in-process, ~35 endpoints |
| Dashboard UI | Hand-written HTML/CSS/JS, Chart.js, no build step |
| Updates | [Velopack](https://github.com/velopack/velopack) |

```
FastApp/
├── Services/            Tracking, hooks, notifications, updates, dashboard server
│   └── Endpoints/       The dashboard's HTTP API, split by area
├── ViewModels/          MainViewModel, app model, EF entities
├── Migrations/          EF Core migrations
├── Themes/              WPF brand palette and control styles
├── wwwroot/             The dashboard: HTML, CSS, JS, fonts
└── scripts/release.ps1  Build, pack, publish
```

The tracker runs on a background thread on a five-second tick, batching into memory and flushing
to SQLite once a minute; daily limits are evaluated on every tick so enforcement reacts in
seconds rather than a minute. The keyboard hook does nothing but compare a key set and queue a
message — anything slower risks Windows uninstalling the hook.

---

## Status

Personal project, built for one machine and shared in case it is useful. Issues and pull
requests are welcome, but there is no roadmap and no support commitment.

No license has been chosen yet, which means default copyright applies: you can read the source,
but you do not have permission to reuse it. If you want to, open an issue and ask.

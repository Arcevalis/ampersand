<div align="center">
  <img src="assets/ampersand.png" width="256" alt="Ampersand logo" />
  <h1>Ampersand</h1>
  <p><b>The s&amp;box launcher for Linux.</b></p>
  <p>Client, editor and dedicated server, with Steam Runtime support, dependency checks and one-click engine builds.</p>
  <p>
    <img src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white" alt=".NET 10" />
    <img src="https://img.shields.io/badge/Avalonia-12-8A2BE2?style=flat-square" alt="Avalonia 12" />
    <img src="https://img.shields.io/badge/Linux-x86__64-FCC624?style=flat-square&logo=linux&logoColor=black" alt="Linux x86_64" />
    <img src="https://img.shields.io/badge/AppImage-supported-7393C1?style=flat-square&logo=appimage&logoColor=white" alt="AppImage" />
  </p>
</div>

## What is this?

Ampersand is a small desktop launcher for running [s&amp;box](https://sbox.game)
from source on Linux. The engine ships as prebuilt natives plus managed code,
needs a handful of host libraries, and behaves best inside Valve's
**Steam Linux Runtime 4.0 (steamrt4)** container. Ampersand wraps all of that
behind a single window with a ▶ button per target.

It can:

- **Launch** the game client (`sbox`), the editor (`sbox-dev`) and a
  **dedicated server** (`sbox-server`), each in its own terminal window,
  or headless in the background with logging.
- **Launch inside the Steam Runtime** container (per-target toggle) so the
  engine sees the exact libraries it was built against.
- **Check dependencies** on the host *and* inside the container, where the
  missing sets are different (notably `libunwind`, which the container may
  not ship. Ampersand keeps a compat cache for those).
- **Build s&amp;box** from source: run the engine's `Setup.sh`, then an
  `ldd` report over the natives, in a real terminal with colour.
- Remember your **s&amp;box checkout location** and your **server game**
  (package ident like `fss.bloodsigil` or a path to a `.sbproj`).

The UI is [Avalonia](https://avaloniaui.net/) with the Fluent dark theme.
There is deliberately no embedded terminal: every desktop already has a real
one with better scrollback, selection and colour handling, so Ampersand
spawns yours and gets out of the way.

## Requirements

| Requirement | Notes |
|---|---|
| Linux x86_64 | Primary target. Wayland works (Qt is forced to `xcb`/XWayland, SDL to the `x11` driver). Scene-view edge-snap needs the temporary **Cursor capture** toggle (XTEST fake motion; first use asks for remote-input consent). |
| s&amp;box source checkout | A folder containing `game/` + `engine/` with `game/sbox` built. |
| .NET 10 SDK | Only to *build* Ampersand. Not needed to *run* the AppImage. |
| Steam + Steam Linux Runtime 4.0 (steamrt4) | Only for containerised launches / container dependency sweep. Install via `steam steam://install/4183110`. Steam must be running. |
| A terminal emulator | `gnome-terminal`, `konsole`, `alacritty`, `kitty`, `foot`, `xterm`, … Launches open there so you get a real TTY (and the engine's own ANSI colour). |

## Quick start

### Option A: AppImage (recommended)

```sh
./install-appimage.sh
./Ampersand-x86_64.AppImage
```

No `dotnet` needed at runtime: the default build is self-contained
(~37 MB download). It also installs itself: the AppImage is copied to
`~/Applications/`, a menu entry goes to
`~/.local/share/applications/Ampersand.desktop` and the icon to
`~/.local/share/icons/`. The menu entry runs in a terminal
(`Terminal=true`), so no extra launcher is needed. Paths are XDG-standard
so non-KDE desktops (GNOME, XFCE and similar) work too; the menu-layout
fix only runs where a KDE layout exists. On first launch, point
it at your s&amp;box checkout when asked.

### Option B: dev build

```sh
./bootstrap.sh                      # dotnet build -c Release
./bin/Release/net10.0/ampersand
```

## Building

### Dev build

`bootstrap.sh` is a thin wrapper over `dotnet build` (extra args pass through):

```sh
./bootstrap.sh                      # Release build into bin/Release/net10.0/
./bootstrap.sh -c Debug             # or Debug, etc.
```

Run it with `dotnet run` if you prefer:

```sh
dotnet run -c Release
```

### AppImage

`install-appimage.sh` publishes Ampersand and packs it with `appimagetool`,
following the same shape as the UZDoom AppImage script (download tool if
missing → prepare AppDir → populate `usr/` tree → repack → clean up):

```sh
./install-appimage.sh [OPTIONS]
```

| Option | Default | Description |
|---|---|---|
| `--source DIR` | script's dir | Ampersand source checkout |
| `--output FILE` | `<source>/Ampersand-x86_64.AppImage` | Where to write the AppImage |
| `--publish-dir DIR` | `<source>/publish/linux-x64` | `dotnet publish` staging dir |
| `--configuration NAME` | `Release` | `Release` or `Debug` |
| `--runtime RID` | `linux-x64` | .NET runtime identifier |
| `--self-contained` | **on** | Bundle the runtime (~93 MB staging → ~37 MB AppImage). Needs nothing on the host. |
| `--framework-dependent` | off | Small build (~23 MB staging) but requires the .NET 10 runtime installed. |
| `--icon FILE` | `<source>/assets/ampersand.png` | App icon PNG |
| `--base-appimage FILE` | (none) | Extract the AppDir skeleton (AppRun, libs, `runtime/`) from an existing AppImage instead of starting minimal; only `usr/bin`, the desktop entry and icon are replaced. |
| `--install-dir DIR` | `~/Applications` | Where to install the finished AppImage, with its `.desktop` entry (runs in a terminal) and icon. |
| `--no-install` | off | Skip the install step, leave the AppImage at `--output` only. |
| `--no-build` | off | Skip `dotnet publish`, just repackage the existing publish dir. |
| `-h`, `--help` | (none) | Show help. |

Examples:

```sh
# Standard self-contained AppImage
./install-appimage.sh

# Small build for a machine that already has the .NET 10 runtime
./install-appimage.sh --framework-dependent --output ~/Ampersand.AppImage

# Fast iteration on packaging (desktop file, icon, AppRun) without recompiling
./install-appimage.sh --no-build
```

To remove an installed AppImage again (installed file, menu entry, icon
and Development menu pinning; build outputs in the source dir are kept):

```sh
./uninstall.sh
```

Inside the AppImage the layout is:

```
usr/bin/ampersand            # published .NET binary (single file when self-contained)
usr/bin/scripts/*.sh         # editable launch scripts (sbox.sh, sbox-dev.sh, sbox-server.sh, …)
ampersand.desktop            # desktop entry (also under usr/share/applications/)
ampersand.png                # icon (also under usr/share/icons/… and .DirIcon)
AppRun                       # mount-point resolver → execs usr/bin/ampersand
```

The logo file [`assets/ampersand.png`](assets/ampersand.png) feeds the README header, the desktop entry and the AppImage icon.

## Usage

### First run

If no valid s&amp;box location is known (persisted settings, then walking up
from the binary), Ampersand asks you to pick it. A valid location contains
`game/` and `engine/` with the `game/sbox` binary. You can change it later in
the **S&BOX LOCATION** field in the sidebar (Enter or click away to apply,
`…` to browse).

### Launch targets

| Row | Script | What it starts |
|---|---|---|
| Client (sbox) | `sbox.sh` | The game client |
| Editor (sbox-dev) | `sbox-dev.sh` | The editor (no `-project` opens the project menu) |
| Dedicated Server (sbox-server) | `sbox-server.sh` | Headless server, needs `+game <ident\|/path/to.sbproj>` |

Select a row, set the toggles, hit **▶**. Each target runs independently and
shows `● running` / `exited N` in its row; non-zero exits pop up the log tail.
**Stop** kills a background run (for terminal runs it closes the window;
emulators that fork can't always take the engine with them).

Per-target toggles:

- **Launch in Steam Runtime**: enter the steamrt4 container via Steam's own
  launcher service (Steam must be running). Forced on/off by the script's
  `# ampersand: runtime=always|never` header where applicable.
- **Launch with system terminal**: open your emulator for this run (default
  on; it's the only way to see engine output). Off runs headless with output
  captured to the log file.

### Dedicated server game

When `sbox-server` is selected, a **DEDICATED SERVER GAME** field appears.
Enter a package ident (`fss.bloodsigil`) or browse for a `.sbproj`;
Ampersand passes it as `+game <value>`. Empty clears it.

### Tools (sidebar)

- **Build S&Box**: runs the engine's `Setup.sh` (git hooks, per-platform
  artifacts, bindings, managed, shaders, content), then Ampersand's own `ldd`
  dependency report over `game/bin/linuxsteamrt64`. Runs in your terminal via
  `ampersand --bootstrap` (`--skip-deps` skips the report on the CLI).
- **Check for missing dependencies**: `ldd` sweep of
  `game/bin/linuxsteamrt64` plus the engine's bundled .NET runtime, on the
  **host and inside steamrt4**, plus the steamrt4 compat cache status. Runs via
  `ampersand --dependency-check`. (Host-clean but container-broken is the
  classic trap. This checks both.)
- **Cursor capture (temporary):** XTEST edge-snap for scene-view camera drags
  on XWayland, until Facepunch fixes cursor capture natively (then this goes
  away). Opt-in checkbox, takes effect on next launch; first drag asks for
  remote-input consent, releasing the mouse stops all injection. Wired via
  `SBOX_XCONFCAPTURE=1` + `LD_PRELOAD` in `apps/_common.sh`, source vendored
  at `apps/patches/xconfinecapture.c` (built to the cache dir on demand).
- **Open log folder**: every run is tee'd to `~/.cache/sbox-ampersand/logs/`
  whether it used a terminal or not. Each log opens with the exact command
  that was run (`$ cd ...` plus env assignments and argv, shell-quoted) so a
  launch can be reproduced from the log alone. Below the header, the scripts
  narrate themselves (`+ [script:line] command` trace lines via `set -x` in
  `apps/_common.sh`, on by default) so inner steps — shim checks, the final
  `exec` of the engine binary with its composed `LD_PRELOAD` — are captured
  too. `SBOX_TRACE=0` silences the trace.

### CLI modes

The GUI has no output surface of its own, so reporting modes re-exec the
binary inside your terminal emulator to keep SGR colour and column alignment:

```sh
ampersand --dependency-check          # coloured host + container sweep
ampersand --bootstrap [--skip-deps]   # engine Setup.sh + ldd report
```

## How it works

- **Scripts are data.** `apps/*.sh` carry `# ampersand: name=…` /
  `# ampersand: runtime=…` headers (`ScriptMetadata.cs` reads them) and are
  copied to `<OutDir>/scripts/` at build time, or `usr/bin/scripts/` in the
  AppImage. They stay loose files so you can edit them and the next launch
  picks it up. `_common.sh` sets the HarfBuzz `LD_PRELOAD`, library paths and
  `QT_QPA_PLATFORM=xcb` workarounds; `sbox-server.sh` intentionally skips the
  desktop-UI workarounds.
- **The container is entered through Steam.** `SteamLauncherService` +
  `SteamRt4Runtime` refuse to build a container command unless the Steam client
  is up, because only its launcher service may create the user namespace.
  `SteamRt4Compat` seeds `libunwind` from the host on first
  containerised launch, plus best-effort `libpcre2-16.so.0` for the editor's
  Qt tools (steamrt4's platform omits it; a miss warns but never blocks).
  Cache: `~/.cache/sbox-ampersand/steamrt4-compat`.
- **Settings** live in
  `${XDG_DATA_HOME:-~/.local}/share/sbox-ampersand/settings.json`
  (`SboxSettings.cs`); stale paths are kept for display and re-prompted.

## Project structure

```
ampersand.csproj        # net10.0 + Avalonia 12; copies apps/** → scripts/
Program.cs              # entry: UI | --dependency-check | --bootstrap | --probe-distro
App.cs / MainWindow.cs  # Avalonia app + entire window (sidebar, targets, status bar)
LaunchTarget.cs         # one script row: script file, runner, toggles
ScriptMetadata.cs       # "# ampersand: key=value" header parser
ProcessRunner.cs        # terminal-emulator or background process spawning
SystemTerminal.cs       # emulator detection + per-emulator --wait flags
SteamRt4Runtime.cs      # find + validate the Steam Linux Runtime install
SteamRt4Compat.cs       # libunwind + editor-only pcre2-16 shim cache for the container
SteamLauncherService.cs # container entry through Steam's launcher service
Bootstrap.cs            # Build S&Box: engine Setup.sh + ldd report
DependencyCheck.cs      # host + container ldd sweep + shim report
SboxSettings.cs         # persisted s&box path + server game (~/.local/share/…)
RepoRoot.cs / AppPaths.cs / RunLog.cs / Ansi.cs / TerminalTheme.cs …
apps/                   # launch scripts (sbox.sh, sbox-dev.sh, sbox-server.sh, build.sh, _common.sh)
apps/patches/           # vendored shim sources (sdlwinfix.c, xconfinecapture.c; built to the ampersand cache dir on demand)
assets/                 # ampersand.desktop, AppRun, ampersand.png (logo)
install-appimage.sh       # publish → AppDir → AppImage → install
uninstall.sh              # remove an install (AppImage, entry, icon, menu pin)
bootstrap.sh            # dev build shortcut (dotnet build -c Release)
```

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| `Repo root not found` / stale-location warning | Pick a folder containing `game/` + `engine/` + `game/sbox`. The field accepts the root, `game/`, or the `game/sbox` file itself. |
| `Steam Linux Runtime not installed` | `steam steam://install/4183110`, then start Steam and sign in. |
| `Steam is not running` | Container entry goes through Steam's launcher service. Start the client first. |
| `HRESULT: 0x80008088` | Missing `libunwind` inside steamrt4. Run **Check for missing dependencies** and do one containerised launch to seed the shim cache. |
| `No terminal emulator found` | Install one (`gnome-terminal`, `konsole`, `alacritty`, `kitty`, `foot`, `xterm`), or untick *Launch with system terminal* to run headless with log capture. |
| `_exe not found (run Build S&Box first)` | The engine isn't built yet. Use **Build S&Box**. |
| AppImage won't run (`fuse` errors) | Install `fuse2`/`libfuse2`, or extract once: `./Ampersand-x86_64.AppImage --appimage-extract` and run `squashfs-root/AppRun`. |
| Rebuild fails with `Text file busy` | The target AppImage is still running. Quit it and run `install-appimage.sh` again (the script checks for this first). |
| Menu entry missing after editing the menu | KDE Menu Editor can write a root `<Exclude>` for `Ampersand.desktop` into `~/.config/menus/applications-kmenuedit.menu`, which hides it everywhere. The install step removes that block, pins the entry in Development and rebuilds the menu cache (backup at `applications-kmenuedit.menu.bak`). |

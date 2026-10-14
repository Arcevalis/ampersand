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
**Steam Linux Runtime 4.0 (steamrt4)** container. Ampersand wraps all of this
behind a single window.

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
| Linux x86_64 | Primary target. Wayland works (Qt is forced to `xcb`/XWayland, SDL to the `x11` driver). |
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
`~/.local/share/icons/`.

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

`install-appimage.sh` publishes Ampersand and packs it with `appimagetool`, (download tool if
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
and Development menu pinning):

```sh
./uninstall.sh
```

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
| Editor (sbox-dev) | `sbox-dev.sh` | The editor (no `-project` opens the sbox launcher) |
| Dedicated Server (sbox-server) | `sbox-server.sh` | Headless server, needs `+game <ident\|/path/to.sbproj>` |

Point to your S&box repo (sbox-public), Select a row, 
set the toggles, hit play.
**Stop** kills a background run (for terminal runs it closes the window;
emulators that fork can't always take the engine with them). sbox-dev detaches
its launcher, so Stop closes its tab but leaves a running launcher alone.
Crashes will show a dialog to help you debug.

Per-target toggles:

- **Launch in Steam Runtime**: enter the steamrt4 container via Steam's own
  launcher service (Steam must be running).
- **Launch with system terminal**: open your emulator for this run (default
  on; it's the only way to see engine output). Off runs headless with output
  captured to the log file.

Global toggles (persisted in settings, take effect on next launch):

- **Casefold**: case-insensitive fallback for wrong-case asset references.
- **Input debug**: engine input diagnostics (`SBOX_INPUT_DEBUG`), so the flag
  no longer has to be in Ampersand's own environment at startup.

### Dedicated server game

When `sbox-server` is selected, a **DEDICATED SERVER GAME** field appears.
Enter a package ident (`fss.causal`) or browse for a local `.sbproj`;
Ampersand passes it as `+game <value>`.

### Tools (sidebar)

- **Build S&Box**: runs the engine's `Setup.sh` (git hooks, per-platform
  artifacts, bindings, managed, shaders, content), then Ampersand's own `ldd`
  dependency report over `game/bin/linuxsteamrt64`. Runs in your terminal via
  `ampersand --bootstrap` (`--skip-deps` skips the report on the CLI); the log
  in `~/.cache/sbox-ampersand/logs/` keeps the exact command while the output
  itself lives in the terminal window.
- **Check for missing dependencies**: `ldd` sweep of
  `game/bin/linuxsteamrt64` plus the engine's bundled .NET runtime, on the
  **host and inside steamrt4**, plus the steamrt4 compat cache status. Runs via
  `ampersand --dependency-check`. (Host-clean but container-broken is the
  classic trap. This checks both.)
- **Open log folder**: every launch and every build leaves a log in
  `~/.cache/sbox-ampersand/logs/`. Each log opens with the exact command
  that was run (`$ cd ...` plus env assignments and argv, shell-quoted) so a
  launch can be reproduced from the log alone. Terminal runs show their output
  in the window instead of the log; background runs (system-terminal toggle
  off) capture everything to the log instead. Either way the scripts narrate
  themselves (`+ [script:line] command` trace lines via `set -x` in
  `apps/_common.sh`, on by default) so inner steps — shim checks, the final
  `exec` of the engine binary with its composed `LD_PRELOAD` — are visible
  too. `SBOX_TRACE=0` silences the trace.

### CLI modes

The GUI has no output surface of its own, so reporting modes re-exec the
binary inside your terminal emulator to keep SGR colour and column alignment:

```sh
ampersand --dependency-check          # coloured host + container sweep
ampersand --bootstrap [--skip-deps]   # engine Setup.sh + ldd report
```

`--launch` is the play button without the window, for scripts and agents
(e.g. reopening the editor on a project after a crash):

```sh
ampersand --launch sbox-dev --project ~/projects/mygame/mygame.sbproj
ampersand --launch sbox-dev --project ~/projects/mygame/mygame.sbproj --wait-ready
ampersand --launch TARGET [--project FILE.sbproj] [--wait-ready [--ready-timeout SECONDS]] [--terminal] [-- ARGS...]
```

It composes the same command as the window (`LaunchComposer.cs`): same
script, env, Steam runtime container entry and casefold shim, with Casefold
and Input debug read from settings and the saved server game appended for
`sbox-server`. The Steam runtime container is always used (Steam must be
running) unless a script's header declares `runtime=never`; Casefold and
Input debug come from the saved toggles, and `--terminal` is the only
per-target flag (default background, log file only). `--project` opens that
project straight in the editor instead of the project picker (a leading `~`
is expanded, relative paths resolve against the caller's directory).

Every launch also gets `FACEPUNCH_ENGINE=<root>/game` in its environment,
beside the repo root, `QT_QPA_PLATFORM=xcb` and the SDL X11 driver. The engine
sets the same variable in its own process, and the sbox-cli agent skill reads
it to find the engine; it crosses into the container like the rest of the
launch environment (`--env`).

Differences from the window: a stale casefold shim is rebuilt without
asking; inherited `SBOX_CASEFOLD` / `SBOX_IN_STEAMRT4` /
`SBOX_STEAMRT4_COMPAT` (e.g. in a shell opened from a terminal Ampersand
launched) are dropped so only this launch's settings apply; failures print
`ampersand: TITLE: MESSAGE` and exit 1 (usage errors exit 2); and the launch
is detached (`setsid`) so it outlives the calling shell. It watches the
launch for 5 s and fails with the log tail if it exits non-zero in that
time (missing binary, container refusing to start), otherwise prints the
log path and exits 0. It does not wait for the editor to finish loading
(`--wait-ready` is the option that does).
With `--terminal` the output stays in the terminal window and is not
logged, and a failed run holds that window open (press Enter to close)
instead of exiting, so the 5 s check only reports failures in the default
background mode.

`--wait-ready` is for a caller that needs a usable editor, not merely a
started one. After the launch it polls a host-side
`sbox-cli --connect PROJECT --call editor_status` every 2 s and only returns
once the editor answers, printing
`ready (sbox-cli editor_status answered after N s)` and exiting 0. It needs
`--project` (sbox-dev only): the poll asks the editor that project opened, so
a launch without one has nothing to wait for. `sbox-cli` must be in the tree,
and a missing one is reported before the editor spends a minute booting.
`--ready-timeout SECONDS` bounds the wait (1..3600, default 300); the clock
runs from the launch itself, not from when the poll starts, so the 5 s
failure window above counts against it. A single probe that does not answer
in 15 s is killed and just counts as not ready. On timeout it exits 1 with
`Not ready`, quotes the last thing sbox-cli said, and leaves the editor
running - the launch is detached - naming the way to stop it:
`ampersand --stop sbox-dev --project …`. If the launch dies first, that is
reported instead, with the editor's exit code and the log tail.

`--stop` is the stop button without the window, for recovering a frozen or
misbehaving engine:

```sh
ampersand --stop sbox-dev --project ~/projects/mygame/mygame.sbproj
ampersand --stop TARGET [--project FILE.sbproj]
```

It finds the target's processes from this checkout by their command line -
no matter who started them (the window, `--launch`, or the sbox project
picker), inside or outside the Steam runtime container - sends `SIGTERM`
to them and their child processes, and `SIGKILL` after 10 s for a frozen
engine that ignores `SIGTERM`. `--project` limits `sbox-dev` to that
project's editor; without it every editor from this checkout is stopped.
The project picker (`game/sbox-launcher`) is left alone. It exits 0 when
the target was stopped or was not running, 1 on failure, and 2 on usage
errors.

`--cli` runs the engine's own `sbox-cli` (`engine/Launcher/SboxCli`) in this
terminal: the command-line bridge to a running editor or game, over a local
socket, which replaced the editor's in-process HTTP MCP server.

```sh
ampersand --cli -- --help
ampersand --cli -- --connect /abs/Project.sbproj --call editor_status
ampersand --cli -- --connect /abs/Project.sbproj   # MCP server on stdin/stdout
ampersand --cli [--] SBOX-CLI-ARGS...
```

Nothing after `--` (or after `--cli` without it) is interpreted: it goes to
sbox-cli unchanged, and its exit code becomes ampersand's. It is a
transparent foreground wrapper - stdin, stdout and stderr stay the caller's,
and ampersand prints nothing of its own while sbox-cli runs - because
sbox-cli's server modes speak newline-delimited JSON on stdio until EOF,
which rules out the log redirect and `setsid` the other modes use.

It runs on the host, not inside the Steam runtime container. That entry
(`steam-runtime-launch-client`, then pressure-vessel's `srt-bwrap`) gives its
children `stdin=/dev/null` and forwards no other file descriptor, so a stdio
session started inside is cancelled before it connects. A host sbox-cli still
reaches an editor running in the container, because the pid namespace and
`/tmp` are shared and that is where the editor's pipe lives. Composition
matches a launch (`LaunchComposer`, settings.json toggles), plus `DOTNET_ROOT`
pointed at the engine's own runtime, `SBOX_CLI_CWD` (so a relative project
argument means what you meant) and `SBOX_TRACE=0` (so `_common.sh` does not
trace over output a caller is parsing).

`TMPDIR` is dropped from the child environment: an editor ampersand launched
into the container never sees ours - only `--env` values cross - so its pipe
is under `/tmp`, and a host sbox-cli looking elsewhere would report no running
editor. The one case that does not fit is a host-launched editor (runtime off)
given a non-`/tmp` `TMPDIR`.

sbox-cli's own `--project` and `--game` are refused (exit 2): they would
start an engine on the host, outside the container. Use
`ampersand --launch sbox-dev --project P --wait-ready`, then attach with
`ampersand --cli -- --connect P`.

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
Program.cs              # entry: UI | --dependency-check | --bootstrap | --probe-distro | --launch | --stop | --cli
App.cs / MainWindow.cs  # Avalonia app + entire window (sidebar, targets, status bar)
LaunchComposer.cs       # UI-free launch composition shared by the window and --launch
HeadlessLaunch.cs       # --launch: arg parsing + detached spawn
HeadlessStop.cs         # --stop: find target processes in /proc, TERM then KILL
HeadlessCli.cs          # --cli: foreground host-side sbox-cli wrapper, stdio passthrough
LaunchTarget.cs         # one script row: script file, runner, toggles
ScriptMetadata.cs       # "# ampersand: key=value" header parser
ProcessRunner.cs        # terminal-emulator or background process spawning
SystemTerminal.cs       # emulator detection + per-emulator stay-attached flags, workdir, env scrub
SteamRt4Runtime.cs      # find + validate the Steam Linux Runtime install
SteamRt4Compat.cs       # libunwind + editor-only pcre2-16 shim cache for the container
SteamLauncherService.cs # container entry through Steam's launcher service
Bootstrap.cs            # Build S&Box: engine Setup.sh + ldd report
DependencyCheck.cs      # host + container ldd sweep + shim report
SboxSettings.cs         # persisted s&box path + server game (~/.local/share/…)
RepoRoot.cs / AppPaths.cs / RunLog.cs / Ansi.cs / TerminalTheme.cs …
apps/                   # launch scripts (sbox.sh, sbox-dev.sh, sbox-server.sh, build.sh, _common.sh)
apps/sbox-cli.sh        # sbox-cli entry point: DOTNET_ROOT, ICU, cd to caller, exec
apps/patches/           # vendored shim sources (casefold.c; built to the ampersand cache dir on demand)
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
| `Preferences > AI Agents` shows `Couldn't run sbox-cli` | Editor launched into the Steam runtime: that container has neither .NET nor ICU, so sbox-cli's apphost aborts before it starts. Investigated, not fixed; see `docs/notes.md`. |
| AppImage won't run (`fuse` errors) | Install `fuse2`/`libfuse2`, or extract once: `./Ampersand-x86_64.AppImage --appimage-extract` and run `squashfs-root/AppRun`. |
| Rebuild fails with `Text file busy` | The target AppImage is still running. Quit it and run `install-appimage.sh` again (the script checks for this first). |

---
name: ampersand
description: Use when launching, stopping or driving the s&box editor on Linux through ampersand, or calling sbox-cli via ampersand --cli.
---

# ampersand

Ampersand is a Linux launcher for running s&box from source: the game client (`sbox`), the editor
(`sbox-dev`) and a dedicated server (`sbox-server`), in Valve's Steam Linux Runtime 4.0 container when
needed, plus the engine's own `sbox-cli` bridge so an agent can drive a running editor.

## 1. Where it lives

- Dev build: `./bootstrap.sh` (a wrapper over `dotnet build -c Release`), then run `<ampersand>` =
  `./bin/Release/net10.0/ampersand` from the checkout. AppImage: `./install-appimage.sh`, then
  `./Ampersand-x86_64.AppImage`, which also installs to `~/Applications/` with a menu entry.
- Settings and logs: `${XDG_DATA_HOME:-~/.local}/share/sbox-ampersand/settings.json` (s&box location,
  the Casefold and Input debug toggles, the server game) and `~/.cache/sbox-ampersand/logs/` (one log
  per launch and build, each opening with the exact command and environment that was run).
- The s&box tree is not an argument: it comes from `settings.json`, set once in the window
  (**S&BOX LOCATION**). With nothing valid saved, ampersand looks for a tree (`game/` and `engine/`)
  above its own binary and saves that. A valid tree contains `game/` and `engine/` with `game/sbox`
  built; if none is found, every headless mode fails with `Repo root not found`.
- Launch scripts sit next to the built app, at `bin/<cfg>/net10.0/scripts/`. They stay loose files, so
  editing the copy changes the next launch.

## 2. Command reference

### window (no arguments)

Where the s&box location and the Casefold / Input debug toggles are set; the headless modes read what
it saved. Not usable headlessly.

### --launch

```
ampersand --launch TARGET [--project FILE.sbproj] [--wait-ready [--ready-timeout SECONDS]] [--terminal] [-- ARGS...]
```

`TARGET` is `sbox`, `sbox-dev` or `sbox-server`. This is the window's play button without the window,
composing the same command the window does.

- The Steam Linux Runtime 4.0 container is always used, so **Steam must be running**, unless the
  script's header declares `runtime=never` (only `apps/sbox-cli.sh`, which `--cli` uses).
- The launch is detached (`setsid`, stdin from `/dev/null`, stdio off the caller's pipes), so the
  engine outlives this process. It is watched for 5 s: a non-zero exit inside that window fails with
  `ampersand: TITLE: MESSAGE` and the log tail on stderr, exit 1. Otherwise it prints the log path and
  exits 0 **without waiting for the editor to load**: exit 0 means launched, not usable.
- `--project` is `sbox-dev` only: it opens that `.sbproj` directly instead of the project picker (a
  leading `~` is expanded, a relative path resolves against the caller's directory). Without it,
  `sbox-dev` starts the picker and exits 0 by design.
- `--wait-ready` is `sbox-dev` only and needs `--project`. It polls a host-side
  `sbox-cli --connect PROJECT --call editor_status` every 2 s; a probe that does not answer within
  15 s is killed and counts as not ready. `--ready-timeout SECONDS` bounds the wait (1..3600, default
  300) and the clock starts at the launch itself, so the 5 s grace above counts against it.
  `--ready-timeout` without `--wait-ready` is a usage error.
- `--terminal` runs the launch in your terminal emulator: output stays in that window instead of the
  log, and a failed run holds the window open (press Enter to close) instead of exiting 1.
- Casefold and Input debug come from `settings.json`; a missing or stale casefold shim is built without
  asking. `sbox-server` gets `+game` from the saved server game, and `-- ARGS...` are appended after
  any `--project`/`+game`.
- Output to parse: `launched <target> (<mode>)` and `log: <path>` on stdout, where mode is
  `steam runtime, background`, `host, background` or the emulator name; with `--wait-ready`, one more
  stdout line, `ready (sbox-cli editor_status answered after N s)`. Non-fatal problems print
  `ampersand: warning: ...` on stderr. Failures exit 1 (`ampersand: TITLE: MESSAGE` on stderr); usage
  errors (`missing TARGET`, unknown option, bad `--ready-timeout`, `--project` with a non-dev target)
  exit 2; `-h`/`--help` prints usage and exits 0.
- On a `--wait-ready` timeout: exit 1 with `Not ready`, the last thing sbox-cli said, and a note that
  the editor was left running plus the exact `--stop` command. If the launch dies first, that is
  reported instead, with the exit code and log tail.

### --stop

```
ampersand --stop TARGET [--project FILE.sbproj]
```

Stops a running target without the window, for recovering a frozen engine.

- It finds the process by scanning `/proc` and matching `argv[0]` against `<sbox-root>/game/<target>`,
  so it reaches editors started by the window, `--launch` or the sbox project picker.
- It sends `SIGTERM` to the matches and the child processes captured in the same snapshot, waits 10 s,
  then `SIGKILL`s any survivor (a frozen engine can ignore `SIGTERM`). The project picker
  (`game/sbox-launcher`) is left alone: it owns the editors and can reopen the project.
- `--project` is `sbox-dev` only, must end in `.sbproj` (the file need not exist), and matches the
  editor's own last `-project` argument, so only that project's editor is stopped. **Without
  `--project`, every editor from this checkout is stopped.**
- Output to parse: `stopping <target> pid N (-project P) and K child process(es)` per match,
  `still running after 10 s - sent SIGKILL to N process(es)` if anything survived, then
  `stopped <target> [on P]`, or `<target> is not running [on P]`.
- Exit 0 when the target was stopped or was not running, 1 on failure (including a pid it was not
  permitted to signal), 2 on usage errors.

### --cli

```
ampersand --cli [--] SBOX-CLI-ARGS...
```

Runs the engine's own `sbox-cli` on the host, in the foreground. Everything after `--` (or after
`--cli` without it) goes to sbox-cli unchanged, ampersand prints nothing of its own while it runs, and
sbox-cli's exit code becomes ampersand's: it is a transparent wrapper, not an interpreter.

- It composes a launch's environment (`LaunchComposer`, settings.json toggles) plus `DOTNET_ROOT`
  pointed at the engine's own runtime, `SBOX_CLI_CWD` (so a relative project argument means what you
  meant) and `SBOX_TRACE=0` (so `_common.sh` does not trace over output a caller parses). No Casefold:
  no engine is started here.
- Failures before sbox-cli starts print `ampersand: TITLE: MESSAGE` on stderr and exit 1; missing
  arguments or a refused mode exit 2. See section 3.

### --dependency-check, --bootstrap, --probe-distro

- `ampersand --dependency-check`: an `ldd` sweep of the engine's natives and bundled .NET runtime, on
  the host **and** inside steamrt4, plus shim cache status. Prints the report and then **waits for
  Enter** (`Press Enter to close.`), so do not run it from an agent - it will hang.
- `ampersand --bootstrap [--skip-deps]`: runs the engine's `Setup.sh`, then ampersand's `ldd` report
  over `game/bin/linuxsteamrt64` unless `--skip-deps`. It **builds the engine and is long-running**,
  and also **waits for Enter**. Its exact command is kept in `~/.cache/sbox-ampersand/logs/`.
- `ampersand --probe-distro [FILE]`: prints distro detection without touching anything - `family`,
  `manager`, `escalation`, `atomic`, an optional `caveat`, then the install command for each required
  and best-effort library. The optional `os-release` path validates other distros' mappings. Exits 0.

## 3. Driving sbox-cli through ampersand

`sbox-cli` ships with the engine at `<sbox-root>/game/bin/managed/sbox-cli` (ampersand uses that same
path) and talks to a running editor or game over a local pipe. For its own flags, tool catalog,
lifetimes and exit codes read the engine's skill at
`<sbox-root>/game/bin/managed/skills/sbox-cli/SKILL.md` and its `references/` (`tools.md`,
`protocol.md`, `run-csharp.md`). The standard loop:

1. Check nothing is already running (see section 4), then launch:
   ```sh
   <ampersand> --launch sbox-dev --project /abs/Project.sbproj --wait-ready
   ```
   It returns only once the editor answers. A timeout exits 1 and leaves the editor running, so stop
   it before retrying.
2. Confirm what you attached to:
   `<ampersand> --cli -- --connect /abs/Project.sbproj --call editor_status`
3. Work through `--call`, one tool per invocation; batch related calls with `call_tools`:
   ```sh
   <ampersand> --cli -- --connect /abs/Project.sbproj --call search_tools '{"query":"scene"}'
   <ampersand> --cli -- --connect /abs/Project.sbproj --call call_tools '{"calls":[{"name":"editor_status"},{"name":"compile_status"},{"name":"read_console","arguments":{"minimumLevel":"Warn","limit":20}}]}'
   ```
4. For anything the toolsets do not cover, run C# inside the engine (`run_csharp` takes a `path`):
   ```sh
   <ampersand> --cli -- --connect /abs/Project.sbproj --call run_csharp '{"path":"/abs/script.cs"}'
   ```
5. When done, stop the editor you launched:
   `<ampersand> --stop sbox-dev --project /abs/Project.sbproj`

sbox-cli's exit codes, passed through unchanged: `0` success, `2` the tool reported an error, `1`
could not connect or bad arguments. Notes on those commands:

- **It runs on the host, not in the container.** The Steam runtime entry
  (`steam-runtime-launch-client`, then pressure-vessel's `srt-bwrap`) gives its children
  `stdin=/dev/null` and forwards no other file descriptor, so sbox-cli's stdio sessions would be
  cancelled before they connect. A host sbox-cli still reaches an editor inside the container, because
  the pid namespace and `/tmp` are shared.
- **`TMPDIR` is removed** from sbox-cli's environment, so it looks under `/tmp` the way an editor
  launched into the container does. The one case this misses: an editor the window started on the host
  with its Steam runtime unticked inherits the window's `TMPDIR`; if that is not `/tmp`, `--cli` will not
  find it - run the engine's own sbox-cli directly with the same `TMPDIR` instead.
- **sbox-cli's own `--project` and `--game` are refused** (exit 2, exact tokens only): they start an
  engine on the host, outside the container. Launch first and attach afterwards, as in steps 1 and 2.
  The refusal is on exact tokens only, so the same text inside a JSON argument is passed through.
- **As an MCP server**: omit `--call` and the connection becomes a newline-delimited JSON server on
  stdin/stdout that ends at stdin EOF, for a long session over one connection:
  ```sh
  <ampersand> --cli -- --connect /abs/Project.sbproj
  ```
  The engine skill registers sbox-cli directly, e.g.
  `claude mcp add sbox -- "<sbox-root>/game/bin/managed/sbox-cli" --connect`, where the client's
  working directory picks the project. Through ampersand the same registration is
  `claude mcp add sbox -- <ampersand> --cli -- --connect /abs/Project.sbproj` (or a bare `--connect`
  to pick by working directory too). With `--connect` the server fails to start if that project's
  editor is not open. Restart the client's server after the editor restarts.

## 4. Rules and pitfalls

- Never two editors on one project. Check what is running first, for example
  `pgrep -af 'game/sbox-dev .*-project'`, and attach to the editor that already has the project open.
- `--stop` only the project you launched, with `--project /abs/Project.sbproj`: without it, `--stop`
  takes down every editor from that checkout.
- Readiness is `--wait-ready` or polling `editor_status`, never `--launch`'s exit code: exit 0 only
  means the process started, and the launch is detached, so a dead agent process does not kill the
  engine. Stop what you started when you are done.
- Do not rebuild `-c Release` while the window is running: it executes from `bin/Release/net10.0/`, and
  a build replaces files that are in use (the same `Text file busy` clash the README notes for a
  running AppImage).
- `apps/*.sh` are copied to `bin/<cfg>/net10.0/scripts/` at build time, so an edit to `apps/*.sh`
  needs a rebuild before `--launch` picks it up - or edit the copies under `scripts/`, which are
  re-read each launch.
- Shells opened from a terminal ampersand launched inherit `SBOX_CASEFOLD`, `SBOX_IN_STEAMRT4`,
  `SBOX_STEAMRT4_COMPAT` and `SBOX_REPO_ROOT`, and `_common.sh` acts on them; `--launch` and `--cli`
  strip the first three from the child environment so only the current run's settings apply.
- `Preferences > AI Agents` showing `Couldn't run sbox-cli` inside the container is what
  `apps/_common.sh`'s `sbox_dotnet_env` fixes: it exports `DOTNET_ROOT=<sbox-root>/game/bin/dotnet` and
  the app-local ICU variable for container launches, which the editor and its sbox-cli child inherit.
  If it still shows, the built scripts are stale - see `docs/notes.md`.
- `--dependency-check` and `--bootstrap` wait for Enter, and `--bootstrap` is a full engine build.
  Neither belongs in an agent loop. A `Repo root not found` failure means the saved location is wrong;
  it is set in the window, not by a flag.

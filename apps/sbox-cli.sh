#!/bin/sh
# Run sbox-cli, the engine's command line bridge to a running editor or game.
#
# ampersand: name=sbox-cli (agent bridge)
# ampersand: runtime=never
#
# sbox-cli (engine/Launcher/SboxCli) replaced the editor's in-process HTTP MCP
# server: it finds the instance's pipe under /tmp by project path and speaks
# newline-delimited JSON on stdin/stdout. It is an ordinary managed apphost at
# game/bin/managed/sbox-cli, so it needs none of sbox_exec's work - no HarfBuzz
# preload (it adds that itself for the editor it owns), no cd into game/, no
# session detach. What it does read is stdin, for as long as the session lasts.
#
# That is why this script runs on the HOST and declares runtime=never. The
# Steam Linux Runtime 4.0 entry (steam-runtime-launch-client, then
# pressure-vessel's srt-bwrap) gives its children stdin = /dev/null and
# forwards no other file descriptor, so in there sbox-cli's stdio sessions are
# cancelled before they connect - and the engine processes its owned modes
# spawn are killed a moment later, with the cancelled session. On the host it
# works: an editor ampersand launched into the container is still reachable,
# because the pid namespace and /tmp are shared, and that is where the editor's
# pipe lives.
#
# Nothing here sets SBOX_DETACH: that is for scripts that exec the engine and
# must outlive the terminal. sbox-cli is the foreground process.

set -eu

. "$( dirname "$0" )/_common.sh"

cli="$GAME_DIR/bin/managed/sbox-cli"
if [ ! -x "$cli" ]; then
	echo "error: $cli not found or not executable - run Ampersand's Build S&Box first" >&2
	exit 1
fi

# sbox-cli is a framework-dependent .NET apphost, so it needs DOTNET_ROOT and
# an ICU. With no argument the helper points it at the engine's own runtime,
# so the CLI matches the runtime the engine ships (and that is the only option
# in a container), and at the ICU beside it. Values the caller set are kept.
sbox_dotnet_env

# sbox-cli resolves a relative project argument against its working directory.
# The launcher already runs us in the caller's folder, so this cd is a no-op;
# it is kept so a run from a wrapper that changes directory still resolves
# relative project paths against the caller's folder. A hand run from
# elsewhere stays where it started.
if [ -n "${SBOX_CLI_CWD:-}" ] && [ -d "$SBOX_CLI_CWD" ]; then
	cd "$SBOX_CLI_CWD"
fi

exec "$cli" "$@"

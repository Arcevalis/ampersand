#!/bin/sh
# Launch the s&box dedicated server. Without a "game" argument it prints help.
#
# ampersand: name=Dedicated Server (sbox-server)
# ampersand: sniper=optional
#
# The server has no desktop UI so it does NOT need the workarounds in
# _common.sh (HarfBuzz LD_PRELOAD, QT_QPA_PLATFORM=xcb). It only needs the
# Steam Runtime compat library path and to run from game/.
#
# When launched from Ampersand the selected game ident (e.g. fss.bloodsigil)
# or path to a .sbproj is passed as "+game <ident|path>" (the engine's
# dedicated-server concommand, see Sandbox.GameInstance/GameInstanceDll.cs:872
# and Editor/ViewportTools.SpawnDedicatedServer). When run by hand pass it
# yourself:
#   ./sbox-server.sh +game fss.bloodsigil
#   ./sbox-server.sh +game /mnt/blue/sboxprojects/bloodsigil/bloodsigil.sbproj
# Bare ident/path is also accepted for backwards compatibility and translated
# to "+game <value>".

set -eu

# Resolve repo root: launcher passes SBOX_REPO_ROOT; manual runs fall back to
# dirname traversal (same logic as _common.sh).
ROOT="${SBOX_REPO_ROOT:-$( cd "$( dirname "$0" )/.." 2>/dev/null && pwd )}"
if [ ! -d "$ROOT/game" ] || [ ! -d "$ROOT/engine" ]; then
	ROOT="${SBOX_REPO_ROOT:-$( cd "$( dirname "$0" )/../.." && pwd )}"
fi
GAME_DIR="$ROOT/game"
NATIVE_DIR="$GAME_DIR/bin/linuxsteamrt64"

_exe="$GAME_DIR/sbox-server"
if [ ! -x "$_exe" ]; then
	echo "error: $_exe not found or not executable - run ./bootstrap.sh first" >&2
	exit 1
fi

# Only LD_LIBRARY_PATH is needed for the server. SBOX_SNIPER_COMPAT is set by
# the launcher when running inside the Steam Runtime (see MainWindow/PrepareSniper)
# and points at cached libunwind + OpenSSL 3 that sniper does not ship. It is
# appended here inside the container/scripts context rather than exported by the
# launcher because LD_LIBRARY_PATH does not cross the pressure-vessel boundary.
LD_LIBRARY_PATH="$NATIVE_DIR${SBOX_SNIPER_COMPAT:+:$SBOX_SNIPER_COMPAT}${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export LD_LIBRARY_PATH

# Translate bare ident/path to "+game <value>" for convenience:
#   ./sbox-server.sh fss.bloodsigil          -> exec sbox-server +game fss.bloodsigil
#   ./sbox-server.sh /path/to/proj.sbproj   -> exec sbox-server +game /path/to/proj.sbproj
# If the caller already passed +game/-game/game or any switch, leave as-is.
if [ $# -gt 0 ]; then
	case "$1" in
		+game|-game|game) ;;
		+*|-*) ;;
		*) set -- +game "$@" ;;
	esac
fi

cd "$GAME_DIR"
exec "$_exe" "$@"

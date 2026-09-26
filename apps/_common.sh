#!/bin/sh
# Shared environment for the ampersand core launch scripts.
#
# The leading underscore keeps this out of the launcher's scanned script list -
# it is sourced, never launched.
#
# Everything here exists to patch a known Linux issue:
#
#   LD_PRELOAD   the engine ships libHarfBuzzSharp.so (SkiaSharp's statically
#                linked HarfBuzz), but a second libharfbuzz.so.0 reaches the
#                process through Qt's xcb platform plugin and fontconfig. Both
#                export the same unversioned hb_* symbols, so a buffer
#                allocated by one is handed to the other's free() and glibc
#                aborts with "free(): invalid pointer". Preloading the engine's
#                copy puts a single implementation first in the global symbol
#                scope.
#
#                Required on the host AND inside the Steam runtime, which ships
#                its own libharfbuzz.so.0 - the container changes which
#                system HarfBuzz you collide with, not whether you collide.
#
#   cwd          the engine resolves content paths relative to the working
#                directory.
#
#   SBOX_STEAMRT4_COMPAT is set by the launcher only when running inside the
#                Steam runtime, pointing at cached copies of the libunwind
#                libraries the container may not ship, plus the editor-only
#                libpcre2-16.so.0 that steamrt4's platform omits (steamrt4
#                brings its own OpenSSL 3, so that is never shimmed).
#                Ordinary environment variables cross the container boundary
#                but LD_LIBRARY_PATH does not, which is why it is appended
#                here - inside - rather than exported by the launcher.

set -eu

# Self-narration: with tracing on, every command below (ROOT probing, shim
# checks, the final exec of the engine binary) is echoed to stderr with its
# script and line number, so it lands in Ampersand's tee'd run log next to
# the launcher-side header. All four launch scripts source this file, so one
# gate covers them all. Default on - a launch costs ~10 lines; SBOX_TRACE=0
# silences it. Only --env allowlisted variables cross into the Steam runtime
# container, so set that via --env to quiet container runs too.
if [ "${SBOX_TRACE:-1}" != "0" ]; then
	# Location-tagged prefix only where the shell supports LINENO (host
	# dash/bash); the Steam runtime container's sh errors on it under set
	# -u and prints PS4 literally, so fall back to a plain prefix there.
	# The subshell confines the probe - its own stderr goes to /dev/null.
	if ( eval ': "$LINENO"' ) 2>/dev/null; then
		export PS4='+ [$0:$LINENO] '
	else
		export PS4='+ '
	fi
	set -x
fi

# The launcher passes SBOX_REPO_ROOT; the fallback keeps the script usable by
# hand from the built scripts dir (OutDir/scripts/) or from repo/apps/.
# When built, scripts live at <OutDir>/scripts/ so dirname $0 is .../scripts
# (parent = build output); in repo they are at ampersand/apps (parent/parent = sbox root).
# Try scripts/ layout first, then legacy apps/ layout.
ROOT="${SBOX_REPO_ROOT:-$( cd "$( dirname "$0" )/.." 2>/dev/null && pwd )}"
if [ ! -d "$ROOT/game" ] || [ ! -d "$ROOT/engine" ]; then
	ROOT="${SBOX_REPO_ROOT:-$( cd "$( dirname "$0" )/../.." && pwd )}"
fi
GAME_DIR="$ROOT/game"
NATIVE_DIR="$GAME_DIR/bin/linuxsteamrt64"

sbox_exec()
{
	_exe_name="$1"
	shift

	_exe="$GAME_DIR/$_exe_name"
	if [ ! -x "$_exe" ]; then
		echo "error: $_exe not found or not executable - run Ampersand's Build S&Box first" >&2
		exit 1
	fi

	_harfbuzz="$NATIVE_DIR/libHarfBuzzSharp.so"
	if [ ! -f "$_harfbuzz" ]; then
		echo "error: HarfBuzz not found at $_harfbuzz - run Ampersand's Build S&Box first" >&2
		exit 1
	fi

	LD_PRELOAD="$_harfbuzz${LD_PRELOAD:+:$LD_PRELOAD}"
	LD_LIBRARY_PATH="$NATIVE_DIR${SBOX_STEAMRT4_COMPAT:+:$SBOX_STEAMRT4_COMPAT}${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
	export LD_PRELOAD LD_LIBRARY_PATH

	# Case-insensitive asset fallback for wrong-case references committed
	# upstream (citizen .vmdl files referencing lowercase FBX paths that only
	# resolve on case-insensitive filesystems). Vendor source at
	# apps/patches/casefold.c, built into the ampersand cache dir on demand
	# like the shims below. Opt-in only: SBOX_CASEFOLD=1 arms it (Casefold
	# toggle, off by default). Override the location with SBOX_CASEFOLD_SO.
	# Appended after HarfBuzz - HarfBuzz-first ordering is required (see
	# above); casefold only interposes the open/stat family, so it is order
	# independent of the shims below. Diagnostics with CASEFOLD_VERBOSE=1.
	# Note: only --env allowlisted variables cross into the Steam runtime
	# container while the .so itself must resolve inside - prefer host-side
	# runs when relying on the fallback.
	if [ "${SBOX_CASEFOLD:-0}" != "0" ]; then
		_casefold_so="${SBOX_CASEFOLD_SO:-${XDG_CACHE_HOME:-$HOME/.cache}/sbox-ampersand/libcasefold.so}"
		if [ ! -f "$_casefold_so" ]; then
			echo "error: SBOX_CASEFOLD=$SBOX_CASEFOLD but casefold shim missing at $_casefold_so" >&2
			echo "launch once via Ampersand with Casefold ticked to build it," >&2
			echo "or build it by hand: gcc -D_GNU_SOURCE -shared -fPIC -O2 -o libcasefold.so casefold.c -ldl" >&2
			exit 1
		fi
		LD_PRELOAD="$LD_PRELOAD:$_casefold_so"
		export LD_PRELOAD
	fi

	# Wayland's Qt platform plugin is not shipped/unstable; force X11 (xcb)
	# via XWayland (only xcb is bundled - "Available platform plugins are: xcb").
	QT_QPA_PLATFORM=xcb
	export QT_QPA_PLATFORM

	# SDL3 equivalent of the above: it defaults to the Wayland backend when
	# WAYLAND_DISPLAY is set, making the viewport a native Wayland surface
	# with no X window and no in-process X press owner (every X grab then
	# fails AlreadyGrabbed and mid-drag warps die in the compositor). Force
	# X11 so the press owner is SDL's in-process X connection. SDL3 name -
	# SDL2 called it SDL_VIDEODRIVER. Only --env allowlisted variables cross
	# into the Steam runtime container, so the launcher passes this one the
	# same way; the hard-set here keeps direct script runs consistent.
	SDL_VIDEO_DRIVER=x11
	export SDL_VIDEO_DRIVER

	cd "$GAME_DIR"
	exec "$_exe" "$@"
}

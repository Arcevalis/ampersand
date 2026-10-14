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
#
#   DOTNET_ROOT / DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU
#                are exported by sbox_dotnet_env, and only for Steam runtime
#                launches (sbox_exec under SBOX_IN_STEAMRT4=1): the container
#                ships neither .NET nor an ICU, and the sbox-cli the editor
#                spawns inherits this environment. sbox-cli.sh calls the same
#                helper on the host, keeping values the caller set.

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

# Points a framework-dependent .NET apphost at a runtime and, where needed, at
# an ICU. sbox-cli (game/bin/managed/sbox-cli) is one, and unlike sbox-dev and
# sbox-launcher it carries no embedded runtime hint: BuildManaged passes
# -p:AppHostRelativeDotNet=bin/dotnet to those, not to the sbox-cli build. So
# it needs DOTNET_ROOT wherever there is no system .NET, and CoreCLR also needs
# an ICU - the container has none - so this points
# DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU at the ICU StageLinuxRuntime puts
# next to CoreCLR. That is the same option the engine's launchers bake in via
# engine/Launcher/Directory.Build.targets (System.Globalization.AppLocalIcu).
#
# force = always the engine's runtime and a freshly detected ICU. Container
# launches use it because an inherited DOTNET_ROOT from Steam's environment
# could name a host path or a runtime without that ICU. No argument = respect
# values the caller already set (sbox-cli.sh's hand-run behaviour). The ICU
# variable is left unset when no libicuuc.so.* sits beside the runtime.
sbox_dotnet_env()
{
	if [ "${1:-}" = force ]; then
		DOTNET_ROOT="$GAME_DIR/bin/dotnet"
		unset DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU
	else
		DOTNET_ROOT="${DOTNET_ROOT:-$GAME_DIR/bin/dotnet}"
	fi
	export DOTNET_ROOT

	if [ -z "${DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU:-}" ]; then
		for _icu in "$DOTNET_ROOT"/shared/Microsoft.NETCore.App/*/libicuuc.so.*; do
			if [ -f "$_icu" ]; then
				DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU="${_icu##*libicuuc.so.}"
				export DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU
				break
			fi
		done
		unset _icu
	fi
}

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

	# The editor runs game/bin/managed/sbox-cli itself (Preferences > AI
	# Agents, the startup skill prompt) and its child inherits this
	# environment. Inside the Steam runtime there is no .NET and no ICU,
	# so without this it fails with "Couldn't run sbox-cli" (see
	# docs/notes.md). Host launches are left alone: the system .NET works
	# there, and an app-local ICU value would leak into every child of the
	# editor (IDEs, other .NET tools) that runs on a runtime without that
	# ICU.
	if [ "${SBOX_IN_STEAMRT4:-0}" = "1" ]; then
		sbox_dotnet_env force
	fi

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

	# Detached launches (sbox-dev starts sbox-launcher as a separate process
	# and returns 0 immediately): the terminal tab would close while the
	# launcher is still booting, and the closing tab's SIGHUP would kill it
	# pre-log with no window and no error anywhere. Run detached in our own
	# session (no controlling terminal) so the launcher outlives the tab,
	# then - on a terminal - wait for the whole detached session below, so
	# the console auto-closes on quit. Only armed per-script via SBOX_DETACH=1
	# (sbox-dev menu mode; -project/-test run in-process) - the game and
	# server run in-process and want the tab's session (Ctrl+C delivery,
	# close-tab-kills). Plain sh fds are untouched throughout, so while the
	# tab lives the trace still shows and isatty still holds.
	if [ "${SBOX_DETACH:-0}" != "0" ]; then
		# Ignore SIGHUP from here on: it survives exec, so the engine and the
		# launcher inherit the immunity. Verified necessary: setsid can leave
		# the new session holding the pty as its controlling terminal (seen
		# in /proc as tty=pts/N), so the session leader's exit HUPs the group
		# and kills a booting launcher mid-flight. TERM/INT stay lethal.
		trap '' HUP
		_code=0
		if command -v setsid >/dev/null 2>&1; then
			# -w waits for sbox-dev itself (instant 0 on detach) and reports
			# its real code deterministically, fork or not.
			setsid -w "$_exe" "$@" || _code=$?
		else
			"$_exe" "$@" || _code=$?
		fi
		# Nonzero = failed before detaching: hand the code up (the launcher's
		# failure hold shows it).
		if [ $_code -ne 0 ]; then
			exit $_code
		fi
		# Detached cleanly: with a terminal, wait for the session so the
		# console lives exactly as long as it. Non-tty stdin (background
		# runs, pipes) skips the wait and keeps instant-exit semantics, so
		# headless tracking can never wedge waiting on a GUI.
		if [ -t 0 ] && command -v pgrep >/dev/null 2>&1 && command -v ps >/dev/null 2>&1; then
			sbox_wait_session
		fi
		# The wait tracks lifetime, not outcome: a launcher crash and a clean
		# quit both end the session, so both close the tab at 0 - the engine's
		# own game/logs carry the crash detail, not this exit code.
		exit 0
	fi
	exec "$_exe" "$@"
}

# Waits for the detached sbox session (launcher hub and/or the editor the hub
# spawns as its child) to end. Either quit order works: the hub may stay open
# beside the editor, or CloseOnLaunch may destroy it while its editor child
# continues - both stay in the launcher's session, so the union covers them.
# Same-second double launches can cross-track (union semantics); the tab then
# lingers until both quit, which is benign. Needs pgrep/ps (checked by the
# caller); give-up exits 1 into the failure hold so a missing launcher is
# diagnosable instead of silent.
sbox_wait_session()
{
	# This function polls ps every couple of seconds for as long as the
	# session runs (hours, potentially): under the launch tracing (set -x)
	# that would flood the terminal with ps/grep/sleep lines and bury the
	# launch output above, so tracing goes off for the tracking below. The
	# startup trace already printed; explicit echoes still show.
	set +x
	echo "sbox-dev detached; waiting for the launcher session to end (terminal closes on quit)."
	# Find the freshly spawned session: newest hub or editor young enough to
	# be ours. The hub itself may live only milliseconds (spawn editor, exit),
	# so the poll matches the union - the long-lived editor is caught reliably
	# even when the hub flashes past unseen. Basename matches - container path
	# remapping changes the dirname. The [/] keeps pgrep from matching this
	# shell's own command line.
	_launch_pid=""
	_tries=0
	# Probe etimes support once (busybox ps lacks it): without it, accept the
	# newest match rather than erroring every candidate out.
	_have_etimes=0
	if ps -o etimes= -p $$ 2>/dev/null | grep -qE '^[[:space:]]*[0-9]+'; then
		_have_etimes=1
	fi
	while [ $_tries -lt 15 ]; do
		_cand=$(pgrep -n -f '[/]sbox-launcher( |$)|sbox-dev .*-project' || true)
		if [ -n "$_cand" ]; then
			if [ $_have_etimes -eq 1 ]; then
				_age=$(ps -o etimes= -p "$_cand" 2>/dev/null | tr -d ' ' || true)
				case "$_age" in
					''|*[!0-9]*)
						;;
					*)
						if [ "$_age" -le 20 ]; then
							_launch_pid=$_cand
							break
						fi
						;;
				esac
			else
				_launch_pid=$_cand
				break
			fi
		fi
		_tries=$((_tries + 1))
		sleep 1
	done
	if [ -z "$_launch_pid" ]; then
		echo "error: sbox-dev detached but no launcher or editor appeared - see game/logs/sbox-launcher.log" >&2
		exit 1
	fi
	# Resolve the launcher's session for union tracking; fall back to global
	# matching when ps -s is unsupported (same-second double launches may
	# then cross-track - benign lingering, documented above).
	_sid=$(ps -o sid= -p "$_launch_pid" 2>/dev/null | tr -d ' ' || true)
	_session_mode=0
	case "$_sid" in
		''|*[!0-9]*)
			;;
		*)
			if ps -s "$_sid" -o pid= 2>/dev/null | grep -q "$_launch_pid"; then
				_session_mode=1
			fi
			;;
	esac
	while :; do
		# Tab gone (stdout no longer a tty)? Bow out quietly - the detached
		# launcher continues on its own (setsid + HUP immunity above).
		if [ ! -t 1 ]; then
			break
		fi
		if [ $_session_mode -eq 1 ]; then
			_list=$(ps -s "$_sid" -o stat=,args= 2>/dev/null || true)
		else
			_list=$(ps -eo stat=,args= 2>/dev/null || true)
		fi
		# Non-zombie hub or in-process editor (-project) still alive?
		if printf '%s\n' "$_list" | grep -v '^Z' | grep -qE 'sbox-launcher|sbox-dev .*-project'; then
			sleep 2
		else
			break
		fi
	done
}

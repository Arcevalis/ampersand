#!/bin/sh
# Build s&box from source - thin wrapper around the engine's Setup.sh.
#
# ampersand: name=Build S&Box
# ampersand: runtime=never
#
# History: this used to be a full port of sbox-public/bootstrap.sh (fetch
# natives, ldd sweep, drive SboxBuild step by step). The engine now owns that
# flow in Setup.sh (git hooks, per-platform artifacts, bindings, managed,
# shaders, content), so this just locates the checkout and hands over. For the
# readable per-binary ldd report use Ampersand's Check-dependencies tool
# (or `ampersand --dependency-check`). The C# Tool (Bootstrap.cs,
# --bootstrap) is the primary entry point - it runs Setup.sh plus the ldd
# report; this script is the loose-file copy that ships to
# <OutDir>/scripts/ via ampersand.csproj.

set -eu

# Resolve repo root the same way _common.sh does: launcher passes
# SBOX_REPO_ROOT; manual runs fall back to dirname traversal.
ROOT="${SBOX_REPO_ROOT:-}"
if [ -z "$ROOT" ] || [ ! -d "$ROOT/game" ] || [ ! -d "$ROOT/engine" ]; then
	ROOT="$( cd "$( dirname "$0" )/.." 2>/dev/null && pwd )"
	if [ ! -d "$ROOT/game" ] || [ ! -d "$ROOT/engine" ]; then
		ROOT="$( cd "$( dirname "$0" )/../.." 2>/dev/null && pwd )"
	fi
	# When running from the built output (ampersand/bin/Release/net10.0/scripts/),
	# parent traversal lands in the ampersand checkout, not the sbox checkout.
	# If that tree has no game/engine, it is still not the right root; leave
	# ROOT as-is and let the missing project check below emit the hint.
fi

if [ ! -d "$ROOT/game" ] || [ ! -d "$ROOT/engine" ]; then
	echo "error: cannot locate sbox repo root (expected game/ and engine/ under \$ROOT)" >&2
	echo "hint: set SBOX_REPO_ROOT or run via Ampersand's Build S&Box tool" >&2
	echo "tried: $ROOT" >&2
	exit 1
fi

# Via sh, not ./. : Setup.sh is stored without the exec bit upstream.
if [ ! -f "$ROOT/Setup.sh" ]; then
	echo "error: $ROOT/Setup.sh not found - this checkout predates the engine's Setup.sh workflow" >&2
	echo "hint: pull sbox-public, then rerun" >&2
	exit 1
fi

case "${1:-}" in
	-h|--help)
		echo "Usage: $0 [--verbose]"
		echo "  Run the engine's Setup.sh (full build incl. shaders and content)."
		echo "  Pass --verbose through for full build output."
		exit 0 ;;
esac

# Freshly downloaded artifacts lose the executable bit, and the engine only
# repairs contentbuilder itself - restore both toolchain bits best-effort so
# the content step can spawn resourcecompiler. Silent on purpose (missing
# files just mean Setup.sh hasn't downloaded them yet); never fails the build.
chmod +x "$ROOT/game/bin/linuxsteamrt64/contentbuilder" "$ROOT/game/bin/linuxsteamrt64/resourcecompiler" 2>/dev/null || true

cd -- "$ROOT"
exec sh Setup.sh "$@"

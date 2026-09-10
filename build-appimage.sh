#!/usr/bin/env bash
# build-appimage.sh
# Builds Ampersand from source and packages it as an AppImage.
#
# Usage:
#   ./build-appimage.sh [OPTIONS]
#
# Options:
#   --source DIR          Ampersand source dir (default: this script's dir)
#   --output FILE         Output AppImage path (default: <source>/Ampersand-x86_64.AppImage)
#   --publish-dir DIR     dotnet publish directory (default: <source>/publish/linux-x64)
#   --configuration NAME  Build configuration (default: Release)
#   --runtime RID         .NET runtime identifier (default: linux-x64)
#   --self-contained      Self-contained publish, no dotnet needed on host (default: on)
#   --framework-dependent Framework-dependent publish, requires .NET 10 runtime on host
#   --icon FILE           App icon PNG (default: <source>/assets/ampersand.png)
#   --base-appimage FILE  AppImage to extract full runtime skeleton from
#                         (AppRun, libs, runtime/ — everything except usr/bin,
#                         which is replaced with the new publish output)
#   --install-dir DIR     Where to install the finished AppImage
#                         (default: $HOME/Applications)
#   --no-install          Skip the install step (leave the AppImage at --output
#                         only, no .desktop / icon / terminal link)
#   --no-build            Skip dotnet publish step (repackage existing publish output)
#   -h, --help            Show this help
#
# Examples:
#   ./build-appimage.sh
#   ./build-appimage.sh --output ~/Ampersand-x86_64.AppImage
#   ./build-appimage.sh --framework-dependent --no-build
#
# Notes:
#   - Requires .NET 10 SDK (dotnet on PATH). The published app targets net10.0.
#   - Self-contained single-file publish is the default: the AppImage runs on
#     any x86_64 Linux without dotnet installed (~80-120 MB).
#   - Framework-dependent publish is smaller (~15-25 MB) but needs the
#     Microsoft.NETCore.App 10.x runtime on the host.
#   - Launch scripts in apps/*.sh are copied to usr/bin/scripts/ inside the
#     AppImage. AppContext.BaseDirectory resolves there at runtime, so
#     AppPaths.FindScriptsDir() works unmodified under /tmp/.mount_*.

set -euo pipefail

# ── Defaults ────────────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
SOURCE_DIR="$SCRIPT_DIR"
PUBLISH_DIR=""          # resolved below if empty
OUTPUT_APPIMAGE=""      # resolved below if empty
CONFIG="Release"
RUNTIME="linux-x64"
SELF_CONTAINED=1
ICON_FILE=""            # resolved below if empty
BASE_APPIMAGE=""
INSTALL_DIR="${HOME}/Applications"
DO_BUILD=1
DO_INSTALL=1

# ── Argument parsing ─────────────────────────────────────────────────────────
while [[ $# -gt 0 ]]; do
	case "$1" in
		--source)          SOURCE_DIR="$2";      shift 2 ;;
		--output)          OUTPUT_APPIMAGE="$2"; shift 2 ;;
		--publish-dir)     PUBLISH_DIR="$2";     shift 2 ;;
		--configuration)   CONFIG="$2";          shift 2 ;;
		--runtime)         RUNTIME="$2";         shift 2 ;;
		--self-contained)  SELF_CONTAINED=1;     shift ;;
		--framework-dependent) SELF_CONTAINED=0; shift ;;
		--icon)            ICON_FILE="$2";       shift 2 ;;
		--base-appimage)   BASE_APPIMAGE="$2";   shift 2 ;;
		--install-dir)     INSTALL_DIR="$2";     shift 2 ;;
		--no-install)      DO_INSTALL=0;         shift ;;
		--no-build)        DO_BUILD=0;           shift ;;
		-h|--help)
			sed -n '2,/^set -/p' "$0" | grep '^#' | sed 's/^# \{0,1\}//'
			exit 0
			;;
		*) echo "Unknown option: $1" >&2; exit 1 ;;
	esac
done

[[ -z "$PUBLISH_DIR" ]] && PUBLISH_DIR="${SOURCE_DIR}/publish/${RUNTIME}"
[[ -z "$OUTPUT_APPIMAGE" ]] && OUTPUT_APPIMAGE="${SOURCE_DIR}/Ampersand-x86_64.AppImage"
[[ -z "$ICON_FILE" ]] && ICON_FILE="${SOURCE_DIR}/assets/ampersand.png"

OUTPUT_DIR="$(dirname "$OUTPUT_APPIMAGE")"
APPIMAGE_TOOL="${OUTPUT_DIR}/appimagetool"
APPDIR="${OUTPUT_DIR}/.ampersand-appdir"

# ── Helpers ──────────────────────────────────────────────────────────────────
log()  { echo "[build-appimage] $*"; }
die()  { echo "[build-appimage] ERROR: $*" >&2; exit 1; }

# Refuse to write over a running executable: the kernel forbids it (ETXTBSY,
# "Text file busy"), so name the PIDs instead of letting the copy/pack fail.
die_if_running() {
	local target="$1"
	[[ -f "$target" ]] || return 0
	local want pids have f
	want="$(readlink -f "$target")"
	pids=""
	for f in /proc/[0-9]*/exe; do
		have="$(readlink "$f" 2>/dev/null || true)"
		have="${have% (deleted)}"
		if [[ -n "$have" && "$have" == "$want" ]]; then
			pids+="$(basename "$(dirname "$f")") "
		fi
	done
	if [[ -n "$pids" ]]; then
		die "File is already running: $target (PID(s): ${pids% }). Quit it and run this script again."
	fi
}

# ── Validate source dir ──────────────────────────────────────────────────────
[[ -f "$SOURCE_DIR/ampersand.csproj" ]] || die "No ampersand.csproj in $SOURCE_DIR (pass --source DIR)"
[[ -d "$SOURCE_DIR/apps" ]] || die "No apps/ dir in $SOURCE_DIR"
[[ -f "$SOURCE_DIR/assets/ampersand.desktop" ]] || die "Missing assets/ampersand.desktop"
[[ -f "$SOURCE_DIR/assets/AppRun" ]] || die "Missing assets/AppRun"

# ── Step 1: dotnet publish ───────────────────────────────────────────────────
if [[ $DO_BUILD -eq 1 ]]; then
	command -v dotnet >/dev/null 2>&1 || die "dotnet not on PATH — install .NET 10 SDK: https://dotnet.microsoft.com/download"

	log "Publishing Ampersand ($CONFIG, $RUNTIME, self-contained=$SELF_CONTAINED)..."
	mkdir -p "$PUBLISH_DIR"

	PUBLISH_ARGS=( "$SOURCE_DIR/ampersand.csproj" -c "$CONFIG" -r "$RUNTIME" -o "$PUBLISH_DIR" )
	if [[ $SELF_CONTAINED -eq 1 ]]; then
		PUBLISH_ARGS+=( --self-contained true
			-p:PublishSingleFile=true
			-p:IncludeNativeLibrariesForSelfExtract=true )
		if [[ "$CONFIG" == "Release" ]]; then
			PUBLISH_ARGS+=( -p:DebugType=none -p:DebugSymbols=false )
		fi
	else
		PUBLISH_ARGS+=( --self-contained false )
	fi

	dotnet publish "${PUBLISH_ARGS[@]}"

	# The csproj copies apps/** to scripts/ on build, but ensure the publish
	# output has them regardless of MSBuild path-separator quirks.
	if [[ ! -f "$PUBLISH_DIR/scripts/sbox.sh" ]]; then
		log "Publish output missing scripts/ — copying from apps/..."
		mkdir -p "$PUBLISH_DIR/scripts"
		cp "$SOURCE_DIR"/apps/*.sh "$PUBLISH_DIR/scripts/"
	fi
	chmod +x "$PUBLISH_DIR"/scripts/*.sh 2>/dev/null || true
fi

# ── Verify publish output ────────────────────────────────────────────────────
BUILT_BINARY="${PUBLISH_DIR}/ampersand"
[[ -f "$BUILT_BINARY" ]] || die "Build output not found: $BUILT_BINARY (run without --no-build first)"
[[ -f "$PUBLISH_DIR/scripts/sbox.sh" ]] || die "Scripts missing: $PUBLISH_DIR/scripts/sbox.sh"

log "Publish output: $(du -sh "$PUBLISH_DIR" | cut -f1) — ${PUBLISH_DIR}"

# ── Step 2: Get appimagetool ─────────────────────────────────────────────────
if [[ ! -x "$APPIMAGE_TOOL" ]]; then
	log "Downloading appimagetool..."
	mkdir -p "$OUTPUT_DIR"
	wget -q "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage" \
		-O "$APPIMAGE_TOOL"
	chmod +x "$APPIMAGE_TOOL"
fi

# ── Step 3: Prepare AppDir ───────────────────────────────────────────────────
log "Preparing AppDir at ${APPDIR}..."
rm -rf "$APPDIR"

if [[ -n "$BASE_APPIMAGE" && -f "$BASE_APPIMAGE" ]]; then
	log "Extracting runtime skeleton from: ${BASE_APPIMAGE}"
	(cd "$OUTPUT_DIR" && "$BASE_APPIMAGE" --appimage-extract 2>/dev/null)
	mv "${OUTPUT_DIR}/squashfs-root" "$APPDIR"
	# Drop the old payload — the fresh publish output replaces it below.
	# Keep AppRun, runtime/, lib/, metadata from the base.
	rm -rf "${APPDIR}/usr/bin/ampersand" "${APPDIR}/usr/bin/ampersand.dll" \
		"${APPDIR}/usr/bin/"*.dll "${APPDIR}/usr/bin/"*.json \
		"${APPDIR}/usr/bin/"*.pdb "${APPDIR}/usr/bin/runtimes" \
		"${APPDIR}/usr/bin/scripts" \
		"${APPDIR}/"*.desktop "${APPDIR}/"*.png "${APPDIR}/.DirIcon"
else
	if [[ -n "$BASE_APPIMAGE" ]]; then
		log "Base AppImage not found ($BASE_APPIMAGE) — creating minimal AppDir skeleton"
	else
		log "No base AppImage — creating minimal AppDir skeleton"
	fi
	mkdir -p "$APPDIR"
fi

# ── Step 4: Populate usr/ tree ───────────────────────────────────────────────
log "Copying publish output into AppDir..."
mkdir -p "${APPDIR}/usr/bin"
mkdir -p "${APPDIR}/usr/share/applications"
mkdir -p "${APPDIR}/usr/share/icons/hicolor/256x256/apps"

# Copy everything dotnet published (binary, dlls, deps.json, runtimeconfig,
# runtimes/<rid>/, scripts/). Exclude PDBs in Release to keep it lean.
if [[ "$CONFIG" == "Release" ]]; then
	for f in "$PUBLISH_DIR"/*; do
		case "$f" in *.pdb) continue;; *) cp -r "$f" "${APPDIR}/usr/bin/";; esac
	done
else
	cp -r "$PUBLISH_DIR"/* "${APPDIR}/usr/bin/"
fi
chmod +x "${APPDIR}/usr/bin/ampersand"
chmod +x "${APPDIR}"/usr/bin/scripts/*.sh 2>/dev/null || true

# Desktop entry (top-level for appimagetool + freedesktop locations)
log "Installing desktop entry + icon..."
cp "${SOURCE_DIR}/assets/ampersand.desktop" "${APPDIR}/ampersand.desktop"
cp "${SOURCE_DIR}/assets/ampersand.desktop" "${APPDIR}/usr/share/applications/ampersand.desktop"

# Icon (top-level for appimagetool + hicolor + .DirIcon)
[[ -f "$ICON_FILE" ]] || die "Icon not found: $ICON_FILE — save the provided logo to assets/ampersand.png or pass --icon FILE"
cp "$ICON_FILE" "${APPDIR}/ampersand.png"
cp "$ICON_FILE" "${APPDIR}/usr/share/icons/hicolor/256x256/apps/ampersand.png"
cp "$ICON_FILE" "${APPDIR}/.DirIcon"

# AppRun launcher
cp "${SOURCE_DIR}/assets/AppRun" "${APPDIR}/AppRun"
chmod +x "${APPDIR}/AppRun"

# ── Step 5: Repack into AppImage ─────────────────────────────────────────────
die_if_running "$OUTPUT_APPIMAGE"
log "Packing AppImage → ${OUTPUT_APPIMAGE}..."
ARCH=x86_64 "$APPIMAGE_TOOL" "$APPDIR" "$OUTPUT_APPIMAGE" 2>&1
chmod +x "$OUTPUT_APPIMAGE"

# ── Step 6: Cleanup ──────────────────────────────────────────────────────────
rm -rf "$APPDIR"

log "Done: $(du -sh "$OUTPUT_APPIMAGE" | cut -f1) — ${OUTPUT_APPIMAGE}"

# ── Step 7: Install ──────────────────────────────────────────────────────────
# Copy the AppImage to its standard home, drop the .desktop entry and icon into
# the usual user locations, and refresh the menu caches — so the app is
# launchable from the menu right after building. The entry itself runs in a
# terminal (Terminal=true), so no extra terminal shim is needed.
if [[ $DO_INSTALL -eq 1 ]]; then
	APPIMAGE_NAME="$(basename "$OUTPUT_APPIMAGE")"
	INSTALL_TARGET="${INSTALL_DIR}/${APPIMAGE_NAME}"
	DESKTOP_FILE="${HOME}/.local/share/applications/Ampersand.desktop"
	ICON_TARGET="${HOME}/.local/share/icons/hicolor/256x256/apps/ampersand.png"

	mkdir -p "$INSTALL_DIR" "$(dirname "$DESKTOP_FILE")" "$(dirname "$ICON_TARGET")"

	if [[ "$(readlink -f "$OUTPUT_APPIMAGE")" != "$(readlink -f "$INSTALL_TARGET")" ]]; then
		die_if_running "$INSTALL_TARGET"
		log "Installing AppImage → ${INSTALL_TARGET}..."
		cp -f "$OUTPUT_APPIMAGE" "$INSTALL_TARGET"
	else
		log "Output is already in the install dir — skipping copy."
	fi
	chmod +x "$INSTALL_TARGET"

	log "Installing icon → ${ICON_TARGET}..."
	cp -f "$ICON_FILE" "$ICON_TARGET"

	log "Installing desktop entry → ${DESKTOP_FILE}..."
	# Mirrors the known-good KDE entry: a bare Exec path (quoting Exec/TryExec
	# breaks launchers, and TryExec is dropped entirely), Terminal=true.
	cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Comment=s&box launcher for Linux
Exec=$INSTALL_TARGET
Icon=$ICON_TARGET
Name=Ampersand
NoDisplay=false
Path=
PrefersNonDefaultGPU=false
StartupNotify=true
Terminal=false
TerminalOptions=
Type=Application
Categories=Development;
X-KDE-SubstituteUID=false
X-KDE-Username=
EOF
	chmod 644 "$DESKTOP_FILE"

	if command -v desktop-file-validate >/dev/null 2>&1; then
		desktop-file-validate "$DESKTOP_FILE" || log "WARNING: desktop-file-validate complained (non-fatal)"
	fi

	# The old ~/.local/bin/ampersand shim is obsolete (the entry itself runs in
	# a terminal now) — remove it if a previous build created one.
	rm -f "${HOME}/.local/bin/ampersand"

	if command -v update-desktop-database >/dev/null 2>&1; then
		update-desktop-database "$(dirname "$DESKTOP_FILE")" >/dev/null 2>&1 || true
	fi
	if command -v kbuildsycoca6 >/dev/null 2>&1; then
		kbuildsycoca6 --noincremental >/dev/null 2>&1 || true
	fi

	log "Installed:"
	log "  app:      $INSTALL_TARGET"
	log "  desktop:  $DESKTOP_FILE"
	log "  icon:     $ICON_TARGET"

	# A kmenuedit layout can hide the entry even when the .desktop file is
	# valid (an <Exclude> wins over submenu placement). kmenuedit itself
	# has no scriptable surface (its MenuFile reader is compiled into the
	# GUI), so fix its file directly: drop our filename from <Exclude>
	# blocks at any level and pin it in the Development submenu's
	# Layout + Include. The cache rebuild below then picks the change up.
	MENU_LAYOUT="${HOME}/.config/menus/applications-kmenuedit.menu"
	if [[ -f "$MENU_LAYOUT" ]]; then
		MENU_STATE="$(python3 - "$MENU_LAYOUT" "Ampersand.desktop" <<'PYEOF' 2>/dev/null
import shutil
import sys
import xml.etree.ElementTree as ET

path, target = sys.argv[1], sys.argv[2]

with open(path, encoding="utf-8") as f:
    original = f.read()

try:
    root = ET.fromstring(original)
except Exception as e:
    print(f"unparseable: {e}")
    sys.exit(0)

def local(tag):
    return tag.rsplit("}", 1)[-1]

changed = []

# 1. Our file must not be excluded anywhere: a root <Exclude> hides it
# menu-wide, a submenu one hides it there. Empty leftovers go too.
for parent in root.iter():
    for child in list(parent):
        if local(child.tag) != "Exclude":
            continue
        for fn in list(child):
            if local(fn.tag) == "Filename" and (fn.text or "").strip() == target:
                child.remove(fn)
                changed.append("unexcluded")
        if len(list(child)) == 0:
            parent.remove(child)

# 2. The Development submenu must list it (Layout + Include), else it can
# vanish from there. Never create submenus — Categories covers defaults.
for child in root:
    if local(child.tag) != "Menu":
        continue
    name = next(((el.text or "").strip() for el in child if local(el.tag) == "Name"), "")
    if name != "Development":
        continue
    for section in ("Layout", "Include"):
        sec = next((el for el in child if local(el.tag) == section), None)
        if sec is None:
            continue
        if not any(local(fn.tag) == "Filename" and (fn.text or "").strip() == target for fn in sec):
            ET.SubElement(sec, "Filename").text = target
            changed.append(section.lower())

if not changed:
    print("clean")
    sys.exit(0)

shutil.copy2(path, path + ".bak")
ET.indent(root, space=" ")
body = ET.tostring(root, encoding="unicode")
head = []
for line in original.splitlines(True):
    s = line.strip()
    if s.startswith("<?xml") or s.startswith("<!DOCTYPE"):
        head.append(line)
    elif s == "":
        continue
    else:
        break
with open(path, "w", encoding="utf-8") as f:
    f.writelines(head)
    f.write(body)
    if not body.endswith("\n"):
        f.write("\n")
print("fixed:" + ",".join(sorted(set(changed))))
PYEOF
)"
		case "$MENU_STATE" in
			fixed:*) log "Menu layout hid Ampersand.desktop — fixed (${MENU_STATE#fixed:}; backup at ${MENU_LAYOUT}.bak)." ;;
			unparseable*) log "WARNING: could not parse ${MENU_LAYOUT} (${MENU_STATE#unparseable: }) — menu visibility unknown." ;;
		esac
	fi
fi

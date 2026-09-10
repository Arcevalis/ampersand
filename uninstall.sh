#!/usr/bin/env bash
# uninstall.sh
# Reverses a ./install-appimage.sh installation: removes the installed
# AppImage, its .desktop entry and icon, drops it from the Development menu
# and refreshes the menu caches. Build outputs in the source dir
# (./Ampersand-*.AppImage, publish/) are left alone.
#
# Usage:
#   ./uninstall.sh [OPTIONS]
#
# Options:
#   --install-dir DIR     Where the AppImage was installed
#                         (default: $HOME/Applications)
#   --name FILE           Installed AppImage filename
#                         (default: Ampersand-x86_64.AppImage)
#   --keep-menu           Leave the kmenuedit layout entries in place
#   -h, --help            Show this help
#
# Examples:
#   ./uninstall.sh
#   ./uninstall.sh --install-dir ~/Apps --name Ampersand.AppImage
#
# Notes:
#   - Refuses to remove a running AppImage (ETXTBSY guard, same as install).
#   - The .desktop file is only removed if it belongs to this install
#     (its Exec points at the install target); a foreign file is kept
#     with a warning.
#   - The menu layout is backed up to applications-kmenuedit.menu.bak.

set -euo pipefail

# ── Defaults ────────────────────────────────────────────────────────────────
INSTALL_DIR="${HOME}/Applications"
APP_NAME="Ampersand-x86_64.AppImage"
DO_MENU=1

# ── Argument parsing ─────────────────────────────────────────────────────────
while [[ $# -gt 0 ]]; do
	case "$1" in
		--install-dir) INSTALL_DIR="$2"; shift 2 ;;
		--name)        APP_NAME="$2";    shift 2 ;;
		--keep-menu)   DO_MENU=0;        shift ;;
		-h|--help)
			sed -n '2,/^set -/p' "$0" | grep '^#' | sed 's/^# \{0,1\}//'
			exit 0
			;;
		*) echo "Unknown option: $1" >&2; exit 1 ;;
	esac
done

# ── Helpers ──────────────────────────────────────────────────────────────────
log()  { echo "[uninstall] $*"; }
die()  { echo "[uninstall] ERROR: $*" >&2; exit 1; }

# Refuse to remove a running executable: quit it first, then uninstall.
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

# ── Step 1: installed AppImage ───────────────────────────────────────────────
INSTALL_TARGET="${INSTALL_DIR}/${APP_NAME}"
die_if_running "$INSTALL_TARGET"
if [[ -f "$INSTALL_TARGET" ]]; then
	log "Removing AppImage → ${INSTALL_TARGET}..."
	rm -f "$INSTALL_TARGET"
else
	log "AppImage not present, skipping: ${INSTALL_TARGET}"
fi

# ── Step 2: desktop entry (only if it is ours) ───────────────────────────────
DESKTOP_FILE="${HOME}/.local/share/applications/Ampersand.desktop"
if [[ -f "$DESKTOP_FILE" ]]; then
	if grep -Fq "$INSTALL_TARGET" "$DESKTOP_FILE" || grep -Fq "$APP_NAME" "$DESKTOP_FILE"; then
		log "Removing desktop entry → ${DESKTOP_FILE}..."
		rm -f "$DESKTOP_FILE"
	else
		log "WARNING: ${DESKTOP_FILE} does not point at ${INSTALL_TARGET}, leaving it alone."
	fi
else
	log "Desktop entry not present, skipping."
fi

# ── Step 3: icon ─────────────────────────────────────────────────────────────
ICON_TARGET="${HOME}/.local/share/icons/hicolor/256x256/apps/ampersand.png"
if [[ -f "$ICON_TARGET" ]]; then
	log "Removing icon → ${ICON_TARGET}..."
	rm -f "$ICON_TARGET"
	rmdir -p "$(dirname "$ICON_TARGET")" 2>/dev/null || true
else
	log "Icon not present, skipping."
fi

# Stale shim from older installs (harmless if absent).
rm -f "${HOME}/.local/bin/ampersand"

# ── Step 4: menu layout ──────────────────────────────────────────────────────
# Drop our file from the Development submenu's Layout + Include so no
# dangling entry stays behind. kmenuedit owns this file and exposes no
# scriptable API, so edit the parsed XML directly (with backup).
MENU_LAYOUT="${HOME}/.config/menus/applications-kmenuedit.menu"
if [[ $DO_MENU -eq 0 ]]; then
	: # --keep-menu: layout untouched.
elif [[ ! -f "$MENU_LAYOUT" ]]; then
	: # No KDE menu layout (e.g. GNOME/XFCE); nothing to clean.
elif ! command -v python3 >/dev/null 2>&1; then
	log "WARNING: python3 not found, menu layout left untouched."
else
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
for child in root:
    if local(child.tag) != "Menu":
        continue
    name = next(((el.text or "").strip() for el in child if local(el.tag) == "Name"), "")
    if name != "Development":
        continue
    for section in ("Layout", "Include", "Exclude"):
        sec = next((el for el in child if local(el.tag) == section), None)
        if sec is None:
            continue
        for fn in list(sec):
            if local(fn.tag) == "Filename" and (fn.text or "").strip() == target:
                sec.remove(fn)
                changed.append(section.lower())
        if local(sec.tag) == "Exclude" and len(list(sec)) == 0:
            child.remove(sec)

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
print("removed:" + ",".join(sorted(set(changed))))
PYEOF
)"
	case "$MENU_STATE" in
		removed:*) log "Dropped Ampersand.desktop from Development menu (${MENU_STATE#removed:}; backup at ${MENU_LAYOUT}.bak)." ;;
		unparseable*) log "WARNING: could not parse ${MENU_LAYOUT} (${MENU_STATE#unparseable: }), left untouched." ;;
	esac
fi

# ── Step 5: refresh caches ───────────────────────────────────────────────────
# Same optional-tool policy as install: refresh whatever the running
# desktop provides, assume nothing.
if command -v update-desktop-database >/dev/null 2>&1; then
	update-desktop-database "$(dirname "$DESKTOP_FILE")" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
	gtk-update-icon-cache -f -t "${HOME}/.local/share/icons/hicolor" >/dev/null 2>&1 || true
fi
for kbuild in kbuildsycoca6 kbuildsycoca5; do
	if command -v "$kbuild" >/dev/null 2>&1; then
		"$kbuild" --noincremental >/dev/null 2>&1 || true
		break
	fi
done

log "Uninstalled."

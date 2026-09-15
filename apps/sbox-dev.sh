#!/bin/sh
# Launch the s&box editor. With no -project it opens the project menu.
#
# ampersand: name=Editor (sbox-dev)
# ampersand: runtime=optional
#
# sbox-dev with no -project starts sbox-launcher as a separate process and
# returns 0 immediately (engine/Launcher/SboxDev/Launcher.cs:23-36). That is
# intended - the launcher reads to pipe EOF rather than waiting on this pid.

set -eu

. "$( dirname "$0" )/_common.sh"

sbox_exec sbox-dev "$@"

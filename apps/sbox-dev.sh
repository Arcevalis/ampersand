#!/bin/sh
# Launch the s&box editor. With no -project it opens the project menu.
#
# ampersand: name=Editor (sbox-dev)
# ampersand: runtime=optional
#
# sbox-dev with no -project starts sbox-launcher as a separate process and
# returns 0 immediately (engine/Launcher/SboxDev/Launcher.cs:23-36). That is
# intended - the launcher reads to pipe EOF rather than waiting on this pid.
#
# SBOX_DETACH=1 (see _common.sh sbox_exec): the instant exit would close the
# terminal tab while the launcher is still booting, and the closing tab's
# SIGHUP would kill it pre-log with no window and no error anywhere. sbox_exec
# answers by running us in our own session (setsid, no controlling terminal)
# and - on a terminal - waiting for the whole detached session (hub and/or the
# editor the hub spawns as its child), so the console auto-closes on quit.
# The game/server scripts must NOT arm this - they run in-process and want the
# tab's session.
#
# In-process modes (-project/-test/-generatesolution run the editor here, no
# detach) leave SBOX_DETACH off: plain exec, terminal session untouched, so
# Ctrl+C keeps working for hand-runners.

set -eu

. "$( dirname "$0" )/_common.sh"

_detach=1
for _a in "$@"; do
	case "$_a" in
		-project|-test|-generatesolution)
			_detach=0
			break
			;;
	esac
done
export SBOX_DETACH=$_detach
sbox_exec sbox-dev "$@"

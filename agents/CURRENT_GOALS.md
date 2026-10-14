SESSION START? YES!

Hello! Thank you for your time we're so excited you're working for us! Happy happy!

All tasks are DONE and WORKED ON by SUBAGENTS. PEER Review is ALSO done by SUBAGENTS. You are simply their task master.

Upon the completion of each task, a SUBAGENT shall write a git commit and push. "current_goal.md" file is included in that commit.

References to variables like X and Y automatically imply that these are to be editor exposed properties for developer tweaking.

Skills for you and subagents:
	box-agent-playtest
	sbox-live-play-agent-loop
	sbox-cli-driving

Here's the next step:

	Task 0: DONE? YES!
		In docs/notes.md it mentions a problem, fix that problem, following solution C.
		
Notes:

Clarification Questions and Answers:

Parent decisions (change if wrong):
	- SESSION START? was already YES!, so no questions were asked up front.
	- "Solution C" is docs/notes.md, Candidate fixes, item C: ampersand hands the editor DOTNET_ROOT (the engine's game/bin/dotnet) and DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU (the ICU version shipped beside CoreCLR there), so the editor's own sbox-cli calls (Preferences > AI Agents) work inside the Steam runtime.
	- Both variables are exported by apps/_common.sh (sbox_exec) only for Steam runtime launches (SBOX_IN_STEAMRT4=1), always pointing at the engine's runtime, even when Steam's environment carries its own DOTNET_ROOT. Host launches stay untouched: the system .NET already works there, and an app-local ICU value leaking into the editor's children (IDEs, other .NET tools) could break them.
	- No separate --env addition: SteamRt4Runtime has no allowlist (--env is just the env LaunchComposer.PrepareRuntime composes), and the launch script plus the _common.sh it sources already run inside the container, so values exported there need not cross the boundary.
	- The ICU lookup moves from apps/sbox-cli.sh into one shared _common.sh helper; sbox-cli.sh behaves as before.
	- Covers the sbox-dev and sbox (game) scripts. sbox-server.sh does not source _common.sh and the server has no AI Agents page, so it is left alone.
	- Engine-side fixes A/B and the "log the non-zero exit" note stay out (engine repo, not ampersand).
	- Verification: (1) container gate: a fake s&box tree whose game/sbox-dev runs the real sbox-cli --skill-status, launched by ampersand --launch through the real Steam runtime; must exit 0 with the new scripts and fail (131) with the old ones. (2) Only if none of your editors are running at that moment: one child launches a real editor on a throwaway project (--launch --wait-ready), runs sbox-cli --skill-status from inside it, and stops only that editor (last session's A4 policy). The Preferences > AI Agents check from the hub picker is left for you.
	- Docs: the notes.md section is marked fixed with what changed; the README troubleshooting row and the HeadlessCli/LaunchComposer comments that say DOTNET_ROOT is deliberately kept out of _common.sh are updated.
	- Push: origin is now cseliot/ampersand and this doc says push, so the task commit goes to origin/pr-cli-integratins (last session's local-only answer was for the arcevalis remote).
	- The commit includes agents/CURRENT_GOALS.md, as this doc says, but not agents/CURRENT_GOALS.md.template.bak: those edits are yours, not Task 0.
	- After the change, bin/Release is rebuilt (only while the Ampersand window is not running) so the window picks up the new scripts.
	- Review findings: accepted the self-contradicting _common.sh header entry, the fix-A-only verify steps in notes.md (a fix C step was added), and a false "host run uses the system .NET" comment in sbox-cli.sh. Rejected: rewording the old candidate-C text (kept as history), first-match ICU glob order (pre-existing, only one runtime ships).


IGNORE: <</goal Update ampersand project state according to document /agents/current_goals.md. If "SESSION START?" is "NO!" then consider best clarifying questions, ask them, and record them below and after that update "SESSION START?" to "YES!" >>

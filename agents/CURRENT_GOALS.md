SESSION START? YES!

Hello! Thank you for your time we're so excited you're working for us! Happy happy!

All tasks are DONE and WORKED ON by SUBAGENTS. PEER Review is ALSO done by SUBAGENTS. You are simply their task master.

Upon the completion of each task, a SUBAGENT shall write a git commit and push.

References to variables like X and Y automatically imply that these are to be editor exposed properties for developer tweaking.

Skills for you and subagents:
	box-agent-playtest
	sbox-live-play-agent-loop
	sbox-cli-driving

Here's the next step:

	Task 0: DONE? YES!
		SBOX MCP has recently been replaced with an sbox-cli. Integrate ampersand's new terminal command features added into this repo with the sbox-cli.

	Task 1: DONE? YES!
		Update all and any research, skills, and memory that refers to the sbox mcp with this new fact that sbox cli has replaced it.

	Task 2: DONE? YES!
		Launching sbox launcher manually via ampersand gui (and then, launching a project) shows the editor reporting (inside Preferences > agents) that the sbox-cli wasn't available. Investigate a fix. Do NOT fix.
	
		
Notes:

Clarification Questions and Answers:

Q1 (Task 0): What should integrating ampersand's --launch/--stop with sbox-cli cover?
A1: All three code items:
	(a) --launch --project gets an opt-in --wait-ready that polls 'sbox-cli --call editor_status' until the editor answers (replaces the old port-7269 readiness check);
	(b) ampersand launches export FACEPUNCH_ENGINE so sbox-cli and the editor's agent-skill code find the engine;
	(c) new 'ampersand --cli -- ARGS' that runs sbox-cli with ampersand's launch env/container, so sbox-cli --project/--game get the same setup.
	Not chosen: docs-only.

Q2 (Task 1): Which places should the MCP-to-sbox-cli update cover?
A2: hermes-sbox skills + memory; 1-SBox 3-Documentation/3-Research notes; project docs in 2-Projects clones (separate repos, separate commits). Not the default Hermes profile.

Q3 (Task 1): How should the MCP content be treated?
A3: Replace sbox-editor-mcp-driving with a new sbox-cli skill that keeps only our Linux/ampersand specifics and points to the engine-shipped sbox-cli skill for the tool catalog. Research notes get a "superseded by sbox-cli" banner, not a rewrite.

Q4 (all tasks): May subagents run real editors?
A4: Yes, but only when none of the user's editors are running: launch via ampersand --launch, stop only what they launched.

Q5 (all tasks): Push policy?
A5: ampersand: commit locally only (user opens the PR). 1-SBox: push (includes the user's 7 unpushed commits on main).

Q6 (Task 1): The 1-SBox root push was rejected (origin/main has a79eed0, README only; local ahead 8 / behind 1). How to reconcile?
A6: git pull --rebase, then push.

Q7 (Task 1): sboxgamejam3-B-Branch is 13 behind origin/main and its doc commit 7a16280 duplicates 3e5cba1 (already pushed from the main clone). What to do with it?
A7: Leave 7a16280 local and the clone as is; the user syncs it.

Q8 (Task 0): Root cause for the --cli failures: the Steam Linux Runtime 4.0 container entry gives the container stdin = /dev/null and forwards no other fd, so sbox-cli's stdin modes (MCP server mode, owned --project/--game launches) cannot work inside it; one-shot --call can. A1(c)'s "owned launches get the container setup" is therefore impossible. How should --cli behave?
A8: Run sbox-cli host-side for attach modes; --cli refuses --project/--game and points to `ampersand --launch ... --wait-ready` then `ampersand --cli -- --connect`.

Parent decisions (change if wrong):
	- "box-agent-playtest" is read as sbox-agent-playtest.
	- Order: Task 0 and Task 2 run in parallel (Task 2 is read-only and owns no source files). Task 1 runs after Task 0 lands, so the new sbox-cli skill documents --wait-ready, --cli and FACEPUNCH_ENGINE.
	- Task 2 findings go in ampersand docs/notes.md (no code fix), committed locally with Task 2.
	- One live-editor driver at a time. Each checks 'pgrep -af game/sbox-dev' first and skips the live part (reports it as pending for you) if any editor is running.
	- 2-Projects clones: commit per repo; push only when that branch is otherwise in sync with its remote, else commit locally and report.
	- Memory edits are applied by the parent (subagents cannot write memory).
	- This goal doc (agents/) stays untracked; it is not committed.
	- Task 1 (decided after the inventory): the new skill is named sbox-cli-driving and sbox-editor-mcp-driving is deleted once it exists. Research notes live in the separate 3-Documentation repo (in sync with origin): banner commits there are pushed; 3-Research/rider-mcp.md is skipped because it holds your uncommitted edits. Investigate-MCP.md (1-SBox root) gets the banner too. In sboxgamejam3{,-B-Branch}/CURRENT_GOAL.md only the skill name is renamed; the .bak copies are left alone. kingofgamesmuseum is 1 commit ahead of origin, so it is committed locally only; unityimporterplus has no commits yet, so it is edited but not committed; securitytesting and settings-gui have no git.


IGNORE: <</goal Update ampersand project state according to document CURRENT_GOALS.md. If "SESSION START?" is "NO!" then consider best clarifying questions, ask them, and record them below and after that update "SESSION START?" to "YES!" >>

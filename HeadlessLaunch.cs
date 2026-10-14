using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Ampersand;

/// <summary>
/// <c>ampersand --launch TARGET [options] [-- ARGS...]</c>: the window's play
/// button without the window, for scripts and agents - e.g. reopening the
/// editor on a project after it crashed.
///
/// The command is composed by the same LaunchComposer calls the window makes
/// (script lookup, env, casefold shim, Steam runtime container, server game),
/// with the persisted global toggles from settings.json standing in for the
/// window's checkboxes. Casefold and Input debug come from the saved settings;
/// the Steam runtime is always on, because the window's per-target checkbox is
/// not persisted and every launch an agent makes should match the
/// containerised setup the engine expects. --terminal is the only per-target
/// flag.
///
/// Differences from the window, all because nobody is there to click:
///  - a missing or stale casefold shim is built without asking;
///  - failures print "ampersand: TITLE: MESSAGE" on stderr and exit 1
///    instead of opening a dialog;
///  - the launch is detached (setsid, stdio off our pipes) and this process
///    exits after a short grace period that only catches immediate failures
///    (see TryDetach). The caller's terminal or agent tool can go away
///    without taking the engine with it, which is the point;
///  - --wait-ready opts out of that last shortcut: the spawned process is kept
///    here, and this process only returns once a host-side sbox-cli has asked
///    the editor for editor_status and been answered (see WaitUntilReady).
///    A caller that needs a usable editor, not merely a started one, uses it.
///
/// Editor note: sbox-dev with no -project starts the sbox launcher (project
/// picker) and exits; with --project it opens that project directly, the same
/// hand-off the picker itself does (engine/Launcher/SboxDev/Launcher.cs and
/// LauncherWindow.LaunchProject). A picker already running elsewhere is not
/// involved and is not needed.
/// </summary>
internal static class HeadlessLaunch
{
	public const string Argument = "--launch";

	private const string Usage =
		"usage: ampersand --launch TARGET [--project FILE.sbproj] [--wait-ready [--ready-timeout SECONDS]] [--terminal] [-- ARGS...]\n"
		+ "\n"
		+ "  TARGET            sbox | sbox-dev | sbox-server\n"
		+ "  --project FILE    sbox-dev only: open this .sbproj directly instead of the project picker\n"
		+ "  --wait-ready      sbox-dev only (needs --project): after launching, poll sbox-cli until\n"
		+ "                    the editor answers editor_status, then print one more line. Keeps this\n"
		+ "                    process (and its exit code) until then.\n"
		+ "  --ready-timeout SECONDS\n"
		+ "                    how long --wait-ready may take, in seconds (1..3600, default 300)\n"
		+ "  --terminal        run in your terminal emulator (default here: background, log file only)\n"
		+ "  -- ARGS...        passed through to the launch script, after any --project/+game\n"
		+ "\n"
		+ "The Steam Linux Runtime 4.0 container is always used (Steam must be running) unless\n"
		+ "the script's header declares runtime=never.\n"
		+ "Casefold and input debug come from settings.json, as set in the window. sbox-server\n"
		+ "gets +game from the saved server game. Watches the launch for 5 s to catch immediate\n"
		+ "failures (exit 1 with the log tail), then prints the log path and exits 0. With\n"
		+ "--wait-ready it holds on instead and polls sbox-cli every 2 s until the editor answers\n"
		+ "editor_status (then one more line on stdout, exit 0), gives up after --ready-timeout\n"
		+ "seconds (exit 1), or notices the launch dying first. Without it, it does not wait for\n"
		+ "the editor to finish loading. The engine outlives this process and its terminal.\n"
		+ "With --terminal the output stays in the terminal window (not the log), and a failed run\n"
		+ "holds that window open (press Enter to close) instead of exiting 1.\n"
		+ "\n"
		+ "ampersand --stop TARGET [--project FILE.sbproj]   stop a running target without the window";

	public static int Run( string[] args )
	{
		if ( !TryParse( args, out var options, out var usageError ) )
		{
			if ( usageError is not null )
			{
				Console.Error.WriteLine( "ampersand: " + usageError );
				Console.Error.WriteLine( Usage );
				return 2;
			}

			Console.WriteLine( Usage );
			return 0;
		}

		LaunchError? error;
		string? logPath = null;
		string? readyLine = null;
		var mode = "";
		var notes = new List<string>();

		try
		{
			error = Launch( options!, out logPath, out mode, out notes, out readyLine );
		}
		catch ( Exception e )
		{
			// The window turns these into a dialog (MainWindow.Launch); here
			// they must still come out as "ampersand: ..." and exit 1, not as
			// an unhandled-exception abort a caller cannot parse.
			error = new LaunchError( "Launch failed", e.Message );
		}

		foreach ( var note in notes )
			Console.Error.WriteLine( "ampersand: warning: " + note );

		if ( error is not null )
		{
			Console.Error.WriteLine( "ampersand: " + error.Title + ": " + error.Message.Trim().Replace( "\n\n", "\n" ) );
			if ( logPath is not null )
				Console.Error.WriteLine( "log: " + logPath );
			return 1;
		}

		Console.WriteLine( "launched " + options!.Target + " (" + mode + ")" );
		Console.WriteLine( "log: " + logPath );
		if ( readyLine is not null )
			Console.WriteLine( readyLine );
		return 0;
	}

	private sealed class Options
	{
		public string Target = "";
		public string ScriptFile = "";
		public string? Project;
		public bool Terminal;
		public bool WaitReady;

		/// <summary>--ready-timeout, in seconds; only read when <see cref="WaitReady"/>.</summary>
		public int ReadyTimeout = DefaultReadyTimeout;

		/// <summary>True when --ready-timeout was given at all, for the usage check.</summary>
		public bool ReadyTimeoutGiven;

		public List<string> PassThrough = new();
	}

	/// <summary>
	/// False with <paramref name="error"/> null means help was asked for.
	/// </summary>
	private static bool TryParse( string[] args, out Options? options, out string? error )
	{
		options = null;
		error = null;

		// args[0] is --launch itself.
		if ( args.Length < 2 || args[1] is "-h" or "--help" )
		{
			if ( args.Length < 2 )
				error = "missing TARGET";
			return false;
		}

		var target = LaunchComposer.Targets.FirstOrDefault( t => t.Name == args[1] );
		if ( target.Name is null )
		{
			error = "unknown TARGET '" + args[1] + "' (expected "
				+ string.Join( ", ", LaunchComposer.Targets.Select( t => t.Name ) ) + ")";
			return false;
		}

		var parsed = new Options { Target = target.Name, ScriptFile = target.ScriptFile };

		for ( var i = 2; i < args.Length; i++ )
		{
			switch ( args[i] )
			{
				case "--":
					parsed.PassThrough.AddRange( args.Skip( i + 1 ) );
					i = args.Length;
					break;

				case "--project":
					if ( parsed.Project is not null )
					{
						error = "--project given twice";
						return false;
					}
					if ( i + 1 >= args.Length || string.IsNullOrWhiteSpace( args[i + 1] ) || args[i + 1].StartsWith( '-' ) )
					{
						error = "--project needs a .sbproj path";
						return false;
					}
					parsed.Project = args[++i];
					break;

				case "--terminal":
					parsed.Terminal = true;
					break;

				case "--wait-ready":
					parsed.WaitReady = true;
					break;

				case "--ready-timeout":
					if ( i + 1 >= args.Length || args[i + 1].StartsWith( '-' ) )
					{
						error = "--ready-timeout needs a number of seconds (1..3600)";
						return false;
					}
					if ( !int.TryParse( args[++i], out var seconds ) || seconds < 1 || seconds > 3600 )
					{
						error = "--ready-timeout must be 1..3600 seconds";
						return false;
					}
					parsed.ReadyTimeout = seconds;
					parsed.ReadyTimeoutGiven = true;
					break;

				case "-h":
				case "--help":
					return false;

				default:
					error = "unknown option '" + args[i] + "' (script arguments go after --)";
					return false;
			}
		}

		if ( parsed.Project is not null && parsed.Target != "sbox-dev" )
		{
			error = "--project only applies to sbox-dev";
			return false;
		}

		// The poller asks the editor that --project opened for editor_status, so
		// a launch without one (the project picker) has nothing to wait for.
		if ( parsed.WaitReady && parsed.Project is null )
		{
			error = "--wait-ready needs --project";
			return false;
		}

		if ( parsed.ReadyTimeoutGiven && !parsed.WaitReady )
		{
			error = "--ready-timeout only applies with --wait-ready";
			return false;
		}

		options = parsed;
		return true;
	}

	private static LaunchError? Launch( Options options, out string? logPath, out string mode, out List<string> notes, out string? readyLine )
	{
		logPath = null;
		readyLine = null;
		mode = "";
		notes = new List<string>();

		var root = SboxSettings.Resolve();
		if ( root is null || !SboxSettings.IsValid( root ) )
		{
			var stale = SboxSettings.GetStalePersistedPath();
			return new LaunchError( "Repo root not found", !string.IsNullOrWhiteSpace( stale )
				? $"Saved s&box path is no longer valid: {stale}\nExpected a folder containing game/ and engine/ with game/sbox."
				: "could not locate the s&box tree - set it once in the window (S&BOX LOCATION)." );
		}

		// --wait-ready has nothing to ask if sbox-cli is not there, and finding
		// that out before the editor spends a minute booting is the whole point.
		// sbox-cli ships with the engine build, not with ampersand.
		if ( options.WaitReady && !File.Exists( LaunchComposer.CliPath( root ) ) )
			return new LaunchError( "sbox-cli not found",
				"--wait-ready needs sbox-cli, but there is no file at "
					+ LaunchComposer.CliPath( root ) + ".\n\n"
					+ "Run Build S&Box to build it, or launch without --wait-ready." );

		string? project = null;
		if ( options.Project is not null )
		{
			project = ResolveProjectPath( options.Project );
			if ( !project.EndsWith( ".sbproj", StringComparison.OrdinalIgnoreCase ) || !File.Exists( project ) )
				return new LaunchError( "Project not found", "no .sbproj file at " + project );
		}

		var script = LaunchComposer.FindScript( root, options.ScriptFile, out var scriptError );
		if ( script is null )
			return scriptError;

		var useRuntime = ScriptMetadata.Read( script ).Runtime != RuntimeMode.Never;

		var casefold = SboxSettings.GetCasefold();
		if ( casefold && options.ScriptFile != LaunchComposer.ServerScript )
		{
			var shimError = EnsureCasefoldShim( notes );
			if ( shimError is not null )
				return shimError;
		}

		var env = LaunchComposer.BaseEnvironment( root, casefold, SboxSettings.GetInputDebug() );
		var command = new List<string>();

		if ( useRuntime )
		{
			if ( !LaunchComposer.PrepareRuntime( root, script, command, env, null, out var runtimeError, out var runtimeNotes ) )
				return runtimeError;
			if ( runtimeNotes.Count > 0 )
				notes.Add( "no Qt shim (" + string.Join( "; ", runtimeNotes ) + ")" );
		}
		else
		{
			command.Add( script );
		}

		LaunchComposer.AppendServerGame( options.ScriptFile, command, SboxSettings.GetSboxServerGame() );

		if ( project is not null )
		{
			command.Add( "-project" );
			command.Add( project );
		}

		command.AddRange( options.PassThrough );

		logPath = RunLog.CreateLogPath( options.Target );
		RunLog.WriteCommandHeader( logPath, command, root, env );

		// Terminal: same as the window - the command is wrapped in a
		// stay-open-on-failure hold and the emulator spawns directly, so the
		// child keeps the pty and interactive input works; the log keeps the
		// command header plus a note that the output is not captured here.
		// Background: the command writes straight into the log.
		IReadOnlyList<string> argv = command;
		var output = logPath;
		var where = "background";
		IReadOnlyDictionary<string, string>? detachEnv = env;
		IReadOnlyList<string>? scrubbedNames = null;

		if ( options.Terminal )
		{
			var held = SystemTerminal.WithHold( command, options.Target );

			if ( SystemTerminal.TryBuild( held, root, env, out var terminalArgv, out var emulator, out var termScrubbed ) )
			{
				// Only on success: a fallback-to-background log must not carry
				// a "not captured" note for output that is captured.
				RunLog.WriteTerminalNote( logPath );
				argv = terminalArgv;
				output = "/dev/null";
				where = emulator!;
				detachEnv = null;
				scrubbedNames = termScrubbed;
			}
			else
			{
				notes.Add( "no terminal emulator found - logging to file instead." );
			}
		}

		// The readiness clock starts here, at the launch itself, not when
		// WaitUntilReady is called: the grace period TryDetach is about to
		// spend watching the launch counts against the timeout.
		var clock = Stopwatch.StartNew();

		if ( !TryDetach( argv, root, detachEnv, output, logPath, scrubbedNames, out var problem, out var launched ) )
			return new LaunchError( "Launch failed", problem );

		mode = ( useRuntime ? "steam runtime, " : "host, " ) + where;

		try
		{
			if ( options.WaitReady )
			{
				// Until this returns, this process is the only one left holding
				// the launch: the engine is detached and outlives us, so a
				// failure here leaves a possibly-booting editor behind for the
				// caller to stop.
				var waitError = WaitUntilReady( launched!, root, project!, useRuntime, logPath, clock, options.ReadyTimeout, out var seconds );
				if ( waitError is not null )
					return waitError;

				readyLine = "ready (sbox-cli editor_status answered after " + seconds + " s)";
			}

			return null;
		}
		finally
		{
			// TryDetach hands the live process back instead of disposing it at
			// the end of the grace period, because --wait-ready watches it in
			// between. Releasing our handle does not touch the detached engine.
			launched?.Dispose();
		}
	}

	/// <summary>
	/// Normalises a --project value into an absolute path. The launch script
	/// cd's into game/ before exec, so a relative path would resolve against
	/// the wrong directory - pin it to ours now. A leading ~ is expanded too:
	/// a caller that execs us without a shell (an agent tool) passes it
	/// literally. HeadlessStop reuses this so a --stop --project matches the
	/// path the editor was launched with.
	/// </summary>
	internal static string ResolveProjectPath( string raw )
	{
		var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		if ( raw == "~" )
			raw = home;
		else if ( raw.StartsWith( "~/", StringComparison.Ordinal ) )
			raw = Path.Combine( home, raw[2..] );

		return Path.GetFullPath( raw );
	}

	/// <summary>
	/// The window's EnsureCasefoldShimAsync, minus the confirmation dialog.
	/// </summary>
	private static LaunchError? EnsureCasefoldShim( List<string> notes )
	{
		switch ( LaunchComposer.CheckCasefoldShim( out var so, out var src ) )
		{
			case CasefoldShimState.Ready:
				return null;

			case CasefoldShimState.Unavailable:
				return new LaunchError( "Casefold shim unavailable",
					"Casefold is on, but there is no library at " + so
						+ " and no casefold.c to build it from. Rebuild ampersand, or turn Casefold off in the window." );
		}

		notes.Add( "casefold shim missing or older than its source - building " + so );
		return LaunchComposer.BuildCasefoldShim( so, src! );
	}

	/// <summary>--ready-timeout when the caller does not give one.</summary>
	private const int DefaultReadyTimeout = 300;

	/// <summary>How often --wait-ready asks the editor for editor_status.</summary>
	private const int ReadyPollMilliseconds = 2000;

	/// <summary>
	/// How long a single poll may sit before it is killed and counted as not
	/// ready. The probe itself returns as soon as sbox-cli can tell, so a slow
	/// one means sbox-cli is stuck, not that the editor is busy.
	/// </summary>
	private const int ReadyProbeMilliseconds = 15000;

	/// <summary>
	/// --wait-ready's loop: poll the editor for editor_status every
	/// <see cref="ReadyPollMilliseconds"/> until it answers, the launch dies,
	/// or <paramref name="timeoutSeconds"/> have passed since the launch.
	/// Returns null when ready (with <paramref name="seconds"/> set to how long
	/// it took), or the error to print otherwise.
	///
	/// The clock is the one started in Launch, immediately before TryDetach, not
	/// one started here: the grace period TryDetach already spent counts against
	/// the timeout - a launch that dies after four seconds has four fewer seconds
	/// to answer.
	/// </summary>
	private static LaunchError? WaitUntilReady(
		Process launched,
		string root,
		string project,
		bool useRuntime,
		string logPath,
		Stopwatch clock,
		int timeoutSeconds,
		out int seconds )
	{
		seconds = 0;

		var cli = LaunchComposer.CliPath( root );
		string? last = null;

		while ( true )
		{
			// Checked before the poll because a dead launch is the more useful
			// answer, and because a process the caller has already waited out
			// would otherwise keep answering "not found" for the whole timeout.
			if ( launched.HasExited )
				return EditorExitError( launched, logPath );

			if ( ProbeEditorStatus( cli, project, useRuntime, out var stderrLine ) )
			{
				seconds = (int)Math.Round( clock.Elapsed.TotalSeconds );
				return null;
			}

			if ( !string.IsNullOrWhiteSpace( stderrLine ) )
				last = stderrLine.Trim();

			if ( clock.Elapsed.TotalSeconds >= timeoutSeconds )
			{
				return new LaunchError( "Not ready",
					"no answer from sbox-cli editor_status within " + timeoutSeconds + " s"
						+ " (last: " + ( last ?? "nothing on sbox-cli's stderr" ) + ").\n\n"
						+ "The editor was left running - stop it with: ampersand --stop sbox-dev --project "
						+ project + ".\n\n"
						+ "If it is up, check Project Settings > AI > Enable MCP Communication." );
			}

			Thread.Sleep( ReadyPollMilliseconds );
		}
	}

	/// <summary>
	/// The launch process died while --wait-ready was still polling it. Exit 0
	/// means the launch chain ended cleanly before the editor answered - the
	/// editor was closed, or MCP never came up before it quit. Nothing is left
	/// running to wait for or to stop, so it reads differently from a crash.
	/// </summary>
	private static LaunchError EditorExitError( Process launched, string logPath )
	{
		var code = launched.ExitCode;

		if ( code == 0 )
			return new LaunchError( "Editor exited before it answered",
				"the launch exited (code 0) before sbox-cli editor_status answered, so nothing was "
					+ "left running to wait for." );

		return new LaunchError( "Editor exited",
			"the launch exited with code " + code + " before it answered sbox-cli. Last log lines:\n"
				+ RunLog.Tail( logPath, maxLines: 12, maxChars: 2000 ) );
	}

	/// <summary>
	/// One --wait-ready poll: run sbox-cli --connect PROJECT --call
	/// editor_status and say whether the editor answered. Exit 0 is an answer
	/// (ready); exit 1 is "no running editor has that project open" and exit 2
	/// is "connected, but the tool reported an error" - both retry, and
	/// <paramref name="stderrLine"/> carries the last line of stderr either way,
	/// so a timeout can quote what sbox-cli kept saying.
	///
	/// The poll runs on this host, not inside the runtime container: the
	/// editor's MCP pipe is an AF_UNIX socket in the temp directory, and host
	/// and container share both /tmp and the pid namespace, so no container is
	/// needed to reach an editor we launched into one. That only works while
	/// both sides look in the same directory, which is why the poller runs with
	/// TMPDIR removed as well: LaunchComposer forwards the env dictionary as
	/// --env (the container never sees our TMPDIR), so a container launch means
	/// the editor used /tmp whatever ours says. A host launch inherits our
	/// environment, TMPDIR included, so there it is left alone.
	///
	/// stdin is not redirected on purpose: sbox-cli's --call mode answers and
	/// exits, and handing it a closed stdin is how its other modes are ended -
	/// there is no reason to invite that from a poll.
	/// </summary>
	private static bool ProbeEditorStatus( string cli, string project, bool useRuntime, out string? stderrLine )
	{
		stderrLine = null;

		var info = new ProcessStartInfo
		{
			FileName = cli,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};

		info.ArgumentList.Add( "--connect" );
		info.ArgumentList.Add( project );
		info.ArgumentList.Add( "--call" );
		info.ArgumentList.Add( "editor_status" );

		if ( useRuntime )
			info.Environment.Remove( "TMPDIR" );

		try
		{
			using var probe = Process.Start( info );
			if ( probe is null )
				return false;

			// Drain both pipes while it runs. sbox-cli's answer is small, but a
			// full pipe would block it and the wait below would kill a poll
			// that was doing fine.
			var stdout = probe.StandardOutput.ReadToEndAsync();
			var stderr = probe.StandardError.ReadToEndAsync();

			if ( !probe.WaitForExit( ReadyProbeMilliseconds ) )
			{
				// A poll that hangs is a not-ready poll, not a failed launch.
				// The wait after Kill is bounded too: if the kill is refused
				// or the child is stuck in a non-interruptible state, we must
				// not sit here past the poll interval.
				try { probe.Kill( entireProcessTree: true ); } catch { }
				probe.WaitForExit( 2000 );
				return false;
			}

			stderrLine = LastLine( stderr.GetAwaiter().GetResult() );

			// Read for its own sake: the answer itself is only ever "exit 0".
			_ = stdout.GetAwaiter().GetResult();

			return probe.ExitCode == 0;
		}
		catch ( Win32Exception )
		{
			// sbox-cli will not start - indistinguishable from "not up yet"
			// here, and the timeout above is what reports it.
			return false;
		}
	}

	/// <summary>The last non-blank line of <paramref name="text"/>, or null.</summary>
	private static string? LastLine( string text )
	{
		foreach ( var line in text.Split( '\n' ).Reverse() )
		{
			var trimmed = line.Trim();
			if ( trimmed.Length > 0 )
				return trimmed;
		}

		return null;
	}

	/// <summary>
	/// How long to watch the detached launch for an immediate failure (missing
	/// engine binary, script error, container refusing to start) before
	/// calling it launched. Long enough for a cold pressure-vessel start to
	/// fail; says nothing about the editor finishing its load.
	/// </summary>
	private const int GraceMilliseconds = 5000;

	/// <summary>
	/// Starts <paramref name="argv"/> in its own session with stdin from
	/// /dev/null and stdout/stderr appended to <paramref name="output"/>,
	/// then watches it for <see cref="GraceMilliseconds"/>. Still running, or
	/// exited 0 (sbox-dev with no -project hands off to the picker and
	/// exits), counts as launched; a non-zero exit inside the grace period is
	/// reported with the log tail.
	///
	/// Two modes feed the same spawn. Background passes the launch env in
	/// <paramref name="env"/>, applied to the spawned process. Terminal
	/// passes <paramref name="env"/> null and <paramref name="scrubbedNames"/>
	/// set: the launch env already travels inside the emulator's argv via
	/// env (SystemTerminal.TryBuild), so adding it here would give it to the
	/// emulator itself; those scrubbed session variables are stripped from
	/// the emulator instead, so a stale LD_PRELOAD or QT_* cannot abort it.
	///
	/// Detaching is what makes this usable from an agent or a script: the
	/// redirects happen in sh before setsid, so nothing of ours is left in the
	/// engine's fds or process group, and this process exiting, or the
	/// caller's terminal closing, cannot SIGPIPE or SIGHUP the engine. setsid
	/// does not fork here (sh is not a process group leader), so the pid we
	/// watch is the engine's own launch chain. --wait only matters if it ever
	/// does fork: it keeps the exit status visible to us.
	///
	/// The started process comes back in <paramref name="process"/> instead of
	/// being disposed here: --wait-ready keeps watching it past the grace
	/// period, and the caller disposes it. Every failure return disposes it on
	/// the way out, so the only process to release is the one that made it this
	/// far.
	/// </summary>
	private static bool TryDetach(
		IReadOnlyList<string> argv,
		string workingDirectory,
		IReadOnlyDictionary<string, string>? env,
		string output,
		string logPath,
		IReadOnlyList<string>? scrubbedNames,
		out string problem,
		out Process? process )
	{
		problem = string.Empty;
		process = null;

		var info = new ProcessStartInfo
		{
			FileName = "/bin/sh",
			WorkingDirectory = workingDirectory,
			UseShellExecute = false
		};

		info.ArgumentList.Add( "-c" );
		info.ArgumentList.Add( "out=$1; shift; exec setsid --wait \"$@\" </dev/null >>\"$out\" 2>&1" );
		info.ArgumentList.Add( "sh" );
		info.ArgumentList.Add( output );
		foreach ( var arg in argv )
			info.ArgumentList.Add( arg );

		// ProcessStartInfo starts from our own environment. A shell opened
		// from a terminal ampersand launched (konsole tabs inherit the
		// launch env) already carries SBOX_CASEFOLD / SBOX_IN_STEAMRT4 /
		// SBOX_STEAMRT4_COMPAT, and _common.sh acts on them whether or not
		// this launch set them - so a host run would pick up the container's
		// library path and Casefold would arm with the toggle off. Drop the
		// variables this launcher owns; env re-adds the ones this launch
		// wants. Unconditional: a terminal run's inner env inherits the
		// emulator's environment, so stale owned vars would reach it too.
		foreach ( var owned in LaunchComposer.OwnedVariables )
			info.Environment.Remove( owned );

		if ( env is not null )
		{
			foreach ( var pair in env )
				info.Environment[pair.Key] = pair.Value;
		}

		if ( scrubbedNames is not null )
			SystemTerminal.ScrubEnvironment( info, scrubbedNames );

		try
		{
			var started = Process.Start( info );
			if ( started is null )
			{
				problem = "/bin/sh did not start";
				return false;
			}

			try
			{
				if ( started.WaitForExit( GraceMilliseconds ) && started.ExitCode != 0 )
				{
					problem = "exited with code " + started.ExitCode + " within "
						+ GraceMilliseconds / 1000 + " s. Last log lines:\n"
						+ RunLog.Tail( logPath, maxLines: 12, maxChars: 2000 );
					started.Dispose();
					return false;
				}
			}
			catch
			{
				// Nothing may escape with the process still undisposed: the
				// caller only ever releases what this method hands back.
				started.Dispose();
				throw;
			}

			process = started;
		}
		catch ( Win32Exception e )
		{
			problem = "could not run /bin/sh: " + e.Message;
			return false;
		}

		return true;
	}
}

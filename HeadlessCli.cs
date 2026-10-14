using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Ampersand;

/// <summary>
/// <c>ampersand --cli [--] ARGS...</c>: the engine's own sbox-cli, run in this
/// terminal on the host.
///
/// sbox-cli (engine/Launcher/SboxCli) talks to a running editor or game over a
/// local socket, and its server modes speak newline-delimited JSON on
/// stdin/stdout until EOF. That rules out everything the launch modes do here:
/// no redirect to a log, no setsid, no grace period, no waiting for an editor
/// to load. This process is a transparent wrapper - sbox-cli gets the caller's
/// stdio, we wait for it, and its exit code is ours.
///
/// It runs on the host, not in the Steam Linux Runtime 4.0 container the
/// launch modes use. That entry (steam-runtime-launch-client, then
/// pressure-vessel's srt-bwrap) gives its children stdin = /dev/null and
/// forwards no other file descriptor, so sbox-cli's stdio sessions - the ones
/// that read stdin - are cancelled before they connect, and the engine
/// processes sbox-cli's own --project/--game modes spawn are killed a moment
/// later. A host sbox-cli still reaches an editor inside the container,
/// because the pid namespace and /tmp are shared and that is where the
/// editor's pipe lives. For that reason --cli refuses sbox-cli's --project and
/// --game: those would start an engine outside the container, which is not a
/// configuration this launcher supports. Use
/// <c>--launch TARGET --project P --wait-ready</c> and attach afterwards.
///
/// Composition is HeadlessLaunch's (script lookup, the persisted global
/// toggles) plus three things the CLI needs and a launch does not: DOTNET_ROOT
/// (the engine's own .NET, harmless on the host and the only option for a hand
/// run inside a container, where there is no system .NET and sbox-cli is a
/// framework-dependent apphost); SBOX_CLI_CWD, because the script changes into
/// it before exec so a relative project argument means what the caller meant;
/// and SBOX_TRACE=0, because sourcing _common.sh would otherwise trace every
/// step to stderr and pollute output a caller is parsing. Casefold is not
/// enabled here: no engine is started from --cli, so the shim has no consumer.
/// ICU is the script's business - it points
/// DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU at the ICU shipped beside the
/// engine's runtime, which a container needs and the host ignores. The child
/// environment also has TMPDIR taken away: an editor ampersand launched into
/// the container uses /tmp for its pipe, so a host sbox-cli pointed anywhere
/// else would find nothing.
///
/// Failures before sbox-cli starts print "ampersand: TITLE: MESSAGE" on stderr
/// and exit 1 (usage errors 2). Once it runs, nothing of ours is printed and
/// its exit code is passed back unchanged.
/// </summary>
internal static class HeadlessCli
{
	public const string Argument = "--cli";

	/// <summary>
	/// The script apps/sbox-cli.sh. Deliberately absent from
	/// <see cref="LaunchComposer.Targets"/>: it is not a launch target, so the
	/// window never offers it and --launch never accepts it.
	/// </summary>
	private const string ScriptFile = "sbox-cli.sh";

	private const string Usage =
		"usage: ampersand --cli [--] SBOX-CLI-ARGS...\n"
		+ "\n"
		+ "  --cli             run sbox-cli, the engine's command line bridge, in this terminal\n"
		+ "                    (foreground, for scripts and agents) instead of starting the window\n"
		+ "  --                optional separator: everything after it - or after --cli without it\n"
		+ "                    - is passed to sbox-cli unchanged\n"
		+ "\n"
		+ "sbox-cli runs on the host, in this terminal, not into a log file: its stdin, stdout\n"
		+ "and stderr are ours, its exit code becomes ours, and this process prints nothing of\n"
		+ "its own while it runs. The Steam Linux Runtime 4.0 container is not used, because the\n"
		+ "entry into it hands its children stdin = /dev/null and that ends sbox-cli's stdio\n"
		+ "sessions before they connect; a host sbox-cli reaches an editor inside the container\n"
		+ "anyway, because the pid namespace and /tmp are shared. DOTNET_ROOT points sbox-cli\n"
		+ "at the engine's own .NET; the rest of the environment is the window's launch\n"
		+ "environment without casefold (no engine is started here), and input debug comes\n"
		+ "from settings.json.\n"
		+ "\n"
		+ "TMPDIR is removed, so sbox-cli looks under /tmp the way an editor the window\n"
		+ "launched into the container does. An editor the window starts on the host\n"
		+ "(its Steam runtime unticked) inherits the window's TMPDIR instead; if that is\n"
		+ "not /tmp, --cli will not find it - run the engine's own sbox-cli directly with\n"
		+ "the same TMPDIR.\n"
		+ "\n"
		+ "sbox-cli's own --project and --game are refused: they start an engine outside the\n"
		+ "Steam runtime container. Start it with ampersand --launch sbox-dev --project P\n"
		+ "--wait-ready and attach to it instead.\n"
		+ "\n"
		+ "ampersand --cli                 this text\n"
		+ "ampersand --cli -- --help       sbox-cli's own help\n"
		+ "ampersand --cli -- --connect /abs/Project.sbproj --call editor_status\n"
		+ "ampersand --cli -- --connect /abs/Project.sbproj   MCP server on stdin/stdout\n"
		+ "\n"
		+ "ampersand --stop TARGET [--project FILE.sbproj]   stop a running target without the window";

	public static int Run( string[] args )
	{
		// args[0] is --cli itself. -h/--help are the only switches read
		// here: anything else belongs to sbox-cli, whose own options must
		// not collide with ours.
		if ( args.Length < 2 )
		{
			Console.Error.WriteLine( "ampersand: missing SBOX-CLI-ARGS" );
			Console.Error.WriteLine( Usage );
			return 2;
		}

		if ( args[1] is "-h" or "--help" )
		{
			Console.WriteLine( Usage );
			return 0;
		}

		var passThrough = new List<string>();
		for ( var i = args[1] == "--" ? 2 : 1; i < args.Length; i++ )
			passThrough.Add( args[i] );

		if ( passThrough.Count == 0 )
		{
			Console.Error.WriteLine( "ampersand: missing SBOX-CLI-ARGS" );
			Console.Error.WriteLine( Usage );
			return 2;
		}

		// sbox-cli's owned modes are refused before anything is composed:
		// they exec an engine directly, on the host, outside the Steam
		// runtime container an ampersand launch always uses. Exact tokens
		// only, so a JSON argument that happens to contain the text (a
		// --call run_csharp payload, say) is still passed through.
		foreach ( var arg in passThrough )
		{
			if ( arg is "--project" or "--game" )
			{
				Console.Error.WriteLine( "ampersand: --cli does not run sbox-cli's --project/--game: those start the engine outside the Steam runtime container." );
				Console.Error.WriteLine( "ampersand: Launch it with: ampersand --launch sbox-dev --project P --wait-ready - then attach with: ampersand --cli -- --connect P" );
				return 2;
			}
		}

		var notes = new List<string>();
		LaunchError? error;
		Plan? plan = null;

		try
		{
			error = Compose( passThrough, notes, out plan );
		}
		catch ( Exception e )
		{
			// Same reason as HeadlessLaunch: an exception must reach the
			// caller as a parseable "ampersand: ..." line, not as an abort.
			error = new LaunchError( "Launch failed", e.Message );
		}

		// Dormant while the script header says runtime=never: this loop only
		// fires if that header is changed back to a runtime.
		foreach ( var note in notes )
			Console.Error.WriteLine( "ampersand: warning: " + note );

		if ( error is not null )
		{
			Console.Error.WriteLine( "ampersand: " + error.Title + ": " + error.Message.Trim().Replace( "\n\n", "\n" ) );
			return 1;
		}

		return RunForeground( plan! );
	}

	/// <summary>The composed command and the environment it runs with.</summary>
	private sealed class Plan
	{
		public List<string> Command = new();
		public Dictionary<string, string> Environment = new();
	}

	/// <summary>
	/// Builds the sbox-cli command the way the window builds a launch: the
	/// same root checks, the same script lookup, the same environment. The
	/// script declares runtime=never, so the container entry is not used and
	/// the command stays the script itself. Returns null with
	/// <paramref name="plan"/> filled when there is something to run.
	/// </summary>
	private static LaunchError? Compose( List<string> passThrough, List<string> notes, out Plan? plan )
	{
		plan = null;

		var root = SboxSettings.Resolve();
		if ( root is null || !SboxSettings.IsValid( root ) )
		{
			var stale = SboxSettings.GetStalePersistedPath();
			return new LaunchError( "Repo root not found", !string.IsNullOrWhiteSpace( stale )
				? $"Saved s&box path is no longer valid: {stale}\nExpected a folder containing game/ and engine/ with game/sbox."
				: "could not locate the s&box tree - set it once in the window (S&BOX LOCATION)." );
		}

		var script = LaunchComposer.FindScript( root, ScriptFile, out var scriptError );
		if ( script is null )
			return scriptError;

		// Checked first: a missing bridge is the caller's actual problem.
		var cli = LaunchComposer.CliPath( root );
		if ( !File.Exists( cli ) )
		{
			return new LaunchError( "sbox-cli not found",
				"No sbox-cli at " + cli + "\n\nRun Ampersand's Build S&Box - the engine builds sbox-cli "
				+ "next to its other managed outputs - or check that the s&box tree at " + root + " is complete." );
		}

		// The header check stays even though the script says runtime=never
		// today: it is what decides whether sbox-cli runs on the host, and
		// the container branch below must not silently come back. See the
		// class doc for why the container cannot host a stdio session.
		var useRuntime = ScriptMetadata.Read( script ).Runtime != RuntimeMode.Never;

		// Casefold is off for --cli: no engine is started from here, so the
		// shim would have nothing to load it. The container branch below
		// does not change that - it exists only for a future header edit.
		var env = LaunchComposer.BaseEnvironment( root, false, SboxSettings.GetInputDebug() );

		// Only this mode sets DOTNET_ROOT. sbox-cli is a framework-dependent
		// apphost; on the host the system .NET runs it, inside a container
		// there is none and the engine's own runtime is the only option.
		// Deliberately not part of BaseEnvironment or _common.sh: the
		// editor's own sbox-cli invocations must stay untouched.
		env["DOTNET_ROOT"] = Path.GetFullPath( Path.Combine( root, "game", "bin", "dotnet" ) );

		// sbox-cli resolves a relative project argument against its working
		// directory. The launcher already runs the script in the caller's
		// folder, so the script's cd is a no-op; it is kept so a run from a
		// wrapper that changes directory still resolves relative project
		// paths against the caller's folder. The window's launches do the
		// same thing with the container's working directory.
		env["SBOX_CLI_CWD"] = Environment.CurrentDirectory;

		// Sourcing _common.sh arms set -x unless told otherwise; sbox-cli's
		// stderr is read by callers, so keep the tracing out of it.
		env["SBOX_TRACE"] = "0";

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

		command.AddRange( passThrough );

		plan = new Plan { Command = command, Environment = env };
		return null;
	}

	/// <summary>
	/// Runs the composed command in the foreground with our own stdin,
	/// stdout and stderr and returns its exit code.
	///
	/// Nothing is redirected and nothing is detached, unlike TryDetach:
	/// sbox-cli's server modes speak newline-delimited JSON on stdio and end
	/// at stdin EOF, so a wrapper that swallowed either would break the
	/// session it exists to host. The working directory is the caller's, so a
	/// relative path argument means what the caller meant by it.
	/// </summary>
	private static int RunForeground( Plan plan )
	{
		var info = new ProcessStartInfo
		{
			FileName = plan.Command[0],
			WorkingDirectory = Environment.CurrentDirectory,
			UseShellExecute = false
		};

		foreach ( var arg in plan.Command.Skip( 1 ) )
			info.ArgumentList.Add( arg );

		// Same scrub as TryDetach, and for the same reason: a shell opened
		// from a terminal ampersand launched already carries SBOX_CASEFOLD,
		// SBOX_IN_STEAMRT4 and SBOX_STEAMRT4_COMPAT, and _common.sh acts on
		// them whether or not this run set them. The composed env below
		// re-adds the ones this run wants.
		foreach ( var owned in LaunchComposer.OwnedVariables )
			info.Environment.Remove( owned );

		// A TMPDIR we inherited must not reach sbox-cli either. An editor
		// ampersand launched into the Steam runtime container never sees our
		// TMPDIR - only --env values cross - so its MCP pipe is under /tmp;
		// a host sbox-cli looking in some other directory would report no
		// running editor. An editor the window starts on the host (its
		// Steam runtime unticked) does inherit the window's TMPDIR, so if
		// that is not /tmp this removal hides it: run the engine's own
		// sbox-cli directly with the same TMPDIR. The removal stays
		// unconditional; this is the only place --cli drops it.
		info.Environment.Remove( "TMPDIR" );

		foreach ( var pair in plan.Environment )
			info.Environment[pair.Key] = pair.Value;

		try
		{
			using var process = Process.Start( info );

			if ( process is null )
			{
				Console.Error.WriteLine( "ampersand: Launch failed: " + plan.Command[0] + " did not start" );
				return 1;
			}

			// sbox-cli's own waits are the only timeout that makes sense
			// here; an interactive session can run as long as the caller
			// keeps its stdin open.
			process.WaitForExit();
			return process.ExitCode;
		}
		catch ( Win32Exception e )
		{
			Console.Error.WriteLine( "ampersand: Launch failed: could not run " + plan.Command[0] + ": " + e.Message );
			return 1;
		}
	}
}

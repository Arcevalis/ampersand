using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Ampersand;

/// <summary>
/// <c>ampersand --stop TARGET [--project FILE.sbproj]</c>: stop a running
/// target without the window, for an agent recovering from a frozen or
/// misbehaving engine.
///
/// The target is found by scanning /proc, not by a pid file, because the
/// process it has to reach may have been started by the window's play button,
/// by --launch, or by the sbox project picker (game/sbox-launcher, which
/// forwards its own argv to the editor) - nobody leaves a pid behind for all
/// three. The scan also works from the host for processes inside the Steam
/// runtime container: pressure-vessel puts them in a different mount
/// namespace, so /proc/&lt;pid&gt;/exe is useless, but their cmdline still
/// carries the full host path of the binary, which is what is matched here.
///
/// Matching by argv[0] against <c>&lt;root&gt;/game/&lt;target&gt;</c> sidesteps
/// the picker's doubled arguments. When --project is given the editor's own
/// last <c>-project</c> is taken (the engine's RunningEditors.cs does the same
/// when it dedupes editors for a project), so only the editor open on that
/// project is stopped.
///
/// The whole process tree is collected before signalling: the engine spawns
/// helper processes (shader and resource compilers etc.); those present when
/// the stop begins are stopped with it. The tree is a single snapshot taken
/// before signalling, so a helper spawned after that snapshot is not included.
/// They get SIGTERM with their parent, and SIGKILL after a grace period,
/// because a frozen engine can ignore SIGTERM forever.
///
/// Each process is identified by its starttime from /proc before a signal is
/// sent, so a pid that died and was reused cannot be signalled by mistake.
///
/// The sbox project picker (game/sbox-launcher) is deliberately NOT a target -
/// it is a long-lived window the user owns and is the PARENT of the editors it
/// opens; it is never matched because its argv[0] is game/sbox-launcher, and
/// the editor is stopped without touching its ancestors, so the picker stays
/// open and can reopen the project.
/// </summary>
internal static class HeadlessStop
{
	public const string Argument = "--stop";

	private const int SigTerm = 15;
	private const int SigKill = 9;
	private const int Eperm = 1;

	/// <summary>How long a SIGTERMed engine gets to exit on its own.</summary>
	private const int TermGraceMilliseconds = 10000;

	/// <summary>How long a SIGKILLed engine gets to disappear.</summary>
	private const int KillGraceMilliseconds = 2000;

	private const int PollIntervalMilliseconds = 200;

	[DllImport( "libc", SetLastError = true )]
	private static extern int kill( int pid, int sig );

	private const string Usage =
		"usage: ampersand --stop TARGET [--project FILE.sbproj]\n"
		+ "\n"
		+ "  TARGET            sbox | sbox-dev | sbox-server\n"
		+ "  --project FILE    sbox-dev only: stop only the editor open on this project\n"
		+ "                    (without it, every editor from this checkout is stopped)\n"
		+ "\n"
		+ "Finds the target's processes by scanning /proc, so it reaches editors started by\n"
		+ "the window, by --launch, or by the sbox project picker, inside or outside the Steam\n"
		+ "runtime container. Sends SIGTERM to them and their child processes, then SIGKILL\n"
		+ "after 10 s for a frozen engine. The project picker (game/sbox-launcher) is left\n"
		+ "alone. exit 0 when stopped or not running, 1 on failure, 2 on usage errors.";

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

		try
		{
			return Stop( options! );
		}
		catch ( Exception e )
		{
			// Never let a scan or a signal call abort as an unhandled
			// exception: the caller is an agent that parses stderr.
			Console.Error.WriteLine( "ampersand: Stop failed: " + e.Message );
			return 1;
		}
	}

	private sealed class Options
	{
		public string Target = "";
		public string? Project;
	}

	/// <summary>
	/// False with <paramref name="error"/> null means help was asked for.
	/// </summary>
	private static bool TryParse( string[] args, out Options? options, out string? error )
	{
		options = null;
		error = null;

		// args[0] is --stop itself.
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

		var parsed = new Options { Target = target.Name };

		for ( var i = 2; i < args.Length; i++ )
		{
			switch ( args[i] )
			{
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

					// A usage error, like an unknown option - and the file
					// need not exist: a deleted or moved project's editor
					// must still be stoppable.
					if ( !parsed.Project.EndsWith( ".sbproj", StringComparison.OrdinalIgnoreCase ) )
					{
						error = "--project needs a .sbproj path";
						return false;
					}
					break;

				case "-h":
				case "--help":
					return false;

				default:
					error = "unknown option '" + args[i] + "'";
					return false;
			}
		}

		if ( parsed.Project is not null && parsed.Target != "sbox-dev" )
		{
			error = "--project only applies to sbox-dev";
			return false;
		}

		options = parsed;
		return true;
	}

	private static int Stop( Options options )
	{
		var root = SboxSettings.Resolve();
		if ( root is null || !SboxSettings.IsValid( root ) )
		{
			var stale = SboxSettings.GetStalePersistedPath();
			Console.Error.WriteLine( "ampersand: Repo root not found: " + ( !string.IsNullOrWhiteSpace( stale )
				? "Saved s&box path is no longer valid: " + stale
					+ "\nExpected a folder containing game/ and engine/ with game/sbox."
				: "could not locate the s&box tree - set it once in the window (S&BOX LOCATION)." ) );
			return 1;
		}

		// Every target script execs $ROOT/game/<target> (apps/_common.sh
		// sbox_exec, apps/sbox-server.sh). root may carry a trailing slash
		// and the scripts build paths like /x/sbox-public//game/sbox-dev;
		// GetFullPath on both sides normalises that away.
		var binary = Path.GetFullPath( Path.Combine( root, "game", options.Target ) );

		string? projectPath = null;
		if ( options.Project is not null )
			projectPath = HeadlessLaunch.ResolveProjectPath( options.Project );

		var scan = FindMatches( binary, projectPath );
		var matched = scan.Matches;
		var suffix = projectPath is null ? "" : " on " + projectPath;

		if ( matched.Count == 0 )
		{
			Console.WriteLine( options.Target + " is not running" + suffix );
			return 0;
		}

		// pid -> starttime. Every process is carried together with the
		// starttime recorded in the single /proc snapshot, so a signal is
		// only ever sent to the same process the scan saw.
		var targets = new Dictionary<int, ulong>();
		foreach ( var m in matched )
		{
			if ( m.Pid != Environment.ProcessId )
				targets.TryAdd( m.Pid, m.StartTime );
			foreach ( var descendant in m.Descendants )
			{
				if ( descendant == Environment.ProcessId )
					continue;
				if ( scan.StartTimes.TryGetValue( descendant, out var startTime ) )
					targets.TryAdd( descendant, startTime );
			}
		}

		foreach ( var m in matched )
		{
			var line = "stopping " + options.Target + " pid " + m.Pid;
			if ( m.Project is not null )
				line += " (-project " + m.Project + ")";
			if ( m.Descendants.Count > 0 )
				line += " and " + m.Descendants.Count + " child process(es)";
			Console.WriteLine( line );
		}

		var permission = new List<int>();

		foreach ( var ( pid, startTime ) in targets )
			Signal( pid, startTime, SigTerm, permission );

		WaitUntilGone( targets, TermGraceMilliseconds );

		var survivors = targets.Where( kv => IsAlive( kv.Key, kv.Value ) ).Select( kv => kv.Key ).ToList();
		if ( survivors.Count > 0 )
		{
			foreach ( var pid in survivors )
				Signal( pid, targets[pid], SigKill, permission );

			Console.WriteLine( "still running after " + TermGraceMilliseconds / 1000
				+ " s - sent SIGKILL to " + survivors.Count + " process(es)" );

			WaitUntilGone( targets, KillGraceMilliseconds );
		}

		var alive = targets.Where( kv => IsAlive( kv.Key, kv.Value ) ).Select( kv => kv.Key ).OrderBy( pid => pid ).ToList();

		if ( permission.Count > 0 )
		{
			Console.Error.WriteLine( "ampersand: Stop failed: not permitted to signal pid(s) "
				+ string.Join( ", ", permission.Distinct().OrderBy( pid => pid ) ) + " (EPERM)" );
			return 1;
		}

		if ( alive.Count > 0 )
		{
			Console.Error.WriteLine( "ampersand: Stop failed: pid(s) "
				+ string.Join( ", ", alive ) + " still running" );
			return 1;
		}

		Console.WriteLine( "stopped " + options.Target + suffix );
		return 0;
	}

	/// <summary>
	/// One process whose argv[0] resolves to the target binary, with the
	/// starttime from the snapshot that identified it, the value of its own
	/// last <c>-project</c> (null when it had none), and its descendants
	/// (already collected once, so signalling and the log line agree).
	/// </summary>
	private sealed record Matched( int Pid, ulong StartTime, string? Project, List<int> Descendants );

	/// <summary>
	/// The result of one pass over /proc: the processes that match, and the
	/// starttime recorded for every pid seen, used later to identify a pid
	/// before signalling it.
	/// </summary>
	private sealed class Scan
	{
		public readonly List<Matched> Matches = new();
		public readonly Dictionary<int, ulong> StartTimes = new();
	}

	/// <summary>
	/// One pass over /proc: read every pid's stat once for (ppid, starttime),
	/// build the child map from those ppids, and match a pid's cmdline in the
	/// same loop. A later kill() only ever targets a pid whose starttime
	/// still matches, so a pid reused after its process died cannot be hit.
	/// </summary>
	private static Scan FindMatches( string binary, string? projectPath )
	{
		var scan = new Scan();
		var children = new Dictionary<int, List<int>>();
		var candidates = new List<(int Pid, ulong StartTime, string? Project)>();

		foreach ( var dir in Directory.EnumerateDirectories( "/proc" ) )
		{
			if ( !int.TryParse( Path.GetFileName( dir ), out var pid ) )
				continue;

			if ( !ReadStat( pid, out var ppid, out var startTime, out _ ) )
				continue;

			scan.StartTimes[pid] = startTime;

			if ( !children.TryGetValue( ppid, out var list ) )
				children[ppid] = list = new List<int>();
			list.Add( pid );

			// We stay in the child map so the process tree is complete, and
			// Stop() skips our own pid when it shows up as a descendant (for
			// example --stop run from a shell the engine spawned), so we never
			// signal ourselves.
			if ( pid == Environment.ProcessId )
				continue;

			var argv = ReadArgv( pid );
			if ( argv is null || argv.Length == 0 )
				continue;

			if ( !ResolvesTo( argv[0], binary ) )
				continue;

			var lastProject = LastProjectArg( argv );

			if ( projectPath is not null )
			{
				// The picker forwards its own args, so the editor's argv can
				// contain the binary path twice and more than one -project;
				// the last one is the effective project (RunningEditors.cs
				// takes the last as well).
				if ( lastProject is null || !ResolvesTo( lastProject, projectPath ) )
					continue;
			}

			candidates.Add( ( pid, startTime, lastProject ) );
		}

		// Descendants are computed after the whole child map exists - a
		// child can be enumerated before its parent.
		foreach ( var ( pid, startTime, lastProject ) in candidates )
			scan.Matches.Add( new Matched( pid, startTime, lastProject, CollectDescendants( pid, children ) ) );

		return scan;
	}

	/// <summary>
	/// Path.GetFullPath both sides and compare as strings; argv[0] can be
	/// relative or garbage, so a failure to normalise is simply no match.
	/// </summary>
	private static bool ResolvesTo( string candidate, string normalised )
	{
		try { return Path.GetFullPath( candidate ) == normalised; }
		catch { return false; }
	}

	/// <summary>
	/// The value after the LAST argv entry equal to -project, or null.
	/// </summary>
	private static string? LastProjectArg( string[] argv )
	{
		for ( var i = argv.Length - 1; i >= 0; i-- )
		{
			if ( argv[i].Equals( "-project", StringComparison.OrdinalIgnoreCase ) && i + 1 < argv.Length )
				return argv[i + 1];
		}

		return null;
	}

	/// <summary>
	/// /proc/&lt;pid&gt;/cmdline is NUL separated and ends with a NUL. Anything
	/// that vanished or cannot be read is skipped silently - a process can
	/// exit between the directory listing and the read.
	/// </summary>
	private static string[]? ReadArgv( int pid )
	{
		byte[] bytes;
		try
		{
			bytes = File.ReadAllBytes( "/proc/" + pid + "/cmdline" );
		}
		catch { return null; }

		if ( bytes.Length == 0 )
			return null;

		var parts = Encoding.UTF8.GetString( bytes ).Split( '\0' );
		if ( parts.Length > 0 && parts[^1].Length == 0 )
			parts = parts[..^1];

		return parts;
	}

	/// <summary>
	/// Reads (ppid, starttime, state) from /proc/&lt;pid&gt;/stat. The comm
	/// field is parenthesised and may itself contain spaces and ')', so the
	/// fields are taken from the LAST ')' : what follows is "state ppid ..."
	/// ([0] state = field 3, [1] ppid = field 4, ... [19] starttime = field 22).
	/// starttime is clock ticks since boot, the process's identity.
	/// </summary>
	private static bool ReadStat( int pid, out int ppid, out ulong startTime, out char state )
	{
		ppid = 0;
		startTime = 0;
		state = '\0';

		string stat;
		try
		{
			stat = File.ReadAllText( "/proc/" + pid + "/stat" );
		}
		catch { return false; }

		var close = stat.LastIndexOf( ')' );
		if ( close < 0 )
			return false;

		var fields = stat[( close + 2 )..].Split( ' ' );
		if ( fields.Length < 20 )
			return false;

		if ( fields[0].Length > 0 )
			state = fields[0][0];

		return int.TryParse( fields[1], out ppid )
			&& ulong.TryParse( fields[19], out startTime );
	}

	/// <summary>
	/// True only while /proc/&lt;pid&gt;/stat is readable, still shows the
	/// same starttime, and the state is not 'Z' - a zombie is dead for us,
	/// its parent reaps it. Any read failure counts as dead. This is what
	/// keeps a signal from reaching a pid that was recycled after its
	/// process exited.
	/// </summary>
	private static bool SameProcess( int pid, ulong startTime )
	{
		return ReadStat( pid, out _, out var current, out var state )
			&& current == startTime
			&& state != 'Z';
	}

	private static bool IsAlive( int pid, ulong startTime )
	{
		return SameProcess( pid, startTime );
	}

	private static List<int> CollectDescendants( int root, Dictionary<int, List<int>> children )
	{
		var seen = new HashSet<int>();
		var queue = new Queue<int>();
		queue.Enqueue( root );

		while ( queue.Count > 0 )
		{
			if ( !children.TryGetValue( queue.Dequeue(), out var kids ) )
				continue;

			foreach ( var kid in kids )
			{
				if ( seen.Add( kid ) )
					queue.Enqueue( kid );
			}
		}

		return seen.ToList();
	}

	private static void Signal( int pid, ulong startTime, int sig, List<int> permission )
	{
		// Confirm the pid is still the same process (same starttime, not a
		// zombie) before signalling: if it exited or the pid was recycled,
		// this is someone else's process and must not be touched. A
		// microsecond window remains between this check and kill();
		// pidfd_send_signal would close it but is not worth a raw-syscall
		// P/Invoke here.
		if ( !SameProcess( pid, startTime ) )
			return;

		if ( kill( pid, sig ) == 0 )
			return;

		// ESRCH (3) means it exited on its own - nothing to do. EPERM (1)
		// is the one error worth reporting: the process exists but this
		// user may not signal it, which must not be mistaken for success.
		if ( Marshal.GetLastPInvokeError() == Eperm )
			permission.Add( pid );
	}

	private static void WaitUntilGone( Dictionary<int, ulong> targets, int timeout )
	{
		var deadline = Environment.TickCount64 + timeout;

		while ( targets.Any( kv => IsAlive( kv.Key, kv.Value ) ) )
		{
			if ( Environment.TickCount64 >= deadline )
				return;

			Thread.Sleep( PollIntervalMilliseconds );
		}
	}
}

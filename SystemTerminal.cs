using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Ampersand;

/// <summary>
/// Opens a command inside the user's terminal emulator on Linux, so tools that
/// need a real console (the dedicated server's stats overlay and command input,
/// the game's own console input) behave like their Windows console window.
///
/// Mirrors the engine's own spawner
/// (<c>game/addons/tools/Code/Editor/TerminalLauncher.cs</c> in sbox-public):
/// the terminal gets a scrubbed environment and the inner command restores what
/// it needs via <c>env</c>, so the child keeps the exact environment a direct
/// spawn would have while the emulator itself starts clean. The child is
/// spawned directly - no <c>tee</c> pipe, no wrapper - so its stdin/stdout/stderr
/// are the terminal's pty: <c>isatty</c> is true, ANSI colour stays on, and the
/// engine's <c>ConsoleInput</c> (gated on <c>Console.BufferWidth &gt; 0</c>)
/// actually reads keys. A <c>"$@" | tee</c> wrapper turns the child's stdout
/// into a pipe (<c>isatty</c> false, winsize ioctl fails), which disables input
/// and the status overlay - measured as 0 escape sequences where the same
/// startup through a pty emits over a thousand.
///
/// The awkward part is that some emulators fork and return immediately, which
/// would lose the child: gnome-terminal is a D-Bus client to
/// gnome-terminal-server and returns in ~110ms without --wait. Each entry below
/// therefore carries whatever flag makes the emulator stay attached; most do
/// not fork at all and need nothing. Konsole's --separate bypasses the
/// single-instance forwarder, which can swallow -e.
/// </summary>
internal static class SystemTerminal
{
	private enum ExecStyle
	{
		DashE,
		DashX,
		DashDash
	}

	private static readonly (string Exe, ExecStyle Style)[] Candidates =
	{
		// Forks unless told otherwise.
		( "gnome-terminal", ExecStyle.DashDash ),
		( "konsole", ExecStyle.DashE ),

		// These run the child directly.
		( "ptyxis", ExecStyle.DashDash ),
		( "alacritty", ExecStyle.DashE ),
		( "kitty", ExecStyle.DashDash ),
		( "wezterm", ExecStyle.DashDash ),
		( "foot", ExecStyle.DashE ),
		( "xfce4-terminal", ExecStyle.DashX ),
		( "tilix", ExecStyle.DashE ),
		( "terminator", ExecStyle.DashX ),
		( "xterm", ExecStyle.DashE ),

		// Debian/Ubuntu alternatives symlink; last because it could be any of
		// the above and we cannot know which flags it honours.
		( "x-terminal-emulator", ExecStyle.DashE )
	};

	/// <summary>
	/// Session variables that abort a foreign terminal emulator when inherited.
	/// Same list as the engine's TerminalLauncher: our session can carry bundled
	/// Qt and native libs (LD_PRELOAD, LD_LIBRARY_PATH) plus Qt tuning (QT_*)
	/// that kill a system terminal on startup - the launch then silently
	/// no-ops: Start succeeds, no window, no child. They are stripped from the
	/// terminal's environment and restored on the inner command via env, so the
	/// child keeps the exact environment a direct spawn would have.
	/// </summary>
	public static readonly string[] ScrubbedVariables =
	{
		"LD_PRELOAD",
		"LD_LIBRARY_PATH",
		"QT_QPA_PLATFORM",
		"QT_QPA_PLATFORM_PLUGIN_PATH",
		"QT_PLUGIN_PATH",
		"QT_XCB_GL_INTEGRATION",
		"QT_WAYLAND_RECONNECT",
		"QML_IMPORT_PATH",
		"QML2_IMPORT_PATH",
	};

	/// <summary>The emulator we would use, or null when none is installed.</summary>
	public static string? Detect()
	{
		if ( TryGetTerminal( out var exe, out _ ) )
			return exe;

		return null;
	}

	private static bool TryGetTerminal( out string exe, out ExecStyle style )
	{
		// $TERMINAL first, like the engine: the user's explicit choice wins.
		// Treated as a single binary name or path; unknown flags, so -e.
		var preferred = Environment.GetEnvironmentVariable( "TERMINAL" );

		if ( !string.IsNullOrWhiteSpace( preferred ) )
		{
			var trimmed = preferred.Trim();

			if ( ( trimmed.Contains( '/' ) && File.Exists( trimmed ) ) || Which( trimmed ) is not null )
			{
				exe = trimmed;
				style = ExecStyle.DashE;
				return true;
			}
		}

		foreach ( var (candidate, candidateStyle) in Candidates )
		{
			if ( Which( candidate ) is not null )
			{
				exe = candidate;
				style = candidateStyle;
				return true;
			}
		}

		exe = string.Empty;
		style = ExecStyle.DashE;
		return false;
	}

	/// <summary>
	/// Builds the argv that runs <paramref name="command"/> in a terminal
	/// window, S&box-style: terminal, its prefix (including the working
	/// directory where the emulator supports it), <c>env</c> with the scrubbed
	/// session variables restored plus <paramref name="extraEnv"/>, then the
	/// command and its arguments. The caller must remove each name in
	/// <paramref name="scrubbedNames"/> from the terminal child's environment
	/// so the terminal starts clean while the inner command keeps the
	/// direct-spawn environment. Returns false when no emulator is installed.
	/// </summary>
	public static bool TryBuild(
		IReadOnlyList<string> command,
		string workingDirectory,
		IReadOnlyDictionary<string, string>? extraEnv,
		out List<string> argv,
		out string? emulator,
		out List<string> scrubbedNames )
	{
		argv = new List<string>();
		emulator = null;
		scrubbedNames = new List<string>();

		if ( command is null || command.Count == 0 )
			return false;

		if ( !TryGetTerminal( out var exe, out var style ) )
			return false;

		var terminalPath = exe.Contains( '/' ) ? exe : Which( exe );

		if ( terminalPath is null )
			return false;

		emulator = exe.Contains( '/' ) ? Path.GetFileName( exe ) : exe;
		argv.Add( terminalPath );
		argv.AddRange( GetPrefixArgs( exe, style, workingDirectory ) );

		// Inner environment: scrubbed session values restored first, then the
		// caller's extra env over the top so it wins on overlap (e.g. our
		// QT_QPA_PLATFORM=xcb over a session value).
		var inner = new Dictionary<string, string>( StringComparer.Ordinal );

		foreach ( var name in ScrubbedVariables )
		{
			var value = Environment.GetEnvironmentVariable( name );

			if ( value is null )
				continue;

			scrubbedNames.Add( name );
			inner[name] = value;
		}

		if ( extraEnv is not null )
		{
			foreach ( var pair in extraEnv )
				inner[pair.Key] = pair.Value ?? string.Empty;
		}

		argv.Add( Which( "env" ) ?? "/usr/bin/env" );

		// A forking emulator (konsole single-instance, gnome-terminal-server)
		// runs the tab from its long-lived server process, not from this
		// client - so the tab would inherit the SERVER's session variables,
		// not ours. Unset every scrubbed name we have no value for, so a
		// stale server-side LD_PRELOAD (or QT_*) cannot leak into the engine
		// behind our backs. Names we do restore (or the caller overrides) go
		// as assignments, which win over both the server and the -u list.
		foreach ( var name in ScrubbedVariables )
		{
			if ( !inner.ContainsKey( name ) )
			{
				argv.Add( "-u" );
				argv.Add( name );
			}
		}

		foreach ( var pair in inner )
			argv.Add( $"{pair.Key}={pair.Value}" );

		argv.AddRange( command );
		return true;
	}

	/// <summary>
	/// Strips the scrubbed session variables (the <paramref name="names"/> out
	/// of <see cref="TryBuild"/>) from the terminal emulator's own environment,
	/// so the emulator starts clean while the inner command restores them via
	/// env. Never throws.
	/// </summary>
	public static void ScrubEnvironment( ProcessStartInfo info, IEnumerable<string> names )
	{
		try
		{
			foreach ( var name in names )
				info.Environment.Remove( name );
		}
		catch { }
	}

	/// <summary>
	/// Wraps <paramref name="command"/> so a failed run stays open with its
	/// output visible instead of flashing shut: the tab runs <c>sh -c</c>
	/// around the real command, which reports a nonzero exit code and waits
	/// for Enter, then exits with the same code. A clean exit closes the
	/// window with the command. (sbox-dev's linger is owned by its script -
	/// SBOX_DETACH waits for the detached session so the tab auto-closes on
	/// quit; this hold is only the safety net for crashes.) This mirrors the
	/// launcher's own re-execed workers, which end with "Press Enter to
	/// close" (see Program.DependencyCheckMain/BootstrapMain).
	///
	/// The command travels as <c>"$@"</c> (never re-quoted into a string), so
	/// argv fidelity is exact. No pipe is involved at any point, so the
	/// child's fds stay on the pty: isatty holds, colours stay on, console
	/// input keeps working. Emulator-agnostic on purpose - gnome-terminal has
	/// no --hold equivalent, so a per-emulator flag matrix would leave gaps.
	/// <paramref name="displayName"/> becomes <c>$0</c> for the exit line.
	/// </summary>
	public static List<string> WithHold( IReadOnlyList<string> command, string displayName )
	{
		var wrapped = new List<string>
		{
			Which( "sh" ) ?? "/bin/sh",
			"-c",
			HoldScript,
			string.IsNullOrWhiteSpace( displayName ) ? "ampersand" : displayName
		};
		wrapped.AddRange( command );
		return wrapped;
	}

	/// <summary>
	/// POSIX sh (runs under dash): run "$@" on the untouched pty; on a nonzero
	/// exit report it and pause so the output stays visible, then propagate
	/// the exit code so --wait tracking and the failure tail dialog still see
	/// the real status. No set -eu here: the read must fall through to exit
	/// even on EOF.
	/// </summary>
	private const string HoldScript = """
		"$@"
		code=$?
		if [ $code -ne 0 ]; then
			echo ""
			echo "$0 exited $code - press Enter to close."
			read dummy || true
		fi
		exit $code
		""";

	/// <summary>
	/// Emulator arguments preceding the command. The execute flag (-e, -x, --)
	/// stays last so it catches the command and its arguments.
	/// </summary>
	private static string[] GetPrefixArgs( string exe, ExecStyle style, string workingDirectory )
	{
		var name = exe.Contains( '/' ) ? Path.GetFileName( exe ) : exe;
		var hasWorkdir = !string.IsNullOrWhiteSpace( workingDirectory );

		// Per-emulator working-directory flags, matching the engine's spawner
		// where they overlap (gnome-terminal, konsole, xfce4-terminal).
		if ( string.Equals( name, "gnome-terminal", StringComparison.Ordinal ) )
		{
			// --wait keeps the D-Bus client attached so Stop/exit tracking
			// sees the window's lifetime, not the ~110ms forward.
			if ( hasWorkdir )
				return new[] { "--wait", $"--working-directory={workingDirectory}", "--" };

			return new[] { "--wait", "--" };
		}

		if ( string.Equals( name, "konsole", StringComparison.Ordinal ) )
		{
			// --separate bypasses the single-instance forwarder (which can
			// swallow -e) and stays attached like the old --nofork.
			if ( hasWorkdir )
				return new[] { "--separate", "--workdir", workingDirectory, "-e" };

			return new[] { "--separate", "-e" };
		}

		if ( string.Equals( name, "xfce4-terminal", StringComparison.Ordinal ) )
		{
			if ( hasWorkdir )
				return new[] { $"--working-directory={workingDirectory}", "-x" };

			return new[] { "-x" };
		}

		// Generic fallbacks: no known working-directory flag, so the child's
		// cwd comes from the spawn's WorkingDirectory (honoured by emulators
		// that run the child directly). wezterm needs "start" before "--".
		return style switch
		{
			ExecStyle.DashX => new[] { "-x" },
			ExecStyle.DashDash when string.Equals( name, "wezterm", StringComparison.Ordinal ) => new[] { "start", "--" },
			ExecStyle.DashDash => new[] { "--" },
			_ => new[] { "-e" },
		};
	}

	private static string? Which( string exe )
	{
		if ( string.IsNullOrEmpty( exe ) || exe.Contains( '/' ) )
			return null;

		var path = Environment.GetEnvironmentVariable( "PATH" );

		if ( string.IsNullOrEmpty( path ) )
			return null;

		foreach ( var directory in path.Split( ':', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var candidate = Path.Combine( directory, exe );

			if ( File.Exists( candidate ) )
				return candidate;
		}

		return null;
	}
}

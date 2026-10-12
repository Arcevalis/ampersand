#pragma warning disable CA2000
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Ampersand;

/// <summary>
/// Runs a launch script, either in the user's own terminal emulator or in the
/// background with its output captured to a log file.
///
/// There used to be a third mode - a pty feeding an emulated terminal pane inside
/// the window - and it was the largest thing in the app. It is gone. Rebuilding a
/// terminal emulator inside a launcher solved a problem nobody has: every desktop
/// this runs on already has a real one, which does scrollback, selection, reflow
/// and resize better than the reimplementation ever did.
///
/// The finding that justified the pty still stands, and a real emulator now
/// satisfies it for free: the engine only emits ANSI colour when its output is a
/// tty - measured at 0 escape sequences in 2,462 lines through a pipe against
/// 1,172 in the same startup through a pty. The terminal path therefore spawns
/// the child directly, engine-style (see SystemTerminal): no tee pipe, no
/// wrapper, so the child's stdin/stdout/stderr are the terminal's pty, isatty
/// holds, and the engine's console input (gated on Console.BufferWidth &gt; 0)
/// and status overlay behave like the Windows console. Background mode has no
/// tty and therefore no colour; its output is captured to the log file instead.
///
/// Stop is honest only in background mode, where the child is ours and Kill( true )
/// reaches its whole tree. An emulator that forks - gnome-terminal is a D-Bus
/// client to gnome-terminal-server - parents the engine somewhere outside our
/// tree, so Stop kills the window we started and not necessarily what is in it.
/// That is why SystemTerminal carries a don't-fork flag per emulator: with
/// --wait the client stays, and killing it takes the session with it.
///
/// Lifetime caveat: the tab lives only as long as its direct child. sbox-dev
/// detaches sbox-launcher and returns 0 immediately by engine design, so its
/// tab would close mid-boot and the closing tab's SIGHUP would kill the
/// launcher pre-log with no window and no error anywhere. sbox-dev.sh arms
/// SBOX_DETACH (see apps/_common.sh sbox_exec): setsid into its own session
/// (no controlling terminal, so the launcher outlives the tab) and - on a
/// terminal - waits for the whole detached session, so the tab auto-closes on
/// quit instead. The game and server run in-process and deliberately keep the
/// tab's session (Ctrl+C, close-kills).
/// </summary>
internal sealed class ProcessRunner
{
	private Process? process;

	/// <summary>
	/// What the child is told it is talking to. Shared, because the containerised
	/// path has to hand this over a second time as a --env argument: env does not
	/// cross the launcher service by inheritance, and an engine with no TERM emits
	/// no colour.
	/// </summary>
	public const string Term = "xterm-256color";

	public string Name { get; }
	public bool IsRunning { get; private set; }
	public int? ExitCode { get; private set; }
	public string? LastLogPath { get; private set; }

	/// <summary>Launcher-generated status lines, already on the UI thread.</summary>
	public event Action<string>? Notice;

	public event Action<ProcessRunner>? StateChanged;

	public ProcessRunner( string name )
	{
		Name = name;
	}

	public void Start(
		IReadOnlyList<string> command,
		string workingDirectory,
		IReadOnlyDictionary<string, string> extraEnv,
		bool useSystemTerminal )
	{
		if ( IsRunning )
			return;

		ExitCode = null;

		if ( useSystemTerminal )
			StartInTerminal( command, workingDirectory, extraEnv );
		else
			StartInBackground( command, workingDirectory, extraEnv );
	}

	private void StartInTerminal(
		IReadOnlyList<string> command,
		string workingDirectory,
		IReadOnlyDictionary<string, string> extraEnv )
	{
		// Direct spawn, engine-style, wrapped in a stay-open-on-failure hold:
		// the terminal window IS the output; a clean exit closes it with the
		// command, while a crash holds it open with the transcript plus a
		// prompt so the error is never lost to a flash. sbox-dev's linger is
		// owned by its script instead (SBOX_DETACH waits for the detached
		// session, so the tab auto-closes on quit). The hold adds no pipe, so
		// the child's fds stay on the pty (isatty true, colours on, server
		// console input works); it propagates the exit code, so tracking and
		// the failure tail dialog still see the real status. The log keeps
		// the exact command for reproduction; output itself lives in the
		// window, not the log.
		LastLogPath = RunLog.CreateLogPath( Name );
		RunLog.WriteCommandHeader( LastLogPath, command, workingDirectory, extraEnv );
		RunLog.WriteTerminalNote( LastLogPath );

		var held = SystemTerminal.WithHold( command, Name );

		if ( !SystemTerminal.TryBuild( held, workingDirectory, extraEnv, out var argv, out var emulator, out var scrubbedNames ) )
		{
			// Fallback: no terminal -> background log capture (logs only).
			Notice?.Invoke( "no terminal emulator found - logging to file instead." );
			StartInBackground( command, workingDirectory, extraEnv );
			return;
		}

		if ( !TryStartTerminal( argv, workingDirectory, scrubbedNames, out var problem ) )
		{
			Notice?.Invoke( "could not launch " + emulator + " - " + problem );
			return;
		}

		Notice?.Invoke( "running in " + emulator + " - log: " + LastLogPath );
		StateChanged?.Invoke( this );
	}

	/// <summary>
	/// No window: capture stdout/stderr directly to log file in ~/.cache.
	/// Used when "Launch with system terminal" is unticked, or as fallback
	/// when no emulator is installed (logs only).
	/// </summary>
	private void StartInBackground(
		IReadOnlyList<string> command,
		string workingDirectory,
		IReadOnlyDictionary<string, string> extraEnv )
	{
		// Reuse existing log if StartInTerminal already created one for fallback
		// (it already wrote the command header, so only write ours on a fresh log).
		var freshLog = LastLogPath is null;
		LastLogPath ??= RunLog.CreateLogPath( Name );

		if ( freshLog )
			RunLog.WriteCommandHeader( LastLogPath, command, workingDirectory, extraEnv );

		if ( !TryStart( command, workingDirectory, extraEnv, redirect: true, out var problem, LastLogPath ) )
		{
			Notice?.Invoke( "could not start - " + problem );
			return;
		}

		Notice?.Invoke( "running in background - log: " + LastLogPath );
		StateChanged?.Invoke( this );
	}

	private bool TryStartTerminal(
		IReadOnlyList<string> argv,
		string workingDirectory,
		IReadOnlyList<string> scrubbedNames,
		out string problem )
	{
		problem = string.Empty;

		var info = new ProcessStartInfo
		{
			FileName = argv[0],
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = false,
			RedirectStandardError = false
		};

		for ( var i = 1; i < argv.Count; i++ )
			info.ArgumentList.Add( argv[i] );

		// The inner command carries its environment via env (see
		// SystemTerminal.TryBuild); the terminal itself starts scrubbed so
		// session Qt/native variables cannot abort the emulator. Never add the
		// launch env to the terminal's own environment.
		SystemTerminal.ScrubEnvironment( info, scrubbedNames );

		try
		{
			process = new Process { StartInfo = info, EnableRaisingEvents = true };
			Track( process );
			process.Start();
		}
		catch ( Exception e )
		{
			problem = e.Message;
			return false;
		}

		IsRunning = true;
		return true;
	}

	private bool TryStart(
		IReadOnlyList<string> argv,
		string workingDirectory,
		IReadOnlyDictionary<string, string> extraEnv,
		bool redirect,
		out string problem,
		string? logPath = null )
	{
		problem = string.Empty;

		var info = new ProcessStartInfo
		{
			FileName = argv[0],
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = redirect,
			RedirectStandardError = redirect
		};

		for ( var i = 1; i < argv.Count; i++ )
			info.ArgumentList.Add( argv[i] );

		foreach ( var pair in extraEnv )
			info.Environment[pair.Key] = pair.Value;

		try
		{
			process = new Process { StartInfo = info, EnableRaisingEvents = true };

			// For background mode, capture output directly to log file.
			object logLock = new();
			StreamWriter? logWriter = null;
			FileStream? logStream = null;
			if ( redirect && logPath is not null )
			{
				try
				{
					logStream = new FileStream( logPath, FileMode.Append, FileAccess.Write, FileShare.Read );
					logWriter = new StreamWriter( logStream ) { AutoFlush = true };
				}
				catch
				{
					logWriter = null;
					try { logStream?.Dispose(); } catch { }
					logStream = null;
				}
			}

			Track( process, () =>
			{
				try { logWriter?.Flush(); logWriter?.Dispose(); } catch { }
				try { logStream?.Dispose(); } catch { }
			} );

			if ( redirect )
			{
				if ( logWriter is not null )
				{
					process.OutputDataReceived += ( _, e ) =>
					{
						if ( e.Data is null ) return;
						lock ( logLock )
						{
							try { logWriter.WriteLine( e.Data ); } catch { }
						}
					};
					process.ErrorDataReceived += ( _, e ) =>
					{
						if ( e.Data is null ) return;
						lock ( logLock )
						{
							try { logWriter.WriteLine( e.Data ); } catch { }
						}
					};
				}
				else
				{
					// Fallback discard (original behavior) if log open failed.
					process.OutputDataReceived += ( _, _ ) => { };
					process.ErrorDataReceived += ( _, _ ) => { };
				}
			}

			process.Start();

			if ( redirect )
			{
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
			}
		}
		catch ( Exception e )
		{
			problem = e.Message;
			return false;
		}

		IsRunning = true;
		return true;
	}

	/// <summary>
	/// Watches a started child: on exit records the code, marks us stopped and
	/// notifies, running <paramref name="first"/> before that (background mode
	/// flushes its log there). One place for both spawn paths.
	/// </summary>
	private void Track( Process proc, Action? first = null )
	{
		proc.Exited += ( _, _ ) => Post( () =>
		{
			try { first?.Invoke(); } catch { }
			ExitCode = SafeExitCode( proc );
			IsRunning = false;
			StateChanged?.Invoke( this );
		} );
	}

	private static int? SafeExitCode( Process? process )
	{
		try
		{
			return process is { HasExited: true } ? process.ExitCode : null;
		}
		catch
		{
			return null;
		}
	}

	public void Stop()
	{
		if ( !IsRunning || process is not { HasExited: false } )
			return;

		try
		{
			process.Kill( true );
		}
		catch
		{
			// Already gone.
		}
	}

	private static void Post( Action action )
	{
		try
		{
			var dispatcher = Avalonia.Threading.Dispatcher.UIThread;
			// In unit/headless context dispatcher may not be initialized; fall back to direct invoke.
			if ( dispatcher != null )
			{
				dispatcher.Post( action );
				return;
			}
		}
		catch { }
		action();
	}
}

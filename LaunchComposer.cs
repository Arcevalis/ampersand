using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Ampersand;

/// <summary>
/// A launch that could not be composed: a short title (status bar, CLI error
/// line) and the explanation that goes under it.
/// </summary>
internal sealed record LaunchError( string Title, string Message );

internal enum CasefoldShimState
{
	/// <summary>Built and newer than its source, or no source to compare against.</summary>
	Ready,

	/// <summary>Missing or older than casefold.c; can be built with gcc.</summary>
	NeedsBuild,

	/// <summary>No built library and no source to build it from.</summary>
	Unavailable
}

/// <summary>
/// Everything about a launch that does not touch the window: which script,
/// which environment, how the Steam runtime container is entered, and the
/// casefold shim it may need.
///
/// The window (MainWindow.LaunchCore) and the headless --launch mode
/// (HeadlessLaunch) both compose their command here, so the two cannot drift:
/// a new env var, shim or container flag added here reaches both. Nothing in
/// this class asks the user anything - callers decide whether a failure is a
/// dialog or a line on stderr.
/// </summary>
internal static class LaunchComposer
{
	/// <summary>The launch targets, in display order: row name and script file.</summary>
	public static readonly (string Name, string ScriptFile)[] Targets =
	{
		( "sbox", "sbox.sh" ),
		( "sbox-dev", "sbox-dev.sh" ),
		( "sbox-server", "sbox-server.sh" )
	};

	public const string ServerScript = "sbox-server.sh";

	/// <summary>
	/// sbox-cli inside the s&box tree, relative to the game directory:
	/// &lt;root&gt;/game/bin/managed/sbox-cli (see <see cref="CliPath"/>).
	/// </summary>
	public const string CliRelativePath = "bin/managed/sbox-cli";

	/// <summary>
	/// The game directory of the s&box tree - &lt;root&gt;/game, absolute and
	/// without a trailing separator. This is the engine root the engine's own
	/// code and the sbox-cli agent skill look for in FACEPUNCH_ENGINE
	/// (LauncherEnvironment.Init sets it to the same value in-process).
	/// </summary>
	public static string EnginePath( string root )
		=> Path.GetFullPath( Path.Combine( root, "game" ) );

	/// <summary>
	/// The sbox-cli binary for <paramref name="root"/>, absolute:
	/// &lt;root&gt;/game/bin/managed/sbox-cli. It is a .NET apphost, so on the
	/// host it needs a system .NET, and inside the Steam runtime container
	/// DOTNET_ROOT must point at the engine's own runtime (the container has
	/// none) - _common.sh sets that for container launches. The caller
	/// decides what to do when it is missing.
	/// </summary>
	public static string CliPath( string root )
		=> Path.GetFullPath( Path.Combine( EnginePath( root ), CliRelativePath ) );

	/// <summary>
	/// Variables whose presence alone changes what the launch scripts do, and
	/// which only this launcher should decide: BaseEnvironment and
	/// PrepareRuntime set them when wanted but never unset them, so a caller
	/// must clear inherited copies before applying the composed env.
	/// SBOX_INPUT_DEBUG is deliberately absent - an ambient value is forwarded
	/// on purpose (see BaseEnvironment).
	/// </summary>
	public static readonly string[] OwnedVariables =
	{
		"SBOX_CASEFOLD",
		"SBOX_IN_STEAMRT4",
		"SBOX_STEAMRT4_COMPAT"
	};

	/// <summary>
	/// The launch script for <paramref name="scriptFile"/>, or null with
	/// <paramref name="error"/> set when no copy can be found.
	/// </summary>
	public static string? FindScript( string root, string scriptFile, out LaunchError? error )
	{
		error = null;

		// Scripts live with the built app (OutDir/scripts/), not inside the
		// sbox checkout. They stay loose files so users can edit them at runtime.
		var script = AppPaths.FindScript( scriptFile );
		if ( script is null )
		{
			// Legacy fallback: ampersand inside sbox checkout (dev layout)
			var legacy = Path.Combine( root, "ampersand", "apps", scriptFile );
			if ( File.Exists( legacy ) )
				script = legacy;
		}

		if ( script is null || !File.Exists( script ) )
		{
			var tried = AppPaths.FindScriptsDir() ?? AppPaths.ScriptsDir;
			error = new LaunchError( "Launch script missing",
				$"Not found: {scriptFile}\n\nLooked in:\n{tried}\n(built scripts dir)\n\n"
				+ "Rebuild ampersand or check that apps/*.sh were copied to <OutDir>/scripts/." );
			return null;
		}

		return script;
	}

	/// <summary>
	/// The environment every launch gets, before any container additions.
	/// <paramref name="casefold"/> and <paramref name="inputDebug"/> are the
	/// global toggles (persisted in settings).
	/// </summary>
	public static Dictionary<string, string> BaseEnvironment( string root, bool casefold, bool inputDebug )
	{
		var env = new Dictionary<string, string>
		{
			["SBOX_REPO_ROOT"] = root,
			// The engine root (<root>/game), absolute, no trailing slash. The
			// engine's own LauncherEnvironment.Init sets the same variable in
			// its own process, and the agent skill's instructions locate the
			// engine through it; a launcher that hands the engine over already
			// knowing where it lives keeps host-side tools (sbox-cli's skill,
			// editors spawned from a shell we started) working without a
			// guess. Crosses into the Steam runtime container via --env.
			["FACEPUNCH_ENGINE"] = EnginePath( root ),
			// Wayland Qt plugin not shipped/supported - force xcb (XWayland).
			// Only xcb is bundled ("Available platform plugins are: xcb").
			// Set here so it crosses the Steam Runtime container boundary via --env;
			// _common.sh also hard-sets it for direct script runs.
			["QT_QPA_PLATFORM"] = "xcb",
			// SDL3 defaults to the Wayland backend when WAYLAND_DISPLAY is set,
			// making the viewport a native Wayland surface: no X window, no
			// in-process X press owner, so every X grab fails AlreadyGrabbed and
			// mid-drag warps die in the compositor. Force X11 so the press owner
			// is SDL's in-process X connection (SDL3 name; SDL2 called it
			// SDL_VIDEODRIVER). Same --env crossing as above.
			["SDL_VIDEO_DRIVER"] = "x11"
		};

		// Case-insensitive asset fallback (Casefold toggle): crosses into the
		// Steam runtime container via --env like the rest, but the .so itself
		// must resolve inside - prefer host-side runs when relying on the
		// fallback.
		if ( casefold )
			env["SBOX_CASEFOLD"] = "1";

		// Engine input diagnostics (Input debug toggle): SBOX_INPUT_DEBUG=1
		// (the engine treats any non-empty value as on). With the toggle off,
		// an ambient value from our own environment is still forwarded, so
		// launching ampersand with the flag set keeps working. Crosses into the
		// container via --env.
		if ( inputDebug )
			env["SBOX_INPUT_DEBUG"] = "1";
		else
		{
			var ambientInputDebug = Environment.GetEnvironmentVariable( "SBOX_INPUT_DEBUG" );
			if ( !string.IsNullOrEmpty( ambientInputDebug ) )
				env["SBOX_INPUT_DEBUG"] = ambientInputDebug;
		}

		return env;
	}

	/// <summary>
	/// Builds the containerised command into <paramref name="command"/>, and
	/// refuses to build one at all unless Steam is up.
	///
	/// Steam running is a hard requirement, not a preference: the container is
	/// entered through the launcher service running alongside the client,
	/// because that is the only context in which the runtime's own bwrap is
	/// allowed to create a user namespace. See SteamLauncherService.
	///
	/// Blocking: the Steam probe alone can sit five seconds and the compat
	/// cache may copy megabytes, so the window must call this off the UI
	/// thread. <paramref name="onRuntimeFound"/> receives the runtime version
	/// as soon as it is known (on the calling thread). <paramref name="notes"/>
	/// collects non-fatal warnings (missing editor-only Qt shims).
	/// </summary>
	public static bool PrepareRuntime(
		string root,
		string script,
		List<string> command,
		Dictionary<string, string> env,
		Action<string>? onRuntimeFound,
		out LaunchError? error,
		out List<string> notes )
	{
		error = null;
		notes = new List<string>();

		var install = SteamRt4Runtime.Find();

		if ( install is null )
		{
			error = new LaunchError( "Steam Linux Runtime not installed",
				"Steam Linux Runtime 4.0 (steamrt4) is not installed.\n\n"
					+ "Install it from Steam:  steam steam://install/" + SteamRt4Runtime.SteamAppId );
			return false;
		}

		onRuntimeFound?.Invoke( install.Version );

		if ( !SteamLauncherService.IsAvailable( install ) )
		{
			error = new LaunchError( "Steam is not running",
				"s&box can only be launched in the Steam runtime while the Steam client is "
					+ "running - the container is entered through Steam's own launcher service.\n\n"
					+ "Start Steam, wait for it to sign in, then try again." );
			return false;
		}

		if ( !SteamRt4Runtime.CheckRequirements( install, out var problems ) )
		{
			error = new LaunchError( "Container cannot start",
				"This host cannot start a pressure-vessel container.\n\n"
					+ string.Join( "\n", problems ) );
			return false;
		}

		if ( !SteamRt4Compat.Ensure( out var missing ) )
		{
			error = new LaunchError( "Compat libraries unavailable", string.Join( "\n", missing ) );
			return false;
		}

		// Editor-only shims (libpcre2-16 for Qt): seeded when the host has
		// them, noted when it does not - but the game needs none of these, so
		// a miss must never block the launch. The durable explanation lives in
		// the dependency check.
		if ( !SteamRt4Compat.EnsureBestEffort( out var warnings ) )
			notes.AddRange( warnings );

		var cache = SteamRt4Compat.CacheDirectory;

		env["SBOX_IN_STEAMRT4"] = "1";
		env["SBOX_STEAMRT4_COMPAT"] = cache;

		// TERM is added here rather than left to the emulator: this one is set on
		// launch-client's own environment, and the child does not inherit it. It
		// is handed over unconditionally - in background mode there is no tty, so
		// the engine suppresses colour regardless of what TERM says.
		var passed = new Dictionary<string, string>( env ) { ["TERM"] = ProcessRunner.Term };

		command.AddRange( SteamLauncherService.Wrap(
			install,
			new[] { install.RunScript, "--filesystem=" + cache, "--", script },
			root,
			passed ) );

		return true;
	}

	/// <summary>
	/// Appends the dedicated server's game argument. The engine expects
	/// "+game &lt;ident|/path/to.sbproj&gt;" as concommand arguments (see
	/// engine/Sandbox.GameInstance/GameInstanceDll.cs:872,
	/// ViewportTools.SpawnDedicatedServer). Settings store only the
	/// ident/path; the "+game" switch is added here. Both host and container
	/// wrapper paths forward "$@" to sbox-server.
	/// </summary>
	public static void AppendServerGame( string scriptFile, List<string> command, string? serverGame )
	{
		if ( scriptFile != ServerScript || string.IsNullOrWhiteSpace( serverGame ) )
			return;

		command.Add( "+game" );
		command.Add( serverGame );
	}

	/// <summary>
	/// Whether the casefold shim can be used as is. <paramref name="so"/> is
	/// where the library is (or would be built), honouring SBOX_CASEFOLD_SO;
	/// <paramref name="src"/> is the vendored casefold.c, null when this
	/// install lacks it.
	/// </summary>
	public static CasefoldShimState CheckCasefoldShim( out string so, out string? src )
	{
		var overridden = Environment.GetEnvironmentVariable( "SBOX_CASEFOLD_SO" );
		so = string.IsNullOrWhiteSpace( overridden ) ? AppPaths.CasefoldLibrary : overridden;
		src = AppPaths.FindCasefoldSource();

		if ( src is null )
			return File.Exists( so ) ? CasefoldShimState.Ready : CasefoldShimState.Unavailable;

		return ShimUpToDate( src, so ) ? CasefoldShimState.Ready : CasefoldShimState.NeedsBuild;
	}

	/// <summary>
	/// True when the built shim library exists and is newer than its source,
	/// so a source edit rebuilds instead of silently shipping a stale .so.
	/// </summary>
	private static bool ShimUpToDate( string? src, string? so )
	{
		try
		{
			if ( string.IsNullOrEmpty( src ) || string.IsNullOrEmpty( so ) ) return false;
			if ( !File.Exists( so ) ) return false;
			return File.GetLastWriteTimeUtc( so ) >= File.GetLastWriteTimeUtc( src );
		}
		catch { return false; }
	}

	/// <summary>
	/// Compiles the vendored casefold.c into <paramref name="so"/> with gcc.
	/// Blocking (a few seconds). Returns null on success.
	/// </summary>
	public static LaunchError? BuildCasefoldShim( string so, string src )
	{
		try { Directory.CreateDirectory( Path.GetDirectoryName( so )! ); } catch { }

		var srcDir = Path.GetDirectoryName( src )!;
		var srcFile = Path.GetFileName( src );

		using var proc = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "gcc",
				WorkingDirectory = srcDir,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			}
		};
		foreach ( var arg in new[] { "-D_GNU_SOURCE", "-shared", "-fPIC", "-O2", "-o", so, srcFile, "-ldl" } )
			proc.StartInfo.ArgumentList.Add( arg );

		try
		{
			proc.Start();
		}
		catch ( System.ComponentModel.Win32Exception e )
		{
			// gcc itself is missing.
			return new LaunchError( "gcc not found",
				"Could not start gcc: " + e.Message + "\n\nInstall gcc to build the shim, or untick Casefold." );
		}

		// gcc output is small; drain stdout first, then wait, then stderr.
		var stdout = proc.StandardOutput.ReadToEnd();
		proc.WaitForExit();
		var stderr = proc.StandardError.ReadToEnd();
		var output = ( stdout + "\n" + stderr ).Trim();

		if ( proc.ExitCode != 0 || !File.Exists( so ) )
			return new LaunchError( "Shim build failed", "gcc exited with code " + proc.ExitCode + ":\n\n" + output );

		return null;
	}
}

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Ampersand;

/// <summary>
/// Runs a command under the Steam client's own AppArmor profile, by handing it to
/// the launcher service pressure-vessel keeps running alongside Steam.
///
/// This is the ONLY way ampersand enters the container. Spawning run-in-steamrt4
/// straight off the filesystem does not work on Ubuntu 24.04+, and fails in a way
/// that misreports its own cause: with
/// kernel.apparmor_restrict_unprivileged_userns=1 the kernel exempts exactly two
/// things, /usr/bin/bwrap and Steam's own tree. pressure-vessel prefers the
/// runtime's srt-bwrap, which is neither, so it runs unconfined, cannot write
/// uid_map, and falls back to /usr/bin/bwrap - whose profile stacks every child
/// into unpriv_bwrap with no CAP_SYS_ADMIN. The nested srt-bwrap then dies with
/// "No permissions to create a new namespace", blaming kernel switches that are
/// fine. See docs/steamrt4-userns-apparmor.md.
///
/// Sent through this service the command runs as a child of Steam, whose profile
/// is flags=(unconfined) and grants userns to its whole tree, so srt-bwrap
/// succeeds on the first attempt and the fallback is never taken. Verified:
/// `--alongside-steam -- cat /proc/self/attr/current` prints "steam (unconfined)".
/// </summary>
internal static class SteamLauncherService
{
	/// <summary>
	/// The bus name that means "Steam is up and its launcher service is running".
	/// Matched exactly: --list also reports org.freedesktop.portal.Flatpak, and a
	/// per-instance suffix (...LaunchAlongsideSteam.Instance76066) which is not the
	/// name to connect to.
	/// </summary>
	public const string BusName = "com.steampowered.PressureVessel.LaunchAlongsideSteam";

	/// <summary>
	/// Whether the service is up. This is the authoritative test - `pgrep steam`
	/// is not, because the client can be running while the service is not (an old
	/// Steam build, or one still starting).
	/// </summary>
	public static bool IsAvailable( SteamRt4Install install )
	{
		if ( !File.Exists( install.LaunchClient ) )
			return false;

		if ( !Run( new[] { install.LaunchClient, "--list" }, 5000, out var output ) )
			return false;

		foreach ( var line in output.Split( '\n' ) )
		{
			if ( line.Trim() == "--bus-name=" + BusName )
				return true;
		}

		return false;
	}

	/// <summary>
	/// Prefixes a command so the service runs it.
	///
	/// The environment MUST be passed explicitly. The child inherits the SERVICE's
	/// environment - Steam's - and not the caller's; that is what --pass-env exists
	/// for. Verified: SBOX_TEST=x set on the client is unset in the child, and
	/// present only when handed over as --env. Anything relying on inheritance
	/// would fail silently, which is why this takes the dictionary rather than
	/// reading Environment itself.
	///
	/// Inheriting Steam's environment is correct, not a compromise: run-in-steamrt4
	/// goes through pressure-vessel-unruntime, which strips Steam's LD_LIBRARY_PATH,
	/// LD_PRELOAD and PATH back off before pressure-vessel-wrap. That is the same
	/// path Steam itself takes to launch a game.
	///
	/// Never pass --terminate: it terminates the launcher SERVICE - Steam's - not
	/// the child.
	/// </summary>
	public static List<string> Wrap(
		SteamRt4Install install,
		IReadOnlyList<string> command,
		string workingDirectory,
		IReadOnlyDictionary<string, string> environment )
	{
		var wrapped = new List<string> { install.LaunchClient, "--alongside-steam" };

		// The service's own cwd otherwise, which is wherever Steam was started.
		if ( !string.IsNullOrEmpty( workingDirectory ) )
			wrapped.Add( "--directory=" + workingDirectory );

		foreach ( var pair in environment )
			wrapped.Add( "--env=" + pair.Key + "=" + pair.Value );

		wrapped.Add( "--" );
		wrapped.AddRange( command );

		return wrapped;
	}

	/// <summary>
	/// Runs a command to completion, merging both streams. False when it could not
	/// be started, timed out, or exited non-zero.
	/// </summary>
	private static bool Run( IReadOnlyList<string> command, int timeoutMs, out string output )
	{
		output = string.Empty;

		var info = new ProcessStartInfo
		{
			FileName = command[0],
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		for ( var i = 1; i < command.Count; i++ )
			info.ArgumentList.Add( command[i] );

		try
		{
			using var process = Process.Start( info );

			if ( process is null )
				return false;

			output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
			process.WaitForExit( timeoutMs );

			return process.HasExited && process.ExitCode == 0;
		}
		catch
		{
			return false;
		}
	}
}

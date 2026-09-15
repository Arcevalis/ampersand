using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Ampersand;

internal sealed class SteamRt4Install
{
	public string Path { get; }
	public string Version { get; }

	public SteamRt4Install( string path, string version )
	{
		Path = path;
		Version = version;
	}

	/// <summary>
	/// The helper that runs a command inside the container. Valve generates
	/// both this and <c>run</c> from the same template (they are identical in
	/// sniper); probe the steamrt4 name first, then the generic one, so a
	/// tool revision that ships only one of them still resolves.
	/// </summary>
	public string RunScript
	{
		get
		{
			foreach ( var name in new[] { "run-in-steamrt4", "run" } )
			{
				var candidate = System.IO.Path.Combine( Path, name );
				if ( File.Exists( candidate ) )
					return candidate;
			}

			return System.IO.Path.Combine( Path, "run-in-steamrt4" );
		}
	}

	public string RequirementsCheck =>
		System.IO.Path.Combine( Path, "pressure-vessel", "bin", "steam-runtime-check-requirements" );

	/// <summary>
	/// The client that hands a command to the launcher service running alongside
	/// Steam. Everything ampersand runs in the container goes through this - see
	/// <see cref="SteamLauncherService"/>.
	/// </summary>
	public string LaunchClient =>
		System.IO.Path.Combine( Path, "pressure-vessel", "bin", "steam-runtime-launch-client" );
}

/// <summary>
/// Finds Steam Linux Runtime 4.0 (steamrt4) and decides whether this host can
/// actually start a container in it.
///
/// Discovery has to cope with every Steam packaging - the Debian package, the
/// upstream tarball, Flatpak and Snap all spell the data root differently - and
/// with the runtime living in any registered library folder rather than the
/// default one.
/// </summary>
internal static class SteamRt4Runtime
{
	public const int SteamAppId = 4183110;

	private static readonly Regex LibraryPath = new( "\"path\"\\s+\"([^\"]*)\"", RegexOptions.Compiled );

	public static SteamRt4Install? Find()
	{
		foreach ( var library in Libraries() )
		{
			var candidate = Path.Combine( library, "steamapps", "common", "SteamLinuxRuntime_4" );

			if ( File.Exists( Path.Combine( candidate, "run-in-steamrt4" ) )
				|| File.Exists( Path.Combine( candidate, "run" ) ) )
				return new SteamRt4Install( candidate, ReadVersion( candidate ) );
		}

		return null;
	}

	private static IEnumerable<string> Libraries()
	{
		var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		var xdgData = Environment.GetEnvironmentVariable( "XDG_DATA_HOME" );

		var roots = new List<string>
		{
			Path.Combine( home, ".steam", "steam" ),
			Path.Combine( home, ".steam", "root" ),
			Path.Combine( home, ".steam", "debian-installation" ),
			Path.Combine( home, ".local", "share", "Steam" ),
			Path.Combine( home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam" ),
			Path.Combine( home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam" ),
			Path.Combine( home, "snap", "steam", "common", ".local", "share", "Steam" ),
			"/usr/share/steam",
			"/usr/local/share/steam"
		};

		if ( !string.IsNullOrEmpty( xdgData ) )
			roots.Insert( 3, Path.Combine( xdgData, "Steam" ) );

		var seen = new HashSet<string>();

		foreach ( var root in roots )
		{
			if ( !Directory.Exists( Path.Combine( root, "steamapps" ) ) )
				continue;

			if ( seen.Add( Resolve( root ) ) )
				yield return root;

			// Every additional library folder the install registers.
			var vdf = Path.Combine( root, "steamapps", "libraryfolders.vdf" );
			if ( !File.Exists( vdf ) )
				continue;

			string text;
			try
			{
				text = File.ReadAllText( vdf );
			}
			catch
			{
				continue;
			}

			foreach ( Match match in LibraryPath.Matches( text ) )
			{
				var library = match.Groups[1].Value;

				if ( Directory.Exists( Path.Combine( library, "steamapps" ) ) && seen.Add( Resolve( library ) ) )
					yield return library;
			}
		}
	}

	private static string Resolve( string path )
	{
		try
		{
			return Directory.ResolveLinkTarget( path, true )?.FullName ?? Path.GetFullPath( path );
		}
		catch
		{
			return path;
		}
	}

	private static string ReadVersion( string installPath )
	{
		try
		{
			foreach ( var line in File.ReadAllLines( Path.Combine( installPath, "VERSIONS.txt" ) ) )
			{
				if ( !line.StartsWith( "depot", StringComparison.Ordinal ) )
					continue;

				var fields = line.Split( '\t', StringSplitOptions.RemoveEmptyEntries );
				if ( fields.Length >= 2 )
					return fields[1].Trim();
			}
		}
		catch
		{
			// fall through
		}

		return "unknown version";
	}

	/// <summary>
	/// pressure-vessel needs bubblewrap and unprivileged user namespaces. Distros
	/// disable those three different ways, so turn a failure into the command that
	/// fixes it rather than an opaque error.
	///
	/// Run THROUGH the launcher service, because run directly it answers about the
	/// wrong context and lies: it exits 0 on an Ubuntu host that cannot in fact
	/// start a container, because pressure-vessel's fallback to /usr/bin/bwrap
	/// works well enough for the probe while the real launch dies later on a
	/// nested bwrap. Asked from inside Steam's profile it tests what a launch will
	/// actually do.
	/// </summary>
	public static bool CheckRequirements( SteamRt4Install install, out List<string> problems )
	{
		problems = new List<string>();

		var checker = install.RequirementsCheck;
		if ( !File.Exists( checker ) )
		{
			problems.Add( "steam-runtime-check-requirements is missing from " + install.Path );
			return false;
		}

		var command = SteamLauncherService.Wrap(
			install, new[] { checker }, string.Empty, new Dictionary<string, string>() );

		var info = new ProcessStartInfo
		{
			FileName = command[0],
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		for ( var i = 1; i < command.Count; i++ )
			info.ArgumentList.Add( command[i] );

		var output = string.Empty;
		int exitCode;

		try
		{
			using var process = Process.Start( info );

			if ( process is null )
			{
				problems.Add( "could not run " + checker );
				return false;
			}

			output = ( process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd() ).Trim();
			process.WaitForExit( 30000 );
			exitCode = process.HasExited ? process.ExitCode : -1;
		}
		catch ( Exception e )
		{
			problems.Add( "could not run " + checker + " - " + e.Message );
			return false;
		}

		if ( exitCode == 0 )
			return true;

		if ( output.Length > 0 )
			problems.Add( output );

		if ( ReadFlag( "/proc/sys/kernel/unprivileged_userns_clone" ) == 0 )
		{
			problems.Add( "Debian-style userns switch is off:" );
			problems.Add( "    sudo sysctl -w kernel.unprivileged_userns_clone=1" );
		}

		if ( ReadFlag( "/proc/sys/user/max_user_namespaces" ) == 0 )
		{
			problems.Add( "RHEL-style userns limit is zero:" );
			problems.Add( "    sudo sysctl -w user.max_user_namespaces=15000" );
		}

		// The probe runs `bwrap --ro-bind ... true`. If bwrap starts but cannot
		// exec, the namespace came up while the filesystem did not - a different
		// fault from the userns switches above, and none of those hints apply.
		if ( output.Contains( "execvp", StringComparison.OrdinalIgnoreCase )
			|| output.Contains( "No such file or directory", StringComparison.Ordinal ) )
		{
			problems.Add( "bwrap started but could not execute inside the sandbox, so the" );
			problems.Add( "container has no usable filesystem. This is NOT the userns switches above." );
			problems.Add( "Check by hand:" );
			problems.Add( "    bwrap --ro-bind / / true      # should print nothing and exit 0" );
			problems.Add( "Common causes: running inside another container (distrobox, toolbox," );
			problems.Add( "Flatpak), an immutable/atomic distro, or an incomplete runtime download" );
			problems.Add( "- verify " + install.Path + " contains a steamrt4_platform_* directory." );
		}

		if ( problems.Count == 0 )
			problems.Add( "steam-runtime-check-requirements failed with exit code " + exitCode );

		return false;
	}

	private static int ReadFlag( string path )
	{
		try
		{
			return int.TryParse( File.ReadAllText( path ).Trim(), out var value ) ? value : -1;
		}
		catch
		{
			return -1;
		}
	}
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Ampersand;

/// <summary>
/// Resolves the engine's shared-library dependencies on the host AND inside the
/// Steam runtime, because the two are different sets.
///
/// Checking only the host is a trap: every binary in game/bin/linuxsteamrt64
/// resolves cleanly inside sniper with nothing missing, while the libraries
/// that actually stop the engine booting - libunwind and OpenSSL 3, needed by
/// the .NET runtime - are missing from the CONTAINER. A host-only check reports
/// all-clear on a setup that cannot start.
/// </summary>
internal static class DependencyCheck
{
	/// <summary>
	/// One ldd sweep, run as a single shell command so the container is
	/// entered once rather than once per binary.
	/// </summary>
	private const string SweepScript = """
		# Mirror _common.sh: the shim cache must join the path INSIDE the
		# container, because LD_LIBRARY_PATH set outside is discarded by
		# pressure-vessel. Without this the sweep reports what sniper lacks
		# natively rather than what a real launch actually sees.
		LD_LIBRARY_PATH="$SBOX_NATIVE${SBOX_SNIPER_COMPAT:+:$SBOX_SNIPER_COMPAT}"
		export LD_LIBRARY_PATH
		total=0
		bad=0
		for dir in "$SBOX_NATIVE" "$SBOX_DOTNET"; do
			[ -d "$dir" ] || continue
			for f in "$dir"/*; do
				[ -f "$f" ] || continue
				case "$f" in *.sh|*.json|*.txt|*.pdb) continue;; esac
				out=$( ldd "$f" 2>/dev/null ) || continue
				case "$out" in *"not a dynamic executable"*) continue;; esac
				total=$(( total + 1 ))
				miss=$( printf '%s\n' "$out" | awk '/not found/ { printf "%s ", $1 }' )
				if [ -n "$miss" ]; then
					bad=$(( bad + 1 ))
					echo "MISS|$( basename "$f" )|$miss"
				fi
			done
		done
		echo "SUMMARY|$total|$bad"
		""";

	public static void Run( string repoRoot, Action<string> emit )
	{
		var native = Path.Combine( repoRoot, "game", "bin", "linuxsteamrt64" );
		var dotnet = FindDotnetRuntime( repoRoot );

		emit( Ansi.Bold + Ansi.White + "=== dependency check ===" + Ansi.NoBold + Ansi.Reset );
		emit( "" );
		emit( Ansi.Dim + "native   " + Ansi.Reset + native
			+ ( Directory.Exists( native ) ? "" : Ansi.Red + "   (MISSING)" + Ansi.Reset ) );
		emit( Ansi.Dim + "dotnet   " + Ansi.Reset
			+ ( dotnet ?? Ansi.Red + "not found - run Build S&Box" + Ansi.Reset ) );
		emit( "" );

		var sweep = new List<string> { "/bin/sh", "-c", SweepScript };

		var env = new Dictionary<string, string>
		{
			["SBOX_NATIVE"] = native,
			["SBOX_DOTNET"] = dotnet ?? string.Empty,
			["SBOX_SNIPER_COMPAT"] = string.Empty
		};

		// --- host ---------------------------------------------------------
		emit( Ansi.Bold + Ansi.Cyan + "--- host ---" + Ansi.NoBold + Ansi.Reset );
		ReportSweep( sweep, env, emit );
		emit( "" );

		// --- container ----------------------------------------------------
		emit( Ansi.Bold + Ansi.Cyan + "--- steam runtime (sniper) ---" + Ansi.NoBold + Ansi.Reset );

		var install = SniperRuntime.Find();
		if ( install is null )
		{
			emit( Ansi.Red + "  sniper is not installed" + Ansi.Reset
				+ " - steam steam://install/" + SniperRuntime.SteamAppId );
			emit( "" );
			ReportShimCache( emit );
			return;
		}

		emit( Ansi.Dim + "  runtime  " + Ansi.Reset + install.Path );
		emit( Ansi.Dim + "  version  " + Ansi.Reset + install.Version );

		// The container is only reachable through Steam's launcher service, so with
		// Steam down there is nothing to sweep - and saying so beats a wall of
		// "not found" that looks like a broken install.
		if ( !SteamLauncherService.IsAvailable( install ) )
		{
			emit( Ansi.Red + "  Steam is not running" + Ansi.Reset
				+ " - the container is entered through Steam's launcher" );
			emit( Ansi.Dim + "  service, so this half cannot be checked. Start Steam and run this again."
				+ Ansi.Reset );
			emit( "" );
			ReportShimCache( emit );
			return;
		}

		emit( Ansi.Green + "  steam launcher service up" + Ansi.Reset );

		if ( !SniperRuntime.CheckRequirements( install, out var problems ) )
		{
			emit( Ansi.Red + "  this host cannot start a container:" + Ansi.Reset );

			foreach ( var problem in problems )
				emit( Ansi.Yellow + "    " + problem + Ansi.Reset );

			emit( "" );
			ReportShimCache( emit );
			return;
		}

		emit( Ansi.Green + "  requirements OK" + Ansi.Reset );

		var cache = SniperCompat.CacheDirectory;
		env["SBOX_SNIPER_COMPAT"] = cache;

		var sniperCommand = SteamLauncherService.Wrap(
			install,
			new List<string> { install.RunScript, "--filesystem=" + cache, "--" }.Concat( sweep ).ToList(),
			repoRoot,
			env );

		ReportSweep( sniperCommand, env, emit );
		emit( "" );
		ReportShimCache( emit );
	}

	/// <summary>
	/// Runs one sweep. The environment is set on the process here for the HOST
	/// sweep; the container sweep gets the same dictionary a second time, as --env
	/// arguments, because nothing crosses the launcher service by inheritance.
	/// A container sweep reporting 0 binaries means that hand-over was missed.
	/// </summary>
	private static void ReportSweep(
		IReadOnlyList<string> command, IReadOnlyDictionary<string, string> env, Action<string> emit )
	{
		var info = new ProcessStartInfo
		{
			FileName = command[0],
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		for ( var i = 1; i < command.Count; i++ )
			info.ArgumentList.Add( command[i] );

		foreach ( var pair in env )
			info.Environment[pair.Key] = pair.Value;

		string output;

		try
		{
			using var process = Process.Start( info );

			if ( process is null )
			{
				emit( Ansi.Red + "  could not run the sweep" + Ansi.Reset );
				return;
			}

			output = process.StandardOutput.ReadToEnd();
			process.WaitForExit( 120000 );
		}
		catch ( Exception e )
		{
			emit( Ansi.Red + "  sweep failed - " + e.Message + Ansi.Reset );
			return;
		}

		var found = false;

		foreach ( var line in output.Split( '\n' ) )
		{
			if ( line.StartsWith( "MISS|", StringComparison.Ordinal ) )
			{
				var parts = line.Split( '|' );
				if ( parts.Length >= 3 )
				{
					var libraries = parts[2].Trim();
					var optional = AllOptional( libraries );

					if ( !optional )
						found = true;

					emit( optional
						? Ansi.Dim + "  optional " + parts[1].PadRight( 42 ) + libraries + Ansi.Reset
						: Ansi.Red + "  MISSING  " + Ansi.Reset + parts[1].PadRight( 42 )
							+ Ansi.Yellow + libraries + Ansi.Reset );
				}
			}
			else if ( line.StartsWith( "SUMMARY|", StringComparison.Ordinal ) )
			{
				var parts = line.Split( '|' );
				if ( parts.Length >= 3 )
				{
					emit( Ansi.Dim + "  checked " + parts[1] + " binaries, " + parts[2].Trim()
						+ " with unresolved libraries" + Ansi.Reset );
				}
			}
		}

		if ( !found )
			emit( Ansi.Green + "  nothing missing that matters" + Ansi.Reset );
	}

	/// <summary>
	/// liblttng-ust is the CoreCLR tracing provider. .NET skips it silently
	/// when absent, and neither sniper nor most desktops ship it, so reporting
	/// it as a failure only sends people chasing a non-problem.
	/// </summary>
	private static readonly string[] OptionalLibraries = { "liblttng-ust" };

	private static bool AllOptional( string libraries )
	{
		foreach ( var library in libraries.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var known = false;

			foreach ( var optional in OptionalLibraries )
			{
				if ( library.StartsWith( optional, StringComparison.Ordinal ) )
				{
					known = true;
					break;
				}
			}

			if ( !known )
				return false;
		}

		return true;
	}

	private static void ReportShimCache( Action<string> emit )
	{
		emit( Ansi.Bold + Ansi.Cyan + "--- sniper compat cache ---" + Ansi.NoBold + Ansi.Reset );
		emit( Ansi.Dim + "  " + SniperCompat.CacheDirectory + Ansi.Reset );

		foreach ( var library in SniperCompat.RequiredLibraries )
		{
			var path = Path.Combine( SniperCompat.CacheDirectory, library );
			emit( File.Exists( path )
				? Ansi.Green + "  present  " + Ansi.Reset + library
				: Ansi.Red + "  ABSENT   " + Ansi.Reset + library );
		}

		emit( "" );
		emit( Ansi.Dim + "Sniper ships neither libunwind nor OpenSSL 3, so these are copied from the" );
		emit( "host on first containerised launch. Without them the engine fails with" );
		emit( "\"HRESULT: 0x80008088\" or a TypeInitializationException in Interop.Crypto." + Ansi.Reset );
	}

	/// <summary>
	/// The engine's own copy of the shared framework, which is not the host's. The launchers are
	/// built with -p:AppHostRelativeDotNet=bin/dotnet (BuildManaged.cs), so this is the runtime a
	/// real launch resolves against; a system install under /usr/lib/dotnet is not even visible
	/// inside the sniper container, which brings its own /usr.
	///
	/// It moved to game/bin/dotnet in sbox-public 9eba55b6 ("game/dotnet -> game/bin/dotnet").
	/// The old top-level path is still probed second, so a tree built before that move keeps
	/// reporting instead of claiming the runtime is absent.
	///
	/// Pointing this at the wrong directory fails quietly as well as loudly: SBOX_DOTNET goes
	/// over as an empty string, the sweep's `[ -d "$dir" ] || continue` drops the runtime half,
	/// and the summary comes back clean - while it is precisely the .NET runtime's libunwind and
	/// OpenSSL 3 that are missing inside the container.
	/// </summary>
	private static string? FindDotnetRuntime( string repoRoot )
	{
		string[] candidates =
		[
			Path.Combine( repoRoot, "game", "bin", "dotnet" ),
			Path.Combine( repoRoot, "game", "dotnet" )
		];

		foreach ( var candidate in candidates )
		{
			var shared = Path.Combine( candidate, "shared", "Microsoft.NETCore.App" );

			if ( !Directory.Exists( shared ) )
				continue;

			var versions = Directory.GetDirectories( shared );
			Array.Sort( versions, StringComparer.Ordinal );

			if ( versions.Length > 0 )
				return versions[^1];
		}

		return null;
	}
}

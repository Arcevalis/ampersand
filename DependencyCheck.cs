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
/// resolves cleanly inside steamrt4 with nothing missing, while the libraries
/// that actually stop the engine booting - libunwind, needed by the .NET
/// runtime - may be missing from the CONTAINER. A host-only check reports
/// all-clear on a setup that cannot start. (steamrt4 ships OpenSSL 3 natively,
/// so the sniper-era OpenSSL shims are gone. Qt's libpcre2-16.so.0 is missing
/// from the platform too - ampersand shims it from the host at launch until
/// the engine ships it.)
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
		# pressure-vessel. Without this the sweep reports what steamrt4 lacks
		# natively rather than what a real launch actually sees.
		LD_LIBRARY_PATH="$SBOX_NATIVE${SBOX_STEAMRT4_COMPAT:+:$SBOX_STEAMRT4_COMPAT}"
		export LD_LIBRARY_PATH
		total=0
		bad=0
		sigs=""
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
					# The tree ships versioned copies as distinct files
					# (libQt5Core.so, .so.5, .so.5.15, .so.5.15.2 are four
					# identical 83MB regular files, not symlinks). Report each
					# unique content once.
					sig="$f"
					if command -v md5sum >/dev/null 2>&1; then
						sig=$( md5sum < "$f" 2>/dev/null ) || sig="$f"
					fi
					case "$sigs" in *"|$sig|"*) continue;; esac
					sigs="$sigs|$sig|"
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
			["SBOX_STEAMRT4_COMPAT"] = string.Empty
		};

		// --- host ---------------------------------------------------------
		emit( Ansi.Bold + Ansi.Cyan + "--- host ---" + Ansi.NoBold + Ansi.Reset );
		ReportSweep( sweep, env, emit );
		emit( "" );

		// --- container ----------------------------------------------------
		emit( Ansi.Bold + Ansi.Cyan + "--- steam runtime (steamrt4) ---" + Ansi.NoBold + Ansi.Reset );

		var install = SteamRt4Runtime.Find();
		if ( install is null )
		{
			emit( Ansi.Red + "  steamrt4 is not installed" + Ansi.Reset
				+ " - steam steam://install/" + SteamRt4Runtime.SteamAppId );
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

		if ( !SteamRt4Runtime.CheckRequirements( install, out var problems ) )
		{
			emit( Ansi.Red + "  this host cannot start a container:" + Ansi.Reset );

			foreach ( var problem in problems )
				emit( Ansi.Yellow + "    " + problem + Ansi.Reset );

			emit( "" );
			ReportShimCache( emit );
			return;
		}

		emit( Ansi.Green + "  requirements OK" + Ansi.Reset );

		var cache = SteamRt4Compat.CacheDirectory;
		env["SBOX_STEAMRT4_COMPAT"] = cache;

		var runtimeCommand = SteamLauncherService.Wrap(
			install,
			new List<string> { install.RunScript, "--filesystem=" + cache, "--" }.Concat( sweep ).ToList(),
			repoRoot,
			env );

		ReportSweep( runtimeCommand, env, emit );
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
		var errors = 0;
		var advisories = 0;
		var total = "?";
		var pcre2 = new List<string>();

		foreach ( var line in output.Split( '\n' ) )
		{
			if ( line.StartsWith( "MISS|", StringComparison.Ordinal ) )
			{
				var parts = line.Split( '|' );
				if ( parts.Length >= 3 )
				{
					var binary = parts[1];
					var libraries = parts[2].Trim();

					if ( AllOptional( libraries ) )
					{
						advisories++;
						emit( Ansi.Dim + "  optional " + binary.PadRight( 42 ) + libraries + Ansi.Reset );
					}
					else if ( AllIcu( libraries ) )
					{
						advisories++;
						// The shipped game/bin/dotnet tree carries fully-versioned
						// libicu*.so.72.1 files but not the .so.72 soname links the
						// loader asks for. .NET shrugs and falls back to invariant
						// globalization, so this is an engine-side packaging note,
						// not a stop-the-presses missing dependency.
						emit( Ansi.Yellow + "  advisory " + Ansi.Reset + binary.PadRight( 42 )
							+ Ansi.Dim + "shipped tree lacks .so.72 soname links ("
							+ libraries + "); invariant-mode fallback" + Ansi.Reset );
					}
					else if ( AllPcre2( libraries ) )
					{
						pcre2.Add( binary );
					}
					else
					{
						found = true;
						errors++;
						emit( Ansi.Red + "  MISSING  " + Ansi.Reset + binary.PadRight( 42 )
							+ Ansi.Yellow + libraries + Ansi.Reset );
					}
				}
			}
			else if ( line.StartsWith( "SUMMARY|", StringComparison.Ordinal ) )
			{
				var parts = line.Split( '|' );
				if ( parts.Length >= 3 )
					total = parts[1];
			}
		}

		// The shell counts every file with any miss; re-report with the
		// advisories and the grouped pcre2 note folded out, so the count
		// agrees with the red lines above it.
		var noted = advisories + pcre2.Count;
		emit( Ansi.Dim + "  checked " + total + " binaries, " + errors + " with unresolved libraries"
			+ ( noted > 0 ? " (+" + noted + " explained below)" : "" ) + Ansi.Reset );

		if ( pcre2.Count > 0 )
			ReportPcre2( pcre2, emit );

		if ( !found )
			emit( Ansi.Green + "  nothing missing that matters" + Ansi.Reset );
	}

	/// <summary>
	/// Qt 5.15 links libpcre2-16.so.0, which the steamrt4 platform does not ship
	/// (it carries only libpcre2-8). Every Qt/tool binary then reports the same
	/// single miss, so they are folded into one note.
	///
	/// The misses describe the shipped tree, not a live launch: at launch the
	/// compat cache joins LD_LIBRARY_PATH inside the container, so a seeded -
	/// or seedable - shim means these resolve in practice. Only a library the
	/// host cannot provide either is a real problem, and even that blocks just
	/// the editor's Qt tools, never the game.
	/// </summary>
	private static void ReportPcre2( List<string> binaries, Action<string> emit )
	{
		var uncached = new List<string>();
		string? hint = null;

		foreach ( var library in SteamRt4Compat.BestEffortLibraries )
		{
			if ( File.Exists( Path.Combine( SteamRt4Compat.CacheDirectory, library ) ) )
				continue;

			uncached.Add( library );

			if ( hint is null && !SteamRt4Compat.ProbeHost( library, out var found ) )
				hint = found;
		}

		if ( uncached.Count == 0 )
		{
			emit( Ansi.Dim + "  shimmed  Qt/tool stack's libpcre2-16.so.0 is in the compat cache;"
				+ " these resolve at launch" + Ansi.Reset );
		}
		else if ( hint is null )
		{
			emit( Ansi.Dim + "  shimmed  Qt/tool stack's libpcre2-16.so.0 seeds from this host"
				+ " on first containerised launch" + Ansi.Reset );
		}
		else
		{
			emit( Ansi.Yellow + "  Qt/tool stack needs libpcre2-16.so.0, which steamrt4 does not ship"
				+ Ansi.Reset );
			emit( Ansi.Yellow + "  and this host lacks it - run: " + hint + Ansi.Reset );
		}

		var shown = string.Join( ", ", binaries.Take( 6 ) );
		if ( binaries.Count > 6 )
			shown += ", ...";

		emit( Ansi.Dim + "  affected (" + binaries.Count + "): " + shown + Ansi.Reset );
		emit( Ansi.Dim + "  Editor/tools affected; game client unaffected."
			+ " Long-term fix ships engine-side." + Ansi.Reset );
	}

	/// <summary>
	/// True when every missing library is a shipped-ICU soname (see above).
	/// </summary>
	private static bool AllIcu( string libraries )
	{
		foreach ( var library in libraries.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
		{
			if ( !library.StartsWith( "libicu", StringComparison.Ordinal ) )
				return false;
		}

		return true;
	}

	/// <summary>
	/// True when every missing library is the steamrt4-absent pcre2-16 (see above).
	/// </summary>
	private static bool AllPcre2( string libraries )
	{
		foreach ( var library in libraries.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) )
		{
			if ( !library.StartsWith( "libpcre2-16", StringComparison.Ordinal ) )
				return false;
		}

		return true;
	}

	/// <summary>
	/// liblttng-ust is the CoreCLR tracing provider. .NET skips it silently
	/// when absent, and neither steamrt4 nor most desktops ship it, so reporting
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

	/// <summary>
	/// The check is report-only and never seeds the cache (that happens on first
	/// containerised launch, via MainWindow.PrepareRuntime). So an empty cache is
	/// the normal pre-launch state, not a failure: entries the host can provide
	/// render as dim "pending", and only a library missing on the HOST - which no
	/// launch could seed - renders red, with the package that provides it.
	/// Editor-only shims get the same rows, tagged; a host miss there warns but
	/// never blocks, because the game launches without them.
	/// </summary>
	private static void ReportShimCache( Action<string> emit )
	{
		emit( Ansi.Bold + Ansi.Cyan + "--- steamrt4 compat cache ---" + Ansi.NoBold + Ansi.Reset );
		emit( Ansi.Dim + "  " + SteamRt4Compat.CacheDirectory + Ansi.Reset );

		var blocked = false;

		foreach ( var library in SteamRt4Compat.RequiredLibraries )
		{
			if ( !ReportCacheRow( library, false, emit ) )
				blocked = true;
		}

		foreach ( var library in SteamRt4Compat.BestEffortLibraries )
			ReportCacheRow( library, true, emit );

		emit( "" );

		if ( blocked )
		{
			emit( Ansi.Yellow + "Install the missing package(s) above, then do one" + Ansi.Reset );
			emit( Ansi.Yellow + "containerised launch to seed the cache." + Ansi.Reset );
			return;
		}

		emit( Ansi.Dim + "libunwind is not guaranteed in steamrt4, so it is copied from the" );
		emit( "host on first containerised launch. Without it the engine can fail with" );
		emit( "\"HRESULT: 0x80008088\"." + Ansi.Reset );
	}

	/// <summary>
	/// One cache row. Returns false only for a required library the host cannot
	/// provide - the one state no launch can recover from. A missing editor-only
	/// shim returns true: unfortunate for Qt tools, irrelevant to the game.
	/// </summary>
	private static bool ReportCacheRow( string library, bool editorOnly, Action<string> emit )
	{
		var path = Path.Combine( SteamRt4Compat.CacheDirectory, library );
		var tag = editorOnly ? "  [editor-only]" : "";

		if ( File.Exists( path ) )
		{
			emit( Ansi.Green + "  present  " + Ansi.Reset + library + Ansi.Dim + tag + Ansi.Reset );
			return true;
		}

		if ( SteamRt4Compat.ProbeHost( library, out var hint ) )
		{
			emit( Ansi.Dim + "  pending  " + Ansi.Reset + library + Ansi.Dim + tag
				+ " - on this host, seeds on first containerised launch" + Ansi.Reset );
			return true;
		}

		emit( Ansi.Red + "  HOST LACKS " + Ansi.Reset + library + Ansi.Dim + tag + Ansi.Reset
			+ Ansi.Yellow + "   run: " + hint + Ansi.Reset );
		return editorOnly;
	}

	/// <summary>
	/// The engine's own copy of the shared framework, which is not the host's. The launchers are
	/// built with -p:AppHostRelativeDotNet=bin/dotnet (BuildManaged.cs), so this is the runtime a
	/// real launch resolves against; a system install under /usr/lib/dotnet is not even visible
	/// inside the steamrt4 container, which brings its own /usr.
	///
	/// It moved to game/bin/dotnet in sbox-public 9eba55b6 ("game/dotnet -> game/bin/dotnet").
	/// The old top-level path is still probed second, so a tree built before that move keeps
	/// reporting instead of claiming the runtime is absent.
	///
	/// Pointing this at the wrong directory fails quietly as well as loudly: SBOX_DOTNET goes
	/// over as an empty string, the sweep's `[ -d "$dir" ] || continue` drops the runtime half,
	/// and the summary comes back clean - while it is precisely the .NET runtime's libunwind
	/// that may be missing inside the container.
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

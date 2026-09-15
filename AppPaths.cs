using System;
using System.IO;

namespace Ampersand;

internal static class AppPaths
{
	/// <summary>
	/// Where the editable launch scripts live next to the built binary.
	/// Source is repo/apps/*.sh, copied at build to &lt;OutDir&gt;/scripts/*.sh
	/// via ampersand.csproj Link="scripts/...". They stay loose files so users
	/// can edit them at runtime and the next launch picks up the change.
	/// </summary>
	public static string ScriptsDir => Path.Combine( AppContext.BaseDirectory, "scripts" );

	/// <summary>
	/// Resolve the scripts directory, with fallbacks for dev runs where the
	/// copy hasn't happened yet (e.g. dotnet run before first build).
	/// </summary>
	public static string? FindScriptsDir()
	{
		var built = ScriptsDir;
		if ( Directory.Exists( built ) && File.Exists( Path.Combine( built, "sbox.sh" ) ) )
			return built;

		// Dev fallback: AppContext.BaseDirectory is ampersand/bin/Debug/net10.0/
		// Walk up looking for repo/apps/sbox.sh
		var dir = new DirectoryInfo( AppContext.BaseDirectory );
		for ( int i = 0; i < 6 && dir is not null; i++ )
		{
			var candidate = Path.Combine( dir.FullName, "apps", "sbox.sh" );
			if ( File.Exists( candidate ) )
				return Path.Combine( dir.FullName, "apps" );

			// Also handle nested net10.0 -> ampersand -> repo case where apps is
			// sibling of bin: repo/ampersand/apps
			var alt = Path.Combine( dir.FullName, "ampersand", "apps", "sbox.sh" );
			if ( File.Exists( alt ) )
				return Path.Combine( dir.FullName, "ampersand", "apps" );

			dir = dir.Parent;
		}

		// Last resort: try alongside the executable
		try
		{
			var exeDir = Path.GetDirectoryName( Environment.ProcessPath );
			if ( !string.IsNullOrEmpty( exeDir ) )
			{
				var cand = Path.Combine( exeDir, "scripts", "sbox.sh" );
				if ( File.Exists( cand ) )
					return Path.Combine( exeDir, "scripts" );
				cand = Path.Combine( exeDir, "apps", "sbox.sh" );
				if ( File.Exists( cand ) )
					return Path.Combine( exeDir, "apps" );
			}
		}
		catch { }

		return Directory.Exists( built ) ? built : null;
	}

	public static string? FindScript( string fileName )
	{
		var dir = FindScriptsDir();
		if ( dir is null ) return null;
		var path = Path.Combine( dir, fileName );
		return File.Exists( path ) ? path : null;
	}

	/// <summary>
	/// Writable cache for ampersand-owned build outputs (same XDG layout as
	/// RunLog/SteamRt4Compat): ~/.cache/sbox-ampersand/.
	/// </summary>
	public static string CacheDir
	{
		get
		{
			var cacheHome = Environment.GetEnvironmentVariable( "XDG_CACHE_HOME" );
			if ( string.IsNullOrEmpty( cacheHome ) )
				cacheHome = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), ".cache" );
			return Path.Combine( cacheHome, "sbox-ampersand" );
		}
	}

	/// <summary>
	/// Default location of the built SDL embed-fix library. It lives in the
	/// cache (not the engine checkout) because the engine no longer ships the
	/// shim source; ampersand vendors it under apps/patches/ and builds it on
	/// demand. Overridable with SBOX_SDLWINFIX_SO.
	/// </summary>
	public static string SdlWinFixLibrary => Path.Combine( CacheDir, "libsdlwinfix.so" );

	/// <summary>
	/// The vendored SDL embed-fix source (apps/patches/sdlwinfix.c, shipped to
	/// scripts/patches/ at build time), or null if this install lacks it.
	/// </summary>
	public static string? FindSdlWinFixSource() => FindScript( Path.Combine( "patches", "sdlwinfix.c" ) );

	/// <summary>
	/// Default location of the built TEMPORARY XTEST edge-snap library
	/// (scene-view cursor gap on XWayland; remove once Facepunch fixes
	/// cursor capture natively). Same cache-dir/on-demand arrangement as above.
	/// Overridable with SBOX_XCONFCAPTURE_SO.
	/// </summary>
	public static string ConfineCaptureLibrary => Path.Combine( CacheDir, "libxconfinecapture.so" );

	/// <summary>
	/// The vendored TEMPORARY edge-snap source (apps/patches/xconfinecapture.c,
	/// shipped to scripts/patches/ at build time), or null if this install lacks it.
	/// </summary>
	public static string? FindConfineCaptureSource() => FindScript( Path.Combine( "patches", "xconfinecapture.c" ) );
}

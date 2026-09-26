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
	/// Default location of the built case-insensitive asset fallback library
	/// (wrong-case FBX references committed upstream; opt-in Casefold toggle,
	/// off by default). Same cache-dir/on-demand arrangement as above.
	/// Overridable with SBOX_CASEFOLD_SO.
	/// </summary>
	public static string CasefoldLibrary => Path.Combine( CacheDir, "libcasefold.so" );

	/// <summary>
	/// The vendored casefold source (apps/patches/casefold.c, shipped to
	/// scripts/patches/ at build time), or null if this install lacks it.
	/// </summary>
	public static string? FindCasefoldSource() => FindScript( Path.Combine( "patches", "casefold.c" ) );
}

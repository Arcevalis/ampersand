using System;
using System.IO;
using System.Text.Json;

namespace Ampersand;

internal sealed record SboxConfig( string SboxRoot, string? SboxServerGame = null, bool Casefold = false );

internal static class SboxSettings
{
	public static string ConfigDirectory
	{
		get
		{
			var dataHome = Environment.GetEnvironmentVariable( "XDG_DATA_HOME" );
			if ( string.IsNullOrEmpty( dataHome ) )
				dataHome = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), ".local", "share" );
			return Path.Combine( dataHome, "sbox-ampersand" );
		}
	}

	public static string ConfigPath => Path.Combine( ConfigDirectory, "settings.json" );

	/// <summary>
	/// True when path contains game/ and engine/ and game/sbox (matches _common.sh expectation).
	/// </summary>
	public static bool IsValid( string? root )
	{
		if ( string.IsNullOrWhiteSpace( root ) ) return false;
		try
		{
			return Directory.Exists( Path.Combine( root, "game" ) )
				&& Directory.Exists( Path.Combine( root, "engine" ) )
				&& File.Exists( Path.Combine( root, "game", "sbox" ) );
		}
		catch { return false; }
	}

	/// <summary>
	/// Accepts repo root, game/, or game/sbox and normalizes to repo root.
	/// Handles ~, quoted strings, env expansion.
	/// Returns null if cannot be resolved.
	/// </summary>
	public static string? Normalize( string? input )
	{
		if ( string.IsNullOrWhiteSpace( input ) ) return null;

		var s = input.Trim();

		// Strip surrounding quotes
		if ( ( s.StartsWith( "\"" ) && s.EndsWith( "\"" ) ) || ( s.StartsWith( "'" ) && s.EndsWith( "'" ) ) )
			s = s[1..^1].Trim();

		// Expand ~ and env vars
		if ( s.StartsWith( "~" ) )
		{
			var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
			if ( s == "~" ) s = home;
			else if ( s.StartsWith( "~/", StringComparison.Ordinal ) || s.StartsWith( "~\\", StringComparison.Ordinal ) )
				s = Path.Combine( home, s[2..] );
		}

		try { s = Environment.ExpandEnvironmentVariables( s ); } catch { }

		string? candidateDir;

		try
		{
			if ( File.Exists( s ) )
			{
				// If they picked a file (e.g. game/sbox), use its directory.
				candidateDir = Path.GetDirectoryName( Path.GetFullPath( s ) );
				if ( candidateDir is null ) return null;
			}
			else
			{
				candidateDir = Path.GetFullPath( s );
			}
		}
		catch { return null; }

		if ( candidateDir is null ) return null;

		// Walk up at most 3 levels looking for a valid root (covers repo root, game/, game/bin/...).
		var dir = new DirectoryInfo( candidateDir );

		for ( int i = 0; i < 4 && dir is not null; i++ )
		{
			var p = dir.FullName;

			if ( IsValid( p ) )
				return p;

			// If we are inside game/ but not at root, parent might be root.
			// Also if we are at .../game we would have just tested parent anyway on next iteration.
			dir = dir.Parent;
		}

		// No valid root found, return the original directory normalized (for error display).
		// But for validation failure we still return this so caller can show it.
		try { return Path.GetFullPath( candidateDir ); } catch { return candidateDir; }
	}

	public static SboxConfig? Load()
	{
		try
		{
			if ( !File.Exists( ConfigPath ) ) return null;
			var text = File.ReadAllText( ConfigPath );
			if ( string.IsNullOrWhiteSpace( text ) ) return null;

			using var doc = JsonDocument.Parse( text );
			var hasRoot = doc.RootElement.TryGetProperty( "sboxRoot", out var el ) || doc.RootElement.TryGetProperty( "SboxRoot", out el );
			string? raw = hasRoot ? el.GetString() : null;
			string? root = null;
			if ( !string.IsNullOrWhiteSpace( raw ) )
			{
				var norm = Normalize( raw );
				root = norm ?? raw;
			}
			else if ( hasRoot )
			{
				// Root was present but empty/whitespace, so keep as empty for display, but still allow serverGame.
				root = raw ?? "";
			}
			else
			{
				// No root key at all, so only allow load if serverGame exists (e.g. file created before root was set).
				// Otherwise it's not a valid config file.
				bool hasGame = doc.RootElement.TryGetProperty( "sboxServerGame", out _ ) || doc.RootElement.TryGetProperty( "SboxServerGame", out _ );
				if ( !hasGame ) return null;
				root = "";
			}

		// Optional: game ident (fss.bloodsigil) or path to .sbproj for sbox-server
		string? serverGame = null;
		if ( doc.RootElement.TryGetProperty( "sboxServerGame", out var sg ) || doc.RootElement.TryGetProperty( "SboxServerGame", out sg ) )
		{
			var sgRaw = sg.GetString();
			if ( !string.IsNullOrWhiteSpace( sgRaw ) )
				serverGame = NormalizeServerGame( sgRaw );
		}

		// Optional case-insensitive asset fallback (vendored
		// apps/patches/casefold.c). Absent key = off.
		var casefold = false;
		if ( doc.RootElement.TryGetProperty( "casefold", out var cf ) || doc.RootElement.TryGetProperty( "Casefold", out cf ) )
		{
			if ( cf.ValueKind == JsonValueKind.True ) casefold = true;
			else if ( cf.ValueKind == JsonValueKind.String )
				casefold = cf.GetString() is string cfs && ( cfs == "1" || cfs.Equals( "true", StringComparison.OrdinalIgnoreCase ) );
		}

		return new SboxConfig( root, serverGame, casefold );
		}
		catch { return null; }
	}

	/// <summary>
	/// Normalizes the sbox-server game value: trims, strips surrounding quotes,
	/// expands ~ and env vars for .sbproj paths, leaves idents (e.g. fss.bloodsigil) as-is.
	/// Returns null for empty input.
	/// </summary>
	public static string? NormalizeServerGame( string? input )
	{
		if ( string.IsNullOrWhiteSpace( input ) ) return null;

		var s = input.Trim();

		// Strip surrounding quotes (user may paste quoted path)
		if ( ( s.StartsWith( "\"" ) && s.EndsWith( "\"" ) ) || ( s.StartsWith( "'" ) && s.EndsWith( "'" ) ) )
			s = s[1..^1].Trim();

		if ( string.IsNullOrWhiteSpace( s ) ) return null;

		// If user pasted a full command fragment like "+game fss.bloodsigil" or "game fss.bloodsigil",
		// strip the leading game switch so we store only the ident/path. Launch will re-add "+game".
		// Handle: "+game", "-game", "game", with optional quotes around value.
		{
			var lower = s.ToLowerInvariant();
			string? prefix = null;
			if ( lower.StartsWith( "+game " ) ) prefix = s[6..];
			else if ( lower.StartsWith( "-game " ) ) prefix = s[6..];
			else if ( lower.StartsWith( "game " ) ) prefix = s[5..];
			if ( prefix is not null )
			{
				s = prefix.Trim();
				if ( ( s.StartsWith( "\"" ) && s.EndsWith( "\"" ) ) || ( s.StartsWith( "'" ) && s.EndsWith( "'" ) ) )
					s = s[1..^1].Trim();
				if ( string.IsNullOrWhiteSpace( s ) ) return null;
			}
		}

		// For .sbproj paths expand ~ and env vars and normalize slashes; for idents keep as-is.
		// Heuristic: contains '/' or '\' or ends with .sbproj -> treat as path.
		var isPath = s.Contains( '/' ) || s.Contains( '\\' ) || s.EndsWith( ".sbproj", StringComparison.OrdinalIgnoreCase );

		if ( isPath )
		{
			if ( s.StartsWith( "~" ) )
			{
				var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
				if ( s == "~" ) s = home;
				else if ( s.StartsWith( "~/", StringComparison.Ordinal ) || s.StartsWith( "~\\", StringComparison.Ordinal ) )
					s = Path.Combine( home, s[2..] );
			}

			try { s = Environment.ExpandEnvironmentVariables( s ); } catch { }

			// Don't require file to exist at save time; user may type path before project exists.
			// Just normalize separators via GetFullPath if it looks absolute.
			try
			{
				if ( Path.IsPathRooted( s ) )
					s = Path.GetFullPath( s );
			}
			catch { }
		}

		return string.IsNullOrWhiteSpace( s ) ? null : s;
	}

	public static string? GetSboxServerGame()
	{
		return Load()?.SboxServerGame;
	}

	public static bool GetCasefold()
	{
		try { return Load()?.Casefold == true; } catch { return false; }
	}

	public static void SaveCasefold( bool enabled )
	{
		var existing = Load();
		var root = existing?.SboxRoot ?? Resolve() ?? GetStalePersistedPath() ?? "";
		if ( string.IsNullOrWhiteSpace( root ) )
		{
			var detected = RepoRoot.Find();
			if ( detected is not null ) root = detected;
		}
		Save( root, existing?.SboxServerGame, existing?.Casefold );
	}

	public static void SaveServerGame( string? serverGame )
	{
		var existing = Load();
		var root = existing?.SboxRoot ?? Resolve() ?? GetStalePersistedPath() ?? "";
		// Don't overwrite stale/invalid root with empty, just preserve what we have.
		// If we have no root at all, saving server game alone still needs a placeholder.
		if ( string.IsNullOrWhiteSpace( root ) )
		{
			// No root known; try detection without saving migration if invalid.
			var detected = RepoRoot.Find();
			if ( detected is not null ) root = detected;
		}
		Save( root, serverGame );
	}

	public static void Save( string sboxRoot )
	{
		// Preserve existing serverGame and shim toggles when only root is being saved (e.g. Resolve migration).
		var existing = Load();
		Save( sboxRoot, existing?.SboxServerGame, existing?.Casefold );
	}

	public static void Save( string sboxRoot, string? serverGame )
	{
		var existing = Load();
		Save( sboxRoot, serverGame, existing?.Casefold );
	}

	public static void Save( string sboxRoot, string? serverGame, bool? casefold = null )
	{
		var norm = Normalize( sboxRoot ) ?? sboxRoot;
		var normGame = NormalizeServerGame( serverGame );
		var cfFlag = casefold ?? Load()?.Casefold ?? false;
		var dir = ConfigDirectory;
		Directory.CreateDirectory( dir );

		string json;
		if ( normGame is not null )
			json = JsonSerializer.Serialize( new { sboxRoot = norm, sboxServerGame = normGame, casefold = cfFlag }, new JsonSerializerOptions { WriteIndented = true } );
		else
			json = JsonSerializer.Serialize( new { sboxRoot = norm, casefold = cfFlag }, new JsonSerializerOptions { WriteIndented = true } );

		var tmp = ConfigPath + ".tmp";
		File.WriteAllText( tmp, json );
		try { File.Move( tmp, ConfigPath, overwrite: true ); }
		catch
		{
			// Fallback: direct write if move fails cross-device
			File.WriteAllText( ConfigPath, json );
			try { File.Delete( tmp ); } catch { }
		}
	}

	/// <summary>
	/// Resolve the s&box root to use: persisted valid -> migration from RepoRoot.Find -> null.
	/// Saves migration result so next start doesn't need to walk.
	/// </summary>
	public static string? Resolve()
	{
		var loaded = Load();
		if ( loaded is not null && IsValid( loaded.SboxRoot ) )
			return loaded.SboxRoot;

		var detected = RepoRoot.Find();
		if ( detected is not null && IsValid( detected ) )
		{
			try { Save( detected ); } catch { }
			return detected;
		}

		// If persisted path exists but is now invalid, still return it for display/stale handling,
		// but Resolve returns null to trigger prompt. Caller can use Load() to get stale path.
		return null;
	}

	public static string? GetStalePersistedPath()
	{
		var loaded = Load();
		return loaded?.SboxRoot;
	}

	public static string ShortenForDisplay( string path, int maxLen = 48 )
	{
		try
		{
			var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
			if ( !string.IsNullOrEmpty( home ) && path.StartsWith( home, StringComparison.Ordinal ) )
				path = "~" + path[home.Length..];
		}
		catch { }

		if ( path.Length <= maxLen ) return path;
		// Middle ellipsis
		var keep = maxLen - 3;
		var head = keep / 2;
		var tail = keep - head;
		return path[..head] + "..." + path[^tail..];
	}
}

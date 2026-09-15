using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Ampersand;

/// <summary>
/// Sniper shipped OpenSSL 1.1 and no libunwind, so .NET 10 needed four host
/// libraries injected into the container. Steamrt4 is Debian 13 based and
/// brings OpenSSL 3 natively (verified: libcrypto.so.3 and libssl.so.3 are in
/// the platform's lib/x86_64-linux-gnu, and no swept binary reports them
/// missing), so only the libunwind pair is still shimmed:
///
///   libunwind.so.8, libunwind-x86_64.so.8   libcoreclr, libclrjit,
///       libmscordaccore and libmscordbi historically DT_NEEDED them, and a
///       runtime dlopen is invisible to ldd, so absence of sweep hits proves
///       nothing. Without these the runtime can die with
///       "Failed to create CoreCLR, HRESULT: 0x80008088".
///
/// OpenSSL is deliberately NOT cached: the cache sits first on
/// LD_LIBRARY_PATH inside the container, so a host copy would shadow the
/// runtime's own OpenSSL 3 and risk version skew.
///
/// Alongside the hard requirements there is one best-effort entry:
///
///   libpcre2-16.so.0                        the engine's Qt 5.15 stack
///       DT_NEEDEDs it, sniper's platform shipped it, but steamrt4's
///       trixie-based platform carries only libpcre2-8. The engine never
///       bundled it because under sniper it never had to. A host copy is a
///       safe stand-in - pcre2 is a leaf C library on a stable .so.0 soname,
///       proven by loading the shipped libQt5Core inside the 4.0 container
///       against Fedora's copy. It is best-effort because only the editor's
///       Qt tools need it: a host without it must still launch the game, so
///       a miss here warns instead of failing (see EnsureBestEffort).
///
/// The cache is handed to scripts as $SBOX_STEAMRT4_COMPAT. It is appended to
/// LD_LIBRARY_PATH inside the container by _common.sh, because LD_LIBRARY_PATH
/// set outside is discarded by pressure-vessel.
/// </summary>
internal static class SteamRt4Compat
{
	public static readonly string[] RequiredLibraries =
	{
		"libunwind.so.8",
		"libunwind-x86_64.so.8"
	};

	/// <summary>
	/// Editor-only shims: seeded when available, warned about when not, but
	/// never allowed to block a launch - the game does not need them.
	/// </summary>
	public static readonly string[] BestEffortLibraries =
	{
		"libpcre2-16.so.0"
	};

	public static string CacheDirectory
	{
		get
		{
			var cacheHome = Environment.GetEnvironmentVariable( "XDG_CACHE_HOME" );

			if ( string.IsNullOrEmpty( cacheHome ) )
			{
				cacheHome = Path.Combine(
					Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), ".cache" );
			}

			return Path.Combine( cacheHome, "sbox-ampersand", "steamrt4-compat" );
		}
	}

	/// <summary>
	/// Populates the cache from the host. Returns false and fills
	/// <paramref name="problems"/> when a library cannot be found anywhere,
	/// showing the exact command to run on this distro.
	/// </summary>
	public static bool Ensure( out List<string> problems )
	{
		if ( SeedLibraries( RequiredLibraries, out var missing, out var copyProblems ) )
		{
			problems = new List<string>();
			return true;
		}

		problems = new List<string>( copyProblems );

		// A copy failure aborts the sweep, so anything unprobed is unknown,
		// not missing - only blame the host when every copy that was tried
		// succeeded.
		if ( copyProblems.Count == 0 && missing.Count > 0 )
		{
			problems.Add( "steamrt4 needs these libraries and this host does not have them:" );

			foreach ( var library in missing )
				problems.Add( "    " + library + "   run: " + InstallHint( library ) );
		}

		return false;
	}

	/// <summary>
	/// Seeds the editor-only shims. Never blocks: returns false with human-
	/// readable <paramref name="warnings"/> when a library is unavailable, so
	/// the caller can note it and launch anyway - the game needs none of these.
	/// </summary>
	public static bool EnsureBestEffort( out List<string> warnings )
	{
		warnings = new List<string>();

		if ( SeedLibraries( BestEffortLibraries, out var missing, out var copyProblems ) )
			return true;

		warnings.AddRange( copyProblems );

		if ( copyProblems.Count == 0 )
		{
			foreach ( var library in missing )
				warnings.Add( library + " unavailable on this host (run: " + InstallHint( library )
					+ ") - Qt-based editor tools will fail inside the container" );
		}

		return false;
	}

	/// <summary>
	/// Copies every absent library from the host into the cache. True when
	/// nothing is left missing; copy failures and absent-on-host libraries are
	/// reported separately so callers can decide severity.
	/// </summary>
	private static bool SeedLibraries(
		IReadOnlyList<string> libraries, out List<string> missing, out List<string> copyProblems )
	{
		copyProblems = new List<string>();
		missing = new List<string>();

		var cache = CacheDirectory;

		try
		{
			Directory.CreateDirectory( cache );
		}
		catch ( Exception e )
		{
			copyProblems.Add( "could not create " + cache + " - " + e.Message );
			return false;
		}

		foreach ( var library in libraries )
		{
			var destination = Path.Combine( cache, library );

			if ( File.Exists( destination ) )
				continue;

			var source = FindHostLibrary( library );

			if ( source is null )
			{
				missing.Add( library );
				continue;
			}

			try
			{
				// Copy rather than symlink: a link would point at a host path
				// that does not exist inside the container.
				File.Copy( source, destination, true );
			}
			catch ( Exception e )
			{
				copyProblems.Add( "could not cache " + library + " - " + e.Message );
				return false;
			}
		}

		return missing.Count == 0;
	}

	/// <summary>
	/// Non-mutating probe for the dependency check, which is report-only and
	/// must not seed the cache: answers whether this host could provide
	/// <paramref name="soname"/>, and gives the exact command to run when it cannot.
	/// </summary>
	public static bool ProbeHost( string soname, out string hint )
	{
		hint = InstallHint( soname );
		return FindHostLibrary( soname ) is not null;
	}

	/// <summary>
	/// ldconfig knows where the loader would find a library on any distro,
	/// which beats guessing between /usr/lib/x86_64-linux-gnu, /usr/lib64 and
	/// /usr/lib. The directory sweep is a fallback for when it is unavailable.
	/// </summary>
	private static string? FindHostLibrary( string soname )
	{
		foreach ( var line in RunLdconfig() )
		{
			var trimmed = line.Trim();

			if ( !trimmed.StartsWith( soname + " ", StringComparison.Ordinal ) )
				continue;

			// libunwind.so.8 (libc6,x86-64) => /usr/lib/x86_64-linux-gnu/libunwind.so.8
			if ( !trimmed.Contains( "x86-64", StringComparison.Ordinal ) )
				continue;

			var arrow = trimmed.IndexOf( "=>", StringComparison.Ordinal );
			if ( arrow < 0 )
				continue;

			var path = trimmed[( arrow + 2 )..].Trim();

			if ( File.Exists( path ) )
				return path;
		}

		foreach ( var directory in new[] { "/usr/lib/x86_64-linux-gnu", "/usr/lib64", "/lib64", "/lib/x86_64-linux-gnu", "/usr/lib" } )
		{
			var candidate = Path.Combine( directory, soname );

			if ( File.Exists( candidate ) )
				return candidate;
		}

		return null;
	}

	private static IEnumerable<string> RunLdconfig()
	{
		// Bare "ldconfig" relies on PATH, which on Debian-family shells
		// historically lacks /sbin - try the absolute locations first.
		var ldconfig = FindOnPath( "ldconfig" );

		if ( ldconfig is null )
			return Array.Empty<string>();

		string output;

		try
		{
			using var process = Process.Start( new ProcessStartInfo
			{
				FileName = ldconfig,
				ArgumentList = { "-p" },
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false
			} );

			if ( process is null )
				return Array.Empty<string>();

			output = process.StandardOutput.ReadToEnd();
			process.WaitForExit( 15000 );
		}
		catch
		{
			return Array.Empty<string>();
		}

		return output.Split( '\n' );
	}

	/// <summary>
	/// Resolves a command to a full path without a shell, searching PATH plus
	/// the sbin directories kept out of user shells on some distros.
	/// </summary>
	private static string? FindOnPath( string name )
	{
		var search = new List<string>();

		try
		{
			search.AddRange( ( Environment.GetEnvironmentVariable( "PATH" ) ?? "" ).Split( ':' ) );
		}
		catch
		{
			// fall through to the fixed directories
		}

		search.AddRange( new[] { "/sbin", "/usr/sbin", "/usr/local/sbin" } );

		foreach ( var directory in search )
		{
			if ( string.IsNullOrWhiteSpace( directory ) )
				continue;

			string candidate;

			try
			{
				candidate = Path.Combine( directory.Trim(), name );
			}
			catch
			{
				continue;
			}

			try
			{
				if ( File.Exists( candidate ) )
					return candidate;
			}
			catch
			{
				// unreadable directory - try the next one
			}
		}

		return null;
	}

	/// <summary>
	/// What the host looks like, for wording install hints. Detection only:
	/// ampersand never invokes a package manager, downloads packages, or
	/// escalates privileges - it inspects (os-release, binary presence) and
	/// tells the user exactly what to run.
	/// </summary>
	internal sealed class DistroInfo
	{
		public string Family { get; }
		public string Manager { get; }
		public string Escalation { get; }
		public bool IsRoot { get; }
		public bool Atomic { get; }
		public string Caveat { get; }

		public DistroInfo(
			string family, string manager, string escalation, bool isRoot, bool atomic, string caveat )
		{
			Family = family;
			Manager = manager;
			Escalation = escalation;
			IsRoot = isRoot;
			Atomic = atomic;
			Caveat = caveat;
		}

		/// <summary>
		/// The full command for a human to run to install <paramref name="package"/>.
		/// Never assumes sudo: uses it only when present, doas next, and says
		/// "(as root)" when neither exists and the user is not root. Atomic
		/// desktops get their own tool plus the reboot they require.
		/// </summary>
		public string FormatInstall( string package )
		{
			if ( Family == "nixos" )
			{
				return $"add {package} to your nix configuration"
					+ $" (e.g. nix-env -iA nixpkgs.{package})";
			}

			var baseCommand = Manager switch
			{
				"dnf" => $"dnf install -y {package}",
				"yum" => $"yum install -y {package}",
				"apt" => $"apt install -y {package}",
				"pacman" => $"pacman -S --needed {package}",
				"zypper" => $"zypper install -y {package}",
				"apk" => $"apk add {package}",
				"emerge" => $"emerge -av {package}",
				"xbps-install" => $"xbps-install -S {package}",
				"eopkg" => $"eopkg install -y {package}",
				"rpm-ostree" => $"rpm-ostree install {package}",
				"transactional-update" => $"transactional-update pkg install {package}",
				_ => (string?)null
			};

			if ( baseCommand is null )
				return $"install {package} with this distro's package manager";

			// rpm-ostree authorises through polkit, so it needs neither sudo
			// nor an as-root note; everything else runs as root one way or another.
			var text = ( Manager == "rpm-ostree" ? "" : Escalation ) + baseCommand;

			if ( Atomic && Manager == "rpm-ostree" )
				text += " && reboot";

			if ( Escalation == "" && !IsRoot && Manager != "rpm-ostree" )
				text += "  (as root)";

			if ( Caveat != "" )
				text += Caveat;

			return text;
		}
	}

	private static DistroInfo? CachedDistro;

	public static DistroInfo DetectDistro() =>
		CachedDistro ??= DetectDistro( "/etc/os-release" );

	/// <summary>
	/// Detects from the given os-release file (overridable for --probe-distro).
	/// Note: only the os-release source is overridable; the ostree-booted
	/// marker and binary probes always describe this machine.
	/// </summary>
	public static DistroInfo DetectDistro( string osReleasePath )
	{
		var fields = ParseOsRelease( ReadOsRelease( osReleasePath ) );
		var family = ParseFamily( fields );
		var atomic = DetectAtomic( fields );
		var manager = DetectManager( name => FindOnPath( name ) is not null );

		// An ostree-booted host is managed by its atomic tool even when the
		// classic manager binary is still lying around.
		if ( atomic )
		{
			manager = family == "suse" ? "transactional-update"
				: family == "fedora" ? "rpm-ostree"
				: manager;
		}

		var isRoot = string.Equals( Environment.UserName, "root", StringComparison.Ordinal );
		var escalation = DetectEscalation( name => FindOnPath( name ) is not null, isRoot );
		var caveat = family == "steamos"
			? "  (SteamOS: `steamos-readonly disable` first; /usr resets on update)"
			: "";

		return new DistroInfo( family, manager, escalation, isRoot, atomic, caveat );
	}

	/// <summary>
	/// Full copy-pasteable install command for the package providing
	/// <paramref name="soname"/> on this host. Wording only - never executed.
	/// </summary>
	public static string InstallHint( string soname )
	{
		var distro = DetectDistro();
		return distro.FormatInstall( PackageFor( distro.Family, soname ) );
	}

	internal static IReadOnlyDictionary<string, string> ParseOsRelease( IEnumerable<string> lines )
	{
		var fields = new Dictionary<string, string>( StringComparer.Ordinal );

		foreach ( var raw in lines )
		{
			var line = raw.Trim();

			if ( line.Length == 0 || line[0] == '#' )
				continue;

			var equals = line.IndexOf( '=' );
			if ( equals <= 0 )
				continue;

			fields[line[..equals].Trim()] = line[( equals + 1 )..].Trim().Trim( '"' );
		}

		return fields;
	}

	private static IEnumerable<string> ReadOsRelease( string path )
	{
		try
		{
			return File.ReadAllLines( path );
		}
		catch
		{
			return Array.Empty<string>();
		}
	}

	internal static string ParseFamily( IReadOnlyDictionary<string, string> fields )
	{
		fields.TryGetValue( "ID", out var id );
		fields.TryGetValue( "ID_LIKE", out var like );
		id ??= "";
		like ??= "";

		// SteamOS first: its ID_LIKE is arch, which would claim it below.
		if ( id.Contains( "steamos", StringComparison.Ordinal ) )
			return "steamos";

		if ( id.Contains( "nixos", StringComparison.Ordinal ) )
			return "nixos";

		if ( id.Contains( "alpine", StringComparison.Ordinal ) )
			return "alpine";

		if ( id.Contains( "gentoo", StringComparison.Ordinal ) )
			return "gentoo";

		if ( id.Contains( "void", StringComparison.Ordinal ) )
			return "void";

		if ( id.Contains( "solus", StringComparison.Ordinal ) )
			return "solus";

		var blob = id + " " + like;

		if ( blob.Contains( "fedora", StringComparison.Ordinal )
			|| blob.Contains( "rhel", StringComparison.Ordinal )
			|| blob.Contains( "centos", StringComparison.Ordinal )
			|| blob.Contains( "almalinux", StringComparison.Ordinal )
			|| blob.Contains( "rocky", StringComparison.Ordinal )
			|| blob.Contains( "nobara", StringComparison.Ordinal ) )
		{
			return "fedora";
		}

		if ( blob.Contains( "arch", StringComparison.Ordinal ) )
			return "arch";

		if ( blob.Contains( "suse", StringComparison.Ordinal )
			|| blob.Contains( "opensuse", StringComparison.Ordinal ) )
		{
			return "suse";
		}

		if ( blob.Contains( "debian", StringComparison.Ordinal )
			|| blob.Contains( "ubuntu", StringComparison.Ordinal )
			|| blob.Contains( "mint", StringComparison.Ordinal )
			|| blob.Contains( "pop", StringComparison.Ordinal ) )
		{
			return "debian";
		}

		return "unknown";
	}

	internal static bool DetectAtomic( IReadOnlyDictionary<string, string> fields )
	{
		if ( fields.TryGetValue( "VARIANT_ID", out var variant ) )
		{
			foreach ( var marker in new[]
				{ "silverblue", "kinoite", "sericea", "onyx", "bazzite", "bluefin", "aurora", "microos", "aeon" } )
			{
				if ( variant.Contains( marker, StringComparison.OrdinalIgnoreCase ) )
					return true;
			}
		}

		try
		{
			if ( File.Exists( "/run/ostree-booted" ) )
				return true;
		}
		catch
		{
			// treat as non-atomic
		}

		return false;
	}

	internal static string DetectManager( Func<string, bool> commandExists )
	{
		foreach ( var manager in new[]
			{ "dnf", "yum", "apt", "pacman", "zypper", "apk", "emerge", "xbps-install", "eopkg" } )
		{
			try
			{
				if ( commandExists( manager ) )
					return manager;
			}
			catch
			{
				// broken probe - keep looking
			}
		}

		return "none";
	}

	internal static string DetectEscalation( Func<string, bool> commandExists, bool isRoot )
	{
		if ( isRoot )
			return "";

		try
		{
			if ( commandExists( "sudo" ) )
				return "sudo ";
		}
		catch
		{
			// fall through to doas
		}

		try
		{
			if ( commandExists( "doas" ) )
				return "doas ";
		}
		catch
		{
			// no escalation available
		}

		return "";
	}

	internal static string PackageFor( string family, string soname )
	{
		var pcre2 = soname.StartsWith( "libpcre2", StringComparison.Ordinal );

		if ( pcre2 )
		{
			return family switch
			{
				"fedora" => "pcre2-utf16",
				"arch" or "steamos" => "pcre2",
				"suse" => "libpcre2-16-0",
				"alpine" => "pcre2",
				"nixos" => "pcre2",
				_ => "libpcre2-16-0"
			};
		}

		return family switch
		{
			"fedora" => "libunwind",
			"arch" or "steamos" => "libunwind",
			"suse" => "libunwind8",
			"alpine" => "libunwind",
			"nixos" => "libunwind",
			_ => "libunwind8"
		};
	}
}

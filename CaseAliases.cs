using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ampersand;

/// <summary>
/// Lowercase image-source aliases for the s&box content build.
/// <para>
/// The native resource compiler resolves a material's texture references by
/// their lowercased <em>identity</em> path and then opens that path on disk.
/// Sources keep the case they were authored with, while the lowercased path is
/// the engine's output tree, so on a case-sensitive filesystem the directory
/// exists and the source inside it does not - every material whose properties
/// point at a raw image fails with <c>LoadFromFile</c> and blocks the models
/// that use it. The engine already creates lowercase sibling links for a
/// wrongly-cased <em>file name</em> (see game/core/decals); a wrongly-cased
/// directory has no such help, and that is what this pass fills in.
/// </para>
/// <para>
/// Only raw image sources are aliased. <c>AssetTypeTable</c> never puts them in
/// the ContentBuilder inventory (their asset types carry no compiler), so the
/// links are invisible to the builder, while <c>.vtex</c>/<c>.vmat</c>/<c>.vmdl</c>
/// sources would be picked up twice under the inventory's case-insensitive
/// comparers and fail as "both compile to". For the same reason this creates
/// real directories, never directory symlinks.
/// </para>
/// <para>
/// Targets something else already satisfies - the engine's own sibling link,
/// or an identical copy - are counted as already present and are never
/// journaled, so <see cref="Revert"/> cannot remove a link Ampersand did not
/// create. The journal lives in the ampersand cache dir rather than in the
/// checkout because <c>game/.source2</c> is disposable build state.
/// </para>
/// <para>
/// This is a shim for a native bug and should be deleted once the compiler
/// resolves identity to the authored source itself. Diagnostics:
/// <c>ampersand --case-aliases --dry-run</c>; escape hatch
/// <c>SBOX_SKIP_CASE_ALIASES=1</c>.
/// </para>
/// </summary>
internal static class CaseAliases
{
	public const string SkipVariable = "SBOX_SKIP_CASE_ALIASES";

	private const string ExcludeHeader = "# ampersand: case aliases (generated; remove with ampersand --revert-case-aliases)";
	private const string ExcludeFooter = "# ampersand: end case aliases";

	/// <summary>
	/// Extensions the material compiler loads as a raw image. The list is
	/// deliberately only the bitmap formats: see the class remarks.
	/// </summary>
	private static readonly string[] ImageExtensions =
	{
		".png", ".tga", ".jpg", ".jpeg", ".exr", ".psd", ".tif", ".tiff", ".hdr"
	};

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	internal readonly record struct EnsureResult(
		int Roots, int Sources, int Linked, int Already, int Conflicts, int DirsCreated, int Collisions,
		string? Note, bool Skipped )
	{
		public string Describe()
		{
			if ( Skipped )
				return "case aliases: skipped (" + ( Note ?? "not needed" ) + ")";

			var text = $"case aliases: {Linked} linked, {Already} already, {Conflicts} conflicts";
			if ( DirsCreated > 0 ) text += $", {DirsCreated} dirs created";
			if ( Collisions > 0 ) text += $" ({Collisions} case-duplicate picks)";
			if ( Note is not null ) text += " - " + Note;
			return text;
		}
	}

	internal readonly record struct RevertResult( int Links, int Dirs, int DirsKept, int Skipped, bool DryRun, string? Note )
	{
		public string Describe()
		{
			var text = DryRun
				? ( Dirs > 0
					? $"case aliases: {Links} links and {Dirs} dirs would be removed"
					: $"case aliases: {Links} links would be removed" )
				: $"case aliases reverted: {Links} links, {Dirs} dirs removed";
			// A dry run cannot know which directories will be empty once the
			// links are gone, so it only reports the links it would remove.
			if ( !DryRun && DirsKept > 0 ) text += $", {DirsKept} dirs left in place";
			if ( Skipped > 0 ) text += $", {Skipped} entries skipped";
			if ( Note is not null ) text += " - " + Note;
			return text;
		}
	}

	private sealed class Journal
	{
		public int Version { get; set; } = 1;
		public string? Root { get; set; }
		public List<string>? Files { get; set; }
		public List<string>? Dirs { get; set; }
	}

	/// <summary>
	/// Engine content roots under game/, matching <c>ContentRoots.Engine</c>:
	/// core plus every addon's asset folder (the name is Assets or assets
	/// depending on the addon, so it is matched case-insensitively).
	/// </summary>
	public static IReadOnlyList<string> Roots( string repoRoot )
	{
		var roots = new List<string>();
		var game = Path.Combine( repoRoot, "game" );

		var core = Path.Combine( game, "core" );
		if ( Directory.Exists( core ) ) roots.Add( Path.GetFullPath( core ) );

		var addons = Path.Combine( game, "addons" );
		if ( Directory.Exists( addons ) )
		{
			try
			{
				foreach ( var addon in Directory.EnumerateDirectories( addons ).OrderBy( x => x, StringComparer.Ordinal ) )
				{
					string? assets = null;
					foreach ( var child in Directory.EnumerateDirectories( addon ).OrderBy( x => x, StringComparer.Ordinal ) )
					{
						if ( string.Equals( Path.GetFileName( child ), "assets", StringComparison.OrdinalIgnoreCase ) )
						{
							assets = child;
							break;
						}
					}

					if ( assets is not null ) roots.Add( Path.GetFullPath( assets ) );
				}
			}
			catch { }
		}

		return roots;
	}

	/// <summary>
	/// Gives every image source whose root-relative path has uppercase a link at
	/// the lowercased path. Idempotent; a second run creates nothing. Never
	/// overwrites an existing file, directory or foreign symlink.
	/// </summary>
	public static EnsureResult Ensure( string repoRoot, Action<string>? log = null, bool dryRun = false )
	{
		if ( !OperatingSystem.IsLinux() )
			return new EnsureResult( 0, 0, 0, 0, 0, 0, 0, "Linux only", true );

		var skip = Environment.GetEnvironmentVariable( SkipVariable );
		if ( !string.IsNullOrEmpty( skip ) && skip != "0" && !skip.Equals( "false", StringComparison.OrdinalIgnoreCase ) )
			return new EnsureResult( 0, 0, 0, 0, 0, 0, 0, SkipVariable, true );

		void Report( string message ) => log?.Invoke( message );

		try
		{
			var roots = Roots( repoRoot );
			var created = new List<string>();
			var createdDirs = new HashSet<string>( StringComparer.Ordinal );
			var ours = new List<string>();
			int sources = 0, linked = 0, already = 0, conflicts = 0, collisions = 0, reported = 0;

			foreach ( var root in roots )
			{
				var groups = Collect( root );
				foreach ( var group in groups.OrderBy( x => x.Key, StringComparer.Ordinal ) )
				{
					var relative = group.Key;
					var candidates = group.Value;
					candidates.Sort( StringComparer.Ordinal );
					sources += candidates.Count;

					// Two sources that only differ by case already collide on
					// Windows; pick one deterministically and say so when the
					// pick would actually change the bytes.
					if ( candidates.Count > 1 )
					{
						collisions++;
						if ( !FilesEqual( Path.Combine( root, candidates[0] ), Path.Combine( root, candidates[1] ) ) )
							Report( "case aliases: " + relative + " merges case variants with different contents: " + string.Join( ", ", candidates ) );
					}

					var sourceRel = candidates[0];
					var sourceAbs = Path.GetFullPath( Path.Combine( root, sourceRel ) );
					var targetAbs = Path.GetFullPath( Path.Combine( root, relative ) );

					var existingLink = LinkTargetAbsolute( targetAbs );
					if ( existingLink is not null )
					{
						if ( SamePath( existingLink, sourceAbs ) ) already++;
						else
						{
							conflicts++;
							if ( reported++ < 10 ) Report( "case aliases: left " + relative + " alone (a symlink to something else)" );
						}
						continue;
					}

					if ( File.Exists( targetAbs ) )
					{
						// An identical copy (upstream fix or old engine behaviour)
						// already resolves the path; leave it and do not journal it.
						if ( FilesEqual( sourceAbs, targetAbs ) ) already++;
						else
						{
							conflicts++;
							if ( reported++ < 10 ) Report( "case aliases: left " + relative + " alone (a file with different contents)" );
						}
						continue;
					}

					if ( Directory.Exists( targetAbs ) )
					{
						conflicts++;
						if ( reported++ < 10 ) Report( "case aliases: left " + relative + " alone (a directory is there)" );
						continue;
					}

					var missing = MissingParents( root, targetAbs );
					if ( missing is null )
					{
						conflicts++;
						if ( reported++ < 10 ) Report( "case aliases: could not create the parent directory of " + relative );
						continue;
					}

					if ( dryRun )
					{
						linked++;
						foreach ( var dir in missing ) createdDirs.Add( RepoRelative( repoRoot, dir ) );
						continue;
					}

					try
					{
						for ( var i = missing.Count - 1; i >= 0; i-- )
						{
							Directory.CreateDirectory( missing[i] );
							createdDirs.Add( RepoRelative( repoRoot, missing[i] ) );
						}

						// Relative target: the checkout stays relocatable.
						File.CreateSymbolicLink( targetAbs, Path.GetRelativePath( Path.GetDirectoryName( targetAbs )!, sourceAbs ) );
						linked++;
						created.Add( targetAbs );
						ours.Add( RepoRelative( repoRoot, targetAbs ) );
					}
					catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
					{
						conflicts++;
						if ( reported++ < 10 ) Report( "case aliases: could not link " + relative + " (" + e.Message + ")" );
					}
				}
			}

			if ( !dryRun )
			{
				WriteJournal( repoRoot, ours, createdDirs );
				CheckGitIgnore( repoRoot, created, Report );
			}

			return new EnsureResult( roots.Count, sources, linked, already, conflicts, createdDirs.Count, collisions, null, false );
		}
		catch ( Exception e )
		{
			Report( "case aliases: failed (" + e.Message + ")" );
			return new EnsureResult( 0, 0, 0, 0, 0, 0, 0, e.Message, false );
		}
	}

	/// <summary>
	/// Removes exactly the links <see cref="Ensure"/> recorded in its journal,
	/// then the directories it created that are now empty. A missing journal
	/// means nothing is removed: the engine's own sibling links live in the
	/// same trees and must survive. Idempotent.
	/// </summary>
	public static RevertResult Revert( string repoRoot, Action<string>? log = null, bool dryRun = false )
	{
		if ( !OperatingSystem.IsLinux() )
			return new RevertResult( 0, 0, 0, 0, dryRun, "Linux only" );

		try
		{
			var journal = ReadJournal( repoRoot );
			if ( journal is null )
			{
				log?.Invoke( "case aliases: no aliases recorded for this checkout; nothing removed" );
				return new RevertResult( 0, 0, 0, 0, dryRun, null );
			}

			var roots = Roots( repoRoot );
			var files = ( journal.Files ?? new List<string>() )
				.Select( f => Path.GetFullPath( Path.Combine( repoRoot, f ) ) ).ToList();
			var dirs = ( journal.Dirs ?? new List<string>() )
				.Select( d => Path.GetFullPath( Path.Combine( repoRoot, d ) ) ).ToList();

			int removed = 0, skipped = 0, kept = 0;
			foreach ( var link in files )
			{
				if ( !LooksLikeOurs( roots, link ) ) { skipped++; continue; }
				if ( !dryRun )
				{
					try { File.Delete( link ); }
					catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException ) { skipped++; continue; }
				}
				removed++;
			}

			var removedDirs = 0;
			foreach ( var dir in dirs.OrderByDescending( d => d.Length ) )
			{
				if ( !Directory.Exists( dir ) ) continue;
				try
				{
					if ( Directory.EnumerateFileSystemEntries( dir ).Any() ) { kept++; continue; }
				}
				catch { kept++; continue; }

				if ( !dryRun )
				{
					try { Directory.Delete( dir, false ); }
					catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException ) { kept++; continue; }
				}
				removedDirs++;
			}

			if ( !dryRun )
			{
				try { File.Delete( JournalPath( repoRoot ) ); } catch { }
			}

			return new RevertResult( removed, removedDirs, kept, skipped, dryRun, null );
		}
		catch ( Exception e )
		{
			log?.Invoke( "case aliases: revert failed (" + e.Message + ")" );
			return new RevertResult( 0, 0, 0, 0, dryRun, e.Message );
		}
	}

	// ---- discovery -------------------------------------------------------

	/// <summary>
	/// Root-relative lowercased path -> the sources that map to it. Only files
	/// whose path has uppercase are considered, so a wrongly-cased file name in
	/// an otherwise lowercase directory is covered too.
	/// </summary>
	private static Dictionary<string, List<string>> Collect( string root )
	{
		var groups = new Dictionary<string, List<string>>( StringComparer.Ordinal );
		Walk( root, ( entry, isLink ) =>
		{
			// Our own links (and the engine's) are not sources.
			if ( isLink ) return;

			var relative = Path.GetRelativePath( root, entry );
			if ( !IsImage( relative ) ) return;

			var lower = relative.ToLowerInvariant();
			if ( string.Equals( relative, lower, StringComparison.Ordinal ) ) return;

			if ( !groups.TryGetValue( lower, out var list ) ) groups[lower] = list = new List<string>();
			list.Add( relative );
		} );
		return groups;
	}

	/// <summary>
	/// Depth-first, iterative walk that never follows directory links (a linked
	/// directory would expose the same files under a second prefix).
	/// <paramref name="onEntry"/> receives non-directories and whether they are
	/// symlinks.
	/// </summary>
	private static void Walk( string root, Action<string, bool> onEntry )
	{
		var stack = new Stack<string>();
		stack.Push( root );
		var seen = new HashSet<string>( StringComparer.Ordinal );

		while ( stack.Count > 0 )
		{
			var dir = stack.Pop();

			string canonical;
			try { canonical = new DirectoryInfo( dir ).ResolveLinkTarget( true )?.FullName ?? Path.GetFullPath( dir ); }
			catch { canonical = Path.GetFullPath( dir ); }
			if ( !seen.Add( canonical ) ) continue;

			string[] entries;
			try { entries = Directory.GetFileSystemEntries( dir ); }
			catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException ) { continue; }

			foreach ( var entry in entries )
			{
				var isLink = LinkTargetAbsolute( entry ) is not null;

				bool isDirectory;
				try { isDirectory = Directory.Exists( entry ); }
				catch { isDirectory = false; }

				if ( isDirectory )
				{
					if ( isLink ) continue;
					stack.Push( entry );
					continue;
				}

				onEntry( entry, isLink );
			}
		}
	}

	/// <summary>
	/// The missing directories between <paramref name="root"/> and the target's
	/// parent, outermost first. Null when a plain file blocks the path.
	/// </summary>
	private static List<string>? MissingParents( string root, string target )
	{
		var parent = Path.GetDirectoryName( target );
		if ( parent is null ) return null;

		var missing = new List<string>();
		for ( var dir = parent; dir is not null && dir.Length > root.Length; dir = Path.GetDirectoryName( dir ) )
		{
			if ( Directory.Exists( dir ) ) break;
			if ( File.Exists( dir ) ) return null;
			missing.Add( dir );
		}

		if ( missing.Count == 0 && !Directory.Exists( parent ) ) return null;
		return missing;
	}

	// ---- journal ---------------------------------------------------------

	private static string JournalPath( string repoRoot )
	{
		var full = Path.GetFullPath( repoRoot );
		var hash = Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( full ) ) )[..16].ToLowerInvariant();
		return Path.Combine( AppPaths.CacheDir, "case-aliases", hash + ".json" );
	}

	private static Journal? ReadJournal( string repoRoot )
	{
		try
		{
			var path = JournalPath( repoRoot );
			if ( !File.Exists( path ) ) return null;

			var journal = JsonSerializer.Deserialize<Journal>( File.ReadAllText( path ) );
			if ( journal is null ) return null;
			if ( journal.Root is not null && !SamePath( journal.Root, Path.GetFullPath( repoRoot ) ) ) return null;
			return journal;
		}
		catch { return null; }
	}

	/// <summary>
	/// Unions with the existing journal: a run that creates nothing must not
	/// forget the links and directories an earlier run created.
	/// </summary>
	private static void WriteJournal( string repoRoot, IEnumerable<string> files, IEnumerable<string> dirs )
	{
		try
		{
			var existing = ReadJournal( repoRoot );
			var allFiles = new HashSet<string>( StringComparer.Ordinal );
			var allDirs = new HashSet<string>( StringComparer.Ordinal );

			foreach ( var file in files ) allFiles.Add( file );
			foreach ( var dir in dirs ) allDirs.Add( dir );
			if ( existing is not null )
			{
				foreach ( var file in existing.Files ?? new List<string>() ) allFiles.Add( file );
				foreach ( var dir in existing.Dirs ?? new List<string>() ) allDirs.Add( dir );
			}

			var path = JournalPath( repoRoot );
			Directory.CreateDirectory( Path.GetDirectoryName( path )! );

			var journal = new Journal
			{
				Root = Path.GetFullPath( repoRoot ),
				Files = allFiles.OrderBy( x => x, StringComparer.Ordinal ).ToList(),
				Dirs = allDirs.OrderBy( x => x, StringComparer.Ordinal ).ToList()
			};

			var temp = path + ".tmp";
			File.WriteAllText( temp, JsonSerializer.Serialize( journal, JsonOptions ) );
			File.Move( temp, path, true );
		}
		catch { }
	}

	// ---- git noise -------------------------------------------------------

	/// <summary>
	/// The repository's default-deny .gitignore already hides every alias, so
	/// this normally does nothing. If an extension is ever un-ignored upstream,
	/// list the visible paths in .git/info/exclude (local, untracked) and say so.
	/// </summary>
	private static void CheckGitIgnore( string repoRoot, IReadOnlyList<string> created, Action<string> log )
	{
		if ( created.Count == 0 ) return;
		if ( !Directory.Exists( Path.Combine( repoRoot, ".git" ) ) ) return;

		try
		{
			var relative = created.Select( p => RepoRelative( repoRoot, p ) ).ToList();
			var ignored = GitCheckIgnore( repoRoot, relative );
			if ( ignored is null ) return;

			var leaks = relative.Where( p => !ignored.Contains( p ) ).ToList();
			if ( leaks.Count == 0 ) return;

			WriteExcludeBlock( repoRoot, leaks );
			log( "case aliases: " + leaks.Count + " alias paths are not covered by .gitignore; added them to .git/info/exclude" );
		}
		catch { }
	}

	private static HashSet<string>? GitCheckIgnore( string repoRoot, IReadOnlyList<string> relative )
	{
		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = "git",
				WorkingDirectory = repoRoot,
				UseShellExecute = false,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			psi.ArgumentList.Add( "check-ignore" );
			psi.ArgumentList.Add( "--stdin" );

			using var proc = Process.Start( psi );
			if ( proc is null ) return null;

			// Drain the pipes while writing: a few thousand paths exceed the
			// pipe buffer and a write-then-read order would deadlock.
			var stdout = proc.StandardOutput.ReadToEndAsync();
			var stderr = proc.StandardError.ReadToEndAsync();
			proc.StandardInput.Write( string.Join( "\n", relative ) + "\n" );
			proc.StandardInput.Close();
			var output = stdout.GetAwaiter().GetResult();
			_ = stderr.GetAwaiter().GetResult();
			proc.WaitForExit();

			// 0 = at least one path ignored, 1 = none. Anything else is not a
			// usable answer (no repository, bad usage).
			if ( proc.ExitCode != 0 && proc.ExitCode != 1 ) return null;

			var ignored = new HashSet<string>( StringComparer.Ordinal );
			foreach ( var line in output.Split( '\n' ) )
			{
				var path = line.TrimEnd( '\r' );
				if ( path.Length > 0 ) ignored.Add( path );
			}
			return ignored;
		}
		catch { return null; }
	}

	private static void WriteExcludeBlock( string repoRoot, IReadOnlyList<string> relative )
	{
		var info = Path.Combine( repoRoot, ".git", "info" );
		var path = Path.Combine( info, "exclude" );
		Directory.CreateDirectory( info );

		var kept = new List<string>();
		var inBlock = false;
		if ( File.Exists( path ) )
		{
			foreach ( var raw in File.ReadAllText( path ).Split( '\n' ) )
			{
				var line = raw.TrimEnd( '\r' );
				if ( line == ExcludeHeader ) { inBlock = true; continue; }
				if ( line == ExcludeFooter ) { inBlock = false; continue; }
				if ( !inBlock ) kept.Add( line );
			}
		}

		while ( kept.Count > 0 && kept[^1].Length == 0 ) kept.RemoveAt( kept.Count - 1 );
		kept.Add( "" );
		kept.Add( ExcludeHeader );
		foreach ( var entry in relative.OrderBy( x => x, StringComparer.Ordinal ) )
			kept.Add( "/" + entry.Replace( '\\', '/' ) );
		kept.Add( ExcludeFooter );
		kept.Add( "" );

		File.WriteAllText( path, string.Join( "\n", kept ) );
	}

	// ---- path helpers ----------------------------------------------------

	private static bool IsImage( string relative )
	{
		var extension = Path.GetExtension( relative );
		foreach ( var candidate in ImageExtensions )
			if ( string.Equals( extension, candidate, StringComparison.OrdinalIgnoreCase ) )
				return true;
		return false;
	}

	/// <summary>
	/// Where a symlink points, as an absolute path, or null when the path is not
	/// a link. Works for dangling links and for links to directories.
	/// </summary>
	private static string? LinkTargetAbsolute( string path )
	{
		try
		{
			var target = new FileInfo( path ).LinkTarget;
			if ( target is null ) return null;

			var parent = Path.GetDirectoryName( path );
			if ( parent is null ) return null;
			return Path.GetFullPath( target, parent );
		}
		catch { return null; }
	}

	/// <summary>
	/// True when the path is a link Ampersand would have made: it points at an
	/// image source inside one of the roots and sits exactly at that source's
	/// lowercased path.
	/// </summary>
	private static bool LooksLikeOurs( IReadOnlyList<string> roots, string linkPath )
	{
		var target = LinkTargetAbsolute( linkPath );
		if ( target is null ) return false;
		if ( !IsUnderAnyRoot( roots, target, out var root ) ) return false;

		var relative = Path.GetRelativePath( root, target );
		if ( !IsImage( relative ) ) return false;

		var lower = relative.ToLowerInvariant();
		if ( string.Equals( relative, lower, StringComparison.Ordinal ) ) return false;

		return SamePath( Path.Combine( root, lower ), linkPath );
	}

	private static bool IsUnderAnyRoot( IReadOnlyList<string> roots, string path, out string root )
	{
		foreach ( var candidate in roots )
		{
			var relative = Path.GetRelativePath( candidate, path );
			if ( relative == ".." || relative.StartsWith( ".." + Path.DirectorySeparatorChar, StringComparison.Ordinal ) )
				continue;

			root = candidate;
			return true;
		}

		root = "";
		return false;
	}

	private static string RepoRelative( string repoRoot, string path ) => Path.GetRelativePath( repoRoot, path );

	private static bool SamePath( string a, string b )
	{
		var left = Path.GetFullPath( a ).TrimEnd( Path.DirectorySeparatorChar );
		var right = Path.GetFullPath( b ).TrimEnd( Path.DirectorySeparatorChar );
		return string.Equals( left, right, StringComparison.Ordinal );
	}

	private static bool FilesEqual( string a, string b )
	{
		try
		{
			var left = new FileInfo( a );
			var right = new FileInfo( b );
			if ( left.Length != right.Length ) return false;

			using var x = File.OpenRead( a );
			using var y = File.OpenRead( b );
			var bufferA = new byte[81920];
			var bufferB = new byte[81920];

			while ( true )
			{
				var readA = x.Read( bufferA, 0, bufferA.Length );
				var readB = y.Read( bufferB, 0, bufferB.Length );
				if ( readA != readB ) return false;
				if ( readA == 0 ) return true;
				if ( !bufferA.AsSpan( 0, readA ).SequenceEqual( bufferB.AsSpan( 0, readB ) ) ) return false;
			}
		}
		catch { return false; }
	}
}

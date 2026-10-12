using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ampersand;

internal static class RunLog
{
	public static string LogDirectory
	{
		get
		{
			var cacheHome = Environment.GetEnvironmentVariable( "XDG_CACHE_HOME" );
			if ( string.IsNullOrEmpty( cacheHome ) )
				cacheHome = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), ".cache" );
			return Path.Combine( cacheHome, "sbox-ampersand", "logs" );
		}
	}

	private static readonly Regex AnsiRegex = new( @"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled );

	public static string CreateLogPath( string targetName )
	{
		var dir = LogDirectory;
		try { Directory.CreateDirectory( dir ); } catch { }

		var safe = Sanitize( targetName );
		var stamp = DateTime.Now.ToString( "yyyy-MM-dd_HH-mm-ss" );
		var unique = $"{safe}-{stamp}_{Environment.ProcessId}_{Guid.NewGuid().ToString( "N" )[..6]}.log";
		var path = Path.Combine( dir, unique );

		// Touch file so the log path exists even when the run's output goes to
		// a terminal window instead of this file (terminal spawns are direct,
		// engine-style, so the child's pty output is not captured here).
		try { File.WriteAllText( path, $"--- {targetName} {DateTime.Now:O} ---{Environment.NewLine}" ); } catch { }

		// Best-effort prune, never fail launch.
		try { PruneOldLogs( 100 ); } catch { }

		return path;
	}

	public static string Sanitize( string name )
	{
		foreach ( var c in Path.GetInvalidFileNameChars() )
			name = name.Replace( c, '-' );
		name = name.Replace( ' ', '-' );
		return string.IsNullOrEmpty( name ) ? "run" : name;
	}

	/// <summary>Keep most recent N logs, delete rest. Never throws.</summary>
	public static void PruneOldLogs( int keep = 100 )
	{
		try
		{
			var dir = LogDirectory;
			if ( !Directory.Exists( dir ) ) return;
			var files = new DirectoryInfo( dir ).GetFiles( "*.log" )
				.OrderByDescending( f => f.LastWriteTimeUtc )
				.ToArray();
			if ( files.Length <= keep ) return;
			foreach ( var f in files.Skip( keep ) )
				try { f.Delete(); } catch { }
		}
		catch { }
	}

	/// <summary>
	/// Appends the exact command the launcher ran (cwd + env + argv) to the
	/// log, so a later reader can reproduce the launch from the log alone.
	/// Shell-quoted, safe to paste into bash. Call right after CreateLogPath,
	/// which already wrote the --- banner line.
	/// </summary>
	public static void WriteCommandHeader(
		string path,
		IReadOnlyList<string> command,
		string workingDirectory,
		IReadOnlyDictionary<string, string> extraEnv )
	{
		try
		{
			using var w = new StreamWriter( new FileStream( path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite ) ) { AutoFlush = true };
			if ( !string.IsNullOrWhiteSpace( workingDirectory ) )
				w.WriteLine( "$ cd " + Quote( workingDirectory ) );
			var parts = new List<string>();
			if ( extraEnv is not null )
				foreach ( var pair in extraEnv )
					parts.Add( $"{pair.Key}={Quote( pair.Value ?? string.Empty )}" );
			if ( command is not null )
				foreach ( var arg in command )
					parts.Add( Quote( arg ?? string.Empty ) );
			w.WriteLine( "$ " + string.Join( " ", parts ) );
			w.WriteLine();
		}
		catch { }
	}

	private static string Quote( string s )
	{
		if ( s.Length == 0 )
			return "''";
		foreach ( var c in s )
		{
			if ( !( char.IsLetterOrDigit( c ) || c is '_' or '-' or '.' or '/' or ':' or ',' or '+' or '=' or '@' or '%' ) )
				return "'" + s.Replace( "'", "'\\''" ) + "'";
		}
		return s;
	}

	/// <summary>
	/// Notes in the log that the run's output went to its terminal window and
	/// was not captured here. Terminal spawns are direct (engine-style), so the
	/// child keeps the pty and interactive input works - at the cost of no
	/// transcript. The header above still records the exact command for
	/// reproduction. Never throws.
	/// </summary>
	public static void WriteTerminalNote( string path )
	{
		try
		{
			using var w = new StreamWriter( new FileStream( path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite ) ) { AutoFlush = true };
			w.WriteLine( "(output shown in terminal window - not captured in this log)" );
			w.WriteLine();
		}
		catch { }
	}

	/// <summary>Last N lines, ANSI-stripped, truncated to maxChars.</summary>
	public static string Tail( string? path, int maxLines = 80, int maxChars = 6000 )
	{
		if ( string.IsNullOrEmpty( path ) || !File.Exists( path ) )
			return "(no log file)";

		try
		{
			// Read efficiently from end for large logs.
			var lines = File.ReadAllLines( path );
			var start = Math.Max( 0, lines.Length - maxLines );
			var slice = lines[start..];
			// Strip ANSI for avalonia TextBlock.
			for ( int i = 0; i < slice.Length; i++ )
				slice[i] = AnsiRegex.Replace( slice[i], "" );

			var text = string.Join( Environment.NewLine, slice ).Trim();
			if ( text.Length > maxChars )
				text = "..." + text[^maxChars..];
			if ( string.IsNullOrWhiteSpace( text ) )
				return "(log empty)";
			return text;
		}
		catch ( Exception e )
		{
			return $"(could not read log: {e.Message})";
		}
	}

	public static bool TryOpenFolder( string? logPath = null )
	{
		var dir = LogDirectory;
		try { Directory.CreateDirectory( dir ); } catch { }
		var target = dir;
		// If a specific log exists, still open folder; xdg-open on file opens editor.
		if ( !string.IsNullOrEmpty( logPath ) && File.Exists( logPath ) )
		{
			// Prefer folder containing file.
			try { target = Path.GetDirectoryName( logPath ) ?? dir; } catch { }
		}
		return TryOpen( target );
	}

	public static bool TryOpenFile( string? logPath )
	{
		if ( string.IsNullOrEmpty( logPath ) || !File.Exists( logPath ) ) return false;
		return TryOpen( logPath );
	}

	private static bool TryOpen( string path )
	{
		// xdg-open is the freedesktop standard; fallback to gio open / kde-open.
		foreach ( var opener in new[] { "xdg-open", "gio", "kde-open", "gnome-open" } )
		{
			try
			{
				var psi = new ProcessStartInfo
				{
					FileName = opener,
					UseShellExecute = false
				};
				if ( opener == "gio" )
				{
					psi.ArgumentList.Add( "open" );
					psi.ArgumentList.Add( path );
				}
				else
				{
					psi.ArgumentList.Add( path );
				}
				using var p = Process.Start( psi );
				if ( p != null ) return true;
			}
			catch { continue; }
		}
		// Last resort: UseShellExecute (lets OS pick).
		try
		{
			Process.Start( new ProcessStartInfo { FileName = path, UseShellExecute = true } );
			return true;
		}
		catch { return false; }
	}
}

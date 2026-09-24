using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Ampersand;

/// <summary>
/// Exec-bit pre-flight for the native content toolchain.
/// <para>
/// Downloaded public artifacts lose the executable bit (HTTP carries no mode
/// metadata) - the staging copies under
/// <c>engine/Tools/SboxBuild/game/bin/linuxsteamrt64/</c> land as
/// <c>-rw-r--r--</c>. The engine repairs <c>contentbuilder</c> itself just
/// before running it (<c>BuildContent.cs</c> restores <c>UserExecute</c>), but
/// nothing repairs <c>resourcecompiler</c>, the small stub
/// <c>contentbuilder</c> spawns per compile - so a fresh artifact restore can
/// fail the content step with a bare permission error. Ampersand checks both
/// live copies under <c>game/bin/linuxsteamrt64/</c> before building: the GUI
/// prompts (see <c>MainWindow.RunBootstrap</c>), the CLI worker repairs
/// silently with a log line (see <c>Bootstrap.Run</c>).
/// </para>
/// </summary>
internal static class ToolchainExec
{
	/// <summary>Platform dir, hardcoded like <c>Bootstrap.CheckNativeDeps</c> - ampersand targets Linux x86_64.</summary>
	public const string PlatformDir = "linuxsteamrt64";

	private static readonly string[] ToolNames = { "contentbuilder", "resourcecompiler" };

	/// <summary>
	/// Live toolchain binaries that exist but lack the owner-execute bit.
	/// Files that don't exist yet are ignored: a fresh checkout with no
	/// <c>game/bin</c> must not prompt for files <c>Setup.sh</c> hasn't
	/// downloaded. Non-Unix platforms report nothing (no exec-bit concept to
	/// repair). Never throws.
	/// </summary>
	public static IReadOnlyList<string> FindNonExecutable( string repoRoot )
	{
		var missing = new List<string>();
		// Direct OS calls (not a helper): CA1416 flow analysis only understands
		// these inline, as in RunLog.EnsureWrapper.
		if ( !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() )
			return missing;

		foreach ( var tool in ToolNames )
		{
			string path;
			try
			{
				path = Path.Combine( repoRoot, "game", "bin", PlatformDir, tool );
				if ( !File.Exists( path ) )
					continue;
			}
			catch
			{
				continue;
			}

			try
			{
				if ( ( File.GetUnixFileMode( path ) & UnixFileMode.UserExecute ) == 0 )
					missing.Add( path );
			}
			catch
			{
				// Unreadable mode - attempt the fix anyway; the chmod child
				// may succeed where the managed API could not even read.
				missing.Add( path );
			}
		}
		return missing;
	}

	/// <summary>
	/// Best-effort <c>chmod +x</c> over <paramref name="paths"/>: first the
	/// managed API (full <c>+x</c>, matching the engine's git-hook mask), then
	/// a <c>/bin/chmod</c> child on failure. Never throws; returns true only
	/// when every path now carries <c>UserExecute</c>. Per-file progress goes
	/// to <paramref name="emit"/> when provided.
	/// </summary>
	public static bool TryFix( IEnumerable<string> paths, Action<string>? emit = null )
	{
		var ok = true;
		foreach ( var path in paths )
		{
			if ( OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() )
			{
				try
				{
					var mode = File.GetUnixFileMode( path );
					File.SetUnixFileMode( path,
						mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute );
				}
				catch
				{
					if ( !TryChmod( path, emit ) )
					{
						ok = false;
						continue;
					}
				}
			}
			else if ( !TryChmod( path, emit ) )
			{
				ok = false;
				continue;
			}

			// Verify the bit actually stuck (read-only checkout, ACLs, ...).
			if ( OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() )
			{
				try
				{
					if ( ( File.GetUnixFileMode( path ) & UnixFileMode.UserExecute ) != 0 )
						emit?.Invoke( "  toolchain: made executable: " + path );
					else
					{
						ok = false;
						emit?.Invoke( "  toolchain: still not executable (read-only checkout?): " + path );
					}
				}
				catch
				{
					emit?.Invoke( "  toolchain: fix attempted (mode unreadable): " + path );
				}
			}
			else
			{
				emit?.Invoke( "  toolchain: fix attempted: " + path );
			}
		}
		return ok;
	}

	private static bool TryChmod( string path, Action<string>? emit )
	{
		try
		{
			using var proc = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = "/bin/chmod",
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
				}
			};
			proc.StartInfo.ArgumentList.Add( "+x" );
			proc.StartInfo.ArgumentList.Add( path );
			try
			{
				proc.Start();
			}
			catch ( Exception e )
			{
				emit?.Invoke( "  toolchain: chmod failed for " + path + ": " + e.Message );
				return false;
			}
			proc.WaitForExit( 10000 );
			if ( proc.ExitCode != 0 )
			{
				emit?.Invoke( $"  toolchain: chmod exited {proc.ExitCode} for " + path );
				return false;
			}
			return true;
		}
		catch ( Exception e )
		{
			emit?.Invoke( "  toolchain: chmod failed for " + path + ": " + e.Message );
			return false;
		}
	}
}

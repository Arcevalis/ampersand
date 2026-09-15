using System;
using Avalonia;

namespace Ampersand;

internal static class Program
{
	/// <summary>
	/// Runs the dependency sweep on stdout instead of starting the UI.
	///
	/// The app has no output surface of its own any more, so the sweep is printed
	/// by a second copy of this binary that the window opens inside the user's
	/// terminal emulator. Re-execing rather than piping the text back keeps the
	/// report as it was written - SGR colour and column alignment, both of which
	/// need a real terminal.
	/// </summary>
	public const string DependencyCheckArgument = "--dependency-check";
	public const string BootstrapArgument = "--bootstrap";

	/// <summary>
	/// Prints distro detection and the resulting install hints without touching
	/// anything. Takes an optional os-release path so mappings for other
	/// distros can be validated from fixture files - e.g.
	/// ampersand --probe-distro /tmp/opencode/os-release-steamos
	/// Note: only the os-release source is overridable; binary probes and the
	/// ostree-booted marker always describe this machine.
	/// </summary>
	public const string ProbeDistroArgument = "--probe-distro";

	[STAThread]
	private static int Main( string[] args )
	{
		if ( args.Length > 0 && args[0] == DependencyCheckArgument )
			return DependencyCheckMain();
		if ( args.Length > 0 && args[0] == BootstrapArgument )
			return BootstrapMain( args );
		if ( args.Length > 0 && args[0] == ProbeDistroArgument )
			return ProbeDistroMain( args );

		return BuildAvaloniaApp().StartWithClassicDesktopLifetime( args );
	}

	private static int DependencyCheckMain()
	{
		var root = SboxSettings.Resolve() ?? RepoRoot.Find();

		if ( root is null )
		{
			Console.Error.WriteLine( "ampersand: could not locate the repo root - expected game/ and engine/ above this binary" );
			Console.Error.WriteLine( "hint: set it in the launcher (Replace s&box path) or run from the repo checkout" );
			return 1;
		}

		DependencyCheck.Run( root, Console.WriteLine );

		// The emulator closes its window the moment the child exits, taking the
		// report with it. Nothing else here is interactive, so this hold is the
		// only thing that makes the output readable.
		Console.WriteLine();
		Console.Write( "Press Enter to close." );
		Console.ReadLine();

		return 0;
	}

	private static int BootstrapMain( string[] args )
	{
		var skipDeps = args.Length > 1 && args[1] == "--skip-deps";
		var root = SboxSettings.Resolve() ?? RepoRoot.Find();

		if ( root is null )
		{
			Console.Error.WriteLine( "ampersand: could not locate the repo root - expected game/ and engine/ above this binary" );
			Console.Error.WriteLine( "hint: set it in the launcher (Replace s&box path) or run from the repo checkout" );
			return 1;
		}

		Bootstrap.Run( root, Console.WriteLine, skipDeps );

		Console.WriteLine();
		Console.Write( "Press Enter to close." );
		Console.ReadLine();

		return 0;
	}

	private static int ProbeDistroMain( string[] args )
	{
		var distro = args.Length > 1
			? SteamRt4Compat.DetectDistro( args[1] )
			: SteamRt4Compat.DetectDistro();

		Console.WriteLine( "family:     " + distro.Family );
		Console.WriteLine( "manager:    " + distro.Manager );
		Console.WriteLine( "escalation: " +
			( distro.Escalation == "" ? ( distro.IsRoot ? "(running as root)" : "(none found)" )
				: "'" + distro.Escalation.Trim() + "'" ) );
		Console.WriteLine( "atomic:     " + ( distro.Atomic ? "yes" : "no" ) );

		if ( distro.Caveat != "" )
			Console.WriteLine( "caveat:    " + distro.Caveat.Trim() );

		foreach ( var library in SteamRt4Compat.RequiredLibraries )
			Console.WriteLine( "required " + library + " -> " + distro.FormatInstall(
				SteamRt4Compat.PackageFor( distro.Family, library ) ) );

		foreach ( var library in SteamRt4Compat.BestEffortLibraries )
			Console.WriteLine( "optional " + library + " -> " + distro.FormatInstall(
				SteamRt4Compat.PackageFor( distro.Family, library ) ) );

		return 0;
	}

	private static AppBuilder BuildAvaloniaApp()
	{
		return AppBuilder.Configure<App>()
			.UsePlatformDetect()
			.LogToTrace();
	}
}

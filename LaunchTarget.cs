using System.ComponentModel;

namespace Ampersand;

/// <summary>
/// One row in the target panel: a script and the process running it.
///
/// It used to own a TerminalControl as well, so that the client and the server
/// could run at once and the pane simply showed whichever row was selected. The
/// pane is gone, and with it the only reason a target had to hold a control - a
/// target is now just state, and every row can still run at once because each
/// launch gets its own terminal window or none at all.
/// </summary>
internal sealed class LaunchTarget : INotifyPropertyChanged
{
	public string Name { get; }
	public string ScriptFile { get; }
	public ProcessRunner Runner { get; }

	/// <summary>Header read from the script; drives the Steam runtime checkbox.</summary>
	public ScriptMetadata Metadata { get; set; } = new();

	/// <summary>Per-target checkbox state. Persisted in step 6.</summary>
	public bool UseRuntime { get; set; }

	/// <summary>
	/// Per-target: open the user's terminal emulator for this run. Default on,
	/// because with no output pane left it is the only way to see anything the
	/// engine prints; unticked runs it with the output discarded.
	/// </summary>
	public bool UseSystemTerminal { get; set; } = true;

	/// <summary>
	/// True from the double-click until the process is actually spawned. The
	/// containerised path can wait minutes on Steam starting, and Runner.IsRunning
	/// is still false throughout - so without this a second double-click would
	/// start a second launch of the same target.
	/// </summary>
	public bool Preparing { get; set; }

	public event PropertyChangedEventHandler? PropertyChanged;

	public LaunchTarget( string name, string scriptFile )
	{
		Name = name;
		ScriptFile = scriptFile;
		Runner = new ProcessRunner( name );
	}

	public string Display
	{
		get
		{
			if ( Runner.IsRunning )
				return Name + "   \u25cf running";

			if ( Runner.ExitCode is int code )
				return Name + ( code == 0 ? "   exited 0" : "   exited " + code );

			return Name;
		}
	}

	public void NotifyStatusChanged()
	{
		PropertyChanged?.Invoke( this, new PropertyChangedEventArgs( nameof( Display ) ) );
	}
}

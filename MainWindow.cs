using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Ampersand;

internal sealed class MainWindow : Window
{
	private readonly AvaloniaList<LaunchTarget> targets = new();

	private readonly ListBox targetList;
	private readonly TextBlock statusText;
	private readonly Button stopButton;
	private readonly Button depCheckButton;
	private readonly Button bootstrapButton;
	private readonly Button logFolderButton;
	private readonly TextBox sboxPathBox;
	private readonly Button browsePathButton;
	private readonly TextBox sboxServerGameBox;
	private readonly Button browseServerGameButton;
	private readonly Border sboxServerGamePanel;
	private readonly CheckBox steamRuntime;
	private readonly CheckBox systemTerminal;
	private readonly CheckBox sdlWinFix;

	private LaunchTarget? selected;
	private bool updatingCheckbox;
	private string? resolvedRoot;
	private bool sboxRootPromptShown;

	public MainWindow()
	{
		Title = "Ampersand";
		Icon = AppIcon.Icon;
		Width = 1040;
		Height = 660;
		RequestedThemeVariant = ThemeVariant.Dark;
		Background = TerminalTheme.Background;

		foreach ( var (name, script) in new[]
		{
			( "sbox", "sbox.sh" ),
			( "sbox-dev", "sbox-dev.sh" ),
			( "sbox-server", "sbox-server.sh" )
		} )
		{
			targets.Add( new LaunchTarget( name, script ) );
		}

		LoadMetadata();

		targetList = new ListBox
		{
			ItemsSource = targets,
			Background = Brushes.Transparent,

			// Each shell script row gets an explicit play button instead of the
			// old invisible double-click/text affordance. Row is a grid: label
			// left, ▶ right. Double-click still works for muscle memory.
			ItemTemplate = new FuncDataTemplate<LaunchTarget>( ( item, _ ) =>
			{
				if ( item is not LaunchTarget target )
					return new TextBlock { Text = "" };

				var grid = new Grid
				{
					ColumnDefinitions = new ColumnDefinitions( "*,Auto" ),
					Background = Brushes.Transparent
				};

				var text = new TextBlock
				{
					Margin = new Thickness( 6, 4 ),
					VerticalAlignment = VerticalAlignment.Center
				};
				text.Bind( TextBlock.TextProperty, new Binding( nameof( LaunchTarget.Display ) ) );
				grid.Children.Add( text );

				var glyph = new TextBlock
				{
					Text = "▶",
					FontSize = 13,
					FontFamily = new FontFamily( "DejaVu Sans Mono" ),
					Foreground = TerminalTheme.Launcher,
					VerticalAlignment = VerticalAlignment.Center,
					HorizontalAlignment = HorizontalAlignment.Center
				};

				var playButton = new Button
				{
					Content = glyph,
					Width = 32,
					Height = 28,
					Padding = new Thickness( 0 ),
					Margin = new Thickness( 4, 2, 4, 2 ),
					CornerRadius = new CornerRadius( 4 ),
					Background = Brushes.Transparent,
					BorderThickness = new Thickness( 0 ),
					HorizontalContentAlignment = HorizontalAlignment.Center,
					VerticalContentAlignment = VerticalAlignment.Center,
					Cursor = new Cursor( StandardCursorType.Hand )
				};

				playButton.Styles.Add( PresenterFill( ":pointerover", TerminalTheme.SidebarHover ) );
				playButton.Styles.Add( PresenterFill( ":pressed", TerminalTheme.SidebarPressed ) );

				void UpdateState()
				{
					var busy = target.Runner.IsRunning || target.Preparing;
					playButton.IsEnabled = !busy;
					glyph.Opacity = busy ? 0.35 : 1.0;
					ToolTip.SetTip( playButton, busy ? "Already running" : $"Launch {target.Name}" );
				}

				playButton.Click += ( _, e ) =>
				{
					e.Handled = true;
					if ( target.Runner.IsRunning || target.Preparing )
						return;
					targetList.SelectedItem = target;
					ShowSelected();
					Launch( target );
				};

				// Keep button state in sync. Capture handlers for later detach.
				System.Action<ProcessRunner> runnerHandler = _ => Avalonia.Threading.Dispatcher.UIThread.Post( UpdateState );
				System.ComponentModel.PropertyChangedEventHandler displayHandler = ( _, e ) =>
				{
					if ( e.PropertyName == nameof( LaunchTarget.Display ) )
						Avalonia.Threading.Dispatcher.UIThread.Post( UpdateState );
				};

				target.Runner.StateChanged += runnerHandler;
				target.PropertyChanged += displayHandler;
				grid.DetachedFromVisualTree += ( _, _ ) =>
				{
					target.Runner.StateChanged -= runnerHandler;
					target.PropertyChanged -= displayHandler;
				};

				UpdateState();

				Grid.SetColumn( playButton, 1 );
				grid.Children.Add( playButton );

				return grid;
			}, supportsRecycling: true )
		};

		// Selection only; launch is exclusively via the per-row play button.
		// Single click selects so the Steam Runtime / terminal toggles can be
		// set before starting; double-click no longer starts a launch.
		targetList.SelectionChanged += ( _, _ ) => ShowSelected();
		targetList.Tapped += ( _, e ) =>
		{
			if ( IsOnRow( e.Source ) )
				ShowSelected();
		};

		steamRuntime = new CheckBox
		{
			Content = "Launch in Steam Runtime",
			IsEnabled = false,
			VerticalAlignment = VerticalAlignment.Center
		};
		steamRuntime.IsCheckedChanged += ( _, _ ) =>
		{
			if ( !updatingCheckbox && selected is not null )
				selected.UseSniper = steamRuntime.IsChecked == true;
		};

		// Decided BEFORE launching, because the emulator has to wrap the command
		// at spawn time - there is nothing to attach to a window opened later.
		systemTerminal = new CheckBox
		{
			Content = "Launch with system terminal",
			IsEnabled = false,
			VerticalAlignment = VerticalAlignment.Center
		};
		systemTerminal.IsCheckedChanged += ( _, _ ) =>
		{
			if ( !updatingCheckbox && selected is not null )
				selected.UseSystemTerminal = systemTerminal.IsChecked == true;
		};

		// TRANSIENT stopgap toggle for the Linux/XWayland SDL embedding gap
		// (frozen play-viewport size, offset menu clicks). Global, persisted in
		// settings.json, takes effect on the next launch. Delete with
		// sdlwinfix.c once Facepunch fixes it natively.
		sdlWinFix = new CheckBox
		{
			Content = "SDL embed fix (Linux)",
			VerticalAlignment = VerticalAlignment.Center,
			IsChecked = SboxSettings.GetSdlWinFix()
		};
		ToolTip.SetTip( sdlWinFix, "TRANSIENT stopgap until Facepunch fixes the native embedding.\n"
			+ "Reports the live play-widget geometry to SDL (fixes fullscreen menu size\n"
			+ "and offset clicks in editor play mode on Linux/XWayland).\n"
			+ "Takes effect on next launch. Observe-only topology logging stays env-only:\n"
			+ "SBOX_SDLWINFIX=observe." );
		sdlWinFix.IsCheckedChanged += async ( _, _ ) =>
		{
			if ( updatingCheckbox ) return;
			try { SboxSettings.SaveSdlWinFix( sdlWinFix.IsChecked == true ); }
			catch ( Exception e )
			{
				await ConfirmDialog.Notify( this, "Could not save settings", e.Message + "\n\nPath: " + SboxSettings.ConfigPath );
			}
		};

		var toggles = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 16,
			VerticalAlignment = VerticalAlignment.Center
		};
		toggles.Children.Add( systemTerminal );
		toggles.Children.Add( steamRuntime );
		toggles.Children.Add( sdlWinFix );

		statusText = new TextBlock
		{
			Text = "select an app and click \u25B6 to launch it",
			Foreground = TerminalTheme.Normal,
			FontSize = 12,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness( 10, 0 ),
			TextTrimming = TextTrimming.CharacterEllipsis
		};

		stopButton = new Button { Content = "Stop", IsEnabled = false, Margin = new Thickness( 8, 6, 10, 6 ) };
		stopButton.Click += ( _, _ ) => selected?.Runner.Stop();

		// One bar, not the old header-plus-strip: with no pane between them there
		// is nothing for two separate surfaces to separate.
		var bar = new Grid { ColumnDefinitions = new ColumnDefinitions( "*,Auto,Auto" ) };
		bar.Children.Add( statusText );
		Grid.SetColumn( toggles, 1 );
		bar.Children.Add( toggles );
		Grid.SetColumn( stopButton, 2 );
		bar.Children.Add( stopButton );

		var targetPanel = Surface( targetList, TerminalTheme.TargetPanel, new Thickness( 0, 0, 0, 1 ) );
		var barPanel = Surface( bar, TerminalTheme.ToolbarPanel, new Thickness( 0 ) );

		// Dedicated server game field, only visible when sbox-server is selected.
		// Accepts either a package ident (fss.bloodsigil) or an absolute path to a .sbproj.
		sboxServerGameBox = new TextBox
		{
			FontSize = 11,
			PlaceholderText = "Game ident (fss.bloodsigil) or path to .sbproj",
			Margin = new Thickness( 10, 6, 0, 6 )
		};
		sboxServerGameBox.KeyDown += ( _, e ) =>
		{
			if ( e.Key == Avalonia.Input.Key.Enter )
				_ = CommitSboxServerGameAsync();
		};
		sboxServerGameBox.LostFocus += async ( _, _ ) => await CommitSboxServerGameAsync();

		browseServerGameButton = new Button
		{
			Content = "…",
			Width = 30,
			Height = 26,
			Padding = new Thickness( 0 ),
			Margin = new Thickness( 6, 6, 10, 6 ),
			CornerRadius = new CornerRadius( 4 ),
			VerticalAlignment = VerticalAlignment.Top,
			HorizontalContentAlignment = HorizontalAlignment.Center,
			VerticalContentAlignment = VerticalAlignment.Center,
			Cursor = new Cursor( StandardCursorType.Hand )
		};
		ToolTip.SetTip( browseServerGameButton, "Browse for a .sbproj file" );
		browseServerGameButton.Styles.Add( PresenterFill( ":pointerover", TerminalTheme.SidebarHover ) );
		browseServerGameButton.Styles.Add( PresenterFill( ":pressed", TerminalTheme.SidebarPressed ) );
		browseServerGameButton.Click += async ( _, _ ) => await PickAndSaveServerGameAsync();

		var sboxServerGameRow = new Grid { ColumnDefinitions = new ColumnDefinitions( "*,Auto" ) };
		sboxServerGameRow.Children.Add( sboxServerGameBox );
		Grid.SetColumn( browseServerGameButton, 1 );
		sboxServerGameRow.Children.Add( browseServerGameButton );

		var sboxServerGameContent = new StackPanel();
		sboxServerGameContent.Children.Add( SidebarHeader( "DEDICATED SERVER GAME" ) );
		sboxServerGameContent.Children.Add( sboxServerGameRow );

		sboxServerGamePanel = Surface( sboxServerGameContent, TerminalTheme.ToolbarPanel, new Thickness( 0, 1, 0, 0 ) );
		sboxServerGamePanel.IsVisible = false;

		var right = new Grid { RowDefinitions = new RowDefinitions( "Auto,*,Auto,Auto" ) };
		right.Children.Add( SidebarHeader( "SHELL SCRIPTS" ) );
		Grid.SetRow( targetPanel, 1 );
		right.Children.Add( targetPanel );
		Grid.SetRow( barPanel, 2 );
		right.Children.Add( barPanel );
		Grid.SetRow( sboxServerGamePanel, 3 );
		right.Children.Add( sboxServerGamePanel );

		depCheckButton = SidebarButton( "Check for missing dependencies" );
		depCheckButton.Click += ( _, _ ) => RunDependencyCheck();

		bootstrapButton = SidebarButton( "Build S&Box" );
		bootstrapButton.Click += ( _, _ ) => RunBootstrap( skipDeps: false );
		ToolTip.SetTip( bootstrapButton, "Fetch natives, check dependencies, then build - port of sbox-public/bootstrap.sh" );

		logFolderButton = SidebarButton( "Open log folder" );
		logFolderButton.Click += ( _, _ ) => OpenLogFolder();

		// The s&box location is an official feature of the program, not a shell
		// action, so it gets its own editable field plus a browse button instead
		// of another ">_" command row. Editing applies on Enter/LostFocus; the
		// "…" button opens the same picker dialog that first-run uses.
		sboxPathBox = new TextBox
		{
			FontSize = 11,
			PlaceholderText = "s&box install folder (contains game/ and engine/)",
			Margin = new Thickness( 10, 6, 0, 0 )
		};
		sboxPathBox.KeyDown += ( _, e ) =>
		{
			if ( e.Key == Avalonia.Input.Key.Enter )
				_ = CommitSboxPathBoxAsync();
		};
		sboxPathBox.LostFocus += async ( _, _ ) => await CommitSboxPathBoxAsync();

		browsePathButton = new Button
		{
			Content = "…",
			Width = 30,
			Height = 26,
			Padding = new Thickness( 0 ),
			Margin = new Thickness( 6, 6, 10, 0 ),
			CornerRadius = new CornerRadius( 4 ),
			VerticalAlignment = VerticalAlignment.Top,
			HorizontalContentAlignment = HorizontalAlignment.Center,
			VerticalContentAlignment = VerticalAlignment.Center,
			Cursor = new Cursor( StandardCursorType.Hand )
		};
		ToolTip.SetTip( browsePathButton, "Browse for the s&box install folder" );
		browsePathButton.Styles.Add( PresenterFill( ":pointerover", TerminalTheme.SidebarHover ) );
		browsePathButton.Styles.Add( PresenterFill( ":pressed", TerminalTheme.SidebarPressed ) );
		browsePathButton.Click += async ( _, _ ) => await PickAndSavePathAsync();

		var sboxPathRow = new Grid { ColumnDefinitions = new ColumnDefinitions( "*,Auto" ) };
		sboxPathRow.Children.Add( sboxPathBox );
		Grid.SetColumn( browsePathButton, 1 );
		sboxPathRow.Children.Add( browsePathButton );

		// Resolve persisted path now (may be null); heavy prompt is deferred to Opened.
		resolvedRoot = SboxSettings.Resolve();
		if ( resolvedRoot is null )
		{
			var stale = SboxSettings.GetStalePersistedPath();
			if ( !string.IsNullOrEmpty( stale ) )
				resolvedRoot = stale; // keep stale for display, will still prompt
		}
		UpdateSboxPathDisplay();
		UpdateSboxServerGameDisplay();

		var sboxLocationPanel = new StackPanel();
		sboxLocationPanel.Children.Add( SidebarHeader( "S&BOX LOCATION" ) );
		sboxLocationPanel.Children.Add( sboxPathRow );

		var actions = new StackPanel();
		actions.Children.Add( bootstrapButton );
		actions.Children.Add( depCheckButton );
		actions.Children.Add( logFolderButton );

		var toolsPanel = new StackPanel();
		toolsPanel.Children.Add( SidebarHeader( "TOOLS" ) );
		toolsPanel.Children.Add( actions );

		var actionScroll = new ScrollViewer
		{
			Content = toolsPanel,
			HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
		};

		// Left sidebar, top to bottom: TOOLS at the very top, then the S&BOX
		// LOCATION field pinned just above the permanent advisory footer.
		// SHELL SCRIPTS header lives on the right panel directly above the
		// actual script buttons, not above the location block.
		var left = new Grid { RowDefinitions = new RowDefinitions( "Auto,*,Auto,Auto" ) };
		left.Children.Add( actionScroll );

		Grid.SetRow( sboxLocationPanel, 2 );
		left.Children.Add( sboxLocationPanel );

		var footer = SidebarFooter( "SHARE SCRIPTS AT YOUR OWN RISK" );
		Grid.SetRow( footer, 3 );
		left.Children.Add( footer );

		var root = new Grid { ColumnDefinitions = new ColumnDefinitions( "25*,75*" ) };
		root.Children.Add( Surface( left, TerminalTheme.SidebarPanel, new Thickness( 0, 0, 1, 0 ) ) );
		Grid.SetColumn( right, 1 );
		root.Children.Add( right );

		Content = root;

		foreach ( var target in targets )
		{
			var captured = target;

			captured.Runner.StateChanged += _ =>
			{
				captured.NotifyStatusChanged();

				if ( ReferenceEquals( captured, selected ) )
					UpdateStatusBar();

				// Non-zero exit: show Avalonia tail dialog (always close konsole, tail here).
				// Only for botched runs; success keeps log silently.
				if ( !captured.Runner.IsRunning && captured.Runner.ExitCode is int code && code != 0 )
				{
					var log = captured.Runner.LastLogPath;
					if ( !string.IsNullOrEmpty( log ) )
					{
						// Post to UI thread; StateChanged already on UI thread but be safe.
						Avalonia.Threading.Dispatcher.UIThread.Post( () =>
						{
							ShowTailDialog( captured, log, code );
						} );
					}
				}
			};

			// The runner's own lines used to be written into that target's pane.
			// There is no pane, so they land on the status bar - and only while
			// that target is the selected one, since the bar describes the
			// selection and a background target overwriting it would be a lie.
			captured.Runner.Notice += line =>
			{
				if ( ReferenceEquals( captured, selected ) )
					statusText.Text = captured.Name + ": " + line;
			};
		}

		if ( SystemTerminal.Detect() is null )
			statusText.Text = "no terminal emulator found - launches will run with their output discarded";

		// Defer path prompt until window is visible so modal has an owner.
		Opened += async ( _, _ ) => await EnsureSboxRootAsync();
	}

	/// <summary>True when a pointer event landed on a row rather than the empty
	/// space below the list.</summary>
	private static bool IsOnRow( object? source )
	{
		return source is Visual visual && visual.FindAncestorOfType<ListBoxItem>( true ) is not null;
	}

	private void ShowSelected()
	{
		if ( targetList.SelectedItem is not LaunchTarget target )
			return;

		if ( ReferenceEquals( target, selected ) )
			return;

		selected = target;
		UpdateStatusBar();
		UpdateServerGamePanelVisibility();
	}

	private void UpdateServerGamePanelVisibility()
	{
		if ( sboxServerGamePanel is null ) return;
		var isServer = selected?.ScriptFile == "sbox-server.sh";
		sboxServerGamePanel.IsVisible = isServer;
		if ( isServer )
			UpdateSboxServerGameDisplay();
	}

	private void UpdateToggles( LaunchTarget target )
	{
		updatingCheckbox = true;

		switch ( target.Metadata.Sniper )
		{
			case SniperMode.Never:
				steamRuntime.IsChecked = false;
				steamRuntime.IsEnabled = false;
				break;

			case SniperMode.Always:
				steamRuntime.IsChecked = true;
				steamRuntime.IsEnabled = false;
				break;

			default:
				steamRuntime.IsChecked = target.UseSniper;
				steamRuntime.IsEnabled = true;
				break;
		}

		// Cannot be changed once running: the choice is baked into the command
		// that was already spawned.
		systemTerminal.IsChecked = target.UseSystemTerminal;
		systemTerminal.IsEnabled = SystemTerminal.Detect() is not null
			&& !target.Runner.IsRunning && !target.Preparing;

		updatingCheckbox = false;
	}

	/// <summary>
	/// Guard around the launch path, and nothing else.
	///
	/// This is reached from a double-click handler, so it is async void: nothing
	/// observes the task, and an exception escaping it is unhandled and takes the
	/// window down. A tool for diagnosing a broken engine must not be killed by
	/// the engine being broken. The prepare path is the live risk - it shells
	/// out, copies megabytes, and waits on Steam.
	/// </summary>
	private async void Launch( LaunchTarget target )
	{
		try
		{
			await LaunchCore( target );
		}
		catch ( Exception e )
		{
			// Preparing is already cleared by LaunchCore's finally, whichever
			// way it left.
			await Fail( target, "Launch failed", e.Message );
		}
	}

	private string? GetSboxRoot()
	{
		if ( !string.IsNullOrEmpty( resolvedRoot ) && SboxSettings.IsValid( resolvedRoot ) )
			return resolvedRoot;

		var resolved = SboxSettings.Resolve();
		if ( resolved is not null && SboxSettings.IsValid( resolved ) )
		{
			resolvedRoot = resolved;
			UpdateSboxPathDisplay();
			return resolved;
		}

		// Stale persisted path - keep for display but treat as invalid for launch.
		return null;
	}

	private string? GetSboxServerGame()
	{
		// Prefer the live text box (may have unsaved edits) over the persisted value.
		string? fromBox = null;
		try { fromBox = sboxServerGameBox?.Text?.Trim(); } catch { }
		if ( !string.IsNullOrWhiteSpace( fromBox ) )
		{
			var norm = SboxSettings.NormalizeServerGame( fromBox );
			if ( !string.IsNullOrWhiteSpace( norm ) )
				return norm;
		}
		return SboxSettings.GetSboxServerGame();
	}

	private async Task LaunchCore( LaunchTarget target )
	{
		var root = GetSboxRoot();
		if ( root is null )
		{
			var stale = SboxSettings.GetStalePersistedPath() ?? resolvedRoot;
			var detail = stale is not null
				? $"Saved s&box path is no longer valid:\n{stale}\n\nExpected a folder containing game/ and engine/ with game/sbox."
				: "ampersand could not locate the s&box tree - it expects game/ and engine/ "
					+ "somewhere above this binary.\n\nPick the repository root (e.g. /home/you/sbox).";

			var shouldPick = await ConfirmDialog.Show( this, "s&box location missing", detail, "Select location…", "Cancel" );
			if ( shouldPick )
			{
				if ( await PickAndSavePathAsync() )
				{
					root = GetSboxRoot();
					if ( root is null ) return;
				}
				else return;
			}
			else
			{
				await Fail( target, "Repo root not found", detail );
				return;
			}
		}

		// Scripts live with the built app (OutDir/scripts/), not inside the
		// sbox checkout. They stay loose files so users can edit them at runtime.
		var script = AppPaths.FindScript( target.ScriptFile );
		if ( script is null )
		{
			// Legacy fallback: ampersand inside sbox checkout (dev layout)
			var legacy = Path.Combine( root, "ampersand", "apps", target.ScriptFile );
			if ( File.Exists( legacy ) )
				script = legacy;
		}
		if ( script is null || !File.Exists( script ) )
		{
			var tried = AppPaths.FindScriptsDir() ?? AppPaths.ScriptsDir;
			await Fail( target, "Launch script missing",
				$"Not found: {target.ScriptFile}\n\nLooked in:\n{tried}\n(built scripts dir)\n\n"
				+ "Rebuild ampersand or check that apps/*.sh were copied to <OutDir>/scripts/." );
			return;
		}

		var env = new Dictionary<string, string>
		{
			["SBOX_REPO_ROOT"] = root,
			// Wayland Qt plugin not shipped/supported - force xcb (XWayland).
			// Only xcb is bundled ("Available platform plugins are: xcb").
			// Set here so it crosses the Steam Runtime container boundary via --env;
			// _common.sh also hard-sets it for direct script runs.
			["QT_QPA_PLATFORM"] = "xcb"
		};
		// TRANSIENT SDL embed fix (see sdlWinFix checkbox): persisted toggle,
		// read live so it applies to this launch. Same --env crossing as above.
		if ( sdlWinFix.IsChecked == true )
			env["SBOX_SDLWINFIX"] = "1";
		var command = new List<string>();

		// Held across the await below, not just the spawn: PrepareSniper can sit
		// for minutes waiting on Steam, and nothing else marks the target busy.
		target.Preparing = true;
		target.NotifyStatusChanged();
		UpdateStatusBar();

		try
		{
			if ( target.UseSniper )
			{
				if ( !await PrepareSniper( target, root, script, command, env ) )
					return;
			}
			else
			{
				command.Add( script );
			}
		}
		finally
		{
			// Clearing the flag is not enough on its own: Preparing raises no
			// change event, so without a notify every failed prepare (Steam
			// not running, missing runtime, Start throwing) leaves the play
			// button disabled after its dialog. Harmless on the success path,
			// where Start fires its own state change right after.
			target.Preparing = false;
			target.NotifyStatusChanged();
			UpdateStatusBar();
		}

		// Append sbox-server game argument (ident or .sbproj path) when launching the
		// dedicated server. The engine expects "+game <ident|/path/to.sbproj>" as
		// concommand arguments (see engine/Sandbox.GameInstance/GameInstanceDll.cs:872,
		// ViewportTools.SpawnDedicatedServer). We store only the ident/path and add
		// the "+game" switch here. Both host and sniper wrapper paths forward "$@" to sbox-server.
		if ( target.ScriptFile == "sbox-server.sh" )
		{
			var serverGame = GetSboxServerGame();
			if ( !string.IsNullOrWhiteSpace( serverGame ) )
			{
				// Persist the box's live edit if it hasn't been committed yet (user hit
				// play without blurring the field).
				try
				{
					var boxText = sboxServerGameBox?.Text?.Trim();
					if ( !string.IsNullOrWhiteSpace( boxText ) )
					{
						var normBox = SboxSettings.NormalizeServerGame( boxText );
						if ( normBox == serverGame )
							SboxSettings.SaveServerGame( serverGame );
					}
				}
				catch { }

				command.Add( "+game" );
				command.Add( serverGame );
			}
		}

		try
		{
			target.Runner.Start( command, root, env, target.UseSystemTerminal );
		}
		catch ( Exception e )
		{
			await Fail( target, "Launch failed", e.Message );
			return;
		}

		target.NotifyStatusChanged();
		UpdateStatusBar();
	}

	/// <summary>
	/// Builds the containerised command, and refuses to build one at all unless
	/// Steam is up.
	///
	/// Steam running is a hard requirement, not a preference: the container is
	/// entered through the launcher service running alongside the client, because
	/// that is the only context in which the runtime's own bwrap is allowed to
	/// create a user namespace. See SteamLauncherService.
	/// </summary>
	private async Task<bool> PrepareSniper(
		LaunchTarget target,
		string root,
		string script,
		List<string> command,
		Dictionary<string, string> env )
	{
		var install = SniperRuntime.Find();

		if ( install is null )
		{
			await Fail( target, "Steam Linux Runtime not installed",
				"Steam Linux Runtime 3.0 (sniper) is not installed.\n\n"
					+ "Install it from Steam:  steam steam://install/" + SniperRuntime.SteamAppId );
			return false;
		}

		statusText.Text = target.Name + ": runtime " + install.Version;

		// Everything below shells out or copies megabytes, so none of it may run
		// on the UI thread - the probe alone can sit five seconds. This is the
		// reason the method is async; awaiting a blocking call would only move
		// the freeze, not remove it.
		if ( !await Task.Run( () => SteamLauncherService.IsAvailable( install ) ) )
		{
			await Fail( target, "Steam is not running",
				"s&box can only be launched in the Steam runtime while the Steam client is "
					+ "running - the container is entered through Steam's own launcher service.\n\n"
					+ "Start Steam, wait for it to sign in, then try again." );
			return false;
		}

		var requirements = await Task.Run( () =>
		{
			var met = SniperRuntime.CheckRequirements( install, out var found );
			return (Met: met, Problems: found);
		} );

		if ( !requirements.Met )
		{
			await Fail( target, "Container cannot start",
				"This host cannot start a pressure-vessel container.\n\n"
					+ string.Join( "\n", requirements.Problems ) );
			return false;
		}

		var compat = await Task.Run( () =>
		{
			var ready = SniperCompat.Ensure( out var found );
			return (Ready: ready, Problems: found);
		} );

		if ( !compat.Ready )
		{
			await Fail( target, "Compat libraries unavailable", string.Join( "\n", compat.Problems ) );
			return false;
		}

		var cache = SniperCompat.CacheDirectory;

		env["SBOX_IN_SNIPER"] = "1";
		env["SBOX_SNIPER_COMPAT"] = cache;

		// TERM is added here rather than left to the emulator: this one is set on
		// launch-client's own environment, and the child does not inherit it. It
		// is handed over unconditionally - in background mode there is no tty, so
		// the engine suppresses colour regardless of what TERM says.
		var passed = new Dictionary<string, string>( env ) { ["TERM"] = ProcessRunner.Term };

		command.AddRange( SteamLauncherService.Wrap(
			install,
			new[] { install.RunScript, "--filesystem=" + cache, "--", script },
			root,
			passed ) );

		return true;
	}

	/// <summary>
	/// How a launch failure is reported now that there is no pane to print it
	/// into: the status bar keeps the short form, and a dialog carries the part
	/// that needs more than one line. Both, because the bar is a glance and the
	/// dialog is the explanation.
	/// </summary>
	private async Task Fail( LaunchTarget target, string title, string message )
	{
		statusText.Text = target.Name + ": " + title.ToLowerInvariant();
		UpdateStatusBar();

		await ConfirmDialog.Notify( this, title, message );
	}

	/// <summary>
	/// Runs the dependency sweep in the user's terminal emulator, by re-execing
	/// this binary with --dependency-check (see Program).
	///
	/// The sweep used to print into the diagnostics pane. Re-execing rather than
	/// running it in-process and piping the text somewhere is what keeps the
	/// report intact: it is coloured with SGR sequences and aligned in columns,
	/// both of which need a real terminal - and this way the app keeps no output
	/// surface of its own at all.
	/// </summary>
	private async void RunDependencyCheck()
	{
		try
		{
			var root = GetSboxRoot();
			if ( root is null )
			{
				var stale = SboxSettings.GetStalePersistedPath() ?? resolvedRoot;
				var detail = stale is not null
					? $"Saved s&box path is no longer valid:\n{stale}"
					: "ampersand could not locate the s&box tree - it expects game/ and engine/ "
						+ "somewhere above this binary.";
				await ConfirmDialog.Notify( this, "Repo root not found", detail + "\n\nUse Replace s&box path in the sidebar." );
				return;
			}

			// Null only for a single-file host that cannot report its own path;
			// Assembly.Location is empty in that case too, so there is nothing
			// better to fall back to.
			var self = Environment.ProcessPath;
			if ( self is null )
			{
				await ConfirmDialog.Notify( this, "Cannot re-exec",
					"ampersand could not determine its own path, so it cannot run the "
						+ "dependency check in a terminal." );
				return;
			}

			if ( !SystemTerminal.TryBuild( new[] { self, Program.DependencyCheckArgument },
					out var argv, out var emulator ) )
			{
				await ConfirmDialog.Notify( this, "No terminal emulator",
					"The dependency check prints a coloured report, so it needs a terminal "
						+ "window - and no emulator was found on PATH.\n\n"
						+ "Install one (gnome-terminal, konsole, alacritty, kitty, foot, xterm...), "
						+ "or run it yourself:\n\n"
						+ self + " " + Program.DependencyCheckArgument );
				return;
			}

			var info = new ProcessStartInfo
			{
				FileName = argv[0],
				WorkingDirectory = root,
				UseShellExecute = false
			};

			for ( var i = 1; i < argv.Count; i++ )
				info.ArgumentList.Add( argv[i] );

			Process.Start( info );
			statusText.Text = "dependency check running in " + emulator;
		}
		catch ( Exception e )
		{
			statusText.Text = "dependency check failed - " + e.Message;
		}
	}

	private async void RunBootstrap( bool skipDeps )
	{
		try
		{
			var root = GetSboxRoot();
			if ( root is null )
			{
				var stale = SboxSettings.GetStalePersistedPath() ?? resolvedRoot;
				var detail = stale is not null
					? $"Saved s&box path is no longer valid:\n{stale}"
					: "ampersand could not locate the s&box tree - it expects game/ and engine/ "
						+ "somewhere above this binary.";
				await ConfirmDialog.Notify( this, "Repo root not found", detail + "\n\nUse Replace s&box path in the sidebar." );
				return;
			}

			var self = Environment.ProcessPath;
			if ( self is null )
			{
				await ConfirmDialog.Notify( this, "Cannot re-exec",
					"ampersand could not determine its own path, so it cannot run bootstrap in a terminal." );
				return;
			}

			var args = new List<string> { self, Program.BootstrapArgument };
			if ( skipDeps ) args.Add( "--skip-deps" );

			if ( !SystemTerminal.TryBuild( args, out var argv, out var emulator ) )
			{
				await ConfirmDialog.Notify( this, "No terminal emulator",
					"Bootstrap prints a coloured build log, so it needs a terminal "
						+ "window - and no emulator was found on PATH.\n\n"
						+ "Install one (gnome-terminal, konsole, alacritty, kitty, foot, xterm...), "
						+ "or run it yourself:\n\n"
						+ self + " " + Program.BootstrapArgument + ( skipDeps ? " --skip-deps" : "" ) );
				return;
			}

			var info = new ProcessStartInfo
			{
				FileName = argv[0],
				WorkingDirectory = root,
				UseShellExecute = false
			};

			for ( var i = 1; i < argv.Count; i++ )
				info.ArgumentList.Add( argv[i] );

			Process.Start( info );
			statusText.Text = "bootstrap running in " + emulator;
		}
		catch ( Exception e )
		{
			statusText.Text = "bootstrap failed - " + e.Message;
		}
	}

	private async void ShowTailDialog( LaunchTarget target, string logPath, int code )
	{
		try
		{
			statusText.Text = target.Name + $": exited {code} - log: {logPath}";
			await ConfirmDialog.NotifyTail( this, target.Name + " failed", logPath, code );
		}
		catch { }
	}

	private void OpenLogFolder()
	{
		try
		{
			var dir = RunLog.LogDirectory;
			Directory.CreateDirectory( dir );
			if ( !RunLog.TryOpenFolder() )
				statusText.Text = "log folder: " + dir;
			else
				statusText.Text = "opened log folder: " + dir;
		}
		catch ( Exception e )
		{
			statusText.Text = "could not open log folder - " + e.Message;
		}
	}

	private void UpdateStatusBar()
	{
		if ( selected is null )
		{
			stopButton.IsEnabled = false;
			return;
		}

		var runner = selected.Runner;
		stopButton.IsEnabled = runner.IsRunning;

		if ( selected.Preparing )
			statusText.Text = "preparing - " + runner.Name;
		else if ( runner.IsRunning )
			statusText.Text = ( selected.UseSystemTerminal ? "running in a terminal - " : "running - " )
				+ runner.Name
				+ ( runner.LastLogPath is not null ? " - log: " + runner.LastLogPath : "" );
		else if ( runner.ExitCode is int code )
			statusText.Text = "exited " + code + " - " + runner.Name
				+ ( runner.LastLogPath is not null ? " - log: " + runner.LastLogPath : "" );
		else
			statusText.Text = "idle - " + runner.Name;

		UpdateToggles( selected );
		UpdateServerGamePanelVisibility();
	}

	private void LoadMetadata()
	{
		// Metadata comes from the built scripts (AppContext.BaseDirectory/scripts/),
		// not from the sbox checkout. Scripts are loose files so they can be
		// edited at runtime.
		var scriptsDir = AppPaths.FindScriptsDir();
		if ( scriptsDir is null )
		{
			// Legacy dev fallback: sboxRoot/ampersand/apps
			var root = GetSboxRoot() ?? SboxSettings.GetStalePersistedPath() ?? RepoRoot.Find();
			if ( root is not null && RepoRoot.IsValidRoot( root ) )
			{
				var legacyDir = Path.Combine( root, "ampersand", "apps" );
				if ( Directory.Exists( legacyDir ) )
					scriptsDir = legacyDir;
			}
		}
		if ( scriptsDir is null )
			return;

		foreach ( var target in targets )
		{
			var script = Path.Combine( scriptsDir, target.ScriptFile );

			if ( !File.Exists( script ) )
				continue;

			target.Metadata = ScriptMetadata.Read( script );
			target.UseSniper = target.Metadata.Sniper == SniperMode.Always;
		}
	}

	private void UpdateSboxPathDisplay()
	{
		if ( sboxPathBox is null ) return;

		if ( string.IsNullOrEmpty( resolvedRoot ) )
		{
			sboxPathBox.Text = "";
			ToolTip.SetTip( sboxPathBox, "No s&box location saved. Stored in " + SboxSettings.ConfigPath );
			return;
		}

		var valid = SboxSettings.IsValid( resolvedRoot );
		var display = SboxSettings.ShortenForDisplay( resolvedRoot, 42 );
		sboxPathBox.Text = valid ? display : "⚠ stale: " + display;
		ToolTip.SetTip( sboxPathBox, resolvedRoot + ( valid ? "" : "\n(stale: folder no longer contains game/ + engine/ + game/sbox)" ) + "\nStored in " + SboxSettings.ConfigPath );
	}

	private void UpdateSboxServerGameDisplay()
	{
		if ( sboxServerGameBox is null ) return;

		var game = SboxSettings.GetSboxServerGame();
		// Only overwrite if box isn't currently focused (don't clobber edit) and differs.
		// For simplicity always sync when not focused; if focused, leave as-is until commit.
		try
		{
			var focused = sboxServerGameBox.IsFocused;
			if ( focused ) return;
		}
		catch { }

		sboxServerGameBox.Text = game ?? "";
		var tip = string.IsNullOrWhiteSpace( game )
			? "Game for sbox-server: ident (fss.bloodsigil) or path to .sbproj.\nStored in " + SboxSettings.ConfigPath
			: game + "\nStored in " + SboxSettings.ConfigPath;
		ToolTip.SetTip( sboxServerGameBox, tip );
	}

	private async Task CommitSboxServerGameAsync()
	{
		if ( sboxServerGameBox is null ) return;

		var raw = sboxServerGameBox.Text?.Trim();
		if ( string.IsNullOrWhiteSpace( raw ) )
		{
			// Empty clears the persisted value.
			try { SboxSettings.SaveServerGame( null ); } catch { }
			sboxServerGameBox.Text = "";
			ToolTip.SetTip( sboxServerGameBox, "Game for sbox-server: ident (fss.bloodsigil) or path to .sbproj.\nStored in " + SboxSettings.ConfigPath );
			statusText.Text = "cleared dedicated server game";
			return;
		}

		var normalized = SboxSettings.NormalizeServerGame( raw );
		if ( normalized is null )
		{
			UpdateSboxServerGameDisplay();
			return;
		}

		try
		{
			SboxSettings.SaveServerGame( normalized );
		}
		catch ( Exception e )
		{
			await ConfirmDialog.Notify( this, "Could not save settings", e.Message + "\n\nPath: " + SboxSettings.ConfigPath );
			return;
		}

		sboxServerGameBox.Text = normalized;
		ToolTip.SetTip( sboxServerGameBox, normalized + "\nStored in " + SboxSettings.ConfigPath );
		statusText.Text = "dedicated server game: " + normalized;
	}

	private async Task PickAndSaveServerGameAsync()
	{
		try
		{
			var sp = StorageProvider;
			if ( sp is not null )
			{
				var current = SboxSettings.GetSboxServerGame();
				Uri? startUri = null;
				if ( !string.IsNullOrWhiteSpace( current ) && ( current.Contains( '/' ) || current.Contains( '\\' ) ) )
				{
					try
					{
						var dir = Path.GetDirectoryName( current );
						if ( dir is not null && Directory.Exists( dir ) )
							startUri = new Uri( dir );
						else if ( File.Exists( current ) )
						{
							var d = Path.GetDirectoryName( Path.GetFullPath( current ) );
							if ( d is not null && Directory.Exists( d ) )
								startUri = new Uri( d );
						}
					}
					catch { }
				}

				// Fallback start: user's sboxprojects or home
				if ( startUri is null )
				{
					try
					{
						var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
						var proj = Path.Combine( home, "sboxprojects" );
						if ( Directory.Exists( proj ) )
							startUri = new Uri( proj );
						else if ( Directory.Exists( home ) )
							startUri = new Uri( home );
					}
					catch { }
				}

				var files = await sp.OpenFilePickerAsync( new FilePickerOpenOptions
				{
					Title = "Select .sbproj or enter ident",
					AllowMultiple = false,
					SuggestedStartLocation = startUri is not null ? await sp.TryGetFolderFromPathAsync( startUri ) : null,
					FileTypeFilter = new[]
					{
						new FilePickerFileType( "s&box Project (*.sbproj)" ) { Patterns = new[] { "*.sbproj" } },
						new FilePickerFileType( "All files" ) { Patterns = new[] { "*" } }
					}
				} );

				if ( files is { Count: > 0 } )
				{
					var picked = files[0].Path.LocalPath;
					if ( !string.IsNullOrEmpty( picked ) )
					{
						sboxServerGameBox.Text = picked;
						await CommitSboxServerGameAsync();
						return;
					}
				}
			}
		}
		catch { }

		// If picker unavailable or cancelled, just focus the text box for manual entry.
		try { sboxServerGameBox.Focus(); } catch { }
	}

	/// <summary>
	/// Commits whatever is in the editable location field, reusing the same
	/// validate->save->refresh path the picker dialog uses. The field is left
	/// alone on a failed attempt so the user can correct it in place.
	/// </summary>
	private async Task CommitSboxPathBoxAsync()
	{
		if ( sboxPathBox is null ) return;

		var raw = sboxPathBox.Text?.Trim();
		if ( string.IsNullOrEmpty( raw ) )
		{
			// Empty field reverts to the persisted/resolved value on next refresh.
			UpdateSboxPathDisplay();
			return;
		}

		if ( await ApplySboxPathAsync( raw ) )
			UpdateSboxPathDisplay();
	}

	/// <summary>
	/// Normalizes, validates, and persists a raw s&box path; on success refreshes
	/// the location field and status. True when a valid path was saved.
	/// Loops only inside the picker path, which needs retry on dialog use.
	/// </summary>
	private async Task<bool> ApplySboxPathAsync( string? raw )
	{
		if ( string.IsNullOrWhiteSpace( raw ) ) return false;

		var normalized = SboxSettings.Normalize( raw );

		if ( normalized is null || !SboxSettings.IsValid( normalized ) )
		{
			var detail = $"Expected a folder containing game/ and engine/ with game/sbox.\n\nYou entered:\n{raw}\n\nNormalized:\n{normalized ?? "(could not resolve)"}";
			await ConfirmDialog.Notify( this, "Invalid s&box location", detail );
			return false;
		}

		try
		{
			SboxSettings.Save( normalized );
		}
		catch ( Exception e )
		{
			await ConfirmDialog.Notify( this, "Could not save settings", e.Message + "\n\nPath: " + SboxSettings.ConfigPath );
			return false;
		}

		resolvedRoot = normalized;
		UpdateSboxPathDisplay();
		LoadMetadata();
		statusText.Text = "s&box location: " + SboxSettings.ShortenForDisplay( resolvedRoot, 60 );
		UpdateStatusBar();
		return true;
	}

	private async Task EnsureSboxRootAsync()
	{
		if ( sboxRootPromptShown ) return;
		sboxRootPromptShown = true;

		var valid = !string.IsNullOrEmpty( resolvedRoot ) && SboxSettings.IsValid( resolvedRoot );
		if ( valid )
		{
			statusText.Text = "s&box: " + SboxSettings.ShortenForDisplay( resolvedRoot!, 60 );
			return;
		}

		// Try resolve (migration from RepoRoot.Find)
		var resolved = SboxSettings.Resolve();
		if ( resolved is not null && SboxSettings.IsValid( resolved ) )
		{
			resolvedRoot = resolved;
			UpdateSboxPathDisplay();
			LoadMetadata();
			statusText.Text = "s&box: " + SboxSettings.ShortenForDisplay( resolvedRoot, 60 );
			UpdateStatusBar();
			return;
		}

		// Still missing or stale -> prompt
		var stale = SboxSettings.GetStalePersistedPath() ?? resolvedRoot;
		if ( stale is not null && !SboxSettings.IsValid( stale ) )
			statusText.Text = "s&box location stale: " + stale;
		else
			statusText.Text = "Select s&box location (no valid install found)";

		await PickAndSavePathAsync( isFirstRun: true );
	}

	/// <summary>
	/// Shows the path picker, validates, saves to .local/share.
	/// Returns true if a valid path was saved.
	/// The validate/save core is shared with the in-place field edit.
	/// </summary>
	private async Task<bool> PickAndSavePathAsync( bool isFirstRun = false )
	{
		while ( true )
		{
			var current = resolvedRoot ?? SboxSettings.GetStalePersistedPath();
			var raw = await PathPickerDialog.Show( this, current );

			if ( raw is null )
			{
				// Cancelled. For first-run keep prompt state; otherwise just return.
				if ( isFirstRun && string.IsNullOrEmpty( resolvedRoot ) )
					statusText.Text = "Select s&box location to launch";
				UpdateSboxPathDisplay();
				return false;
			}

			if ( await ApplySboxPathAsync( raw ) )
				return true;
		}
	}

	/// <summary>
	/// A sidebar command row: flush to the panel edges, no chrome of its own,
	/// and a shell glyph in the accent colour. Fluent puts a button's fill on
	/// the templated ContentPresenter, so the hover and pressed states have to
	/// be overridden there rather than on the Button.
	/// </summary>
	private static Button SidebarButton( string text )
	{
		var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

		// ">_" is the conventional shell glyph and, unlike an emoji, cannot
		// fail to render on any font the system happens to have.
		//
		// The family has to be a real one. "monospace" is a fontconfig alias,
		// not a family, so Avalonia does not resolve it and the glyph silently
		// falls back to the proportional UI font - which is the one thing a
		// shell prompt must not look like.
		row.Children.Add( new TextBlock
		{
			Text = ">_",
			FontFamily = new FontFamily( "DejaVu Sans Mono" ),
			FontSize = 12,
			Foreground = TerminalTheme.Launcher,
			VerticalAlignment = VerticalAlignment.Center
		} );

		row.Children.Add( new TextBlock
		{
			Text = text,
			FontSize = 12,
			TextWrapping = TextWrapping.Wrap,
			Foreground = TerminalTheme.Normal,
			VerticalAlignment = VerticalAlignment.Center
		} );

		var button = new Button
		{
			Content = row,
			Background = Brushes.Transparent,
			BorderThickness = new Thickness( 0 ),
			CornerRadius = new CornerRadius( 0 ),
			Padding = new Thickness( 10, 7 ),
			HorizontalAlignment = HorizontalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Left
		};

		button.Styles.Add( PresenterFill( ":pointerover", TerminalTheme.SidebarHover ) );
		button.Styles.Add( PresenterFill( ":pressed", TerminalTheme.SidebarPressed ) );

		return button;
	}

	private static Style PresenterFill( string state, IBrush fill )
	{
		var style = new Style( x => x.OfType<Button>().Class( state ).Template().OfType<ContentPresenter>() );
		style.Setters.Add( new Setter( ContentPresenter.BackgroundProperty, fill ) );
		return style;
	}

	/// <summary>
	/// An uppercase section header on the toolbar surface, in the manner of the
	/// VS Code sidebar. Pairs with the all-caps advisory at the foot.
	/// </summary>
	private static Border SidebarHeader( string text )
	{
		return new Border
		{
			Background = TerminalTheme.ToolbarPanel,
			BorderBrush = TerminalTheme.PanelBorder,
			BorderThickness = new Thickness( 0, 0, 0, 1 ),
			Padding = new Thickness( 10, 6 ),
			Child = new TextBlock
			{
				Text = text,
				FontSize = 11,
				FontWeight = FontWeight.SemiBold,
				Foreground = TerminalTheme.HeaderText
			}
		};
	}

	/// <summary>
	/// Standing advisory. The left panel runs arbitrary executables dropped into
	/// a scanned folder (spec 4b), including ones shared by other people, so the
	/// warning is permanent rather than a dismissible prompt.
	/// </summary>
	private static Border SidebarFooter( string text )
	{
		var row = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 6
		};

		row.Children.Add( new TextBlock
		{
			Text = "ⓘ",
			FontSize = 13,
			Foreground = TerminalTheme.FooterText,
			VerticalAlignment = VerticalAlignment.Center
		} );

		row.Children.Add( new TextBlock
		{
			Text = text,
			FontSize = 10,
			TextWrapping = TextWrapping.Wrap,
			Foreground = TerminalTheme.FooterText,
			VerticalAlignment = VerticalAlignment.Center
		} );

		return new Border
		{
			Background = TerminalTheme.ToolbarPanel,
			BorderBrush = TerminalTheme.PanelBorder,
			BorderThickness = new Thickness( 0, 1, 0, 0 ),
			Padding = new Thickness( 10, 7 ),
			Child = row
		};
	}

	/// <summary>
	/// A flush panel: no margin, its own background, and a 1px rule only on the
	/// edges that face another panel - so adjacent borders never double up.
	/// </summary>
	private static Border Surface( Control child, IBrush background, Thickness border )
	{
		return new Border
		{
			Background = background,
			BorderBrush = TerminalTheme.PanelBorder,
			BorderThickness = border,
			Child = child
		};
	}
}

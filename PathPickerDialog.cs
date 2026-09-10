using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;

namespace Ampersand;

internal static class PathPickerDialog
{
	/// <summary>
	/// Shows a path input dialog with TextBox + Browse.
	/// Returns the raw string the user typed/picked, or null if cancelled.
	/// Validation (Normalize/IsValid) is done by the caller so the dialog stays open for correction.
	/// </summary>
	public static Task<string?> Show( Window owner, string? currentPath )
	{
		var tcs = new TaskCompletionSource<string?>();

		var dialog = new Window
		{
			Title = "Select s&box location",
			Icon = AppIcon.Icon,
			Width = 560,
			SizeToContent = SizeToContent.Height,
			CanResize = false,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			RequestedThemeVariant = ThemeVariant.Dark,
			Background = TerminalTheme.ToolbarPanel
		};

		var hint = new TextBlock
		{
			Text = "Pick the s&box install folder (the one containing game/ and engine/)."
				+ "\nYou can also paste a path to game/ or game/sbox; it will be normalized.",
			TextWrapping = TextWrapping.Wrap,
			Foreground = TerminalTheme.FooterText,
			FontSize = 11,
			Margin = new Thickness( 0, 0, 0, 8 )
		};

		var currentLabel = new TextBlock
		{
			Text = string.IsNullOrEmpty( currentPath ) ? "No location saved yet." : "Current: " + currentPath,
			TextWrapping = TextWrapping.Wrap,
			Foreground = TerminalTheme.Normal,
			FontSize = 11,
			Margin = new Thickness( 0, 0, 0, 8 )
		};

		var input = new TextBox
		{
			Text = currentPath ?? "",
			PlaceholderText = "/home/you/sbox  or  ~/sbox  or  /home/you/sbox/game",
			FontSize = 12,
			MinWidth = 360
		};

		var browseBtn = new Button { Content = "Browse…", MinWidth = 80 };

		var inputRow = new Grid
		{
			ColumnDefinitions = new ColumnDefinitions( "*,Auto" ),
			Margin = new Thickness( 0, 0, 0, 6 )
		};
		inputRow.Children.Add( input );
		Grid.SetColumn( browseBtn, 1 );
		inputRow.Children.Add( browseBtn );
		browseBtn.Margin = new Thickness( 8, 0, 0, 0 );

		var error = new TextBlock
		{
			Text = "",
			TextWrapping = TextWrapping.Wrap,
			Foreground = new SolidColorBrush( Color.FromRgb( 0xFF, 0x6B, 0x6B ) ),
			FontSize = 11,
			IsVisible = false,
			Margin = new Thickness( 0, 0, 0, 4 )
		};

		var cancelBtn = new Button { Content = "Cancel", IsCancel = true };
		var saveBtn = new Button { Content = "Save", IsDefault = true };

		var buttons = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness( 0, 12, 0, 0 )
		};
		buttons.Children.Add( cancelBtn );
		buttons.Children.Add( saveBtn );

		var body = new StackPanel { Margin = new Thickness( 16 ) };
		body.Children.Add( hint );
		body.Children.Add( currentLabel );
		body.Children.Add( inputRow );
		body.Children.Add( error );
		body.Children.Add( buttons );

		dialog.Content = body;

		// Close behaviors
		cancelBtn.Click += ( _, _ ) =>
		{
			tcs.TrySetResult( null );
			dialog.Close();
		};

		dialog.Closing += ( _, _ ) =>
		{
			if ( !tcs.Task.IsCompleted )
				tcs.TrySetResult( null );
		};

		void ShowError( string msg )
		{
			error.Text = msg;
			error.IsVisible = true;
		}

		saveBtn.Click += ( _, _ ) =>
		{
			var raw = input.Text?.Trim();
			if ( string.IsNullOrWhiteSpace( raw ) )
			{
				ShowError( "Please enter a path." );
				return;
			}
			tcs.TrySetResult( raw );
			dialog.Close();
		};

		browseBtn.Click += async ( _, _ ) =>
		{
			try
			{
				var sp = dialog.StorageProvider;
				if ( sp is not null && sp.CanPickFolder )
				{
					var start = SboxSettings.Normalize( input.Text ) ?? input.Text;
					Uri? startUri = null;
					if ( !string.IsNullOrWhiteSpace( start ) )
					{
						try
						{
							var dir = System.IO.Directory.Exists( start ) ? start : System.IO.Path.GetDirectoryName( start );
							if ( dir is not null && System.IO.Directory.Exists( dir ) )
								startUri = new Uri( dir );
						}
						catch { }
					}

					var folders = await sp.OpenFolderPickerAsync( new FolderPickerOpenOptions
					{
						Title = "Select s&box install folder (contains game/ and engine/)",
						AllowMultiple = false,
						SuggestedStartLocation = startUri is not null ? await sp.TryGetFolderFromPathAsync( startUri ) : null
					} );

					if ( folders is { Count: > 0 } )
					{
						var picked = folders[0].Path.LocalPath;
						if ( !string.IsNullOrEmpty( picked ) )
							input.Text = picked;
					}
				}
			}
			catch { }
		};

		// Focus input when opened
		dialog.Opened += ( _, _ ) => input.Focus();

		// Show non-blocking; caller awaits tcs.
		_ = dialog.ShowDialog( owner );

		return tcs.Task;
	}
}

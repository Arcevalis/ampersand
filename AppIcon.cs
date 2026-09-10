using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Ampersand;

/// <summary>
/// The tool-belt ampersand, embedded as an Avalonia resource so every window
/// shows it in the title bar and taskbar instead of the toolkit's X11 fallback.
///
/// Loaded once and kept for the life of the app: the bitmap decodes from the
/// retained buffer, so the resource stream never needs to stay open. Null when
/// the resource is missing, so a broken icon degrades to the default rather
/// than taking the window down.
/// </summary>
internal static class AppIcon
{
	private static WindowIcon? cached;
	private static MemoryStream? retained;
	private static bool loaded;

	public static WindowIcon? Icon
	{
		get
		{
			if ( !loaded )
			{
				loaded = true;

				try
				{
					using var stream = AssetLoader.Open( new Uri( "avares://ampersand/assets/ampersand.png" ) );
					var buffer = new MemoryStream();
					stream.CopyTo( buffer );
					buffer.Position = 0;
					retained = buffer;
					cached = new WindowIcon( new Bitmap( buffer ) );
				}
				catch
				{
					retained = null;
					cached = null;
				}
			}

			return cached;
		}
	}
}

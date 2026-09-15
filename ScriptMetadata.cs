using System;
using System.IO;

namespace Ampersand;

internal enum RuntimeMode
{
	Never,
	Optional,
	Always
}

/// <summary>
/// The "# ampersand: key=value" header a launch script may carry. Unknown
/// keys are ignored so new fields never break older scripts.
/// </summary>
internal sealed class ScriptMetadata
{
	private const int HeaderLines = 20;

	public string? Name { get; private set; }
	public RuntimeMode Runtime { get; private set; } = RuntimeMode.Optional;

	public static ScriptMetadata Read( string path )
	{
		var metadata = new ScriptMetadata();

		string[] lines;
		try
		{
			lines = File.ReadAllLines( path );
		}
		catch
		{
			return metadata;
		}

		for ( var i = 0; i < lines.Length && i < HeaderLines; i++ )
		{
			var line = lines[i].Trim();

			if ( !line.StartsWith( "#", StringComparison.Ordinal ) )
				continue;

			var marker = line.IndexOf( "ampersand:", StringComparison.Ordinal );
			if ( marker < 0 )
				continue;

			var pair = line[( marker + "ampersand:".Length )..].Trim();
			var split = pair.IndexOf( '=' );
			if ( split < 0 )
				continue;

			var key = pair[..split].Trim();
			var value = pair[( split + 1 )..].Trim();

			switch ( key )
			{
				case "name":
					metadata.Name = value;
					break;

				case "runtime":
					metadata.Runtime = value switch
					{
						"never" => RuntimeMode.Never,
						"always" => RuntimeMode.Always,
						_ => RuntimeMode.Optional
					};
					break;
			}
		}

		return metadata;
	}
}

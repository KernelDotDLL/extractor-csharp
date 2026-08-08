using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Interfaces;

namespace MaNGOS.Extractor.Formats.Dbc;

/// <summary>
/// Maps a map ID to the client directory that holds its WDT/ADT files, read
/// from Map.dbc field 1 (InternalName).
///
/// The client is the only authority here. A hardcoded list gets it wrong in two
/// different ways: it is missing every instance (map 48 is "Blackfathom", not
/// "Map0048") and it is wrong even for a continent it does list (map 530 is
/// "Expansion01"; "Outland" is the display name, not the directory). Both
/// produce a WDT lookup that cannot succeed, and both silently break anything
/// else keyed on the directory name — the per-tile resolution table included.
/// </summary>
public sealed class MapDirectoryResolver
{
    private static readonly string[] MapDbcCandidates =
    {
        "DBFilesClient\\Map.dbc",
        "DBFilesClient\\map.dbc",
        "dbfilesclient\\map.dbc",
        "DBFilesClient/Map.dbc",
        "Map.dbc",
    };

    private readonly Dictionary<uint, string> _directories;

    private MapDirectoryResolver(Dictionary<uint, string> directories)
    {
        _directories = directories;
    }

    /// <summary>Number of map IDs resolved from Map.dbc (0 when it could not be read).</summary>
    public int Count => _directories.Count;

    /// <summary>
    /// Read Map.dbc from the archives. If it cannot be found or parsed the
    /// resolver still works, falling back per lookup to
    /// <see cref="WowConstants.GetMapDirectory"/>.
    /// </summary>
    public static MapDirectoryResolver Load(IArchiveReader archives, ILogger? logger = null)
    {
        var directories = new Dictionary<uint, string>();

        try
        {
            ReadOnlyMemory<byte> data = default;
            string? found = null;
            foreach (string candidate in MapDbcCandidates)
            {
                if (!archives.TryReadFile(candidate, out data)) continue;
                found = candidate;
                break;
            }

            if (found == null)
            {
                logger?.LogWarning(
                    "[Dbc] Map.dbc not found in the archives — map directories fall back to the built-in list, "
                    + "which only covers the four continents.");
                return new MapDirectoryResolver(directories);
            }

            var reader = DbcReader<MapDbcRow>.Parse(data.Span);
            foreach (var row in reader.Rows)
            {
                string dir = reader.GetString(row, 1);
                if (!string.IsNullOrEmpty(dir))
                    directories[row.Id] = dir;
            }

            logger?.LogInformation("[Dbc] Map directories: {Count} entries from {Path}", directories.Count, found);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[Dbc] Could not read Map.dbc — map directories fall back to the built-in list.");
            directories.Clear();
        }

        return new MapDirectoryResolver(directories);
    }

    /// <summary>
    /// Directory name for a map ID. Falls back to the built-in list when
    /// Map.dbc is unavailable or has no row for the ID.
    /// </summary>
    public string GetDirectory(uint mapId)
        => _directories.TryGetValue(mapId, out string? dir) ? dir : WowConstants.GetMapDirectory(mapId);
}

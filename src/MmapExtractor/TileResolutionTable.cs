using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Constants;

namespace MaNGOS.Extractor.MmapExtractor;

/// <summary>
/// Per-ADT-tile Recast resolution, expressed as the number of voxel cells along
/// one ADT side. The cell size follows directly: cs = TileSize / resolution.
///
/// The table is optional. Without it every tile is rasterized at the default
/// cell size, which is what the extractor has always done. With it, individual
/// tiles can be sharpened — dense cities and twisty WMO dungeons benefit, flat
/// terrain does not, and the cost grows as the square of the resolution.
///
/// File format, one entry per line, as produced by the reference mesher:
///     &lt;MapDirectory&gt;_&lt;tileX&gt;_&lt;tileY&gt;:&lt;resolution&gt;
/// for example
///     Azeroth_30_48:2600
/// Blank lines and lines starting with '#' or '/' are ignored. The map
/// directory may itself contain '_' or spaces, so the tile coordinates are
/// parsed from the right.
/// </summary>
public sealed class TileResolutionTable
{
    private readonly Dictionary<string, int> _resolutions;

    /// <summary>Resolution used when a tile has no entry.</summary>
    public int DefaultResolution { get; }

    public int Count => _resolutions.Count;

    private TileResolutionTable(Dictionary<string, int> resolutions, int defaultResolution)
    {
        _resolutions = resolutions;
        DefaultResolution = defaultResolution;
    }

    /// <summary>An empty table: every tile falls back to the default cell size.</summary>
    public static TileResolutionTable Empty { get; } =
        new(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), 0);

    /// <summary>
    /// Load the table from disk. A missing path, a missing file or an
    /// unreadable file all yield <see cref="Empty"/> — the resolution table is
    /// a refinement, never a prerequisite.
    /// </summary>
    public static TileResolutionTable Load(string? path, int defaultResolution, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Empty;

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int malformed = 0;

        try
        {
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == '/') continue;

                int colon = line.LastIndexOf(':');
                if (colon <= 0) { malformed++; continue; }

                if (!int.TryParse(line.AsSpan(colon + 1).Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int resolution)
                    || resolution <= 0)
                {
                    malformed++;
                    continue;
                }

                string key = line.Substring(0, colon).Trim();
                if (key.Length == 0) { malformed++; continue; }

                // Later entries win, so a hand-written override can be appended
                // to the end of the file without editing the generated body.
                map[key] = resolution;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[Mmap] Could not read the tile resolution table {Path}", path);
            return Empty;
        }

        logger?.LogInformation(
            "[Mmap] Tile resolutions: {Count} entries from {Path} ({Malformed} malformed lines skipped), " +
            "default {Default}",
            map.Count, path, malformed, defaultResolution);

        return new TileResolutionTable(map, defaultResolution);
    }

    /// <summary>
    /// Resolution for one ADT tile, or 0 when the table has no entry for it.
    /// </summary>
    public int GetResolution(string mapName, int tileX, int tileY)
    {
        if (_resolutions.Count == 0 || string.IsNullOrEmpty(mapName)) return 0;
        string key = string.Create(CultureInfo.InvariantCulture, $"{mapName}_{tileX}_{tileY}");
        return _resolutions.TryGetValue(key, out int r) ? r : 0;
    }

    /// <summary>
    /// Cell size for one ADT tile. Falls back to <paramref name="defaultCellSize"/>
    /// when the tile has no entry, and also when the entry matches the default
    /// resolution — that keeps the overwhelmingly common case bit-identical to a
    /// run without any table at all.
    /// </summary>
    public float GetCellSize(string mapName, int tileX, int tileY, float defaultCellSize)
    {
        int resolution = GetResolution(mapName, tileX, tileY);
        if (resolution <= 0 || resolution == DefaultResolution) return defaultCellSize;
        return WowConstants.TileSize / resolution;
    }
}

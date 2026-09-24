using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MaNGOS.Extractor.VmapExtractor;

/// <summary>
/// Doodad models that block the player in game but carry no collision mesh in
/// their M2, so <c>TryParseBoundingMesh</c> returns nothing and MaNGOS drops
/// the placement. For these, and only these, the extractor falls back to the
/// model header's bounding box.
///
/// Matching is a case-insensitive substring test on the M2 path, so one entry
/// covers every variant of a model family (BattleGladeShield1/2, the uppercase
/// and mixed-case spellings the ADTs use, ...).
///
/// The list is deliberately small. A blanket fallback is NOT an option: of the
/// 1588 skipped models whose header we could read, every single one had an
/// empty collision box, so the fallback would use the RENDER bounding box —
/// which includes flames, banners and foliage — on more than 1.6 million
/// placements.
/// </summary>
public static class KnownPhysicalDoodads
{
    public const string FileName = "known-physical-doodads.json";

    /// <summary>Largest box side accepted, in model units. Guards against a
    /// render bbox that swallows an effect far larger than the solid part.</summary>
    public const float MaxBoxSide = 6.0f;

    private static string[] _fragments = Array.Empty<string>();

    public static int Count => _fragments.Length;

    public static bool Matches(string m2Path)
    {
        if (_fragments.Length == 0 || string.IsNullOrEmpty(m2Path)) return false;
        foreach (string f in _fragments)
        {
            if (m2Path.Contains(f, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Loads the list from <paramref name="path"/>, or from <see cref="FileName"/>
    /// next to the executable when <paramref name="path"/> is null. Missing file
    /// is not an error: the fallback simply stays off.
    /// </summary>
    public static void Load(string? path, ILogger? logger = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, FileName);
        if (!File.Exists(path))
        {
            logger?.LogInformation("[Doodads] No {File} — header-bbox fallback disabled", FileName);
            _fragments = Array.Empty<string>();
            return;
        }

        try
        {
            var root = JsonSerializer.Deserialize<Root>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            _fragments = root?.Entries?
                .Where(e => !string.IsNullOrWhiteSpace(e.Model))
                .Select(e => e.Model!.Trim())
                .ToArray() ?? Array.Empty<string>();

            logger?.LogInformation("[Doodads] {Count} model patterns loaded from {Path}", _fragments.Length, path);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[Doodads] Could not read {Path} — fallback disabled", path);
            _fragments = Array.Empty<string>();
        }
    }

    private sealed class Root
    {
        public List<Entry>? Entries { get; set; }
    }

    private sealed class Entry
    {
        public string? Model { get; set; }
        public string? Comment { get; set; }
    }
}

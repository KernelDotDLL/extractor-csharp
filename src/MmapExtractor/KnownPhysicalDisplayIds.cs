using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MaNGOS.Extractor.MmapExtractor;

/// <summary>
/// DisplayIDs of GameObjects that physically block player movement and therefore
/// must contribute collision to the navmesh, even when their M2 has no proper
/// bounding mesh and no valid .skin mesh.
///
/// **All DisplayIds are configured externally via JSON** (see
/// <see cref="DisplayIdWhitelistFile"/> schema). The C# default set is
/// intentionally **EMPTY** — the loader MUST be called with a JSON path before
/// any GameObject spawn gets collision; if no JSON is loaded, ZERO entries are
/// honoured and the extractor falls back to its old "ignore all GameObjects"
/// behaviour (no pollution, no per-object collision).
///
/// To use this extractor, ALWAYS pass `--displayids <path>` on the CLI. A
/// sample file lives at `extractor-csharp/known-physical-displayids.json`.
/// </summary>
public static class KnownPhysicalDisplayIds
{
    /// <summary>
    /// Compile-time defaults. **Empty by design** — every DisplayId is owned
    /// by the JSON whitelist file. This intentionally wipes out the previous
    /// hardcoded list so no MagicNumber is left in the code.
    /// </summary>
    public static readonly HashSet<uint> DefaultIds = new();

    /// <summary>
    /// DisplayIds loaded from an external JSON file via
    /// <see cref="LoadFromFile"/>. The active whitelist is JSON-only.
    /// </summary>
    private static HashSet<uint> _extraIds = new();
    private static string? _extraSourcePath;
    private static bool _importAll;

    /// <summary>
    /// When true (set by JSON flag <c>importAll</c>), the whitelist is bypassed
    /// — every GameObject from the .bin receives collision. Equivalent to the
    /// legacy MaNGOS behavior.
    /// </summary>
    public static bool ImportAllEnabled => _importAll;

    /// <summary>Effective whitelist = JSON-loaded entries (defaults are empty).</summary>
    public static IReadOnlyCollection<uint> Ids => _extraIds.ToArray();

    public static bool Contains(uint displayId) => _importAll || _extraIds.Contains(displayId);

    /// <summary>
    /// Load the DisplayId whitelist from a JSON file. **Replaces** any prior
    /// extra-id set. The hardcoded <see cref="DefaultIds"/> is empty by design
    /// so the result is purely the JSON content.
    /// </summary>
    /// <param name="path">JSON file path. If null/empty/missing, the whitelist is empty.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public static void LoadFromFile(string? path, ILogger? logger = null)
    {
        _extraIds.Clear();
        _extraSourcePath = path;

        if (string.IsNullOrEmpty(path))
        {
            logger?.LogWarning("[Whitelist] No JSON override path supplied — whitelist is EMPTY (no GameObject collision). Pass --displayids <path> to enable.");
            return;
        }

        if (!File.Exists(path))
        {
            logger?.LogWarning("[Whitelist] JSON override not found at {Path} — whitelist is EMPTY.", path);
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var file = JsonSerializer.Deserialize<DisplayIdWhitelistFile>(json, options);
            if (file is null)
            {
                logger?.LogWarning("[Whitelist] JSON at {Path} deserialized to null — whitelist is EMPTY.", path);
                return;
            }

            // SINGLE FLAG: importAll bypasses the whitelist entirely.
            if (file.ImportAll)
            {
                _importAll = true;
                logger?.LogWarning(
                    "[Whitelist] JSON flag importAll=true — WHITELIST BYPASSED. Every GameObject from .bin will receive collision. This matches the legacy MaNGOS behavior and may add small obstacles (invisible spell effects, triggers, decorative props).");
                return;
            }

            _importAll = false;
            if (file.Entries == null || file.Entries.Count == 0)
            {
                logger?.LogWarning("[Whitelist] JSON at {Path} contained no entries and importAll=false — whitelist is EMPTY (no GameObject collision).", path);
                return;
            }

            int duplicates = 0;
            foreach (var entry in file.Entries)
            {
                if (entry.DisplayId == 0) continue;
                if (_extraIds.Contains(entry.DisplayId))
                {
                    duplicates++;
                    continue;
                }
                _extraIds.Add(entry.DisplayId);
            }

            logger?.LogInformation(
                "[Whitelist] Loaded {Count} DisplayIds from {Path} ({DuplicateCount} duplicates ignored). Whitelist active: {List}",
                _extraIds.Count, path, duplicates, string.Join(",", _extraIds.OrderBy(x => x)));
        }
        catch (JsonException ex)
        {
            logger?.LogError(ex, "[Whitelist] Malformed JSON at {Path} — whitelist is EMPTY.", path);
        }
    }

    /// <summary>Path of the last loaded JSON override file (null if none).</summary>
    public static string? LastSourcePath => _extraSourcePath;
}

/// <summary>JSON root schema for known-physical-displayids.json.</summary>
public sealed class DisplayIdWhitelistFile
{
    /// <summary>Free-form schema version annotation. Ignored by the loader.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    /// <summary>Human-readable comment describing the purpose of this whitelist.</summary>
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    /// <summary>
    /// SINGLE FLAG MODE: when true, every GameObject from .bin receives collision
    /// (legacy mangos behavior). When false (default), only the explicit entries
    /// below are honoured. Setting this to true is the equivalent of the user's
    /// "ONE ID that imports everything" — it makes the whitelist a no-op.
    /// </summary>
    [JsonPropertyName("importAll")]
    public bool ImportAll { get; set; }

    /// <summary>Whitelist entries. Used only when <see cref="ImportAll"/> is false.</summary>
    [JsonPropertyName("entries")]
    public List<DisplayIdWhitelistEntry> Entries { get; set; } = new();
}

/// <summary>A single DisplayId whitelist entry.</summary>
public sealed class DisplayIdWhitelistEntry
{
    /// <summary>DisplayId to whitelist. Omit to leave this entry commented-only.</summary>
    [JsonPropertyName("displayId")]
    public uint DisplayId { get; set; }

    /// <summary>Optional gameobject entry ID for documentation (resolves to displayId via DB).</summary>
    [JsonPropertyName("entry")]
    public uint? Entry { get; set; }

    /// <summary>Human-readable comment describing what this entry is.</summary>
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

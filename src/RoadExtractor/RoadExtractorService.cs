using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MaNGOS.Extractor.Core.Constants;
using MaNGOS.Extractor.Core.Interfaces;
using MaNGOS.Extractor.Core.Models;
using MaNGOS.Extractor.Formats.Wdt;

namespace MaNGOS.Extractor.RoadExtractor;

public sealed class RoadExtractorService
{
    private readonly IArchiveReader _archive;
    private readonly WdtReader _wdtReader;
    private readonly ILogger _logger;
    private readonly string _outputDir;

    private static readonly string[] RoadPatterns =
    {
        "road", "cobblestone", "path", "footprint",
        "bridgefloor", "bridge_stone"
    };

    public RoadExtractorService(
        IArchiveReader archive,
        ILoggerFactory loggerFactory,
        string outputDir)
    {
        _archive = archive;
        _logger = loggerFactory.CreateLogger<RoadExtractorService>();
        _wdtReader = new WdtReader(archive);
        _outputDir = outputDir;
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);
        _logger.LogInformation("[Road] Output directory: {OutputDir}", outputDir);
    }

    public async Task<int> ExtractMapAsync(
        uint mapId,
        string mapName,
        IProgress<TileProgressEvent>? progress,
        CancellationToken ct = default,
        int? onlyTileX = null,
        int? onlyTileY = null)
    {
        _logger.LogInformation("[Road] Starting road extraction for {MapName} (id={MapId})", mapName, mapId);

        if (!await _wdtReader.LoadAsync(mapName, ct))
        {
            _logger.LogError("[Road] Failed to load WDT for map: {MapName}", mapName);
            return 0;
        }

        var tiles = _wdtReader.GetExistingTiles();
        if (onlyTileX.HasValue && onlyTileY.HasValue)
            tiles = tiles.Where(t => t.X == onlyTileX.Value && t.Y == onlyTileY.Value).ToList();
        
        _logger.LogInformation("[Road] Found {Count} ADT tiles for {MapName}", tiles.Count, mapName);

        int successCount = 0, failCount = 0;
        int totalRoadChunks = 0, totalChunks = 0;

        foreach (var (tileX, tileY) in tiles)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY, TileStatus.Processing, ExtractionPhase.Road));

            var (success, roadChunks, chunks) = await ProcessTileAsync(mapId, mapName, tileX, tileY, ct);

            progress?.Report(new TileProgressEvent(
                (int)mapId, tileX, tileY,
                success ? TileStatus.Done : TileStatus.Failed,
                ExtractionPhase.Road));

            if (success) successCount++; else failCount++;
            totalRoadChunks += roadChunks; totalChunks += chunks;
        }

        _logger.LogInformation("[Road] Extraction complete for {MapName}: {Success} OK, {Failed} failed. " +
            "Road chunks: {RoadChunks}/{TotalChunks}",
            mapName, successCount, failCount, totalRoadChunks, totalChunks);

        return successCount;
    }

    private async Task<(bool ok, int roadChunkCount, int totalChunkCount)> ProcessTileAsync(uint mapId, string mapName, int tileX, int tileY, CancellationToken ct)
    {
        string fileName = $"{mapId:D3}{tileY:D2}{tileX:D2}.road";
        string filePath = Path.Combine(_outputDir, fileName);

        if (File.Exists(filePath))
        {
            _logger.LogDebug("[Road] Skipping ({TileX},{TileY}) — already exists", tileX, tileY);
            return (true, 0, 256);
        }

        string adtPath = $"World\\Maps\\{mapName}\\{mapName}_{tileX}_{tileY}.adt";

        if (!_archive.TryReadFile(adtPath, out var fileData))
        {
            _logger.LogWarning("[Road] Failed to read ADT ({TileX},{TileY})", tileX, tileY);
            // In C++, if ADT fails to load, no .road file is created
            return (false, 0, 256);
        }

        // 4×4 sub-zones per chunk × 256 chunks = 4096 bits = 512 bytes per ADT.
        // Each bit says whether the sub-zone (8.33 × 8.33 yards) is a road
        // (topmost opaque MCLY layer is road-textured). MmapExtruder reads
        // this and applies NAV_ROAD per sub-triangle (each sub-triangle
        // inherits the bit of its parent 2×2 sub-square).
        byte[] roadMask = new byte[512];
        bool hasRoad = ExtractRoadMask(fileData.Span, roadMask);

        int roadBits = 0;
        for (int i = 0; i < roadMask.Length; i++) roadBits += CountBits(roadMask[i]);

        if (roadBits > 0)
            _logger.LogInformation("[Road] ADT ({TileX},{TileY}): {RoadBits}/4096 road sub-zones detected",
                tileX, tileY, roadBits);


        try
        {
            await File.WriteAllBytesAsync(filePath, roadMask, ct);
            return (true, roadBits, 4096);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Road] Failed to write road file: ({TileX},{TileY})", tileX, tileY);
            return (false, roadBits, 4096);
        }
    }

    private static bool ExtractRoadMask(ReadOnlySpan<byte> data, byte[] roadMask)
    {
        Array.Clear(roadMask, 0, roadMask.Length);

        if (data.Length < 20)
            return false;

        int mtexOffset = -1;
        uint mtexSize = 0;

        List<uint> mcnkOffsets = new List<uint>();

        uint pos = 0;
        uint fileSize = (uint)data.Length;

        while (pos + 8 <= fileSize)
        {
            uint magic = BitConverter.ToUInt32(data.Slice((int)pos, 4));
            uint size = BitConverter.ToUInt32(data.Slice((int)pos + 4, 4));

            uint remaining = fileSize - pos - 8;
            if (size > remaining)
                break;

            if (magic == 0x4D544558) // 'MTEX' -> M(0x4D), T(0x54), E(0x45), X(0x58) -> Little-endian uint32: 0x5845544D.
            {
                // Wait! In C++, `magic == 0x4D544558`.
                // In C++, magic is *(uint32*). On x86, it reads the bytes as little-endian.
                // Memory: 'M' (0x4D), 'T' (0x54), 'E' (0x45), 'X' (0x58).
                // *(uint32*) = 0x5845544D. So `magic == 0x4D544558` in C++ means memory has 0x58, 0x45, 0x54, 0x4D which is "XETM".
                // Let's use the exact same comparison as C++. BitConverter.ToUInt32 will give the exact same result as *(uint32*).
            }

            // Actually, let's just check the exact same integer as the C++ code to be byte-for-byte compatible.
            if (magic == 0x4D544558)
            {
                mtexOffset = (int)(pos + 8);
                mtexSize = size;
            }

            if (magic == 0x4D434E4B) // 'MCNK' magic check in C++
            {
                mcnkOffsets.Add(pos);
            }

            pos += 8 + size;
        }

        if (mtexOffset == -1 || mtexSize == 0)
            return false;

        // Build texture name table
        List<string> texNames = new List<string>();
        int p = mtexOffset;
        int end = mtexOffset + (int)mtexSize;
        while (p < end)
        {
            int strEnd = p;
            while (strEnd < end && data[strEnd] != 0) strEnd++;
            if (strEnd > p)
            {
                string texName = System.Text.Encoding.ASCII.GetString(data.Slice(p, strEnd - p));
                texNames.Add(texName);
            }
            else
            {
                texNames.Add(string.Empty);
            }
            p = strEnd + 1;
        }

        bool anyRoad = false;
        for (int ci = 0; ci < mcnkOffsets.Count && ci < 256; ++ci)
        {
            int chunkRow = ci / 16;
            int chunkCol = ci % 16;

            uint mcnkOff = mcnkOffsets[ci];
            uint mcnkSize = BitConverter.ToUInt32(data.Slice((int)mcnkOff + 4, 4));
            uint mcnkEnd = mcnkOff + 8 + mcnkSize;

            if (mcnkEnd > fileSize)
                continue;

            // ofsLayer at offset 0x1C in SMChunk header (mcnkOff + 8 + 0x1C)
            uint ofsLayer = BitConverter.ToUInt32(data.Slice((int)mcnkOff + 8 + 0x1C, 4));
            if (ofsLayer == 0)
                continue;

            uint mclyOff = mcnkOff + ofsLayer;
            if (mclyOff + 8 > mcnkEnd)
                continue;

            uint mclyMagic = BitConverter.ToUInt32(data.Slice((int)mclyOff, 4));
            uint mclySize = BitConverter.ToUInt32(data.Slice((int)mclyOff + 4, 4));

            if (mclyMagic != 0x4D434C59) // 'MCLY' magic check in C++
                continue;

            int numLayers = (int)(mclySize / 16); // MCLYEntry size is 16
            if (numLayers <= 0)
                continue;

            // Parse all MCLY entries: (textureId, flags, ofsAlpha).
            // ofsAlpha is relative to the MCNK magic start, same convention
            // as ofsLayer above. The alpha data lives inside the MCNK's MCAL
            // sub-chunk (which is a sibling of MCLY in the MCNK sub-chunk
            // list) but we don't need to parse the MCAL chunk header — the
            // ofsAlpha offset jumps straight to this layer's alpha grid.
            uint[] layerTexIdx = new uint[numLayers];
            uint[] layerFlags  = new uint[numLayers];
            uint[] layerOfsAlpha = new uint[numLayers];
            bool[] layerIsRoad   = new bool[numLayers];
            for (int l = 0; l < numLayers; ++l)
            {
                int layerOffset = (int)(mclyOff + 8 + l * 16);
                layerTexIdx[l]   = BitConverter.ToUInt32(data.Slice(layerOffset, 4));
                layerFlags[l]    = BitConverter.ToUInt32(data.Slice(layerOffset + 4, 4));
                layerOfsAlpha[l] = BitConverter.ToUInt32(data.Slice(layerOffset + 8, 4));
                layerIsRoad[l]   = layerTexIdx[l] < texNames.Count
                                   && IsRoadTexture(texNames[(int)layerTexIdx[l]]);
            }

            // MCLY flags (WotLK MCNK::Layers): 0x100 use_alpha_map, 0x200 alpha_map_compressed
            const uint FLAG_USE_ALPHA = 0x100u;
            const uint FLAG_ALPHA_COMPRESSED = 0x200u;
            const int ALPHA_THRESHOLD = 128; // 0-255; above this = layer is "visible" at that sub-zone

            // MCAL: one 64x64 alpha map per non-base layer, reduced here to the 4x4 sub-zones.
            uint mcnkOfsAlpha  = BitConverter.ToUInt32(data.Slice((int)mcnkOff + 8 + 0x24, 4));
            uint mcnkSizeAlpha = BitConverter.ToUInt32(data.Slice((int)mcnkOff + 8 + 0x28, 4));
            long mcalData = (long)mcnkOff + mcnkOfsAlpha + 8;
            long mcalEnd  = mcalData + (mcnkSizeAlpha >= 8 ? mcnkSizeAlpha - 8 : 0);
            if (mcalEnd > data.Length) mcalEnd = data.Length;

            byte[][] layerSubZoneAlpha = new byte[numLayers][];
            for (int l = 0; l < numLayers; ++l)
            {
                if (l == 0 || (layerFlags[l] & FLAG_USE_ALPHA) == 0)
                    continue; // base layer, or no alpha map: opaque everywhere

                long start = mcalData + layerOfsAlpha[l];
                if (start < 0 || start >= mcalEnd)
                    continue;

                long avail = mcalEnd - start;
                for (int n = 0; n < numLayers; ++n)
                {
                    if (n == l || (layerFlags[n] & FLAG_USE_ALPHA) == 0) continue;
                    long other = mcalData + layerOfsAlpha[n];
                    if (other > start && other - start < avail) avail = other - start;
                }

                byte[]? full = DecodeAlphaMap(data, start, avail,
                    (layerFlags[l] & FLAG_ALPHA_COMPRESSED) != 0);
                if (full == null)
                    continue;

                byte[] sub = new byte[16];
                for (int szi = 0; szi < 16; ++szi)
                {
                    int by = (szi / 4) * 16;
                    int bx = (szi % 4) * 16;
                    int sum = 0;
                    for (int yy = 0; yy < 16; ++yy)
                        for (int xx = 0; xx < 16; ++xx)
                            sum += full[(by + yy) * 64 + (bx + xx)];
                    sub[szi] = (byte)(sum / 256);
                }
                layerSubZoneAlpha[l] = sub;
            }

            // 4×4 sub-zones per chunk. 16 sub-zones × 256 chunks = 4096 bits.
            // Each sub-zone covers a 2×2 quad of the 8×8 sub-triangle grid.
            int chunkByteBase = ci * 2; // 2 bytes per chunk × 256 chunks = 512 bytes
            for (int sz = 0; sz < 16; ++sz)
            {
                int subZoneY = sz / 4;
                int subZoneX = sz % 4;

                // Walk layers from top to bottom; the first one with effective
                // opacity > ALPHA_THRESHOLD at this sub-zone is the topmost
                // visible layer. Effective opacity:
                //   - base layer (l == 0)        : 1.0 everywhere (covers all
                //                                   by default; only opaque
                //                                   below 255 in the alpha map
                //                                   if FLAG_USE_ALPHA is set)
                //   - any layer with FLAG_USE_ALPHA: alpha[sub-zone] from MCAL
                //   - any layer without FLAG_USE_ALPHA: 1.0 everywhere
                int topmostLayer = -1;
                for (int l = numLayers - 1; l >= 0; --l)
                {
                    byte[] szAlpha = layerSubZoneAlpha[l];
                    int opacity = szAlpha != null ? szAlpha[sz] : 255;

                    if (opacity > ALPHA_THRESHOLD)
                    {
                        topmostLayer = l;
                        break;
                    }
                }

                if (topmostLayer >= 0 && layerIsRoad[topmostLayer])
                {
                    int bitIndex = sz;
                    roadMask[chunkByteBase + (bitIndex >> 3)] |= (byte)(1 << (bitIndex & 7));
                    anyRoad = true;
                }
            }
        }

        return anyRoad;
    }

    /// <summary>
    /// Decodes one MCAL alpha map into a 64x64 byte grid: RLE-compressed, 4096 raw
    /// bytes, or 2048 bytes of 4-bit pairs. Null when the source is too short.
    /// </summary>
    private static byte[]? DecodeAlphaMap(ReadOnlySpan<byte> data, long start, long avail, bool compressed)
    {
        var outBuf = new byte[4096];

        if (compressed)
        {
            long i = start;
            long end = start + avail;
            int o = 0;
            while (o < 4096 && i < end)
            {
                byte ctrl = data[(int)i++];
                int n = ctrl & 0x7F;
                if (n == 0) break;
                if ((ctrl & 0x80) != 0)
                {
                    if (i >= end) break;
                    byte v = data[(int)i++];
                    while (n-- > 0 && o < 4096) outBuf[o++] = v;
                }
                else
                {
                    while (n-- > 0 && o < 4096 && i < end) outBuf[o++] = data[(int)i++];
                }
            }
            return o == 0 ? null : outBuf;
        }

        if (avail >= 4096)
        {
            for (int k = 0; k < 4096; k++) outBuf[k] = data[(int)(start + k)];
            return outBuf;
        }

        if (avail >= 2048)
        {
            for (int k = 0; k < 2048; k++)
            {
                byte b = data[(int)(start + k)];
                outBuf[k * 2]     = (byte)((b & 0x0F) * 17);
                outBuf[k * 2 + 1] = (byte)((b >> 4) * 17);
            }
            return outBuf;
        }

        return null;
    }

    private static bool IsRoadTexture(string textureName)
    {
        if (string.IsNullOrEmpty(textureName))
            return false;

        string lower = textureName.ToLowerInvariant();
        foreach (var pattern in RoadPatterns)
        {
            if (lower.Contains(pattern))
                return true;
        }
        return false;
    }

    private static int CountBits(byte b)
    {
        // Brian Kernighan's bit count: b & (b-1) clears the lowest set bit
        // each iteration. Faster than a naive popcnt on older hardware and
        // identical result for our 512-byte road masks.
        int c = 0;
        while (b != 0) { b &= (byte)(b - 1); c++; }
        return c;
    }

    internal void ClearCache() { }
}

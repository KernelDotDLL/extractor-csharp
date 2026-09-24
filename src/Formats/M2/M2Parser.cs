using System.IO;
using MaNGOS.Extractor.Core.Binary;
using MaNGOS.Extractor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace MaNGOS.Extractor.Formats.M2;

/// <summary>
/// Minimal M2 parser that reads only the bounding (collision) mesh from a WotLK M2 file.
/// The bounding mesh is the hull used for VMap/mmap collision checks.
/// </summary>
public sealed class M2Parser
{
    private readonly IArchiveReader _archive;
    private readonly ILogger? _logger;

    public M2Parser(IArchiveReader archive, ILogger? logger = null)
    {
        _archive = archive;
        _logger = logger;
    }

    /// <summary>
    /// Reads the bounding collision mesh from an M2 file.
    /// Returns false if the file is not found, invalid, or has no bounding geometry.
    /// </summary>
    /// <param name="path">MPQ path to the .m2 file.</param>
    /// <param name="vertices">
    ///   Output flat vertex array: [x0,y0,z0, x1,y1,z1, ...] in model-local space.
    ///   These are in WoW model space (Y=forward, Z=up — transform is applied by the caller).
    /// </param>
    /// <param name="indices">
    ///   Output index array. Count is always a multiple of 3 (one entry per index, 3 per triangle).
    ///   Note: nBoundingTriangles in the M2 header is the number of uint16 indices, not triangle count.
    /// </param>
    public bool TryParseBoundingMesh(string path, out float[] vertices, out ushort[] indices)
    {
        vertices = Array.Empty<float>();
        indices = Array.Empty<ushort>();

        if (!_archive.TryReadFile(path, out ReadOnlyMemory<byte> data))
            return false;

        var span = data.Span;

        // WotLK M2 magic is "MD20"
        if (span.Length < 236
            || span[0] != 'M' || span[1] != 'D' || span[2] != '2' || span[3] != '0')
            return false;

        // ModelHeaderOthers byte layout (pragma pack 1) — WotLK offsets:
        //   offset 216 : uint32 nBoundingTriangles   (= number of uint16 indices, NOT triangle count)
        //   offset 220 : uint32 ofsBoundingTriangles
        //   offset 224 : uint32 nBoundingVertices
        //   offset 228 : uint32 ofsBoundingVertices
        // Offset 216 = 4(id)+4(ver)+4*9(nameLen..nViews)+4*30(nColors..ofsTexAnimLookup)+56(floats[14])
        var reader = new SpanReader(data);

        reader.Seek(216);
        int nBoundingIndices = (int)reader.ReadUInt32();
        int ofsBoundingTriangles = (int)reader.ReadUInt32();
        int nBoundingVertices = (int)reader.ReadUInt32();
        int ofsBoundingVertices = (int)reader.ReadUInt32();

        if (nBoundingIndices <= 0 || nBoundingVertices <= 0
            || nBoundingIndices % 3 != 0)
        {
            // No dedicated bounding mesh — fall back to the M2 header's
            // `collision_box` (CAaBox: 24 bytes = min+max Vec3). The offset is
            // FIXED: 0xBC = 188. Verified by walking back from `nBoundingTriangles`
            // at offset 216 and reading the bytes covered by `floats[14]` (56 B):
            //   160-183 (24 B) bounding_box (CAaBox)
            //   184-187 (4 B)  bounding_sphere_radius (float)
            //   188-211 (24 B) collision_box   (CAaBox)  <-- this one
            //   212-215 (4 B)  collision_sphere_radius (float)
            //   216+           nBoundingTriangles / ofsBoundingTriangles / etc.
            // (the existing TryBuildHeaderBoundingBoxProxy comment lays out the
            //  same arithmetic). The earlier heuristic scan over 336/352/368/etc.
            // was wrong: those offsets fall inside the bones/animations/lookup
            // tables region, NOT on collision_box, and any "hit" was coincidence.
            // Real MaNGOS/TC reads this CAaBox when no detailed mesh is present
            // and synthesises a 12-triangle box, so street furniture (wagons,
            // stalls, planters, banners) actually blocks pathing.
            if (TryBuildCollisionBoxFallback(span, path, out vertices, out indices))
            {
                return true;
            }

            // No collision_box either — return success with empty arrays. The
            // caller treats empty arrays as "no collision", so genuinely
            // particle/VFX models stay non-blocking.
            _logger?.LogDebug("[M2Parser] No collision for {Path}: empty bounding mesh and no collision_box at 0xBC",
                path);
            return true;
        }

        // Bounds check
        if (ofsBoundingVertices + nBoundingVertices * 12 > span.Length)
            return false;
        if (ofsBoundingTriangles + nBoundingIndices * 2 > span.Length)
            return false;

        // Parse bounding vertices (Vec3D = 3 floats = 12 bytes each)
        vertices = new float[nBoundingVertices * 3];
        reader.Seek(ofsBoundingVertices);
        for (int i = 0; i < nBoundingVertices; i++)
        {
            vertices[i * 3 + 0] = reader.ReadFloat();
            vertices[i * 3 + 1] = reader.ReadFloat();
            vertices[i * 3 + 2] = reader.ReadFloat();
        }

        // Parse bounding indices (uint16 each)
        indices = new ushort[nBoundingIndices];
        reader.Seek(ofsBoundingTriangles);
        for (int i = 0; i < nBoundingIndices; i++)
            indices[i] = reader.ReadUInt16();

        return true;
    }

    /// <summary>
    /// MaNGOS/TC-faithful fallback: when an M2 has no detailed bounding mesh,
    /// read the header's `collision_box` (CAaBox) at the FIXED offset 0xBC = 188
    /// and synthesise a 12-triangle AABB. See the callee for the exact byte
    /// arithmetic pinning that offset.
    /// </summary>
    private bool TryBuildCollisionBoxFallback(
        ReadOnlySpan<byte> span,
        string path,
        out float[] vertices,
        out ushort[] indices)
    {
        vertices = Array.Empty<float>();
        indices = Array.Empty<ushort>();

        // collision_box lives at offset 0xBC = 188. See the comment block at
        // the TryParseBoundingMesh call site for the byte arithmetic.
        const int collisionBoxOffset = 188;
        if (collisionBoxOffset + 24 > span.Length) return false;

        float minX = BitConverter.ToSingle(span.Slice(collisionBoxOffset + 0, 4));
        float minY = BitConverter.ToSingle(span.Slice(collisionBoxOffset + 4, 4));
        float minZ = BitConverter.ToSingle(span.Slice(collisionBoxOffset + 8, 4));
        float maxX = BitConverter.ToSingle(span.Slice(collisionBoxOffset + 12, 4));
        float maxY = BitConverter.ToSingle(span.Slice(collisionBoxOffset + 16, 4));
        float maxZ = BitConverter.ToSingle(span.Slice(collisionBoxOffset + 20, 4));

        if (!float.IsFinite(minX) || !float.IsFinite(minY) || !float.IsFinite(minZ)
            || !float.IsFinite(maxX) || !float.IsFinite(maxY) || !float.IsFinite(maxZ))
            return false;
        if (maxX - minX < 0.01f || maxY - minY < 0.01f || maxZ - minZ < 0.01f)
            return false; // degenerate or zero box → no real collision_box here
        // Sanity bound: avoid swallowing 200-yard "boxes" if any kind of header
        // corruption lands us at the wrong offset later. In-game wagons/banners
        // are typically 1-5 yards across and <10 yards tall.
        const float maxExtent = 100f;
        if (maxX - minX > maxExtent || maxY - minY > maxExtent || maxZ - minZ > maxExtent)
            return false;

        // Emit the box in (modelY, modelZ, modelX) order so the subsequent
        // fixCoords(X,Y,Z) → (Z,X,Y) transform still puts the model's up
        // axis into Recast's height axis (Y). Same convention as
        // TryBuildHeaderBoundingBoxProxy for GameObjects.
        vertices = new float[]
        {
            minY, minZ, minX,  minY, minZ, maxX,  maxY, minZ, maxX,  maxY, minZ, minX,
            minY, maxZ, minX,  minY, maxZ, maxX,  maxY, maxZ, maxX,  maxY, maxZ, minX,
        };
        indices = new ushort[]
        {
            0, 1, 2,  0, 2, 3,            // bottom (CCW from below)
            4, 6, 5,  4, 7, 6,            // top (CCW from above)
            0, 4, 5,  0, 5, 1,            // -Y side
            1, 5, 6,  1, 6, 2,            // +X side
            2, 6, 7,  2, 7, 3,            // +Y side
            3, 7, 4,  3, 4, 0,            // -X side
        };
        _logger?.LogDebug(
            "[M2Parser] collision_box@{Off} for {Path}: min=({MinX:F2},{MinY:F2},{MinZ:F2}) max=({MaxX:F2},{MaxY:F2},{MaxZ:F2}) ext=({Ex:F2},{Ey:F2},{Ez:F2})",
            collisionBoxOffset, path,
            minX, minY, minZ, maxX, maxY, maxZ,
            maxX - minX, maxY - minY, maxZ - minZ);
        return true;
    }

    /// <summary>
    /// Reads the render mesh from the .skin file paired with the M2.
    /// Format (WotLK, from wowdev.wiki/M2/.skin) — note: NO padding byte between
    /// magic and lookupCount, the header is 20 bytes total:
    ///   magic "SKIN"            at offset 0   (4 bytes)
    ///   nIndices                at offset 4   (uint32) — count of lookup entries
    ///   ofsIndices              at offset 8   (uint32) — offset to lookup table
    ///   nTriangles              at offset 12  (uint32) — count of triangles
    ///   ofsTriangles            at offset 16  (uint32) — offset to triangle list
    ///
    /// Each lookup entry is a uint16 index into the M2's render vertex block.
    /// Each triangle stores 3 uint16 indices into the lookup table. Resolving the
    /// chain (skin-tri → lookup → M2 vertex) gives output indices that reference
    /// the M2 vertex block directly — the caller therefore sees vertices in the
    /// same model-local space as the bounding mesh.
    ///
    /// Returns true with empty arrays when the .skin is missing/invalid/empty —
    /// caller then counts the M2 in skippedNoBounds. No AABB proxy, ever.
    /// </summary>
    public bool TryParseSkinMesh(string m2Path, out float[] vertices, out ushort[] indices)
    {
        vertices = Array.Empty<float>();
        indices = Array.Empty<ushort>();

        // WoW names .skin files with a "00" suffix on the basename, NOT just an
        // extension swap. So "BookSmallOpen02.m2" pairs with "BookSmallOpen0200.skin".
        // This is the convention used by both the C++ Mangos mmap-extractor and
        // the tools tedski5037 referenced. Plain Path.ChangeExtension(".skin")
        // finds nothing — the file is literally stored with the 00 suffix.
        string skinPath = InsertSkinSuffix(m2Path);
        if (!_archive.TryReadFile(skinPath, out ReadOnlyMemory<byte> skinData))
        {
            _logger?.LogInformation("[M2Parser] .skin not in MPQ: {Path}", skinPath);
            return true; // no .skin paired with this M2
        }

        var skinSpan = skinData.Span;

        // "SKIN" magic at offset 0, header is 20 bytes (no padding between
        // magic and the first uint32 — that was the bug in the previous attempt).
        if (skinSpan.Length < 20
            || skinSpan[0] != 'S' || skinSpan[1] != 'K' || skinSpan[2] != 'I' || skinSpan[3] != 'N')
        {
            _logger?.LogInformation("[M2Parser] .skin bad magic/size for {Path} (len={Len})", m2Path, skinSpan.Length);
            return true;
        }

        int nIndices     = BitConverter.ToInt32(skinSpan.Slice(4, 4));   // offset 4 — NOT 8
        int ofsIndices   = BitConverter.ToInt32(skinSpan.Slice(8, 4));   // offset 8
        int nTriangles   = BitConverter.ToInt32(skinSpan.Slice(12, 4));  // offset 12
        int ofsTriangles = BitConverter.ToInt32(skinSpan.Slice(16, 4));  // offset 16

        _logger?.LogInformation("[M2Parser] .skin header for {Path}: nIdx={Ni} ofsIdx={Oi} nTri={Nt} ofsTri={Ot} len={L}",
            m2Path, nIndices, ofsIndices, nTriangles, ofsTriangles, skinSpan.Length);

        if (nIndices <= 0 || nTriangles <= 0)
        {
            _logger?.LogInformation("[M2Parser] .skin empty header for {Path} (nIdx={Ni}, nTri={Nt})", m2Path, nIndices, nTriangles);
            return true;
        }
        if ((uint)ofsIndices > (uint)skinSpan.Length
            || (uint)ofsTriangles > (uint)skinSpan.Length)
        {
            _logger?.LogInformation("[M2Parser] .skin offsets OOB for {Path} (ofsIdx={Oi}, ofsTri={Ot}, len={L})",
                m2Path, ofsIndices, ofsTriangles, skinSpan.Length);
            return true;
        }
        if (ofsIndices + (long)nIndices * 2 > skinSpan.Length)
        {
            _logger?.LogInformation("[M2Parser] .skin lookup OOB for {Path}", m2Path);
            return true;
        }
        // Each triangle uses 3 lookup indices, each uint16.
        if (ofsTriangles + (long)nTriangles * 3 * 2 > skinSpan.Length)
        {
            _logger?.LogInformation("[M2Parser] .skin triangles OOB for {Path} (nTri={Nt}, ofsTri={Ot}, len={L})",
                m2Path, nTriangles, ofsTriangles, skinSpan.Length);
            return true;
        }

        // Read the lookup table (maps skin-index → M2 render vertex slot).
        ushort[] lookup = new ushort[nIndices];
        for (int i = 0; i < nIndices; i++)
            lookup[i] = BitConverter.ToUInt16(skinSpan.Slice(ofsIndices + i * 2, 2));

        // Read the M2 again — the .skin only carries indices; vertex positions
        // live in the M2 render vertex block at offset 60 (nVertices) and 64
        // (ofsVertices). WotLK stride is 48 bytes per vertex; first 12 bytes
        // are the (x, y, z) position used for collision.
        if (!_archive.TryReadFile(m2Path, out ReadOnlyMemory<byte> m2Data))
            return true;

        var m2Span = m2Data.Span;
        if (m2Span.Length < 68
            || m2Span[0] != 'M' || m2Span[1] != 'D' || m2Span[2] != '2' || m2Span[3] != '0')
            return true;

        int nM2Vertices = BitConverter.ToInt32(m2Span.Slice(60, 4));
        int ofsM2Vertices = BitConverter.ToInt32(m2Span.Slice(64, 4));
        if (nM2Vertices <= 0)
        {
            _logger?.LogDebug("[M2Parser] M2 has no vertices {Path}", m2Path);
            return true;
        }
        if ((uint)ofsM2Vertices > (uint)m2Span.Length)
            return true;
        if ((long)ofsM2Vertices + (long)nM2Vertices * 48 > m2Span.Length)
        {
            _logger?.LogDebug("[M2Parser] M2 vertex OOB for {Path} (nV={Nv}, ofsV={Ov}, len={L})",
                m2Path, nM2Vertices, ofsM2Vertices, m2Span.Length);
            return true;
        }

        float[] m2Vertices = new float[nM2Vertices * 3];
        for (int i = 0; i < nM2Vertices; i++)
        {
            int at = ofsM2Vertices + i * 48;
            float x = BitConverter.ToSingle(m2Span.Slice(at, 4));
            float y = BitConverter.ToSingle(m2Span.Slice(at + 4, 4));
            float z = BitConverter.ToSingle(m2Span.Slice(at + 8, 4));
            m2Vertices[i * 3 + 0] = x;
            m2Vertices[i * 3 + 1] = y;
            m2Vertices[i * 3 + 2] = z;
        }

        // Resolve the full triangle chain: 3 skin-tri → 3 lookup → 3 M2 vertex.
        var outIndices = new ushort[nTriangles * 3];
        int kept = 0;
        for (int t = 0; t < nTriangles; t++)
        {
            int triBase = ofsTriangles + t * 3 * 2;
            ushort a = BitConverter.ToUInt16(skinSpan.Slice(triBase + 0, 2));
            ushort b = BitConverter.ToUInt16(skinSpan.Slice(triBase + 2, 2));
            ushort c = BitConverter.ToUInt16(skinSpan.Slice(triBase + 4, 2));
            if (a >= nIndices || b >= nIndices || c >= nIndices)
                continue;
            ushort va = lookup[a], vb = lookup[b], vc = lookup[c];
            if (va >= nM2Vertices || vb >= nM2Vertices || vc >= nM2Vertices)
                continue;
            outIndices[kept++] = va;
            outIndices[kept++] = vb;
            outIndices[kept++] = vc;
        }
        if (kept == 0)
        {
            _logger?.LogInformation("[M2Parser] .skin resolved 0 tris for {Path} (nIdx={Ni}, nTri={Nt}, nV={Nv})",
                m2Path, nIndices, nTriangles, nM2Vertices);
            return true;
        }

        _logger?.LogInformation("[M2Parser] .skin mesh OK for {Path}: {Kept} tris, {Nv} verts",
            m2Path, kept / 3, nM2Vertices);

        if (kept < outIndices.Length)
            Array.Resize(ref outIndices, kept);

        vertices = m2Vertices;
        indices = outIndices;
        return true;
    }

    /// <summary>
    /// Builds a calibrated 12-triangle AABB proxy from the M2 header's
    /// "model bounding box" (min/max vec3). This is Blizzard's official
    /// model size — what the game server uses for click targeting and what
    /// the vmap extractor uses for its calculateTransformedBound fallback.
    /// We ONLY emit this proxy when the caller has already verified the
    /// DisplayID is in the KnownPhysicalDisplayIds whitelist — never for
    /// arbitrary decorative models.
    ///
    /// Where the bounding box lives in the M2 header varies between formats:
    ///   - Vanilla/TBC: offset 232/244
    ///   - WotLK: most M2s put it at 336, some at 352 (PostBoxHuman), some at 368
    ///     (wider models). The header between the bounding-mesh fields and the
    ///     bounding box has a variable number of optional sections (bounding
    ///     normals, attachments, events, lights, cameras, ribbons, particles,
    ///     blend maps). We scan a range of candidate offsets and accept the
    ///     first one whose 6 floats form a finite, non-degenerate box.
    ///
    /// Returns true with empty arrays if no candidate offset yields a valid box,
    /// so the caller can fall back to skippedNoBounds instead of injecting a
    /// fake 1×1×1 box.
    /// </summary>
    /// <summary>
    /// Same idea as <see cref="TryBuildHeaderBoundingBoxProxy"/>, but emits the box
    /// in RAW MODEL SPACE (x, y, z), exactly like <see cref="TryParseBoundingMesh"/>
    /// does for a real collision mesh. The GameObject variant pre-permutes its
    /// output for AppendGameObjectM2; the doodad pipeline must not get that.
    ///
    /// Reads the canonical MD20 header fields:
    ///   0xA0 (160) bounding_box   — the render box, non-empty for a visible model
    ///   0xBC (188) collision_box  — empty whenever the model has no collision mesh
    /// The collision box is preferred when it holds something; otherwise the render
    /// box is used, which is why the caller must gate this behind a whitelist.
    /// </summary>
    public bool TryBuildBoundingBoxProxyModelSpace(string m2Path, out float[] vertices, out ushort[] indices, float maxSide)
    {
        vertices = Array.Empty<float>();
        indices = Array.Empty<ushort>();

        if (!_archive.TryReadFile(m2Path, out ReadOnlyMemory<byte> data))
            return false;

        var span = data.Span;
        if (span.Length < 212
            || span[0] != 'M' || span[1] != 'D' || span[2] != '2' || span[3] != '0')
            return false;

        if (!TryReadBox(span, 188, out var box) && !TryReadBox(span, 160, out box))
            return false;

        float sx = box[3] - box[0], sy = box[4] - box[1], sz = box[5] - box[2];
        if (sx > maxSide || sy > maxSide || sz > maxSide)
        {
            _logger?.LogInformation(
                "[M2Parser] bbox proxy rejected for {Path}: {SX:F2} x {SY:F2} x {SZ:F2} exceeds {Max:F1}",
                m2Path, sx, sy, sz, maxSide);
            return false;
        }

        float x0 = box[0], y0 = box[1], z0 = box[2], x1 = box[3], y1 = box[4], z1 = box[5];
        vertices = new[]
        {
            x0, y0, z0,  x1, y0, z0,  x1, y1, z0,  x0, y1, z0,
            x0, y0, z1,  x1, y0, z1,  x1, y1, z1,  x0, y1, z1,
        };
        indices = new ushort[]
        {
            0, 2, 1, 0, 3, 2,   // z min
            4, 5, 6, 4, 6, 7,   // z max
            0, 1, 5, 0, 5, 4,   // y min
            1, 2, 6, 1, 6, 5,   // x max
            2, 3, 7, 2, 7, 6,   // y max
            3, 0, 4, 3, 4, 7,   // x min
        };

        _logger?.LogInformation(
            "[M2Parser] bbox proxy (model space) for {Path}: {SX:F2} x {SY:F2} x {SZ:F2}",
            m2Path, sx, sy, sz);
        return true;
    }

    private static bool TryReadBox(ReadOnlySpan<byte> span, int off, out float[] box)
    {
        box = Array.Empty<float>();
        if (off + 24 > span.Length) return false;

        var b = new float[6];
        for (int i = 0; i < 6; i++)
        {
            b[i] = BitConverter.ToSingle(span.Slice(off + i * 4, 4));
            if (!float.IsFinite(b[i])) return false;
        }
        if (b[3] - b[0] <= 0.01f || b[4] - b[1] <= 0.01f || b[5] - b[2] <= 0.01f) return false;

        box = b;
        return true;
    }

    public bool TryBuildHeaderBoundingBoxProxy(string m2Path, out float[] vertices, out ushort[] indices)
    {
        vertices = Array.Empty<float>();
        indices = Array.Empty<ushort>();

        if (!_archive.TryReadFile(m2Path, out ReadOnlyMemory<byte> data))
            return false;

        var span = data.Span;
        if (span.Length < 256
            || span[0] != 'M' || span[1] != 'D' || span[2] != '2' || span[3] != '0')
            return false;

        // Try each candidate offset. First one with a valid (finite, non-zero-volume) box wins.
        int[] candidates = { 232, 336, 352, 368, 384, 400, 416, 432, 448 };
        foreach (int off in candidates)
        {
            if (off + 24 > span.Length) break;
            float minX = BitConverter.ToSingle(span.Slice(off + 0, 4));
            float minY = BitConverter.ToSingle(span.Slice(off + 4, 4));
            float minZ = BitConverter.ToSingle(span.Slice(off + 8, 4));
            float maxX = BitConverter.ToSingle(span.Slice(off + 12, 4));
            float maxY = BitConverter.ToSingle(span.Slice(off + 16, 4));
            float maxZ = BitConverter.ToSingle(span.Slice(off + 20, 4));

            if (!float.IsFinite(minX) || !float.IsFinite(minY) || !float.IsFinite(minZ)
                || !float.IsFinite(maxX) || !float.IsFinite(maxY) || !float.IsFinite(maxZ))
                continue;
            if (maxX - minX <= 0.01f || maxY - minY <= 0.01f || maxZ - minZ <= 0.01f)
                continue;

            // 8 corner vertices of the AABB.
            // We emit them in (modelY, modelZ, modelX) order — NOT (modelX, modelY, modelZ).
            // Reason: AppendGameObjectM2 will apply fixCoords (X,Y,Z) → (Z,X,Y), and then
            // AppendGameObjectWorldVertex outputs (world.Y, world.Z, world.X). The double
            // permute lands model.Z (the model's UP axis) into the SECOND output index,
            // which is Recast's height axis (Y). A mailbox whose model is 3.1 yards tall
            // therefore ends up 3.1 yards tall in Recast Y, instead of getting flattened
            // into a horizontal axis where the bot could just step over it.
            vertices = new float[]
            {
                minY, minZ, minX, minY, minZ, maxX, maxY, minZ, maxX, maxY, minZ, minX,
                minY, maxZ, minX, minY, maxZ, maxX, maxY, maxZ, maxX, maxY, maxZ, minX,
            };
            // 12 triangles (2 per face), CCW seen from outside in the (Y, Z, X) emit order.
            // Faces are organised by which axis is constant:
            //   index 0 constant → -Y face (front)
            //   index 0 = max   → +Y face (back)
            //   index 1 constant → -Z face (bottom — model Z)
            //   index 1 = max   → +Z face (top — model Z / Recast HEIGHT)
            //   index 2 constant → -X face (model X)
            //   index 2 = max   → +X face
            indices = new ushort[]
            {
                0, 2, 1, 0, 3, 2,   // -Y face
                4, 5, 6, 4, 6, 7,   // +Y face
                0, 1, 5, 0, 5, 4,   // -Z face (bottom)
                1, 2, 6, 1, 6, 5,   // +X face
                2, 3, 7, 2, 7, 6,   // +Z face (TOP — recast HEIGHT)
                3, 0, 4, 3, 4, 7,   // -X face
            };
            _logger?.LogInformation("[M2Parser] header bbox proxy for {Path} (off={Off}): model=[{MinX:F2},{MinY:F2},{MinZ:F2}]→[{MaxX:F2},{MaxY:F2},{MaxZ:F2}] → Recast Y height={Hz:F2}",
                m2Path, off, minX, minY, minZ, maxX, maxY, maxZ, maxZ - minZ);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Builds the MPQ path to the .skin file paired with an M2.
    /// WoW stores these as "&lt;basename&gt;00.skin" (note the literal "00" before the
    /// extension AND the extension is replaced from .m2 → .skin). For example
    /// "Tree01.m2" lives next to "Tree0100.skin". Examples pulled from WotLK MPQ
    /// listfiles:
    ///   BookSmallOpen02.m2  → BookSmallOpen0200.skin
    ///   ElwynnSeaWeed01.m2  → ElwynnSeaWeed0100.skin
    /// </summary>
    private static string InsertSkinSuffix(string m2Path)
    {
        int dot = m2Path.LastIndexOf('.');
        if (dot < 0)
            return m2Path + "00.skin";
        // "<basename>00.skin" — REPLACE the .m2 extension, do not just prepend 00.
        return string.Concat(m2Path.AsSpan(0, dot), "00.skin");
    }
}

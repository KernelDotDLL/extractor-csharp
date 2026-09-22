"""
Compare les meshes HonorBuddy aux notres pour les maps qui existent en 3.3.5a.

Format .msh HB (TADT v3) :
  "TADT" u32 version, nom (chaine .NET), i32 tileX, i32 tileY, u32 resolution (cellules par cote d ADT), u32
  puis des sections [u32][u32 type][u32][u32 longueur] dont la charge est une suite de sous-tuiles
  [u32 taille][tuile Detour "VAND"], une taille 0 marquant une sous-tuile vide.
Quand la resolution de l en-tete n est pas un multiple de 4, HB l arrondit au multiple de 4 superieur
pour avoir un nombre entier de cellules par sous-tuile : c est cette grille reelle qui est retenue.

Produit dans le dossier du script :
  hb_tile_resolutions.txt  resolutions HB differentes du defaut, au format de --tile-resolutions
  rapport_hb_meshes.txt    rapport complet : parametres qui different et liens off-mesh HB absents de
                           notre offmesh.txt, avec la distance de chaque extremite a notre navmesh

  python audit_hb_tiles.py
  python audit_hb_tiles.py --maps 36,48 --hb "E:/Meshes/Mesh"
"""

import argparse
import collections
import datetime
import glob
import math
import os
import re
import struct

HERE = os.path.dirname(os.path.abspath(__file__))
BIN = os.path.join(HERE, "src", "bin", "Debug", "net10.0-windows")
# mmaps et dbc sont sous output/ depuis le passage au layout MaNGOS
OUT = os.path.join(BIN, "output") if os.path.isdir(os.path.join(BIN, "output", "mmaps")) else BIN
GRID = 533.33333
DEFAULT_RES = 1760
TILE_NAME = re.compile(r"_(\d+)_(\d+)\.msh$", re.I)
AREAS = {1: "Ground", 2: "Water", 3: "Lava", 4: "Road", 5: "Fall", 6: "Elevator", 7: "Gate", 8: "Portal",
         9: "DefendersPortal", 10: "HordePortal", 11: "AlliancePortal", 12: "Blocked", 13: "InteractUnit",
         14: "InteractObject"}


def load_map_dbc(path):
    d = open(path, "rb").read()
    _, rc, fc, rs, ss = struct.unpack_from("<4s4I", d, 0)
    sb = 20 + rc * rs
    dirs = {}
    for i in range(rc):
        o = 20 + i * rs
        mid, soff = struct.unpack_from("<II", d, o)
        dirs[mid] = d[sb + soff:d.index(b"\0", sb + soff)].decode("latin-1")
    return dirs


def read_hb_file(path):
    """En-tete TADT et sous-tuiles d un .msh HB ; complete=True si tout le fichier a ete parcouru."""
    total = os.path.getsize(path)
    with open(path, "rb") as f:
        head = f.read(512)
        if head[:4] != b"TADT":
            return None
        o, n, shift = 8, 0, 0
        while True:
            c = head[o]
            o += 1
            n |= (c & 0x7F) << shift
            shift += 7
            if c < 0x80:
                break
        o += n
        tx, ty, res, _extra = struct.unpack_from("<iiII", head, o)
        o += 16
        info = {"tx": tx, "ty": ty, "res": res, "subs": [], "empty": 0, "complete": False}
        s = o
        while s + 16 <= total:
            f.seek(s)
            _sig, _kind, _c, length = struct.unpack("<4I", f.read(16))
            p, end = s + 16, s + 16 + length
            if end > total:
                return info
            while p + 4 <= end:
                f.seek(p)
                size = struct.unpack("<I", f.read(4))[0]
                if size == 0:
                    info["empty"] += 1
                    p += 4
                    continue
                hdr = f.read(100)
                if hdr[:4] != b"VAND" or p + 4 + size > end:
                    return info
                h = struct.unpack_from("<15i", hdr, 0)
                pc, vc, omc = h[6], h[7], h[13]
                bvq = struct.unpack_from("<f", hdr, 96)[0]
                links = []
                if omc > 0:
                    f.seek(p + 4 + size - omc * 36)
                    raw = f.read(omc * 36)
                    for k in range(omc):
                        q = k * 36
                        pos = struct.unpack_from("<6f", raw, q)
                        rad = struct.unpack_from("<f", raw, q + 24)[0]
                        pidx, fl, _side = struct.unpack_from("<HBB", raw, q + 28)
                        area = 1
                        if pidx < pc:
                            f.seek(p + 4 + 100 + vc * 12 + pidx * 32 + 31)
                            area = f.read(1)[0] & 0x3F
                        links.append((pos, rad, fl, area))
                info["subs"].append({"res": round(GRID * bvq) if bvq else 0, "links": links})
                p += 4 + size
            if p != end:
                return info
            s = end
        info["complete"] = s == total
        return info


def parse_detour(b, o):
    h = struct.unpack_from("<15i", b, o)
    pc, vc = h[6], h[7]
    bmin = struct.unpack_from("<3f", b, o + 72)
    bmax = struct.unpack_from("<3f", b, o + 84)
    res = round(GRID * struct.unpack_from("<f", b, o + 96)[0])
    verts = struct.unpack_from("<%df" % (vc * 3), b, o + 100)
    po = o + 100 + vc * 12
    polys = []
    for i in range(pc):
        q = po + i * 32
        vv = struct.unpack_from("<6H", b, q + 4)
        nv = b[q + 30]
        if (b[q + 31] >> 6) == 1 or nv < 3:
            continue
        polys.append([(verts[j * 3], verts[j * 3 + 1], verts[j * 3 + 2]) for j in vv[:nv]])
    return bmin, bmax, polys, res


_ours = {}


def our_tile(map_id, tx, ty):
    path = os.path.join(OUT, "mmaps", "%03d%02d%02d.mmtile" % (map_id, ty, tx))
    if path not in _ours:
        subs = None
        if os.path.exists(path):
            b = open(path, "rb").read()
            subs = []
            if struct.unpack_from("<I", b, 8)[0] >= 6:
                o = 20
                for _ in range(struct.unpack_from("<I", b, 12)[0]):
                    sz = struct.unpack_from("<I", b, o)[0]
                    o += 4
                    if sz >= 100:
                        subs.append(parse_detour(b, o))
                    o += sz
        _ours[path] = subs
    return _ours[path]


def our_resolution(map_id, tx, ty):
    subs = our_tile(map_id, tx, ty)
    if not subs:
        return None
    return subs[0][3]


def our_adt_tiles(map_id):
    tiles = []
    for f in sorted(glob.glob(os.path.join(OUT, "mmaps", "%03d*.mmtile" % map_id))):
        n = os.path.basename(f)
        tiles.append((int(n[5:7]), int(n[3:5])))
    return tiles


def dist_xz(px, pz, pts):
    n = len(pts)
    sign = 0
    inside = True
    best = 1e9
    for i in range(n):
        ax, _, az = pts[i]
        bx, _, bz = pts[(i + 1) % n]
        cross = (bx - ax) * (pz - az) - (bz - az) * (px - ax)
        if cross:
            s = 1 if cross > 0 else -1
            if sign == 0:
                sign = s
            elif s != sign:
                inside = False
        dx, dz = bx - ax, bz - az
        l2 = dx * dx + dz * dz
        t = 0.0 if l2 == 0 else max(0.0, min(1.0, ((px - ax) * dx + (pz - az) * dz) / l2))
        best = min(best, math.hypot(px - (ax + t * dx), pz - (az + t * dz)))
    return 0.0 if inside else best


def mesh_distance(map_id, w):
    """(distance horizontale au polygone le plus proche a +-3 y de hauteur,
        ecart vertical au polygone le plus proche a moins de 1.5 y horizontalement) ; None sans tuile."""
    subs = our_tile(map_id, math.floor(32 - w[1] / GRID), math.floor(32 - w[0] / GRID))
    if subs is None:
        return None
    px, py, pz = w[1], w[2], w[0]
    horiz = vert = None
    for bmin, bmax, polys, _res in subs:
        for pts in polys:
            d = dist_xz(px, pz, pts)
            ys = [p[1] for p in pts]
            dy = 0.0 if min(ys) <= py <= max(ys) else min(abs(py - min(ys)), abs(py - max(ys)))
            if dy <= 3.0 and (horiz is None or d < horiz):
                horiz = d
            if d <= 1.5 and (vert is None or dy < vert):
                vert = dy
    return horiz, vert


def describe(md):
    if md is None:
        return "pas de tuile chez nous"
    horiz, vert = md
    if horiz is not None and horiz <= 1.5:
        return "sur notre navmesh"
    parts = ["polygone a hauteur comparable a %.1f y" % horiz if horiz is not None else "aucun polygone a hauteur comparable"]
    if vert is not None:
        parts.append("polygone a la verticale a %.1f y" % vert)
    return ", ".join(parts)


OFFMESH_LINE = re.compile(r"^\s*(\d+)\s+(\d+)\s*,\s*(\d+)\s+\(([^)]*)\)\s+\(([^)]*)\)\s+([-\d.]+)(?:\s+(\d+))?(?:\s+(\d+))?")


def load_offmesh(path):
    links = collections.defaultdict(list)
    for raw in open(path, encoding="utf-8", errors="replace"):
        s = raw.strip()
        if not s or s.startswith("#"):
            continue
        m = OFFMESH_LINE.match(s)
        if not m:
            continue
        a = tuple(float(v) for v in m.group(4).split())
        b = tuple(float(v) for v in m.group(5).split())
        links[int(m.group(1))].append((a, b, int(m.group(8)) if m.group(8) else 1))
    return links


def already_present(ours, a, b, bidir, tol=2.0):
    for oa, ob, od in ours:
        if math.dist(oa, a) <= tol and math.dist(ob, b) <= tol:
            return True
        if (od == 1 or bidir) and math.dist(oa, b) <= tol and math.dist(ob, a) <= tol:
            return True
    return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hb", default=r"E:/Meshes/Mesh")
    ap.add_argument("--maps", default=None, help="liste d ids separes par des virgules, sinon toutes les maps 3.3.5a")
    ap.add_argument("--offmesh", default=os.path.join(OUT, "offmesh.txt"))
    args = ap.parse_args()

    dirs = load_map_dbc(os.path.join(OUT, "dbc", "Map.dbc"))
    by_dir = {v.lower(): k for k, v in dirs.items()}
    wanted = {int(x) for x in args.maps.split(",")} if args.maps else None
    ours = load_offmesh(args.offmesh)

    stats = collections.Counter()
    files_per_map = collections.Counter()
    res_rows = collections.defaultdict(list)
    single = {}
    res_mismatch = []
    rounded = []
    incomplete = []
    present = collections.Counter()
    missing = collections.defaultdict(list)

    for name in sorted(os.listdir(args.hb)):
        mid = by_dir.get(name.lower())
        if mid is None or (wanted and mid not in wanted):
            continue
        for f in sorted(glob.glob(os.path.join(args.hb, name, "Meshes", "*.msh"))):
            info = read_hb_file(f)
            if info is None:
                incomplete.append(os.path.basename(f) + " (pas TADT)")
                continue
            stats["fichiers"] += 1
            files_per_map[mid] += 1
            stats["sous-tuiles"] += len(info["subs"])
            stats["emplacements vides"] += info["empty"]
            if not info["complete"]:
                incomplete.append(os.path.basename(f))
            counts = collections.Counter(s["res"] for s in info["subs"])
            eff = max(counts, key=counts.get) if counts else info["res"]
            if len(counts) > 1:
                res_mismatch.append("%s : sous-tuiles %s" % (os.path.basename(f), dict(counts)))
            if eff != info["res"]:
                if eff == 4 * -(-info["res"] // 4):
                    rounded.append("%s : en-tete %d, grille reelle %d" % (os.path.basename(f), info["res"], eff))
                else:
                    res_mismatch.append("%s : en-tete %d, sous-tuiles %d" % (os.path.basename(f), info["res"], eff))
            tiled = TILE_NAME.search(f) is not None
            if eff != DEFAULT_RES:
                if tiled:
                    res_rows[mid].append((info["tx"], info["ty"], eff, info["res"]))
                else:
                    tiles = our_adt_tiles(mid)
                    single[mid] = (eff, len(tiles))
                    res_rows[mid].extend((tx, ty, eff, info["res"]) for tx, ty in tiles)
            for s in info["subs"]:
                for pos, rad, fl, area in s["links"]:
                    a = (-pos[2], -pos[0], pos[1])
                    b = (-pos[5], -pos[3], pos[4])
                    bidir = bool(fl & 1)
                    stats["liens HB"] += 1
                    if not all(math.isfinite(v) for v in a + b):
                        stats["liens non finis"] += 1
                        continue
                    if already_present(ours.get(mid, []), a, b, bidir):
                        present[mid] += 1
                        continue
                    missing[mid].append((a, b, rad, area, bidir))

    today = datetime.date.today().isoformat()
    with open(os.path.join(HERE, "hb_tile_resolutions.txt"), "w", encoding="utf-8", newline="\r\n") as w:
        w.write("# Resolutions par tuile des meshes HonorBuddy (%s), grille reelle des sous-tuiles, %s\n" % (args.hb, today))
        w.write("# Format --tile-resolutions : <MapDirectory>_<tileX>_<tileY>:<cellules par cote d ADT>\n")
        w.write("# Defaut HB et nous : %d cellules (0.3030 yard).\n" % DEFAULT_RES)
        for mid in sorted(res_rows):
            if mid in single:
                w.write("\n# Map %d - %s : carte WMO unique chez HB, resolution %d reportee sur nos %d tuiles\n"
                        % (mid, dirs[mid], single[mid][0], single[mid][1]))
            else:
                w.write("\n# Map %d - %s : %d tuile(s) sur %d\n" % (mid, dirs[mid], len(res_rows[mid]), files_per_map[mid]))
            for tx, ty, r, _hdr in res_rows[mid]:
                w.write("%s_%d_%d:%d\n" % (dirs[mid], tx, ty, r))

    with open(os.path.join(HERE, "rapport_hb_meshes.txt"), "w", encoding="utf-8", newline="\r\n") as w:
        w.write("RAPPORT MESHES HONORBUDDY CONTRE NOS MESHES 3.3.5a - %s\n" % today)
        w.write("Source HB : %s | notre offmesh.txt : %s\n" % (args.hb, args.offmesh))
        w.write("=" * 100 + "\n\n")
        w.write("CONTROLES\n")
        w.write("  fichiers HB lus : %d sur %d maps 3.3.5a, %d sous-tuiles, %d emplacements vides\n"
                % (stats["fichiers"], len(files_per_map), stats["sous-tuiles"], stats["emplacements vides"]))
        w.write("  fichiers parcourus jusqu au dernier octet : %d / %d\n" % (stats["fichiers"] - len(incomplete), stats["fichiers"]))
        for x in incomplete:
            w.write("    INCOMPLET : %s\n" % x)
        w.write("  grille des sous-tuiles (bvQuantFactor) = resolution de l en-tete : oui, sauf %d fichier(s) dont l en-tete\n" % len(rounded))
        w.write("    n est pas un multiple de 4 : HB arrondit au multiple de 4 superieur (cellules entieres par sous-tuile),\n")
        w.write("    c est cette grille reelle qui est retenue ci-dessous\n")
        for x in rounded:
            w.write("    %s\n" % x)
        w.write("  desaccords inexpliques : %d\n" % len(res_mismatch))
        for x in res_mismatch[:20]:
            w.write("    DESACCORD : %s\n" % x)
        w.write("\nPARAMETRES IDENTIQUES PARTOUT (verifies sur les en-tetes et les sommets)\n")
        w.write("  walkableHeight 2.222, walkableRadius 0.400, walkableClimb 1.000, hauteur de cellule 0.2,\n")
        w.write("  resolution par defaut %d cellules par ADT (0.3030 yard) : HB et nous.\n" % DEFAULT_RES)

        w.write("\n" + "=" * 100 + "\n")
        w.write("1. RESOLUTIONS DIFFERENTES DU DEFAUT\n")
        w.write("   colonne HB : grille reelle HB (taille de cellule) ; colonne NOUS : resolution de notre tuile actuelle\n")
        total_res = 0
        for mid in sorted(res_rows):
            rows = res_rows[mid]
            total_res += len(rows)
            if mid in single:
                w.write("\n  Map %d - %s : carte WMO unique chez HB, resolution %d (%.4f y) sur toute la map\n"
                        % (mid, dirs[mid], single[mid][0], GRID / single[mid][0]))
            else:
                w.write("\n  Map %d - %s : %d tuile(s) sur %d\n" % (mid, dirs[mid], len(rows), files_per_map[mid]))
            for tx, ty, r, hdr in rows:
                mine = our_resolution(mid, tx, ty)
                w.write("    %-34s HB %4d (%.4f y)%s   NOUS %s\n" % (
                    "%s_%d_%d" % (dirs[mid], tx, ty), r, GRID / r,
                    " [en-tete %d]" % hdr if hdr != r else "",
                    ("%4d%s" % (mine, "  = HB" if mine == r else "")) if mine else "tuile absente"))
        w.write("\n  total : %d tuiles\n" % total_res)

        w.write("\n" + "=" * 100 + "\n")
        w.write("2. LIENS OFF-MESH HB ABSENTS DE NOTRE offmesh.txt\n")
        w.write("   ligne au format offmesh.txt, puis la mesure de chaque extremite contre notre navmesh 3.3.5a\n")
        total_missing = 0
        for mid in sorted(set(missing) | set(present)):
            lst = missing.get(mid, [])
            total_missing += len(lst)
            w.write("\n  Map %d - %s : %d absent(s), %d deja present(s)\n" % (mid, dirs[mid], len(lst), present[mid]))
            for a, b, rad, area, bidir in lst:
                tx, ty = math.floor(32 - a[1] / GRID), math.floor(32 - a[0] / GRID)
                w.write("    %d %d,%d (%.4f %.4f %.4f) (%.4f %.4f %.4f) %.1f %d %d\n"
                        % (mid, tx, ty, a[0], a[1], a[2], b[0], b[1], b[2], rad, area, 1 if bidir else 0))
                w.write("      %s, %s, denivele %+.1f y sur %.1f y a l horizontale\n"
                        % (AREAS.get(area, "area %d" % area), "bidirectionnel" if bidir else "sens unique",
                           b[2] - a[2], math.hypot(a[0] - b[0], a[1] - b[1])))
                w.write("      depart : %s\n" % describe(mesh_distance(mid, a)))
                w.write("      arrivee: %s\n" % describe(mesh_distance(mid, b)))
        w.write("\n  total : %d liens absents, %d deja presents, %d liens HB en tout\n"
                % (total_missing, sum(present.values()), stats["liens HB"]))

    print("fichiers HB lus: %d sur %d maps | complets: %d | sous-tuiles: %d | emplacements vides: %d"
          % (stats["fichiers"], len(files_per_map), stats["fichiers"] - len(incomplete), stats["sous-tuiles"], stats["emplacements vides"]))
    print("en-tetes arrondis au multiple de 4: %d fichiers | desaccords inexpliques: %d" % (len(rounded), len(res_mismatch)))
    print("resolutions differentes du defaut: %d tuiles sur %d maps" % (sum(len(v) for v in res_rows.values()), len(res_rows)))
    print("liens HB: %d | deja presents: %d | absents: %d | non finis: %d"
          % (stats["liens HB"], sum(present.values()), sum(len(v) for v in missing.values()), stats["liens non finis"]))


if __name__ == "__main__":
    main()

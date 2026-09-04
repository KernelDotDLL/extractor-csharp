"""
Verifie qu'une DisplayId whitelistee produit bien un trou dans la navmesh.

Pour chaque spawn de l'objet on sonde le point central plus un anneau de 8 sondes
a 4 yards. Si l'anneau est marchable a moins de 6/8 le cas est ecarte (interieur,
falaise, bord d'eau). Sinon, centre non couvert = collision presente.

  python verify_go_collision.py --map 1
  python verify_go_collision.py --map 1 --ids 714,7735 --mmaps "C:/.../mmaps"
"""

import argparse
import collections
import json
import math
import os
import struct
import subprocess
import sys

GRID = 533.33333
HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_MMAPS = os.path.join(HERE, "src", "bin", "Debug", "net10.0-windows", "mmaps")
MYSQL = r"C:/Users/Texy6/Desktop/Naaru Blizzlike Repack 3.3.5a v2025.07/mysql/bin/mysql.exe"

HEADER_FIELDS = ("version x y layer userId polyCount vertCount maxLinkCount "
                 "detailMeshCount detailVertCount detailTriCount bvNodeCount "
                 "offMeshConCount offMeshBase walkableHeight walkableRadius "
                 "walkableClimb bminx bminy bminz bmaxx bmaxy bmaxz bvQuant").split()


class Mmaps(object):
    """Lecture des .mmtile V6 (16 sous-tuiles Detour par ADT)."""

    def __init__(self, directory, map_id):
        self.dir = directory
        self.map_id = map_id
        self._tiles = {}
        self._geom = {}

    def _tile(self, wx, wy):
        tx = int(math.floor(32 - wy / GRID))
        ty = int(math.floor(32 - wx / GRID))
        key = (tx, ty)
        if key not in self._tiles:
            path = os.path.join(self.dir, "%03d%02d%02d.mmtile" % (self.map_id, ty, tx))
            if not os.path.exists(path):
                self._tiles[key] = None
            else:
                buf = open(path, "rb").read()
                count = struct.unpack_from("<I", buf, 12)[0]
                off = 20
                subs = []
                for i in range(count):
                    size = struct.unpack_from("<I", buf, off)[0]
                    off += 4
                    if size == 0:
                        continue
                    head = struct.unpack_from("<14i3f6ff", buf, off + 4)
                    sub = dict(zip(HEADER_FIELDS, head))
                    sub["off"] = off
                    sub["sub"] = i
                    subs.append(sub)
                    off += size
                self._tiles[key] = (buf, subs)
        return self._tiles[key]

    def _polys(self, buf, sub):
        key = (id(buf), sub["sub"])
        if key not in self._geom:
            base = sub["off"]
            nv = sub["vertCount"]
            verts = struct.unpack_from("<%df" % (nv * 3), buf, base + 100)
            poly_off = base + 100 + ((12 * nv + 3) & ~3)
            polys = []
            for i in range(sub["polyCount"]):
                o = poly_off + 32 * i
                idx = struct.unpack_from("<6H", buf, o + 4)
                vc = buf[o + 30]
                at = buf[o + 31]
                polys.append((idx[:vc], at >> 6))
            self._geom[key] = (verts, polys)
        return self._geom[key]

    def covered(self, wx, wy, wz, vtol=6.0):
        tile = self._tile(wx, wy)
        if tile is None:
            return False
        buf, subs = tile
        dx, dy, dz = wy, wz, wx           # Detour x = WoW Y, y = hauteur, z = WoW X
        for sub in subs:
            if not (sub["bminx"] <= dx <= sub["bmaxx"] and sub["bminz"] <= dz <= sub["bmaxz"]):
                continue
            verts, polys = self._polys(buf, sub)
            for idx, ptype in polys:
                if ptype != 0:            # ignore les polys de lien off-mesh
                    continue
                inside = False
                heights = []
                n = len(idx)
                j = n - 1
                for i in range(n):
                    xi, yi, zi = verts[idx[i] * 3], verts[idx[i] * 3 + 1], verts[idx[i] * 3 + 2]
                    xj, zj = verts[idx[j] * 3], verts[idx[j] * 3 + 2]
                    heights.append(yi)
                    if ((zi > dz) != (zj > dz)) and (dx < (xj - xi) * (dz - zi) / (zj - zi + 1e-12) + xi):
                        inside = not inside
                    j = i
                if inside and abs(sum(heights) / len(heights) - dy) <= vtol:
                    return True
            return False
        return False

    def probe(self, wx, wy, wz):
        center = self.covered(wx, wy, wz)
        ring = sum(self.covered(wx + 4 * math.cos(a), wy + 4 * math.sin(a), wz)
                   for a in [i * math.pi / 4 for i in range(8)])
        return center, ring


def whitelist_ids(path):
    doc = json.load(open(path, encoding="utf-8"))
    return [e["displayId"] for e in doc.get("entries", []) if e.get("displayId")]


def spawns(map_id, ids):
    csv = ",".join(str(i) for i in ids)
    sql = ("SELECT gt.displayId, g.position_x, g.position_y, g.position_z "
           "FROM world.gameobject g JOIN world.gameobject_template gt ON gt.entry = g.id "
           "WHERE g.map = %d AND gt.displayId IN (%s)" % (map_id, csv))
    out = subprocess.run([MYSQL, "-u", "root", "-N", "-B", "-e", sql],
                         capture_output=True, text=True, timeout=600)
    if out.returncode != 0:
        sys.exit("MySQL: " + out.stderr.strip())
    rows = collections.defaultdict(list)
    for line in out.stdout.splitlines():
        p = line.split("\t")
        if len(p) == 4:
            rows[int(p[0])].append((float(p[1]), float(p[2]), float(p[3])))
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--map", type=int, required=True)
    ap.add_argument("--mmaps", default=DEFAULT_MMAPS)
    ap.add_argument("--displayids", default=os.path.join(HERE, "known-physical-displayids.json"))
    ap.add_argument("--ids", default=None, help="liste separee par des virgules, sinon toute la whitelist")
    ap.add_argument("--max", type=int, default=40, help="spawns sondes par DisplayId")
    args = ap.parse_args()

    ids = [int(x) for x in args.ids.split(",")] if args.ids else whitelist_ids(args.displayids)
    print("mmaps      : %s" % args.mmaps)
    print("whitelist  : %s (%d DisplayIds)" % (args.displayids, len(ids)))
    print("map        : %d" % args.map)
    print()

    mesh = Mmaps(args.mmaps, args.map)
    rows = spawns(args.map, ids)
    print("%8s %8s %8s %8s %8s   %s" % ("dispId", "spawns", "retenus", "trous", "taux", "verdict"))
    total = [0, 0]
    for did in sorted(ids):
        pts = rows.get(did, [])
        kept = holes = 0
        for (x, y, z) in pts[:args.max]:
            center, ring = mesh.probe(x, y, z)
            if ring < 6:
                continue
            kept += 1
            if not center:
                holes += 1
        if not pts:
            verdict = "absent de cette map"
        elif kept == 0:
            verdict = "non concluant (entourage non marchable)"
        else:
            rate = 100.0 * holes / kept
            verdict = "collision presente" if rate >= 60 else (
                "partielle" if rate >= 20 else "AUCUNE COLLISION")
            total[0] += holes
            total[1] += kept
        rate_txt = "%.0f%%" % (100.0 * holes / kept) if kept else "-"
        print("%8d %8d %8d %8d %8s   %s" % (did, len(pts), kept, holes, rate_txt, verdict))
    if total[1]:
        print()
        print("global : %d trous / %d cas retenus (%.0f%%)" % (total[0], total[1], 100.0 * total[0] / total[1]))


if __name__ == "__main__":
    main()

"""
Regenerate gameobject_spawns.bin from the Naaru MySQL repack DB.

Format (GPSW, 40 bytes per entry, big-endian? no, little-endian per the C# struct):
  uint32 magic   = 0x47505753 'GPSW'
  uint32 version = 1
  uint32 count
  for each entry (40 bytes):
    uint32 mapId
    uint32 displayId               (resolved from gameobject_template.DisplayId)
    float  posX   (world X = east-west)
    float  posY   (world Y = north-south)
    float  posZ   (world Z = height)
    float  rot0   (quaternion X)
    float  rot1   (quaternion Y)
    float  rot2   (quaternion Z)
    float  rot3   (quaternion W)
    float  scale

The .bin stores WORLD coordinates directly. The C# mmap pipeline reads them
as-is (posX = spawn.PosX) and applies the standard C++ transform (flip X/Y,
output (Y, Z, X) for Recast).
"""

import argparse
import struct
import subprocess
import sys
from pathlib import Path

MYSQL_BIN = r"C:\Users\Texy6\Desktop\Naaru Blizzlike Repack 3.3.5a v2025.07\mysql\bin\mysql.exe"
BIN_PATH = Path(r"C:\Users\Texy6\Desktop\newhcb\Navigation View3D\Extractor_projects-master\extractor-csharp\gameobject_spawns.bin")

# MoP 5.4.8 repack: the schema is the same, only the server and the database differ.
#   --mysql-bin "...\MOPPREMIUM\Database\_Server\mysql\bin\mysql.exe"
#   --user root --password ascent --db world --out gameobject_spawns.mop.bin
DB_NAME = "world"
DB_USER = "root"
DB_PASSWORD = ""

GPSW_MAGIC = 0x47505753
GPSW_VERSION = 1
ENTRY_SIZE = 40

# Identity quaternion for gameobjects whose rotation is 0,0,0,0 in the DB
# (rotation0..3 = 0 means identity rotation when normalised).
IDENTITY_QUAT = (0.0, 0.0, 0.0, 1.0)

# No type filter here on purpose. Every gameobject row that has a template goes
# into the .bin; known-physical-displayids.json is the only thing that decides
# which DisplayIds actually get collision. Filtering by gameobject_template.type
# used to drop the whole SPELL_FOCUS family (braziers, bonfires, forges, anvils)
# before the whitelist ever saw it, which forced a per-entry force-include list.


def mysql_query(sql: str) -> list[tuple]:
    cmd = [MYSQL_BIN, "-u", DB_USER]
    if DB_PASSWORD:
        cmd.append("-p" + DB_PASSWORD)
    cmd += ["-N", "-B", "-e", sql]
    result = subprocess.run(cmd, capture_output=True, text=True, timeout=300)
    if result.returncode != 0:
        raise RuntimeError(f"MySQL error: {result.stderr.strip()}")
    return [tuple(line.split("\t")) for line in result.stdout.strip().splitlines() if line]


def main() -> None:
    print("=== Querying gameobject spawns ===")
    # Fetch every gameobject row joined with its template's DisplayId.
    # We normalise the quaternion to a unit identity when all 4 components
    # are 0 (MaNGOS often leaves rotation0..3 at 0 and stores a single
    # 'orientation' float instead). For gameobjects that already have a
    # proper quaternion, we use it as-is.
    rows = mysql_query(
        "SELECT g.id, g.map, g.position_x, g.position_y, g.position_z, "
        "       g.rotation0, g.rotation1, g.rotation2, g.rotation3, "
        "       COALESCE(gt.DisplayId, 0), COALESCE(NULLIF(gt.size, 0), 1) "
        f"FROM {DB_NAME}.gameobject g "
        f"LEFT JOIN {DB_NAME}.gameobject_template gt ON gt.entry = g.id"
    )
    print(f"  Fetched {len(rows)} spawn rows")

    # Scale comes from gameobject_template.size, which is what the client applies
    # to the model. Writing 1.0 for everyone left every scaled object with a
    # collision hole the wrong size: the Orgrimmar shop signs are all size 1.33,
    # so the navmesh hole was a third too small and a mounted bot clipped them.
    records = []
    skipped = 0
    for r in rows:
        try:
            entry_id = int(r[0])
            map_id = int(r[1])
            pos_x = float(r[2])
            pos_y = float(r[3])
            pos_z = float(r[4])
            rot0 = float(r[5])
            rot1 = float(r[6])
            rot2 = float(r[7])
            rot3 = float(r[8])
            display_id = int(r[9])
            scale = float(r[10])
            if display_id == 0:
                # Template not found in gameobject_template. Skip — there is no
                # model to rasterise anyway.
                skipped += 1
                continue
        except (ValueError, IndexError) as e:
            print(f"  Skipping malformed row: {r} ({e})")
            skipped += 1
            continue

        # Normalise a zero quaternion to identity. MaNGOS frequently leaves
        # rotation0..3 at 0 for gameobjects whose orientation is given by the
        # single orientation field. The mmap loader expects a unit quaternion.
        if rot0 == 0.0 and rot1 == 0.0 and rot2 == 0.0 and rot3 == 0.0:
            rot0, rot1, rot2, rot3 = IDENTITY_QUAT
        else:
            # Normalise to unit length.
            length_sq = rot0 * rot0 + rot1 * rot1 + rot2 * rot2 + rot3 * rot3
            if length_sq > 0.0:
                inv = 1.0 / (length_sq ** 0.5)
                rot0 *= inv
                rot1 *= inv
                rot2 *= inv
                rot3 *= inv

        records.append((
            map_id,
            display_id,
            pos_x, pos_y, pos_z,
            rot0, rot1, rot2, rot3,
            scale,
        ))

    print(f"  {len(records)} valid records ({skipped} skipped without template)")

    # Write the .bin file.
    BIN_PATH.parent.mkdir(parents=True, exist_ok=True)
    with BIN_PATH.open("wb") as f:
        f.write(struct.pack("<III", GPSW_MAGIC, GPSW_VERSION, len(records)))
        for rec in records:
            f.write(struct.pack("<IIffffffff", *rec))

    size = BIN_PATH.stat().st_size
    expected = 12 + ENTRY_SIZE * len(records)
    print(f"\n=== Wrote {BIN_PATH} ({size:,} bytes) ===")
    print(f"  Expected: {expected:,} bytes (12-byte header + {ENTRY_SIZE} bytes × {len(records)} records)")
    assert size == expected, f"Size mismatch: {size} != {expected}"


def parse_args() -> None:
    global MYSQL_BIN, BIN_PATH, DB_NAME, DB_USER, DB_PASSWORD
    ap = argparse.ArgumentParser(description="Write gameobject_spawns.bin from a server world database")
    ap.add_argument("--mysql-bin", default=MYSQL_BIN)
    ap.add_argument("--db", default=DB_NAME)
    ap.add_argument("--user", default=DB_USER)
    ap.add_argument("--password", default=DB_PASSWORD)
    ap.add_argument("--out", default=str(BIN_PATH))
    a = ap.parse_args()
    MYSQL_BIN, DB_NAME, DB_USER, DB_PASSWORD = a.mysql_bin, a.db, a.user, a.password
    BIN_PATH = Path(a.out)


if __name__ == "__main__":
    try:
        parse_args()
        main()
    except Exception as e:
        print(f"ERROR: {e}", file=sys.stderr)
        sys.exit(1)

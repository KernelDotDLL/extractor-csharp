# GameObject Whitelist Guide for Mmap Extraction

This guide explains how to add physical collision for specific GameObjects (like mailboxes, carts, and event-specific trees) to your Navmesh, while ignoring invisible triggers, traps, and decorative clutter.

## The Problem
By default, the WoW database (`gameobject_spawns`) contains thousands of invisible traps, spell effects, and tiny decorations that have 3D collision boxes in the client. If extracted unconditionally, they create unwanted obstacles (small circles/hexagons) on the Navmesh.

## The Solution
The C# Extractor uses a **Strict Whitelist** approach. It will **ONLY** generate collision for GameObjects whose `DisplayId` is explicitly listed in the JSON whitelist:

`known-physical-displayids.json`

The whitelist is **JSON-only**. The `DefaultIds` set in `src/MmapExtractor/KnownPhysicalDisplayIds.cs` is deliberately **empty** — there are no hardcoded IDs left in the code. If no JSON is found, zero GameObjects get collision and the extractor behaves as if GameObject extraction were off.

> Older versions of this guide told you to edit `KnownPhysicalDisplayIds.cs` and recompile. That is no longer true — editing the JSON is enough, and **no rebuild is required**.

### Where the JSON is looked up
First match wins:

1. The path given to `--displayids <path>`
2. `known-physical-displayids.json` next to `MaNGOS.Extractor.exe`
3. `known-physical-displayids.json` in the current working directory
4. Walking up from the current directory until the repo root (`src/MaNGOS.Extractor.csproj`) is reached

So in practice you can just drop the file next to the exe and never pass `--displayids` at all.

---

## How to Add a Missing Object (Workflow)

If you notice an object is missing collision (e.g., a tree in Stormwind that is actually a GameObject), follow these steps:

### 1. Find the Object In-Game
Log into the game with a GM (Game Master) account and stand right next to the missing object.

### 2. Useful GM Commands
Use the following commands in the game chat to identify the object:

- **`.gobject near`** (or `.go near`)
  Lists all GameObjects near you. This is the most useful command. It will give you the **Entry ID** and the **GUID**.
- **`.gobject target`**
  Selects the nearest GameObject.
- **`.gobject info`**
  Shows detailed information about the currently targeted GameObject.
- **`.lookup object <name>`**
  Example: `.lookup object Mailbox` - Searches the database for all objects matching that name and returns their Entry IDs.
- **`.go object <guid>`**
  Teleports your character directly to the GameObject with the specified GUID (useful if you are checking coordinates).

**If `.gobject near` finds nothing, stop here.** The object is not a GameObject and the whitelist cannot help — see the warning at the bottom of this guide.

### 3. Get the Display ID
Once you have the **Entry ID** from the commands above, you need to find its **Display ID**:
1. Open your MySQL Database tool (e.g., HeidiSQL, Navicat).
2. Open the `world` database.
3. Open the `gameobject_template` table.
4. Search for your Entry ID in the `entry` column.
5. Look at the **`displayId`** column and note the number.

### 4. Update the Whitelist
Open `known-physical-displayids.json` and add an entry to the `entries` array:

```json
{
  "$schema": "v1",
  "importAll": false,
  "entries": [
    { "displayId": 1907, "comment": "Mailbox (PostBoxHuman)" },
    { "displayId": 1234, "entry": 5678, "comment": "Orgrimmar lamp post" }
  ]
}
```

Per-entry fields:

| Field | Required | Meaning |
| --- | --- | --- |
| `displayId` | yes | The DisplayId to whitelist. An entry with `displayId: 0` (or missing) is skipped, so you can leave documentation-only entries in the file. |
| `entry` | no | The GameObject entry ID. Documentation only — the extractor never reads it for matching. |
| `comment` | no | Free text, for you. |

The loader tolerates `//` comments and trailing commas, and it ignores duplicate `displayId` values (it logs how many it dropped).

Root-level `importAll` is the escape hatch: set it to `true` and **every** GameObject in the `.bin` gets collision, exactly like the legacy MaNGOS C++ extractor. The `entries` array is then ignored entirely. Useful to check whether a missing object is a GameObject at all, but expect the Navmesh pollution the strict whitelist exists to avoid.

### 5. Re-run the Extractor
No rebuild. Just run the extractor again for the tile or map you are fixing:

```powershell
MaNGOS.Extractor.exe --wow <client path> --out <output path> --maps 1 --tile 32,27
```

Confirm the whitelist actually loaded by looking for these lines in the log:

```
[Whitelist] Auto-discovered JSON next to exe: ...\known-physical-displayids.json
[Whitelist] Loaded 8 DisplayIds from ... (0 duplicates ignored). Whitelist active: 1907,1947,...
```

If instead you see `whitelist is EMPTY`, the JSON was not found, was malformed, or had no entries — nothing will get collision until that is fixed. A malformed JSON does **not** stop the run, it just silently produces an empty whitelist, so always check the log.

If you did rebuild the extractor for some other reason:

```powershell
dotnet build src\MaNGOS.Extractor.csproj -c Release
```

Make sure `known-physical-displayids.json` is next to the freshly built exe, or pass `--displayids` explicitly.

---

## ⚠️ CRITICAL WARNING: GameObjects vs. ADT Map Objects
Not everything you see in the world is a GameObject!
Most of the terrain, buildings, rocks, and normal trees are **ADT Doodads** or **WMOs** (World Map Objects).

If you stand next to a tree and type `.gobject near` and the game says **"No gameobjects found"**, it means that object is part of the base map. **The whitelist will do nothing for it.** You only whitelist dynamic objects spawned by the SQL database (chests, event trees, doors, mailboxes).

A base-map object still missing from the Navmesh is a different bug, in a different place:

- **M2 doodads** (lamp posts, signs, wagons, crates, planters) go through `src/Formats/M2/M2Parser.cs`. Many WotLK M2s have no detailed `boundingTriangles` collision mesh, only a `collision_box` AABB at header offset 188 (0xBC); the parser falls back to that AABB. If an M2 is still missing, that fallback is what to investigate.
- **WMO doodads** (the props placed inside buildings) go through `src/Formats/Wmo/Parsing/WmoParser.cs` — MODN / MODS / MODD, with the 0-size MOVV separator that WotLK root files put before them.

Whitelisting a DisplayId in those two cases changes nothing, because the object never reaches the GameObject code path in the first place.

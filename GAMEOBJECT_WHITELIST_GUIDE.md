# GameObject Whitelist Guide for Mmap Extraction

This guide explains how to add physical collision for specific GameObjects (like mailboxes, carts, and event-specific trees) to your Navmesh, while ignoring invisible triggers, traps, and decorative clutter.

## The Problem
By default, the WoW database (`gameobject_spawns`) contains thousands of invisible traps, spell effects, and tiny decorations that have 3D collision boxes in the client. If extracted unconditionally, they create unwanted obstacles (small circles/hexagons) on the Navmesh.

## The Solution
The C# Extractor uses a **Strict Whitelist** approach. It will **ONLY** generate collision for GameObjects whose `DisplayId` is explicitly listed in:
`src/MmapExtractor/KnownPhysicalDisplayIds.cs`

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

### 3. Get the Display ID
Once you have the **Entry ID** from the commands above, you need to find its **Display ID**:
1. Open your MySQL Database tool (e.g., HeidiSQL, Navicat).
2. Open the `world` database.
3. Open the `gameobject_template` table.
4. Search for your Entry ID in the `entry` column.
5. Look at the **`displayId`** column and note the number.

### 4. Update the Whitelist
1. Open `src/MmapExtractor/KnownPhysicalDisplayIds.cs` in a text editor.
2. Add the `displayId` you found to the `Ids` list.
   ```csharp
   public static readonly HashSet<uint> Ids = new()
   {
       1907, // Mailbox
       // Add your new ID below:
       1234, // Example: Stormwind Tree
   };
   ```

### 5. Recompile and Extract
1. Open your terminal in the `extractor-csharp` folder.
2. Run: `dotnet build src\MaNGOS.Extractor.csproj -c Release`
3. Run the extractor again to generate the updated `.mmap` tile!

---

## ⚠️ CRITICAL WARNING: GameObjects vs. ADT Map Objects
Not everything you see in the world is a GameObject!
Most of the terrain, buildings, rocks, and normal trees are **ADT Doodads** or **WMOs** (World Map Objects). 

If you stand next to a tree and type `.gobject near` and the game says **"No gameobjects found"**, it means that tree is part of the base map. **You do NOT need to whitelist it.** The VMAP/MMAP system already handles map objects automatically. 
You only need to whitelist dynamic objects spawned by the SQL database (chests, event trees, doors, mailboxes).

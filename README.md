# Apocapocket 2.0.0

Six item and weapon slots for Apocalypter (Unity 2020.3, BepInEx 5, PlayMaker).

## Controls and slots

- Hold an item or weapon and press an empty slot's key to store it. Press an occupied item slot to take its item into your hand at its recorded pose.
- Selecting a weapon draws it; pressing its key again holsters it. Empty slots can be selected with bare hands.
- Switching while holding an item returns it to its origin, another free slot, or the world if all slots are occupied. **Weapon off** uses the same return rule.
- **Use / F** picks up weapons into the first empty logical slot in order 1–6. Items occupy slots too. A weapon picked up into the selected empty slot is drawn automatically.
- Small / medium / large / huge backpacks unlock 1 / 2 / 3 / 3 extra slots. Removing or downsizing a backpack ejects overflow after 1.5 seconds of stable gameplay.
- The original slot card shows **1–4**, **1–5**, then **1–6**, keeping numerical order and its right edge fixed beside the ammo/values panel. Added width extends left and moves the slot row and grenade/drop/unequip hints left. Its middle texture repeats at the original scale and its end caps stay intact; locked slot widgets are hidden.
- Slots 4–6 draw weapons through the game's three native weapon slots. Their logical contents remain unchanged during borrowing; the displaced native content is parked separately.
- Vehicle third-person and menus gate new input. Existing transactions keep advancing and have explicit rollback. Slot 4–6 keys are ignored in vehicles.
- The Controls screen labels the keys **Item 1–6**. Extra key bindings persist in `com.denis.apocalypter.apocapocket.keys.cfg`.

Merchant stock, PartAdjusterTools, crates and objects containing other saveable items cannot be pocketed.

## Saves and icons

Inventory mappings and hand poses are stored under **Apocapocket.Slots** inside the game's `.es3` save. During serialization, ordinary items and extra-slot weapons are visible, physical world items at the player's drop point. Native-slot weapons retain their vanilla representation. Inventory is restored after serialization. Loading the same save with the plugin disabled leaves the extra items in the world.

The mapping is written to the ES3 cache immediately before `NewGO_ArrayList` executes its `StoreCachedFile` actions, then written and verified in both cache and file after the save. A saved vanilla Timeline value detects stale mapping keys retained by vanilla re-saves. Legacy 1.x holder reference IDs remain registered in this migration release only; new item saves do not use those parents.

Weapons and items with native `imageUI` textures use those textures. Other icons are queued one per gameplay frame and cached in `BepInEx/cache/Apocapocket/icons`. Deleting this cache only regenerates icons. There are no external inventory save files.

Optional Apocasaver integration shares item hand poses. Hand-carried items during a save remain under vanilla/Apocasaver handling.

## Installation and configuration

Install `Apocapocket.dll` in `BepInEx/plugins/Apocapocket`. Keep only one copy of the DLL. Restart the game after replacing it.

The plugin GUID remains `com.denis.apocalypter.apocapocket`.

| Setting | Default | Behavior |
| --- | --- | --- |
| `[General] Enabled` | true | Disable to eject ordinary pocketed items and extra-slot weapons, hide extra UI and restore vanilla input. Native-slot weapons stay. |
| `[General] RequireBackpack` | true | Disable to unlock all six slots. |
| `[Debug] VerboseLog` | false | Enable transaction phases, model snapshots, mapping JSON and icon-cache messages. |

The existing Apocasetter menu integration and old configuration migration are retained.

## Build and verify

Windows, using the installed Unity Mono compiler and the game's own managed assemblies:

```powershell
./build.ps1
./verify.ps1
./build.ps1 -Deploy
```

Override `-GameDir` or `-MonoDir` when needed. Builds use **mcs, C# 7, -nostdlib**. `build.sh` provides the equivalent build for a shell with `mcs` on PATH.

`verify.ps1` executes the production inventory, transaction and persistence code through headless Unity/PlayMaker/ES3 adapters, then applies every Harmony patch against the actual game assemblies and checks plugin metadata. See [VALIDATION.md](VALIDATION.md) for coverage and the outstanding game acceptance checks.

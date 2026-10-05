# Apocapocket 2.0.0

Six logical slots now hold items and weapons, with explicit inventory transactions and rollback when a game operation times out.

- Store and retrieve ordinary items with their recorded hand pose. Switching slots and Weapon off return held items to their origin, a free slot, or the world when all slots are occupied.
- Pick up weapons with Use / F into the first free slot in order 1–6, including automatic drawing when that slot is already selected.
- Unlock slots 4, 4–5 or 4–6 with the worn backpack. Removing or downsizing it ejects overflow after a short stable gameplay delay.
- Expand the original slot card progressively to 1–4, 1–5 and 1–6 while preserving texture scale and end caps. The ammo/value panel and card's right edge stay fixed; the slot row and grenade/drop/unequip hints move left as capacity increases.
- Save logical slot mappings and hand poses inside the game's ES3 save. Ordinary stored items and extra-slot weapons are normalized to physical world items during serialization, then restored to the inventory.
- Cache generated item icons on disk; retain native item icons when available.
- Fix the Unity `Start() can not take parameters` error by renaming the transaction entry method. Verification now checks Unity lifecycle signatures on all plugin MonoBehaviours.

## Installation

Requires Apocalypter and BepInEx 5.4.x. Copy `Apocapocket.dll` into `BepInEx/plugins/Apocapocket`, replacing the previous version. Keep only one copy of the plugin DLL and restart the game. Existing extra-slot key bindings and the plugin GUID are retained.

## Validation and known limits

The production inventory, transaction and persistence code passed 77 checks with headless adapters. The plugin compiled against the installed game assemblies; all four Harmony patches and Unity lifecycle signatures passed the assembly smoke checks.

The expanding artwork was confirmed in-game. The latest leftward layout correction and the complete save/load, vehicle and physical-item acceptance scenarios still need in-game verification; see `VALIDATION.md` in the source and release archive. The native Unity mesh check could not run because the local editor lacked an activated license.

Stale inventory mappings from saves written without the plugin are detected using the saved vanilla Timeline. A re-save with an exactly unchanged Timeline cannot be distinguished by this check. Hand-carried items remain under vanilla/Apocasaver save handling.

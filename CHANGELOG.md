# Apocapocket 2.0.7

- Weapon watchdog: fixes the "weapon hints on screen, nothing in the hands, can't change weapon" lock-up within about 3-5 seconds. The game's weapon logic can get stuck after a grenade, bandage, food or a held item (it waits for a weapon to come back but has none recorded), or keep weapons away because of a held item that is gone or invisible. The watchdog checks once a second (negligible cost), repairs the hand or puts the weapon back / holsters it, and writes a "Weapon watchdog:" line to the log. It never acts in vehicles, menus, during saves/loads or while a slot change is in progress.

# Apocapocket 2.0.6

- Slot 4-6 keys work in vehicles too (first and third person), like the game's slots 1-3.

# Apocapocket 2.0.5

- Fix: items without the game's usual item scripts (e.g. the empty alcohol canister, which has only an ID script) are restored to their slot on load instead of staying on the ground ("Load: missing pocket item ..."). The restore now looks up every saved scene object by its exact name.
- Same-named objects: the one lying where the save put it is taken; if several match (or none), nothing is taken and a warning explains why.
- Slot contents left in a slot after a load are recognised without the item-script requirement as well.

# Apocapocket 2.0.4

- Fix: slots 1-6 are really restored when a save is loaded (2.0.0-2.0.2 left pocketed items lying on the ground). Three bugs, all in the save code:
  - the slot list was never written into the save (the JSON serializer silently skipped it) - entries are now stored as plain text;
  - the load read the mapping from the wrong save file (stale "SaveFile" FSM variables) - it now reads only the save the game actually loaded;
  - a stale-mapping check compared the saved timeline with the running clock and always failed - replaced by "the item still lies where the save put it".
- Saves made with 2.0.0-2.0.2 contain no slot list: their items stay on the ground once; pick them up and slot them again.

# Apocapocket 2.0.2

- Fix: items in slots 4-6 no longer fly out when a save is loaded. The load restored the slots before the worn backpack was back on the character, so slots 4-6 counted as locked. Slots are now always restored, and the backpack lock check runs 5 s after the load during normal play (taking the backpack off still drops slots 4-6 as before).

# Apocapocket 2.0.1

- Fix: items taken out of a slot can be attached to cars again (spikes, bumpers and other attachables). Storing an item switches off its `Attach`/`CheckBool` FSMs; 2.0.0 never switched them back on when the item came back into the hand.
- Attachables already taken out with 2.0.0 (and saved that way) are repaired automatically the next time you pick them up.

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

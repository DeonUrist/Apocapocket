# 2.0.0 implementation validation

The implementation is built and checked locally. It has not been certified by running all thirteen acceptance scenarios in Apocalypter. No existing player save was modified by verification.

## Automated checks

Run `./verify.ps1` from this repository.

- The production `Inventory.cs`, `InventoryWorld.cs` and `Persistence.cs` run through a headless Unity/PlayMaker/ES3 adapter. Checks cover recorded component restoration, pose roundtrip, return-to-origin, selection restoration with a held item, transactions during pause, timeout rollback, interruption before borrow insertion, queued input, 20 repeated draw sequences, dropped/consumed borrowed weapons, aid transitions, custody guards, runtime disable, ordered pickup while drawn, automatic draw in empty selected slots, borrow-host exclusion, backpack sizes and overflow ejection.
- Persistence checks cover synchronous normalization, vanilla world parents/physics, logical mapping during borrow, cache-before-flush ordering, waiting for registry serialization, delayed file verification, reload by stable item names, disabled loads/saves, new-game isolation and stale mappings after a vanilla re-save.
- `mcs -nostdlib -langversion:7` compiles against the installed game's libraries.
- A Mono smoke executable applies all four Harmony patches to the actual game assemblies, then removes them. It verifies the unchanged GUID and plain `System.Version(2,0,0)` metadata.
- The smoke executable checks parameterless Unity lifecycle callback signatures on every plugin MonoBehaviour. A negative-control fixture confirms it rejects the `Start(Op)` collision that was missed by the original headless tests and reported by the real game. The transaction entry method is now named `BeginOperation`.
- `git diff --check` validates whitespace.

The adapters simulate the relevant FSM outcomes. They do not certify Unity rendering, terrain collision, native FSM action side effects, native ES3 serialization, or the actual game UI.

The expanding card uses `ExtendedCardGraphic` to retain the original artwork density and repeat its middle. `verify-ui.ps1` provides a native Unity 2020.3 mesh check for the 3/4/5/6-slot widths, cap UVs, coverage and unchanged horizontal UV density. The local attempt could not start the editor because Unity reported no activated ULF license. The user confirmed the expanding artwork works in-game. The latest correction holds the ammo/value panel and card's right edge fixed, shifting the slot row and left-side hints left; that layout correction still needs real-game inspection.

## In-game acceptance still required

The following scenarios capture the remaining in-game acceptance coverage. Run them on a disposable save:

1. Gas can store/take-out in slots 1 and 4–6, including hand pose.
2. Every backpack size and downsizing/removal with occupied extra slots.
   The original card must extend left by one, two or three slot pitches while preserving numerical order 1–4, 1–5, 1–6, without stretching the artwork or exposing locked slot widgets. Its right edge and adjacent ammo/value panel must stay fixed, and grenade/drop/unequip hints must move left by the added width. Restore the original card width/positions on disable and check shrinking when backpack capacity decreases.
3. Icon rendering on first encounter and PNG loading after restarting the game.
4. Six ordered F pickups, including a drawn weapon and a generic-item-occupied native slot.
5. Auto-draw when pickup's first free slot is already selected. Earlier slots must be occupied to make the selected slot the first free slot; the specification's first-free pickup rule otherwise takes precedence.
6. Rapid real key presses through the full draw/holster sequence.
7. Drop Weapon and consumed blast lances while borrowing occupied native slots, including ground collision.
8. Aid use and automatic re-equip during borrow.
9. Save/load while borrowing: same logical slots and selection, mapping present, no mod-holder parent references on normalized items.
10. Load that original save without the DLL; items must be visible, solid and pick-up-able. Reinstall the DLL and load the original save. Also re-save with the DLL absent and verify stale mapping rejection.
11. Disable/re-enable through the Mods menu and inspect UI/hint restoration.
12. Ten vehicle entries/exits and third-person toggles with a radio, cassette and headlight pocketed.
13. Hand-grab a gun, store in slot 3, draw and drop onto terrain.

## Compatibility details

Fixed holder reference IDs are retained only to resolve 1.x saves. Runtime staging is never serialized as an item parent. A 2.x save keeps native-slot weapons in their normal game slots and normalizes ordinary items and extra-slot weapons to world items.

The vanilla save cache may preserve unknown mod keys when saving without a DLL. The mapping therefore records the saved `SaveLoadGame.Timeline` value and ignores a mapping whose saved clock differs from the loaded vanilla clock. An absent-mod re-save with an exactly unchanged saved clock cannot be distinguished by this check; that edge case needs observation in the real game. The original unmodified save retains its mapping.

Hand-carried items remain under vanilla/Apocasaver save handling. Slot poses are persisted independently of Apocasaver. Item renderer/collider enable sets are captured from the visible loaded item when reconstructing the inventory.

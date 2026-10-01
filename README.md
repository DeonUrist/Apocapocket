# Apocapocket

**Item slots** for Apocalypter (BepInEx 5 plugin): the weapon slots 1 / 2 / 3 also hold ordinary hand-carried items, and a
backpack adds up to three item-only slots 4 / 5 / 6.

## Features

- Holding an item and pressing **1 / 2 / 3** on an empty slot pockets it there (hidden, frozen, out of the way), leaving you
  empty-handed. The slot shows a rendered icon of the item.
- Pressing the key of a slot that holds a pocketed item puts it back in your hands through the game's own grab logic, at the
  position it had when you stored it (drop / throw / rotate / scroll work as usual).
- Pressing the item's own key again, or **Weapon off**, hides it back into its slot (a world-picked item goes to the first free
  slot; if there is none it is dropped). Switching to another slot while holding an item that cannot go back drops it.
- Slots holding weapons behave exactly as before; a slot with an item counts as occupied for weapon pickups.
- Pocketed items survive saves/loads (they are stored the same way as holstered weapons). Slot keys are ignored in third person
  (vehicle camera). Disabling the mod ejects every pocketed item in front of you.
- **Extra slots 4 / 5 / 6**, shown on a second slot card to the left of 1-3. They are unlocked by the backpack
  you wear (cumulative): small = slot 4, medium = 4-5, large / huge = 4-6. Items go in and out exactly like in slots 1-3.
  Weapons make them **extra holsters**: a gun in your hand goes into an empty slot 4-6 with its key; pressing the key of a
  slot that holds a gun draws it and it stays that slot's gun (its key again, Weapon off, another slot key or dropping
  it holsters it back there); pressing the key of an empty slot 4-6 while a weapon is drawn moves it there.
  Anything in a slot that becomes locked (backpack taken off or swapped for a smaller one) or that sits in slots 4-6 while
  the mod is disabled is thrown out in front of you, weapons included; so is anything an older save left under the
  slot holders that no slot owns.
  While slots 4-6 are visible, the Unequip / Drop / Grenade hints move left out of their way.
- The game's **Controls** screen shows the slot keys as **Item 1 / 2 / 3** and adds **Item 4 / 5 / 6** right below them;
  rebinding there is saved to this mod's config (and config edits show up there).
- Optional: with [Apocasaver](../Apocasaver) 1.4+ installed, the hand pose of pocketed items is saved with the game, so they come
  out where you left them after a load. Apocasaver also keeps the item in your *hand* across saves.
- Opt-in entry in the [Apocasetter](../Apocasetter) Mods menu (no dependency on it).

## Installation

Install [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) (win_x64), run the game once, then copy `Apocapocket.dll` to
`BepInEx\plugins\`.

Config: `BepInEx\config\com.denis.apocalypter.apocapocket.cfg` (also editable in the Apocasetter Mods menu)

| Section / key | Default | Description |
| --- | --- | --- |
| `[General] Enabled` | `true` | Turn the item slots on/off (off = vanilla keys; everything kept in slots 4-6 is dropped in front of you) |
| `[General] RequireBackpack` | `true` | Slots 4-6 are unlocked by the worn backpack: small = 4, medium = 4-5, large / huge = 4-6 (off = always available) |
| `[Debug] VerboseLog` | `false` | Log every step (debugging only) |

The keys for slots 4-6 are rebound in the game's **Controls** screen (rows "Item 4/5/6"); they are stored in
`com.denis.apocalypter.apocapocket.keys.cfg`. Everything else (icon size, pocket blacklist — crates, PartAdjusterTools —, hand
pose defaults, fallback keys) is built in since 1.5.0; old config files are cleaned up automatically on the first start.

## Building

- `dotnet build` (override the game path with `-p:GameDir=...`); deploys to `BepInEx\plugins` after build, or
- `./build.sh` with mono `mcs` (`MANAGED` / `BEPCORE` env vars).

## How it works

Everything runs through the game's own PlayMaker FSMs. Storing replays the `takeWeapon` recipe the game uses for holstering
(`not_Hold` to `GrabItem`, parent under `Slot N`, renderers/colliders off, rigidbody frozen); taking out holsters a drawn weapon,
parents the item under the `GrabItem` FSM's `Hand`, sets its `Item` variable and switches the FSM to its `Grab` state. A Harmony
prefix on `GetButtonDown` keeps the game's own weapon-key handling away from slots that hold items. Pocketed items keep the
game's `LockPhysics` FSM parked in `off` every frame (entering a vehicle pokes it and it would glue the item to the car), and an
item that is moved out of its slot by anything else is put straight back. Icons are rendered once per item type from a stripped
copy of the item with a private camera and cached for the session (weapons show the game's own `imageUI` icon). Slots 4-6
are holder objects under `PlayerCamera` (registered with Easy Save under fixed reference ids, so their contents save like
slots 1-3); their UI is cloned from the game's slot widgets and card. The game's draw pipeline (`Weapons` FSM state
`Slot N`, `WeaponInHand`, the slot's `UseWeapon`/`DropWeapon` FSMs) is hard-wired to the three `Slot` objects, so a weapon
kept in slot 4-6 borrows a game slot while it is drawn (the game's `SlotEmptyFull` FSM then treats it as holstered and the
`Weapons` FSM is switched to that slot); the game slot's own content is parked in the extra slot meanwhile and the UI
keeps showing both where they belong. As soon as the `Weapons` FSM leaves that slot state, or the weapon leaves the slot
(dropped), everything moves back. A save made while a gun is drawn from slot 4-6 stores it in the borrowed slot 1-3. The Item 4-6 controls are extra `KeyAction`s in the game's InsaneSystems InputManager storage,
re-added on every start.

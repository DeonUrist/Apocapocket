# Apocapocket

**Item slots** for Apocalypter (BepInEx 5 plugin): the weapon slots 1 / 2 / 3 also hold ordinary hand-carried items.

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
- Optional: with [Apocasaver](../Apocasaver) 1.4+ installed, the hand pose of pocketed items is saved with the game, so they come
  out where you left them after a load. Apocasaver also keeps the item in your *hand* across saves.
- Opt-in entry in the [Apocasetter](../Apocasetter) Mods menu (no dependency on it).

## Installation

Install [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) (win_x64), run the game once, then copy `Apocapocket.dll` to
`BepInEx\plugins\`.

Config: `BepInEx\config\com.denis.apocalypter.apocapocket.cfg`

| Section / key | Default | Description |
| --- | --- | --- |
| `[General] Enabled` | `true` | Turn item slots on/off (off = pocketed items are ejected) |
| `[General] BlacklistIDs` | `PartAdjusterTools;box_cardboard;crate_metal;crate_plastic` | Items that can never be pocketed, by `ID` string or prefab name (`;`-separated); crates carry other items |
| `[General] IconSize` | `128` | Pixel size of the rendered slot icons |
| `[General] VerboseLog` | `true` | Log every step |
| `[HandPose] DefaultX/Y/Z` | `0` | Hand offset used when no pose is known |
| `[Keys] FallbackSlot1..3 / FallbackWeaponOff` | `Digit1..3` / `None` | Used only if the game's legacy input axes cannot be read |

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
copy of the item with a private camera and cached for the session.

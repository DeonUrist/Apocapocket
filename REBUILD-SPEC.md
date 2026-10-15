# Apocapocket — Rebuild Specification (current 1.5.0 → target 2.0.0)

Audience: the developer / LLM that rebuilds the mod from scratch. Self-contained; background game facts are included.
Source of the current version: `V:\Repos\Apocalypter\Apocapocket` (HEAD 130bf81 = 1.5.0), files Plugin.cs, ExtraSlots.cs,
Keybinds.cs, Icons.cs. Reuse the parts marked **KEEP**; rewrite the parts marked **REPLACE**.

---------------------------------------------------------------------------------------------------------------------------

## 0. Game & platform facts the implementation depends on

- Apocalypter, Unity 2020.3.49f1, Mono, BepInEx 5.4.23.x, Harmony. Game logic = PlayMaker FSMs (no game C# to call).
- Build with `mcs -nostdlib` against the game's own `Apocalypter_Data\Managed` DLLs (C# 7, no local functions, `lock` is a
  keyword). Never reference newer BCL overloads.
- Plugin GUID `com.denis.apocalypter.apocapocket` (KEEP — configs and saves depend on it). Version must be a plain
  `System.Version` (e.g. `2.0.0`), otherwise BepInEx skips the plugin silently.
- The game destroys plugin GameObjects on scene load → all per-frame logic on a hidden `DontDestroyOnLoad` runner, recreated
  on `sceneLoaded`. Re-find all player references after every scene load / when they become null.
- `PlayMakerFSM.FindFsmOnGameObject` logs warnings on misses → find FSMs by iterating `GetComponents<PlayMakerFSM>()`.
- Check `fsm.Fsm.Initialized` before reading `ActiveStateName`.
- **PlayMaker restarts FSMs when their GameObject is re-enabled** (`Fsm.RestartOnEnable`, default true). The vehicle
  third-person camera disables the whole `PlayerCamera` hierarchy; on return every FSM under it restarts. Vehicle parts'
  `CheckTag` FSM start state does `SetParent(null)` + `AddComponent<Rigidbody>` → pocketed vehicle parts get thrown out.
  Rule: while an object is in the mod's custody set `RestartOnEnable = false` on its `CheckTag` and `LockPhysics` FSMs;
  restore `true` when it leaves custody.
- Entering a vehicle pokes items' `LockPhysics` FSM even when that FSM component is disabled (LockPhysics_OFF → 3 s →
  raycasts → parent to the car + destroy Rigidbody). Pocketed objects must have LockPhysics parked in state `off` and the
  component disabled, checked every frame.

### Player objects (paths under the active scene)
| Object | What it is |
|---|---|
| `PlayerCameraHolder/PlayerCamera` | camera; has FSM `GrabItem` (hand-carrying), children `Hand`, `HandItemUse`, `ItemDrop`, `Guide`, `QuickItems` |
| `PlayerCamera/Hand` | parent of a hand-carried item |
| `PlayerCamera/HandItemUse` | when a weapon is drawn its first child is the object named `Weapons` |
| `.../Weapons` (FSM `Weapons`, `WeaponInHand`, `UseWeapon`) | the weapon system; children `Slot 1`, `Slot 2`, `Slot 3` |
| `Weapons/Slot N` (FSMs `SlotEmptyFull`, `UseWeapon`, `DropWeapon`) | game slot N; a holstered weapon is its child |
| `PlayerCamera/QuickItems/Backpack_Item` | the worn backpack is its child (prefab names contain small/medium/large/huge) |
| `__GameManager__` FSMs `Menu` (state `play` = in game), `GrabItem_Pause` (bool `GrabItem_Pause`) | pause / menu state |
| `Player` FSM `InCar` (state `OnFoot` = not in a vehicle) | vehicle state |
| `DriveTrigger` FSM `Camera` (states `1st`/`3rd`, enabled only while driving) | third person exists only in vehicles |

### Vanilla FSM behaviour (from FSM dumps)
- **GrabItem** (hand carry): idle → over → `Grab` → `ItemInHand` (+ Rotate/Forward/Backward); var `Item` = carried object.
  `Grab` aborts if a weapon is drawn. Global event `not_Hold` → `notHold` (releases without velocity), `drop` → Drop.
- **Weapons** FSM states: `off 2` (holstered; polls legacy buttons `Weapon 1/2/3`), `Slot 1/2/3` (drawn; polls `Weapon off`
  and the other two), `useAid` (global event `Deactivate`, waits 2.1 s) → `reEquip`, `checkHandItem`. Global `Activate` →
  `off 2`. Local event `back` = holster immediately. Bools `weapon1_Bool..weapon3_Bool`, `weaponOff_Bool` decide what
  `reEquip` returns to. Entering `Slot N` enables Slot N's `UseWeapon`+`DropWeapon` FSMs and sends `Slot_N` to `WeaponInHand`.
- **WeaponInHand**: on `Slot_N` → `GetChild(Slot N, tag Rigidbody)` → reads that child's `weaponType` FSM string →
  activates the matching arm model (`PlayerCamera/WeaponsArm/Parent/<type>`); no child / unknown type → bare `hands`.
- **SlotEmptyFull** (per game slot): polls child count every frame but runs its actions **only on state entry**: `full` =
  GetChild(tag Rigidbody), layer 0, reparent+reset, icon from the child's `imageUI` FSM texture into the slot widget,
  sends `Deactivate` + `CollidersDisable` to the child; `empty` = empty texture. Swapping a child in place does NOT re-run
  them → leave the slot empty for one frame before inserting a new child.
- **UseWeapon** (pick up from the ground with Use = F):
  - `Weapons [UseWeapon]` (enabled while holstered): MousePick 2 m (layer 9) → `checkID` (target's FSM `ID` string ==
    `"weapon"`) → `over` (hint text "Take Weapon (F)" in its `UI_ItemUse` GameObject var, `GetButtonDown Use`) → loops
    Slot 1..3, takes the first slot with no children (`takeWeapon` recipe below). No free slot → nothing happens.
  - `Slot N [UseWeapon]` (enabled while slot N is drawn) only checks its own slot (which holds the drawn weapon) → in
    practice never takes anything while a weapon is drawn.
  - `takeWeapon` recipe: EnableFSM `Attach`/`CheckBool` off on the item, send `not_Hold` to GrabItem, SetParent(slot,
    reset local), `SetVisibility false` (root renderer), send `CollidersDisable` to the item's `Colliders` FSM,
    gravity off, kinematic on, collider trigger on, play `draw_holster`.
- **DropWeapon** (per slot, button `Drop Weapon`, enabled only for the drawn slot): child → layer 9, `CollidersActivate`,
  `Activate`, `SetVisibility true`, position at `ItemDrop`, gravity on, kinematic off, trigger off, unparent.
  **It only restores what the game itself switched off.** Anything the mod disabled (extra renderers, colliders) stays
  off → the dropped object falls through the terrain (this caused every "dropped gun disappeared" bug so far).
- Weapon objects have FSMs: Buyable, Colliders, ForBuy, ID (`"weapon"`), imageUI, ItemName, LockPhysics, saveItemVar,
  Value, Visibility, weaponType. Generic items have no Visibility/weaponType/imageUI (some aid items have imageUI).
- Throwable weapons (blastlance_1/_2): their arm-model `Attack` FSM spawns a projectile and **destroys** the slot's child.
- Holstered weapons stay children of their Slot N even while drawn (visual = arm model).

### Input
- The game's own actions: `InsaneSystems` `InputController.GetKeyActionIsDown("Weapon 1")` etc. follow rebinds; legacy
  `Input.GetButtonDown("Weapon 1"|"Weapon 2"|"Weapon 3"|"Weapon off"|"Use"|"Drop Weapon")` is what the FSMs poll.
- To suppress the game's reaction to a key: Harmony prefix on `HutongGames.PlayMaker.Actions.GetButtonDown.OnUpdate`
  (set `storeResult=false`, return false) for the relevant `buttonName` and owning FSM.
- Hardcoded game F-keys: F2 = Heal, F9 = toggle UI. Never default a mod key to an F-key.

### Easy Save 3 (saves)
- Saves: `%USERPROFILE%\AppData\LocalLow\SawyerK Games\Apocalypter\SaveGameN.es3` (JSON). Items are saved through the
  game's `NewGO_ArrayList` registry: transform incl. **parent reference ID**, rigidbody state, etc.
- Default ES3 location is **Cache**; the game flushes the cache to the file at the end of a save → anything written straight
  to the file during the save is wiped. Write mod keys **after** the save completes (≈1 s after `SaveLoadGame` returns to
  `isPlay`) to both cache and file, OR write them into the cache before the flush (preferred if the hook point is reliable).
- Current slot path = `ES3Settings.defaultSettings.path` during a save; during a load it may be empty and
  `SaveLoadGame.SaveFile` may be stale → try all candidates. `ES3.Load<string>(key, "", settings)` binds to the wrong
  overload — use `ES3.Load<T>(key, settings)`.
- Apocasaver (sister mod) already hooks save/load phases; see `claude/apocasaver-notes.md` for the exact SaveLoadGame FSM
  states. Apocasaver also stores a per-item hand pose (`Apocasaver.HandPose.Get/Set(itemName, "x,y,z,qx,qy,qz,qw")`) inside
  the save; Apocapocket may use it by reflection (optional dependency).
- Item GameObject names are unique and stable across save/load (`prefab(Clone)<counter>`, counter from the game's
  itemNameID) → use `gameObject.name` as the persistent item identity.

---------------------------------------------------------------------------------------------------------------------------

## 1. Requirements (from Denis) and current status

| # | Requirement | Current 1.5.0 | Verdict |
|---|---|---|---|
| R1 | Any item can be put in inventory slots, not just weapons | Works for slots 1-6; blacklist (crates, PartAdjusterTools) + merchant stock refused | Mostly OK — keep, minimal exclusions (§3.1) |
| R2 | Backpack small/medium/large/huge = +1/+2/+3/+3 slots | Implemented (`UpdateBackpack`, prefab-name match; unknown backpack = +1) | OK — KEEP logic |
| R3 | Icons generated once, cached on disk, reused; render (spawn fake item) only if missing locally | Rendered from a stripped clone with a private camera, cached **in memory per session** only | REPLACE (§3.6) |
| R4 | F pickup goes to slots in order 1-2-3-4-5-6 | Vanilla fills 1-3; mod fills 4-6 **only when the vanilla search fails**. With a weapon drawn the vanilla search is Slot N-only → mod puts it in 4-6 even if slot 2 is empty | BUG — REPLACE (§3.4) |
| R5 | Taking out items/weapons never fails (now "pressing 4 sometimes doesn't take out the weapon") | Proxy/borrow state machine + per-frame step queue that pauses while `!GameplayActive`, retries ≤10 frames then cancels, unproxy chains, rescue/sweep passes fighting each other | BUG — REPLACE (§3.3, §3.5) |
| R6 | If slot X is selected (hands shown) and a picked-up weapon goes into X → draw it | Not implemented; empty slots 4-6 cannot be selected at all | MISSING (§3.4, §3.2) |
| R7 | All persistence via the game save (Steam Cloud safe), no external files | Slot contents persist via ES3 parent refs (holders registered under fixed ES3 ref IDs) — inside the save, OK. Proxy state not persisted (weapon saved in the borrowed slot). Key bindings in a `.keys.cfg` (settings, acceptable) | Partly OK, but breaks R8 — REPLACE (§3.7) |
| R8 | Mod disabled, or save loaded with the mod absent/disabled → pocketed items fly out of the character | Runtime disable: `EjectAll` for 4-6 + non-weapons in 1-3. **Save loaded without the mod: undefined** — items parented to holders that don't exist (unresolved ES3 ref) / hidden items in game slots that the game treats as weapons, renderers/colliders possibly disabled | BUG — REPLACE (§3.7, §3.8) |

---------------------------------------------------------------------------------------------------------------------------

## 2. Current architecture (1.5.0) — what to keep, what failed

**Slots.** Logical slots 0-2 = game `Slot 1..3`; 3-5 = mod holders `PlayerCamera/Apocapocket_ExtraSlots/Slot 4..6`, each
holder registered in `ES3ReferenceMgrBase.Current` under fixed IDs (GO 7431505000000000401/501/601, Transform …402/502/602).

**Pocketing an item** (`StoreHeld`): item recipe = Attach/CheckBool FSMs off, GrabItem `not_Hold`, parent under slot
(reset local), **all** renderers + colliders disabled (recorded), rigidbody kinematic/no gravity, LockPhysics off,
RestartGuard on. A weapon into a game slot (1.4.1) instead uses the game recipe (`ToGameHolster`).

**Taking out** (`TakeOut`/`Grab`): holster drawn weapon, parent under GrabItem's `Hand` var, restore pose (Apocasaver
HandPose or default), restore renderers/colliders, rigidbody dynamic/no gravity, set GrabItem `Item`, `Fsm.SetState("Grab")`.

**Weapons in 4-6 (proxy/borrow, 1.3.1–1.4.0).** The draw pipeline only knows Slot 1..3, so drawing a weapon from slot k
"borrows" a game slot n: holster (`back`), wait until HandItemUse empty, park slot n's content in holder k, leave n empty one
frame, move the weapon into n (`ToGameHolster`), `Weapons.Fsm.SetState("Slot n")`. UI shows the weapon in k and the parked
content in n. `ProxyIntact()` watches; when the game leaves `Slot n` (Weapon off, other key, drop, destroy) `Unproxy` swaps
back over two frames.

**Why it misbehaves (root causes to design out):**
1. Multi-frame transactions driven by a FIFO of lambdas (`_steps`) that is **paused whenever `GameplayActive()` is false**
   (menu, third person, GrabItem_Pause) and **silently cancels** after 10 retries → key presses lost, half-finished swaps.
2. Several independent per-frame "repair" passes (Rescan re-register/re-hide, rescue, SweepStranded, lock-eject,
   ProxyIntact/Unproxy) each mutate the same objects with their own idea of truth; no single source of truth.
3. State held in several parallel structures (`_stored[]`, `_heldItem/_heldFrom`, `_inTransit`, `_pxK/_pxN/_pxW/_pxD`)
   that can disagree (e.g. a weapon in a game slot is "the game's" and its record is dropped, then the mod's hide is never
   undone → falls through the ground on drop).
4. Hiding by disabling **all** renderers/colliders, which the game never undoes.
5. F pickup only patches the failure branch of the vanilla search (R4 bug).
6. Persistence = vanilla parent refs to mod-only objects → save not loadable without the mod (R8).
7. Icons re-rendered every session by spawning a stripped clone (R3).

**KEEP (proven):** backpack detection; UI cloning of slot widgets/card + hint shift (ExtraSlots.cs `BuildUi`, `UpdateUi`,
`ReadSlotUi` reading the SlotEmptyFull actions to find the right RawImage — never look UI up by name, names are reused);
Controls-screen integration (Keybinds.cs: adds "Item 4/5/6" KeyActions every start because InputStorage drops them,
renames Weapon 1-3 rows to Item 1-3, two-way sync with `.keys.cfg`); RestartGuard; LockPhysics guard; ToGameHolster recipe;
`BeforeGameDrop` safety net; Harmony on GetButtonDown; third-person/menu gating for keys; Apocasaver HandPose bridge;
Config (`[General] Enabled`, `[General] RequireBackpack`, `[Debug] VerboseLog=false`), `MigrateOldConfig`.

---------------------------------------------------------------------------------------------------------------------------

## 3. Target design (2.0.0)

### 3.0 Core model — one source of truth

```
class SlotState { GameObject Content; Kind Kind; /* Empty | Item | Weapon */ Pose HandPose; }
SlotState[6] Slots;            // logical slots 1..6 (index 0..5)
int Selected;                  // logical slot whose hands are shown / weapon drawn, -1 = none
GameObject Held;               // object in GrabItem's hand (read from GrabItem each frame, never cached as truth)
Op CurrentOp;                  // at most ONE transaction in flight (see 3.3)
int Unlocked;                  // 3 + backpack bonus (or 6 if RequireBackpack=false)
```

- Every pocketed object is in exactly one place: `Slots[i].Content`, the hand, or the world. An invariant checker runs
  every frame (after the transaction step) and only **logs + repairs toward the model** (never invents new state).
- Physical location rule: slots 1-3 contents are children of game `Slot N`; slots 4-6 contents are children of mod
  holder `Slot 4..6` — **except while drawn via borrow** (3.5), where the model still says "slot k" and a separate
  `Borrow {k, n, parked}` record describes the temporary physical swap.
- The model is rebuilt from the save on load (3.7), never inferred from transforms except for vanilla weapons in 1-3.

### 3.1 What can be pocketed (R1)
- Anything GrabItem can hold (`GrabItem.Item` while in Grab/ItemInHand/Rotate/Forward/Backward) and any weapon.
- Refuse only: merchant stock (`ForBuy.ForBuyInt == 1`), objects whose children include other saveable items (containers:
  crates `crate_metal`, `crate_plastic`, `box_cardboard` — pocketing them would also hide/strand their contents), and
  `PartAdjusterTools`. Keep as a hardcoded list; log the refusal once.

### 3.2 Hiding / showing objects (fixes root cause 4)
- **Weapons in any slot** use the game's own recipe only: `takeWeapon` (Attach/CheckBool off, `not_Hold`, parent, layer 0,
  `Visibility` FSM `Deactivate` / `SetVisibility false`, `Colliders` FSM `CollidersDisable`, gravity off, kinematic, trigger,
  RestartGuard on). Never disable extra renderers/colliders on a weapon. Then vanilla DropWeapon always works.
- **Non-weapon items**: record exactly which renderers/colliders were enabled, disable those, kinematic, LockPhysics parked
  off + disabled, RestartGuard on. Restore exactly the recorded set on take-out/eject. Keep the record in the model.
- Before the game takes over any object the mod hid (drop from a game slot, eject, save-normalisation 3.7) the mod restores
  everything itself first (generalised `BeforeGameDrop`).
- Hand-carried weapon pressed into a slot (1-6) = weapon recipe (1.4.1 behaviour), never the item recipe.

### 3.3 Transactions (fixes R5, root causes 1-3)
- All user actions become an `Op` (TakeOut, Store, Draw, Holster, Swap, Pickup, Eject) executed by a small explicit state
  machine with named phases, one phase per frame at most, **advanced every frame regardless of pause** but only *started*
  when gameplay is active. Phases that need the game (wait for HandItemUse to empty after `back`) have a timeout
  (e.g. 30 frames / 1 s unscaled) and a defined **rollback** that returns every object to its slot (never "cancel and
  leave"). A key press while an Op runs is queued (latest press wins, max 1 queued), never dropped.
- Holster always with Weapons event `back` (immediate), never `Deactivate` (2.1 s useAid). Set `weaponN_Bool`/
  `weaponOff_Bool` yourself before any transition that relies on `reEquip`.
- Key handling (slot i pressed, gameplay active, not third person, extra keys ignored in vehicles):
  | State | Action |
  |---|---|
  | Holding an item/weapon, slot i empty | Store into i (weapon/item recipe). Hands empty. |
  | Holding, slot i occupied | Put held back into its origin slot if free, else first free slot, else drop (GrabItem `drop`); then act as "not holding" |
  | Not holding, slot i = Item | Holster drawn weapon (if any) → Take out item into hand (Grab). Selected = i |
  | Not holding, slot i = Weapon, not drawn | Draw it (i≤3: `Weapons.SetState("Slot i")` with bools; i≥4: Borrow 3.5). Selected = i |
  | slot i = Weapon and it is the drawn one | Holster (`back`). Selected = -1 (vanilla toggle behaviour) |
  | slot i empty, not holding | Select i with bare hands: holster current, i≤3 → vanilla `Slot i` state (shows `hands`); i≥4 → Selected = i, holster, show outline on extra slot i (no game slot borrowed) |
  | `Weapon off` | Holding a slot item → back to its slot (or first free / drop). Else holster, Selected = -1 |
- Suppress the game's own `Weapon 1/2/3` / `Weapon off` handling (GetButtonDown prefix) on frames the mod handled, while an
  Op runs, and whenever the target slot holds an item or the hand holds something.

### 3.4 Picking up weapons with F (R4, R6)
- Own the whole pickup: Harmony prefix on `GetButtonDown.OnUpdate` for `buttonName == "Use"` when the owning FSM is
  named `UseWeapon` (Weapons-level or any Slot N). Let the FSM keep doing its MousePick/`checkID` (its var `Item` is the
  targeted weapon); in state `over` the mod rewrites the hint text (`UI_ItemUse` var → `UnityEngine.UI.Text`) to
  `"Take Weapon to Slot K (F)"` and on Use performs the pickup itself, returning false so the vanilla takeWeapon never runs.
- Target slot K = first logical slot in order 1,2,3,4,5,6 (up to `Unlocked`) whose `Kind == Empty` and that is not the
  physical host of a borrow. An item-occupied slot is NOT free. If none → hint "Slots full", do nothing.
- Placement: K≤3 → game recipe into `Slot K` (wait one frame if `Slot K` was just emptied); K≥4 → game recipe into holder K.
- R6: if `Selected == K` (player had slot K selected with bare hands) → immediately draw it (K≤3: re-enter `Slot K` state
  after SlotEmptyFull ran `full` (one frame) so WeaponInHand re-reads the child; K≥4: start a Borrow draw).

### 3.5 Drawing a weapon from slots 4-6 (Borrow) — robust version
- Host game slot n: an **empty** game slot if any; else the slot of the drawn weapon; else the first game slot.
- Phases: (1) `back` if drawn; wait HandItemUse empty (timeout→rollback). (2) If n occupied: move its content to a hidden
  staging transform (`Apocapocket_ExtraSlots/Staging`, registered like the holders) using the correct recipe for its kind;
  n is now empty this frame. (3) Next frame: weapon k → `Slot n` with game recipe. (4) Next frame (SlotEmptyFull ran
  `full`): set bools, `Weapons.Fsm.SetState("Slot n")`. Record `Borrow{k,n,parked}`. UI: extra slot k shows the weapon
  selected; game slot n's widget shows `parked` (per-frame override of the RawImage/outline, as ExtraSlots.ShowInGameSlot).
- End of borrow (detected every frame): Weapons state no longer `Slot n` (excluding `useAid`/`reEquip`/`checkHandItem`
  which return by themselves), or the weapon left `Slot n` (dropped), or the weapon object was destroyed (thrown lance).
  Then: weapon (if it still exists and is still in Slot n) → holder k; parked → back to Slot n (one empty frame between);
  if destroyed/dropped → slot k becomes Empty, parked → Slot n. Never touch the slot that the player just pressed until
  the restore finished (that press is the queued Op).
- Save during a borrow: normalise first (end the borrow synchronously) — see 3.7.

### 3.6 Icons (R3)
- Weapons and items that have an `imageUI` FSM texture → use it (no rendering).
- Others: icon key = prefab name (strip `(Clone)` and trailing digits) + icon size + `IconVersion` constant.
  Look up memory cache → disk cache `BepInEx\cache\Apocapocket\icons\<key>.png` (load with `Texture2D.LoadImage`) →
  only if both miss, render once (stripped clone: no FSMs/colliders/rigidbody/audio/scripts, own layer, private camera,
  RenderTexture), destroy the clone the same frame, save PNG to disk, add to memory. The disk cache is a local cache only
  (not game state); deleting it just causes re-rendering. Never render while paused/loading; queue and render one icon per
  frame.

### 3.7 Persistence via the game save, vanilla-safe (R7, R8)
Principle: **the save must look like a vanilla save.** Pocketed objects are saved as ordinary world items lying at the
player's feet; the slot mapping is a mod key inside the same save file.

- Save start (hook the SaveLoadGame FSM save state entry or the `SaveGame` event, before items write their data):
  1. Finish/rollback any running Op; end any Borrow (restore physical locations).
  2. Item in hand: leave to Apocasaver/vanilla (out of scope), or treat like a pocketed item if Apocasaver is absent.
  3. For every pocketed **non-weapon** and every weapon in **slots 4-6**: restore renderers/colliders, unparent, place at
     `ItemDrop` (spread a few cm apart), rigidbody **non-kinematic, gravity on, velocity 0**, LockPhysics enabled,
     RestartGuard off. Weapons in game slots 1-3 stay as vanilla holstered weapons (vanilla format).
  4. Build the mapping JSON: `[{slot:4, name:"akms(Clone)27", kind:"Weapon", pose:"…"}, …]`, `selected`, version.
- Save end (`SaveLoadGame` back to `isPlay`): re-pocket everything from the mapping (same frame, before physics moves
  them), then write the mapping with `ES3.Save<string>("Apocapocket.Slots", json, settings)` to the cache **and** the file
  of the current slot (≈1 s after isPlay, the game flush wipes earlier writes — see Apocasaver). Prefer writing into the
  cache before the flush if a reliable hook exists; verify by reading the file back.
- Load (after the game finished spawning saved items; same phase Apocasaver uses): read `Apocapocket.Slots` from the loaded
  slot file (try all path candidates), find each object by name among loaded items, re-pocket it (respecting `Unlocked`;
  overflow → stays in the world at the player's feet). No key → nothing to do (vanilla save or saved without the mod).
- Do **not** register holders in the ES3 reference manager any more; nothing in the save references mod objects.
- Migration from 1.x saves: on load, objects found under the old holders (`Apocapocket_ExtraSlots/Slot 4..6`) or hidden
  non-weapons in game slots → adopt them into the model (or eject if locked). Keep the 1.x ES3 holder registration for
  one release only so such saves still resolve, then drop it.
- Result: mod absent → items simply load where they were saved, next to the player, visible and physical ("fly out").
  Steam Cloud syncs only the save; no other files are needed (keys config and icon cache are local settings/cache).

### 3.8 Disable / eject (R8)
- `Enabled` switched off at runtime, or slot locked (backpack removed/downsized, stable ≥1.5 s in gameplay): eject the
  affected contents — restore visuals/colliders, unparent at `ItemDrop`, give a small outward impulse (≈2 m/s forward-up
  plus random spread) so they visibly fly out, rigidbody dynamic, LockPhysics enabled + `LockPhysics_OFF`, RestartGuard off.
  Weapons in game slots 1-3: stay (they are vanilla weapons). End any Borrow first.
- On mod disable also: hide the extra-slot UI, restore shifted hints, stop suppressing input.

### 3.9 UI (KEEP, small changes)
- Clone slot widgets + card (ExtraSlots.BuildUi) — keep. Show only `Unlocked-3` extra slots.
- Selection outline follows `Selected` (including an empty selected 4-6 slot).
- Icons from 3.6. Game slot widgets: override only while a Borrow hosts a different object.

### 3.10 Config (KEEP 1.5.0)
`[General] Enabled=true`, `[General] RequireBackpack=true`, `[Debug] VerboseLog=false`; keys 4-6 in
`com.denis.apocalypter.apocapocket.keys.cfg` synced with the Controls screen. Everything else hardcoded.

---------------------------------------------------------------------------------------------------------------------------

## 4. Acceptance tests (all must pass before release)

1. Grab a gas can, press 1 → hidden in slot 1 with icon; press 1 → in hand at the same pose. Same for 4/5/6 with a large backpack.
2. Small/medium/large/huge backpack → 4 / 4-5 / 4-6 / 4-6 visible; remove backpack with items in 5-6 → they fly out.
3. Icons: first ever pocket of a can renders once (log); restart game, pocket again → loaded from disk (log), no clone spawned.
4. Slots 1-6 empty, hands empty: F on 6 guns in a row → slots 1,2,3,4,5,6 in that order. Repeat with a weapon drawn in slot 1
   and slot 2 empty → next gun goes to 2, not 4. Slot 2 holding an item → skipped.
5. Press 2 on an empty slot (bare hands), F on a gun → goes to 2 and is drawn. Same with empty slot 5 selected.
6. Weapons in 1-3 and 4-6: press 4,5,6,1,4,4,6 rapidly 20× → every press draws/holsters the right weapon, no lost press, no
   weapon in the wrong slot, log shows no rollback except timeouts.
7. Draw from 5 (1-3 full) and Drop Weapon → gun lies on the ground, slot 5 empty, the parked gun is back in its slot.
   Throw a blast lance drawn from 4 → slot 4 empty, parked gun back.
8. Use a bandage while a 4-6 weapon is drawn → the same weapon comes back.
9. Save with items in 1-6 and one weapon drawn from 5; reload → identical slots. Inspect the .es3: no references to
   `Apocapocket_ExtraSlots`; key `Apocapocket.Slots` present.
10. Remove Apocapocket.dll, load that save → all pocketed non-weapons and 4-6 weapons lie next to the player, visible,
    pick-up-able; weapons 1-3 normal. Put the DLL back, load the save again → items are on the ground (mapping refers to
    world items now? — expected: mod re-pockets them from the key; document the chosen behaviour).
11. Disable the mod in the Mods menu → contents of 4-6 and items in 1-3 fly out; enable → normal.
12. Enter/leave a vehicle and toggle 3rd person 10× with a radio, a cassette and a headlight pocketed → all stay pocketed.
13. Hand-grab a gun, press 3, draw, Drop Weapon → gun lands on the ground (not through it).

## 5. Logging
VerboseLog off: only warnings (rollback, timeout, refused item, eject, invariant repair). VerboseLog on: every Op with phases,
model snapshot after each Op, save/load mapping JSON.

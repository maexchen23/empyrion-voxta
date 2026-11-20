---
name: empyrion-containers
description: "Empyrion cargo: which ship or base the tools work from, container labels, finding items, and moving items between containers and the player's bag or hotbar. Load before looking for, moving or handing out items."
metadata:
  augmentations: empyrion
  tools: empyrion_get_current_ship empyrion_get_structure empyrion_set_active_structure empyrion_find_item empyrion_transfer_items empyrion_get_player_inventory
---

# Containers, items and moves

## Which structure the tools work from

- Containers, find, transfers, inventory moves and crafting work from one **active structure** of the player's:
  - the own ship or base they are aboard;
  - on foot (also inside a POI): the one they were last aboard, if within transfer range (about 1 km);
  - else the nearest own one in range.
- `empyrion_set_active_structure` picks another (e.g. the base under the parked ship), or `auto`. A set one stays until
  `auto`, a playfield change or leaving range.
- Every answer ends with **"Working from: …"**. If something fails, read that line first (wrong structure, none in range).
- POIs and other players' structures are never used.

## What is in reach

- The active structure, the ships docked to it (or, if it is docked, its carrier and everything docked there), and the
  player's own ships and bases within about 1 km, like the game's matter transmitters.
- Ships never dock to bases; they only hover near them. There is no chain docking.
- `empyrion_get_current_ship` shows the tree "Structures in reach"; the containers of other structures in range only with
  `structure=<name>`.
- `empyrion_get_structure` gives the status of any own ship or base on the playfield (tanks, power, shield, what is docked).

## Labels

- A container label is `<container> @ <structure>`, e.g. `H Ship Ammo @ Test Hauler`. The part before `@` alone is
  enough when it is unique. Unnamed containers are numbered by kind: `ammo box 1`, `cargo box 3`, `harvest box 1`, `fridge 1`.
- Words like "ammo box", "cargo", "fridge" mean all containers of that kind; a move target must be exactly one container.
- When you speak, drop the `@ structure` part unless several structures matter, and the player's sorting prefix (a
  single letter like `H `): say "Ship Ammo" for `H Ship Ammo`.

## Finding

- `empyrion_find_item` searches the bag, the toolbar and every container in reach in one go, with a total per item. Part
  of a name finds several items ("ingot"). Use it before moving or crafting instead of listing every ship.

## Moving

- `empyrion_transfer_items` moves items between containers in reach and the player (`player` or `bag`; `toolbar` for
  the hotbar). Only when the player asks; the player confirms each move unless they allowed it permanently.
- At most 10,000 items per move. What doesn't fit stays where it was; nothing is lost. Say what moved.
- Given items go into the bag (merged into existing stacks first). Items given to the toolbar are appended in order: to
  set up the hotbar give them one by one in the wanted order; a specific slot can't be picked.
- Bag ↔ toolbar directly isn't possible: go through a container. Giving stops at the server's volume limit (default
  1000 L). Taking rewrites the inventory: ask the player not to shuffle items meanwhile.
- A container whose inventory window is open in the game can't be used; ask the player to close it and try again.
- Weapons, tools and food keep their state (loaded rounds, wear, spoilage) and move as whole items, one per slot.

## Tanks

- Each ship's fuel, oxygen and pentaxid tank can be a move target or source by name: `Fuel Tank`, `Oxygen Tank`,
  `Pentaxid Tank` (with `@ <structure>` if several ships are in reach). Only when named; tanks never show up in
  finding, and moves without a source never take from them.
- Filling: move the item into the tank, e.g. 20 Fuel Cells → `Fuel Tank`. The tank takes what it accepts (fuel: fuel
  cells and bio fuel; oxygen: oxygen bottles; pentaxid: Refined Pentaxid) and only as much as fits; the rest stays.
- Taking out works like the game: only as the item the game gives back (Fuel Cell, Oxygen Bottle, Refined Pentaxid),
  e.g. 5 Fuel Cell from `Fuel Tank` → a cargo box. Fuel cell count = tank content ÷ 50 (oxygen ÷ 250, pentaxid 1 each).
- Tank levels: `empyrion_get_structure` or `empyrion_get_current_ship`.

## "Give me 10 mags"

- `empyrion_search_items` shows the magazine with the ammo a weapon takes: "magazine 30" means 10 mags = 300 rounds;
  "1 per magazine, 250 shots" (energy weapons) means one charge refills it, so 10 mags = 10 charges.

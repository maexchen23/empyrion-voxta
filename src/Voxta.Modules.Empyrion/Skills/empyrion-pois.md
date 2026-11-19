---
name: empyrion-pois
description: "Points of interest on the current planet or sector: classes, danger, factions and standing, complexes, and the 'Nearby' line. Load before talking about what is around, where to go, or whether a place is dangerous."
metadata:
  augmentations: empyrion
  tools: empyrion_scan_pois empyrion_get_structure empyrion_poi_trade empyrion_poi_terminal
---

# Points of interest

- `empyrion_scan_pois` lists the POIs the player knows of (discovered, or within 2 km), closest first: class, faction,
  the player's standing, turrets, traders. `scope` all covers the whole playfield.
- The live game state has a **Nearby:** line with the closest trading places, bases and dungeons, and the player's own
  ships and bases within 2 km (main ships only; docked ones count with their carrier). It refreshes every few seconds.

## Classes

| Class | Means |
|---|---|
| trading | traders inside; safe when the faction is Neutral or better |
| major faction base | many turrets: keep big ships away when hostile |
| small faction outpost | a few turrets, beatable with a small ship |
| abandoned dungeon | looted on foot, enemies inside, often an info terminal at the entrance |
| wreck/salvage | usually harmless loot |
| civilian | unarmed |

- Buildings of one faction close together are one **complex** with summed turrets; its trading places are named.
- Unfriendly or Hated standing means the turrets fire.
- Answer briefly: the few POIs that matter for what the player is doing, not the whole list.

## Info terminals

- The scan marks POIs with an info terminal (`info terminal 'Reactor Control'`). `empyrion_poi_terminal` reads its
  screens: lore, warnings, what is inside, codes. Only for POIs the player is at (within 2 km) or has visited, like in
  the game.
- Retell what matters for the player (a danger, a hint, what to do), in character; don't read every screen out.

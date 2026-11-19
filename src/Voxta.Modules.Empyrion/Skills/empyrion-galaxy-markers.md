---
name: empyrion-galaxy-markers
description: "Marking star systems on the player's galaxy map: temporary Voxta markers, warp routes, cleanup, and permanent markers the player keeps. Load before marking, clearing or naming systems on the map."
metadata:
  augmentations: empyrion
  tools: empyrion_mark_stars empyrion_clear_marks empyrion_add_map_marker
---

# Galaxy map markers

- `empyrion_mark_stars`: star names as on the map, comma separated. `route` yes numbers them 1, 2, 3 in warp order.
- Your markers end in `(Voxta)`. New ones replace your earlier ones unless `keep` is yes. The player's own markers are
  never touched.
- They show on the galaxy map at once, on the HUD only after the player's next login (until then the HUD shows your
  previous markers).
- To warp, the player clicks the marked star on the galaxy map and locks it as target; you can't warp or lock targets.
- A reached system's marker is renamed `done: …`; done and unused markers disappear when the player logs off.
  `empyrion_clear_marks` removes all of yours (at logoff).
- A marker the player wants to keep (e.g. "Water Outpost"): `empyrion_add_map_marker` with a name and `hud` yes or no. You
  never move or remove it; the player edits or deletes it in the game.

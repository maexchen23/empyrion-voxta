---
name: empyrion-crafting
description: "Crafting for the player from the VC container with the game's recipes, and 'make sure I have N of X'. Load before planning or crafting anything, or when the player wants a number of an item."
metadata:
  augmentations: empyrion
  tools: empyrion_plan_craft empyrion_craft empyrion_search_items empyrion_find_item empyrion_transfer_items
---

# Crafting

- You craft instantly from the container whose name ends with `VC` (e.g. `A Production VC`): on the active structure's
  docking group first, else on one of the player's ships or bases within transfer range (also on foot or inside a POI,
  like the game's matter transmitter). The closest one wins; if two are equally close, pass `structure`.
- It uses the game's own recipes incl. intermediates (ore → ingots → plates → ammo where the recipes allow). Inputs come
  only from the VC container; results and leftover intermediates go back into it.
- Item names differ from what people say ("12.7mm Ammo" is "12.7mm Rounds"): check with `empyrion_search_items`; when
  crafting says "Did you mean …", retry with that exact name.

## Steps

1. `empyrion_plan_craft` with the real amount (one craft often makes several: 5 Iron Ingot → 10 Steel Plates; don't
   multiply a plan for 1).
2. Tell the player what it uses, makes and leaves over, or what is missing (they put missing materials into the VC).
3. Only after they agree: `empyrion_craft`.
4. Offer to move the results where they belong with `empyrion_transfer_items`.

## "Make sure I have 500 rifle bullets"

1. `empyrion_search_items` for the weapon's ammo (and magazine size, for "N mags").
2. `empyrion_find_item`: what the player carries and what is in reach.
3. Move existing ones to the player with `empyrion_transfer_items`.
4. Plan crafting only for the rest, ask, craft, then move the result to the player too.

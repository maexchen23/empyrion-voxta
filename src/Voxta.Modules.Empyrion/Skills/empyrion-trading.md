---
name: empyrion-trading
description: "Where the player can buy an item, what a trading POI sells or buys, price tiers, and which known planets or unexplored stars could have the right trader. Load before answering where to buy or sell something."
metadata:
  augmentations: empyrion
  tools: empyrion_where_to_buy empyrion_poi_trade empyrion_system_info empyrion_mark_stars
---

# Buying and selling

## Where to buy an item: `empyrion_where_to_buy`

The answer comes in steps, use them in this order:

1. **Known sellers**: stations the player stood on or traded at, closest first, with light years.
2. **Traders that sell it** and the kinds of places they spawn (POI names, planet/moon types, space), with faction.
3. **Known planets** whose type can have such a trader (systems the player or their faction discovered): a possibility
   from the planet type, not a sighting.
4. **Unexplored stars** where such a trader is likely, from star class and territory only (very likely, likely, maybe).

- Places of factions the player stands below Neutral with are left out.
- What a trader *can* have; stock is not known, so never give amounts.
- Offer to mark the suggested stars on the galaxy map (`empyrion_mark_stars`).

## What a POI trades: `empyrion_poi_trade`

- For a trading POI the player discovered; for an unknown one only what such a place usually trades.
- Items are grouped by price against their normal market price: below, about, above, far above market. Say "cheap here"
  or "overpriced", not numbers. Exact credits (`detail` yes) only for places the player has visited, and only when asked.
- Everyday goods (food, ingots, steel plates, building blocks) are only counted; `all` yes lists them.
- Name the few items that fit what the player needs, not the whole list.

## Systems and planets: `empyrion_system_info`

- Star class, planets, moons and gas giants with their types, for the current system or any system the player or their
  faction discovered (warping to its star is enough); `known` lists them. Undiscovered systems only show star class and
  territory on the galaxy map.

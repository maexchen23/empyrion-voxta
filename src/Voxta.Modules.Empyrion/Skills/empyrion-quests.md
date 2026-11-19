---
name: empyrion-quests
description: "Trader missions: factories that want items before they trade, work orders, follow-up jobs, and the one mission the player pursues. Load when the player talks about a factory's request, a mission, what a trader wanted, or why a factory won't trade."
metadata:
  augmentations: empyrion
  tools: empyrion_list_quests empyrion_pursue_quest empyrion_where_to_buy empyrion_find_item empyrion_transfer_items
---

# Trader missions

- Many specialised factories (and some shops) don't trade until the player does a job for their trader: bring a fixed
  amount of one or more items. The game doesn't track these missions in the PDA; the character remembers them for the
  player with `empyrion_list_quests` (read from the game's save, so every trader the player has talked to is there).
- Unlocks are **per factory**: another factory of the same kind elsewhere starts locked, with its own random mission.
- The items must be in the player's **personal inventory** when they talk to the trader. Containers don't count.
- **Work order**: instead of the items, the player can buy a work order at a **GIN console** (on most trading stations
  and factories) and hand that in. It costs credits and gives no credit or reputation reward.
- Once a factory trades, its trader may offer **optional follow-up missions** ("Offer assistance") for money and
  reputation. They are not needed for trading.

## Pursuing one

- When the player wants to work on one, set it with `empyrion_pursue_quest`. Its line then stays in the live game state
  ("Pursuing: …") and you hear when it's done. One at a time; `none` clears it.
- The pursued line shows how many of the items the player already carries (only the personal inventory counts for the
  hand-in). You are told when everything is there; remind them to talk to the trader then.
- New and newly pursued missions come with what is already in reach (inventory, ship, own structures nearby). If it's
  in a container, offer to move it into the player's inventory with empyrion_transfer_items.
- For the items, `empyrion_find_item` shows what is already in reach and `empyrion_where_to_buy` where to get the rest.
  Where-to-buy marks factories whose trades are still locked for the player; prefer one that trades already.
- When the player gets a new mission or hands one in, you are told (spoken or silently, per the module settings).
  New: say what it wants in a sentence and offer to keep track of it; don't push. Done: a short well done, and
  mention when the factory trades now.
- Keep it short: name the factory, what it wants and the reward or work-order price; details only when asked.

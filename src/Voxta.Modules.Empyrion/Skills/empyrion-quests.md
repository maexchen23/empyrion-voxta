---
name: empyrion-quests
description: "Trader missions (factories that want items before they trade, work orders, follow-up jobs, the one mission the player pursues) and mining contracts from Orbital Refineries. Load when the player talks about a factory's request, a mission, what a trader wanted, why a factory won't trade, or mining contracts."
metadata:
  augmentations: empyrion
  tools: empyrion_list_quests empyrion_pursue_quest empyrion_mining_contracts empyrion_where_to_buy empyrion_find_item empyrion_transfer_items
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

## Mining contracts

- Polaris **Orbital Refineries** hand out mining contracts: a token ("Mining Contract: Iron") that a faction pays for
  when the player delivers an amount of one ore. `empyrion_mining_contracts` knows every contract (which faction wants
  how much of which ore, the credits and reputation): use it for "does anyone want magnesium?" or "who pays most for
  titanium?" instead of guessing.
- "Can I turn in a contract?": call it with `mine` = yes. It lists the player's contract tokens (inventory or containers
  in reach) and how much of the wanted ore is in reach, and what is still missing.
- Hand-in: the **contract token must be in the player's inventory** (it unlocks the dialogue option), the **ore goes
  into the refinery's quest container**, then the player talks to the refinery. Offer to move a token from a container
  into the inventory with empyrion_transfer_items.
- Pay per ore is the same for every faction; factions ask for different amounts. Every contract gives reputation with
  its faction, also with enemy factions (e.g. the Zirax), which is a way to mend relations.
- A refinery gives each player one contract every 2 hours of game time, a random one; once the player is Honored with
  Polaris, they can choose the faction.
- When the player takes a contract or hands one in, you may be told (silently by default, per the module settings).
  On a new one, the check with `mine` = yes shows what is still missing; on a hand-in, a short well done is enough.

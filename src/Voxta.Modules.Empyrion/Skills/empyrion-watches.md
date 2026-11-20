---
name: empyrion-watches
description: "Watches ('tell me when …'): being notified when the player boards or leaves a ship, takes or leaves the pilot seat, a ship docks, undocks, lands or lifts off, or a signal or device changes; the fields and how to act when one fires. Load before adding, listing or removing a watch, or when a [WATCH #n fired] message comes in."
metadata:
  augmentations: empyrion
  tools: empyrion_watch empyrion_ship_control empyrion_find_ship_controls
---

# Watches

- A watch makes the game tell you when something changes; then you decide what to do. Set one when the player asks
  ("when I leave the miner docked, power it down") or agrees to your suggestion. It stays until removed, also in later
  chats, for this game server and player.
- `empyrion_watch` `add`: `conditions` = `field=value` pairs separated by `;`, **all** must match (no OR: add a second
  watch), plus a `note`: why, and what to do when it fires. `list` shows them with ids, `remove` takes an id or `all`.
- Events (`event=`): `seat_taken`, `seat_left` (pilot seat), `entered`, `left` (a ship or base; walking from one onto
  another is `left` + `entered`), `docked`, `undocked`, `landed`, `lifted_off` (landing gear), `signal`, `device` (a named
  signal or device goes on or off; needs `name=`), `new_structure` (a ship or base spawned from a blueprint),
  `starter_placed` (a starter block for a new base or vessel).
- Fields to match: `structure` (the ship or base the event is about, by name; for signal/device default the player's),
  `type` (`ba`, `cv`, `sv`, `hv`),
  `seat` (`pilot`, `aboard`, `on_foot`, after the event), `where` (`space`, `planet`), `playfield`, `landed` and
  `docked` (`true`/`false`, of that ship), `docked_to`, `other_structure` (the carrier for docked/undocked), `name`,
  `state` (`on`/`off`).
- Examples:
  - Miner: `event=left; structure=Miner; docked=true` note "power the miner down"; and
    `event=seat_taken; structure=Miner` note "power the miner up".
  - Parking: `event=seat_left; structure=Zeus` note "parking mode: POWERSAVER on, Production off, only if landed or in
    space; undo when the player takes the seat again".
- When `[WATCH #n fired]` comes in: it shows your note and the full state. Do what the note says if it still fits the
  state and the chat (e.g. one `empyrion_ship_control` call), then say in a few words what you did. Don't ask again for
  what the player already asked for when setting the watch.
- Ship names are compared without case. A field the event doesn't have never matches (e.g. `landed` when the game
  version can't read the landing gear).

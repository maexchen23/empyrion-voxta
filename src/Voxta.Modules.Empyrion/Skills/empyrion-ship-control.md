---
name: empyrion-ship-control
description: "Switching things on the player's own ships and bases: main power, shield, signals and named devices; finding their names; what can't be switched. Load before switching anything on a ship or base."
metadata:
  augmentations: empyrion
  tools: empyrion_find_ship_controls empyrion_ship_control
---

# Ship control

- `empyrion_ship_control` switches main power, the shield, signals and named devices or device groups on one own ship
  or base. Default: the active structure, like containers and transfers (skill empyrion-containers); name another in
  reach, e.g. a docked miner. Answers end with "Working from: …". Only when the player asks or agreed.
- Several changes in one call, e.g. a parking routine: `POWERSAVER=on; Production=off`. Each line of the answer says
  what happened; say it briefly.
- Names are the player's: signals from the control panel or switches, and the custom names or device groups of devices.
  Devices without a name or group can't be switched; tell the player to name it in the control panel.
- Unsure of a name: `empyrion_find_ship_controls` with a part of it or a block type ('turret', 'light'). It never lists
  everything, so search for what the player means. Unknown names in a change also come back with close matches.
- Shield: switches the shield generator; it needs a short warm-up after switching on, and the ship's own signal logic
  (e.g. a sensor at a teleporter) may switch it back.
- Power off stops everything on that structure, including thrusters: on a planet a ship that isn't landed may drop.
  That's the player's call; mention it if they didn't think of it.
- Ship-specific setups (which signals a "parking mode" means on which ship) come from the player; remember them.

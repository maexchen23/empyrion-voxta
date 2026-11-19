---
name: empyrion-micro-jump
description: "Micro jumping the ship the player flies: requirements, aiming vs. distance, Pentaxid cost from the VJ container, cooldown, and the plan-then-jump steps. Load before planning or doing a micro jump."
metadata:
  augmentations: empyrion
  tools: empyrion_plan_microjump empyrion_microjump
---

# Micro jump

- Needs: the player in the pilot seat, in space, not docked, a Prototype Micro Jump Drive on board.
- Either a distance straight ahead (`distance_km`, up to 25 km), or to what the ship's nose points at right now (an
  object, a HUD marker, a planet or a moon); an aimed jump stops a safe distance short of the target.
- Cost: 1 Refined Pentaxid per started 5 km (at least 1; the plan shows the real cost), from the one container whose
  name ends in `VJ` (the player fills it; refused if there is none or too little).
- The drive cools down for about a minute. It refuses while the ship is under attack (the error says how long).
- Docked ships come along.

## Steps

1. `empyrion_plan_microjump`; tell the player where it goes and what it costs.
2. Only after they agree: `empyrion_microjump` (for an aimed jump pass the target name from the plan).
3. If nothing is ahead, ask the player to point the ship at the target, or offer a distance.

#!/usr/bin/env python3
"""Builds docs/memory-book/empyrion-voxta.book.json (Voxta memory book, Voxta JSON format).
Facts only since 1.0.0 (2026-11-19): the how-tos moved into the module skills (src/Voxta.Modules.Empyrion/Skills/*.md).
Ids are stable (uuid5), so a re-import replaces entries instead of duplicating them."""
import json, uuid, datetime, pathlib

NS = uuid.UUID("6f1c5c1e-7a1e-4b8e-9a51-2f0e0c0e5a11")
BOOK_ID = str(uuid.uuid5(NS, "book"))
VERSION = "1.0.2"
NOW = datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="milliseconds")

entries = [
    ('overview', ['Empyrion', 'game', 'VoxtaBridge', 'integration', 'tools', 'what can you do'],
     "You follow the player's Empyrion: Galactic Survival game live through the VoxtaBridge server mod. You can look at the player's location, status, inventory, their ships and bases with all containers, the points of interest around, traders and the galaxy map; react to game events; and, when the player asks, move items, craft, mark stars and micro jump the ship. You cannot steer, fight or build for them. The empyrion-* skills explain each of these: load the one that fits before using its tools."),
    ('live-context', ['status', 'game state', 'what do you know', 'summary', 'vitals'],
     "You always have a short live summary of the game: playfield and solar system (and whether it is a PvP zone), the player's health, suit oxygen and food in percent, the ship or base they are on with its fuel, oxygen and pentaxid tanks, and what the factory is building. For details use the empyrion tools: location, player status, inventory, current ship."),
    ('items', ['item', 'item name', 'description', 'what is', 'Detector'],
     "Item names are the game's own, from the active scenario (e.g. Reforged Eden 2): 'Detector', not the internal 'RadarSuitT1'. When you ask the inventory tool about a specific item with a filter, it also returns the in-game description; with a broad filter you only get names, so ask about one item at a time."),
    ('tokens', ['token', 'keycard', 'guide', 'trading card', 'Empyriopedia'],
     "Tokens are special items like keycards, guides and trading cards. They show their real names, e.g. 'Reforged Eden 2 Gameplay Guide' or 'Trading Card - Akua', and their description. You can move tokens like other items; they keep their identity."),
    ('events', ['warning', 'event', 'low oxygen', 'arrived', 'fuel', 'react'],
     'You speak up on your own (at most every 30 seconds) about game events the player switched on in the module settings: by default arriving on another playfield (warning about PvP zones), health, suit oxygen and food dropping to warning, low or empty levels (health empty means the player died), ship fuel and the ship oxygen tank running out (then the player breathes suit oxygen), the shield going down, a new ship or base spawned from a blueprint or a starter block placed, new or finished factory missions and a factory blueprint being ready. Others, like entering or leaving a ship or base and taking or leaving a pilot seat, are passed to you silently. Only changes are reported.'),
    ('structure-types', ['BA', 'CV', 'SV', 'HV', 'base', 'capital vessel', 'small vessel', 'hover vessel', 'ship types', 'docking'],
     "Empyrion has four kinds of structures. BA: a base; it doesn't move, it stands on a planet or floats in space. CV: a capital vessel; large, used mostly in space and to travel between planets and systems (most of its heavy weapons and its drills can't fire in an atmosphere); it can be a combat ship, mobile base, carrier, space miner or a mix. SV: a small vessel; usually used on planets, to mine surface deposits or attack planetary POIs. HV: a hover vessel; a ground vehicle that hovers close to the surface, e.g. a miner or a heavy tank against planetary POIs; in space it can barely move. Ships dock to other ships or to bases, usually to one of the same size or larger (SVs and HVs to a CV or a base, a CV to a CV), but only one level deep: nothing docks to a ship that is itself docked, so a CV parks next to a base instead of docking (it still counts as in reach). A starter block creates a new structure of one of these types."),
    ('limits', ["can't", 'cannot', 'fly', 'warp', 'PDA', 'mission', 'limit'],
     "You can't steer ships (only micro jump them, skill empyrion-micro-jump), warp or lock warp targets (you can only mark stars on the galaxy map, skill empyrion-galaxy-markers), fight, or open the PDA and mission texts. You only see live data from the playfield the player is on; ships parked elsewhere are out of sight. Trader stock and prices aren't visible live."),
    ('galaxy-hints', ['star class', 'G class', 'F class', 'A class', 'M class', 'neutral territory', 'where to find', 'research vessel', 'casino'],
     'Hints from the Reforged Eden PDA: G class star systems are the most hospitable (temperate, desert and snow planets), F class the second most; A class stars may have more hot planets. Dead class planets are more common around D and K stars. UCH Research Vessels are in the starting systems or in some G, F and A systems in neutral territory (blue triangle on the system map); Syndicate Casinos are mostly around G, F and A stars in neutral or pirate territory (green station). The galaxy map can search by star type (G, G2, …) and by name; territories can be shown on it.'),
]

pinned = {"say-labels"}
items = []
for key, keywords, text in entries:
    items.append({
        "Id": str(uuid.uuid5(NS, key)),
        "MemoryBookId": BOOK_ID,
        "Keywords": keywords,
        "Text": text,
        "Weight": 0,
        "CreatedAt": NOW,
        "Pinned": key in pinned,
        "Validated": False,
        "Explicit": False,
    })

book = {
    "$type": "book",
    "MemoryBook": {
        "PackageId": str(uuid.uuid5(NS, "package")),
        "Description": "Empyrion: Galactic Survival integration (VoxtaBridge)",
        "ExplicitContent": False,
        "RequiresValidation": False,
        "Creator": "Maexchen",
        "Name": "Empyrion (VoxtaBridge)",
        "Readme": "How the Voxta <-> Empyrion integration behaves, so the character uses the empyrion tools well and talks "
                  "about containers naturally. Generated by tools/make-memory-book.py.",
        "AppControlled": False,
        "Locked": False,
        "Version": VERSION,
        "DateCreated": NOW,
        "DateModified": NOW,
        "Id": BOOK_ID,
    },
    "Items": items,
    "Subjects": [],
    "Name": "Empyrion (VoxtaBridge)",
    "Version": VERSION,
    "DateCreated": NOW,
    "DateModified": NOW,
    "Id": BOOK_ID,
}

out = pathlib.Path(__file__).resolve().parent.parent / "docs" / "memory-book" / "empyrion-voxta.book.json"
out.write_text(json.dumps(book, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
longest = max(len(t) for _, _, t in entries)
print(f"{out}: {len(items)} entries, longest {longest} chars")

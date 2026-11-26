using Voxta.Modules.Empyrion.Configuration;

namespace Voxta.Modules.Empyrion.Bridge;

/// <summary>Something that happened between two polls.</summary>
/// <param name="Name">Short event id, e.g. <c>LowOxygen</c>.</param>
/// <param name="Description">What happened, written for the character.</param>
/// <param name="Important">True: the character should react out loud. False: it only needs to know.</param>
/// <param name="MissionKey">For new-mission events: the mission, so the description can be completed (what is in reach).</param>
/// <param name="Id">The settings id (e.g. <c>fuel_low</c>) the hidden watches match on.</param>
public sealed record GameEvent(string Name, string Description, bool Important, string? MissionKey = null, string? Id = null);

/// <summary>
/// Smooths over two gaps in the game's "which ship is the player in":
/// <list type="bullet">
/// <item>A warp: on the new playfield the player is briefly on no structure until the ship arrives there too, which
/// would read as "left the ship, now on foot" and then "boarded the ship". Only then (ship gone and the playfield
/// changed) the last ship state is kept, for at most <see cref="HoldFor"/>.</item>
/// <item>Leaving the pilot seat: the game reports no structure for a moment, and the bridge falls back to "standing on"
/// the ship. Standing on (or in) the ship the player was aboard counts as still aboard; leaving it (standing on nothing
/// or on something else) is the exit. So getting out of the seat is no event, and being thrown out of a moving ship
/// becomes "left" once it has moved away.</item>
/// </list>
/// On the same playfield a missing ship is a real exit and shows at once.
/// </summary>
public sealed class WarpSmoother
{
    public static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(60);

    private GameState? _lastInShip;
    private DateTimeOffset? _lostAt;

    /// <summary>The state to show and diff: <paramref name="raw"/>, or it with the held ship while a warp may be running.</summary>
    public GameState? Apply(GameState? raw, DateTimeOffset now)
    {
        if (raw == null) { Reset(); return null; }
        if (raw.InShip) { _lastInShip = raw; _lostAt = null; return raw; }
        if (_lastInShip == null) return raw;
        if (raw.Standing && raw.ShipName != null && raw.ShipName == _lastInShip.ShipName && raw.Playfield == _lastInShip.Playfield)
            return raw with { Standing = false, IsPilot = false }; // still on the ship they were aboard: aboard
        if (raw.Playfield == _lastInShip.Playfield) { Reset(); return raw; } // same playfield: really got out
        _lostAt ??= now;
        if (now - _lostAt.Value > HoldFor) { Reset(); return raw; }
        var held = _lastInShip;
        return raw with
        {
            ShipName = held.ShipName, ShipType = held.ShipType, IsPilot = held.IsPilot,
            ShipFuel = held.ShipFuel, ShipFuelMax = held.ShipFuelMax, ShipOxygen = held.ShipOxygen, ShipOxygenMax = held.ShipOxygenMax,
            ShipPentaxid = held.ShipPentaxid, ShipPentaxidMax = held.ShipPentaxidMax, ShieldActive = held.ShieldActive, Powered = held.Powered,
            DockedTo = held.DockedTo, Landed = held.Landed,
        };
    }

    private void Reset() { _lastInShip = null; _lostAt = null; }
}

/// <summary>
/// Compares two polls and names what changed. Only crossings count (a tank that was already empty when
/// the chat started is not an event), so the character is not told the same thing every poll. Every event carries its
/// settings id; <see cref="GameEventConfig.Apply"/> (the hidden watches) decides announced, silent or dropped.
/// </summary>
public static class GameEvents
{
    /// <param name="prev">State of the previous poll; null when the player was offline or this is the first poll.</param>
    /// <param name="next">State now; null when the player is offline.</param>
    /// <param name="firstPoll">True for the first poll of a chat: nothing is reported, the context covers it.</param>
    /// <param name="config">Modes and thresholds from the settings; null = defaults.</param>
    /// <summary>
    /// "Items ready" for the pursued mission: every wanted item is in reach (inventory, the ship and own structures nearby),
    /// counted by the caller. Null while something is missing or when the setting is Off. Hand-ins check the player's
    /// inventory, so the text says what still has to be moved there.
    /// </summary>
    public static GameEvent? ItemsReady(string missionLine, IReadOnlyList<(MissionNeed Need, int InInventory, int InReach)> rows,
        GameEventConfig? config = null)
    {
        config ??= GameEventConfig.Default;
        var mode = config.ModeOf("pursued_items_ready");
        if (mode == GameEventSettings.Off || rows.Count == 0 || rows.Any(r => r.InReach < r.Need.Count)) return null;
        static string N(int v) => v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        var toMove = rows.Where(r => r.InInventory < r.Need.Count).ToList();
        var text = $"Everything the pursued mission wants is in reach now: {missionLine}.";
        text += toMove.Count == 0
            ? " All of it is in the player's inventory: ready to hand in."
            : " To hand it in, the items must be in the player's inventory; still to move there: " +
              string.Join(", ", toMove.Select(r => $"{N(r.Need.Count - r.InInventory)} {r.Need.Name}")) +
              (mode == GameEventSettings.On ? ". Offer to move them (empyrion_transfer_items)." : ".");
        return new GameEvent("PursuedItemsReady", text, mode == GameEventSettings.On, Id: "pursued_items_ready");
    }

    public static List<GameEvent> Diff(GameState? prev, GameState? next, bool firstPoll = false, GameEventConfig? config = null)
    {
        config ??= GameEventConfig.Default;
        return config.Apply(Raw(prev, next, firstPoll, config));
    }

    private static List<GameEvent> Raw(GameState? prev, GameState? next, bool firstPoll, GameEventConfig config)
    {
        var events = new List<GameEvent>();
        if (firstPoll) return events;

        if (prev == null && next != null)
        {
            events.Add(new("PlayerJoined", $"{next.Player} joined the game, on {next.Playfield}.", true, Id: "player_joined"));
            return events;
        }
        if (prev != null && next == null)
        {
            events.Add(new("PlayerLeft", $"{prev.Player} left the game.", false, Id: "player_left"));
            return events;
        }
        if (prev == null || next == null) return events;

        if (prev.Playfield != next.Playfield && next.Playfield != null)
        {
            var warped = prev.InShip && next.InShip && prev.ShipName == next.ShipName;
            var desc = warped ? $"Warped aboard the {next.ShipType} '{next.ShipName}' to {next.Playfield}" : $"Arrived at {next.Playfield}";
            if (next.PlayfieldType != null) desc += $" ({next.PlayfieldType})";
            if (next.System != null && next.System != prev.System) desc += $" in the {next.System} system";
            desc += $", coming from {prev.Playfield}.";
            if (next.Pvp == true && prev.Pvp != true) desc += " This is a PvP zone: other players can attack.";
            events.Add(new("PlayfieldChanged", desc, true, Id: "playfield_changed"));
        }

        var sameShip = prev.InShip && next.InShip && prev.ShipName == next.ShipName;
        if (prev.InShip && !sameShip)
            events.Add(new("LeftStructure", $"Left the {TypeWord(prev.ShipType)} '{prev.ShipName}'{(next.InShip ? "" : ", now on foot")}.", false, Id: "left_structure"));
        if (next.InShip && !sameShip)
            events.Add(new("EnteredStructure", $"Entered the {TypeWord(next.ShipType)} '{next.ShipName}'{(next.IsPilot ? " and took the controls" : "")}.", false, Id: "entered_structure"));
        if (sameShip && !prev.IsPilot && next.IsPilot)
            events.Add(new("TookControls", $"Took the controls of the {TypeWord(next.ShipType)} '{next.ShipName}'.", false, Id: "took_controls"));
        if (sameShip && prev.IsPilot && !next.IsPilot)
            events.Add(new("LeftPilotSeat", $"Left the pilot seat of the {TypeWord(next.ShipType)} '{next.ShipName}', still aboard.", false, Id: "left_pilot_seat"));

        Level(events, config, "health", "health", null, prev.Health, prev.HealthMax, next.Health, next.HealthMax, next.Player);
        Level(events, config, "suit_oxygen", "suit oxygen", null, prev.Oxygen, prev.OxygenMax, next.Oxygen, next.OxygenMax, next.Player);
        Level(events, config, "food", "food", null, prev.Food, prev.FoodMax, next.Food, next.FoodMax, next.Player);

        // Ship tanks and shield: only while the player stays on the same ship.
        if (sameShip)
        {
            Level(events, config, "fuel", "fuel tank", next.ShipName, prev.ShipFuel, prev.ShipFuelMax, next.ShipFuel, next.ShipFuelMax, next.Player);
            Level(events, config, "ship_oxygen", "oxygen tank", next.ShipName, prev.ShipOxygen, prev.ShipOxygenMax, next.ShipOxygen, next.ShipOxygenMax, next.Player);
            Level(events, config, "pentaxid", "pentaxid tank", next.ShipName, prev.ShipPentaxid, prev.ShipPentaxidMax, next.ShipPentaxid, next.ShipPentaxidMax, next.Player);

            // the game drops a docked ship's shield: not worth a warning
            if (prev.ShieldActive == true && next.ShieldActive == false && next.DockedTo == null)
                events.Add(new("ShieldDown", $"The shield of '{next.ShipName}' went down.", true, Id: "shield_down"));
        }

        if (prev.PursuingLine != null && prev.PursuingStatus is "open" or "locked" && next.PursuingStatus == "done")
            events.Add(new("PursuedMissionDone", $"The pursued trader mission is done: {next.PursuingLine}.", true, Id: "pursued_mission_done"));

        // new trader missions: only against a known earlier list (the first list of a chat is the baseline)
        if (prev.OpenMissions is { } before && next.OpenMissions is { } now)
        {
            var known = before.Select(m => m.Key).ToHashSet();
            foreach (var m in now.Where(m => !known.Contains(m.Key)))
            {
                var factory = m.Kind == "factory";
                var id = factory ? "new_factory_mission" : "new_trader_mission";
                var speak = config.ModeOf(id) == GameEventSettings.On;
                events.Add(new(factory ? "NewFactoryMission" : "NewTraderMission",
                    $"The player just got a new {(factory ? "factory" : "trader")} mission: {m.Line}." +
                    (speak ? " Mention it briefly; empyrion_pursue_quest keeps track of it if they want." : ""), speak, m.Key, id));
            }
            // gone from the open list = handed in (missions can't be dropped); the pursued one has its own event
            var open = now.Select(m => m.Key).ToHashSet();
            foreach (var m in before.Where(m => !open.Contains(m.Key) && !m.Pursued))
            {
                var factory = m.Kind == "factory";
                var where = m.Where ?? m.Line;
                var desc = factory
                    ? $"The player completed the factory mission for {where}{(m.FollowUp ? "" : "; its trades are unlocked now")}."
                    : $"The player completed the job for {where}.";
                events.Add(new(factory ? "FactoryMissionDone" : "TraderMissionDone", desc, true,
                    Id: factory ? "factory_mission_done" : "trader_mission_done"));
            }
        }

        // Delivery jobs (mining sites): a job the player hasn't done shows up on the playfield they arrived at. Framed as
        // a broadcast on the local channel, so the character may bring it up before the player talked to the NPC.
        if (prev.SiteJobs is { } jobsBefore && next.SiteJobs is { } jobsNow)
            foreach (var job in jobsNow.Where(j => !j.Done && jobsBefore.All(b => b.Key != j.Key)))
            {
                var speak = config.ModeOf("mining_site_job") == GameEventSettings.On;
                events.Add(new("MiningSiteJob",
                    $"On the local channel, the {job.Where} is broadcasting a job: they want {job.NeedsText} in their '{job.Container}' " +
                    $"container, for {job.RewardText}{(job.Rep != null ? $" (they only deal with {job.Rep} standing or better)" : "")}." +
                    (speak ? " Tell the player about it like a broadcast you just picked up." : ""),
                    speak, "site:" + job.Key, "mining_site_job"));
            }

        // Mining contracts. New: a refinery's offer row for the player changed (it changes with every contract given; moving
        // the token between the inventory and containers doesn't touch it). Handed in: a contract token left the inventory
        // and the credits rose by at least its pay (moving it into a container doesn't pay).
        if (prev.ContractOffers is { } offersBefore && next.ContractOffers is { } offersNow)
        {
            var was = offersBefore.ToDictionary(o => o.Key, o => o.Value);
            foreach (var o in offersNow.Where(o => !was.TryGetValue(o.Key, out var v) || v != o.Value))
            {
                var gained = next.HeldContracts?.FirstOrDefault(h => h.Count > (prev.HeldContracts?.FirstOrDefault(p => p.Token == h.Token)?.Count ?? 0));
                var what = gained is null ? "a mining contract" : $"{gained.Name} for {gained.Faction}: {gained.Wants} for {gained.Credits.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} credits";
                var speak = config.ModeOf("new_mining_contract") == GameEventSettings.On;
                events.Add(new("NewMiningContract",
                    $"The player just took {what}{(o.Where is { } w ? $" at {w}" : "")}." +
                    (speak ? " Mention it briefly; empyrion_mining_contracts with mine=yes shows how much of the ore is in reach." : ""),
                    speak, Id: "new_mining_contract"));
            }
        }
        if (prev.HeldContracts is { } heldBefore && next.HeldContracts is { } heldNow && prev.Credits is { } c0 && next.Credits is { } c1)
            foreach (var h in heldBefore)
            {
                var left = h.Count - (heldNow.FirstOrDefault(x => x.Token == h.Token)?.Count ?? 0);
                if (left <= 0 || c1 - c0 < h.Credits) continue;
                events.Add(new("MiningContractDone",
                    $"The player handed in {h.Name} for {h.Faction}: {h.Credits.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} credits and {h.Reputation.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} {h.Faction} reputation.",
                    config.ModeOf("mining_contract_done") == GameEventSettings.On, Id: "mining_contract_done"));
            }

        // new ships and bases: each id once (the bridge lists it for a minute)
        var knownNew = prev.NewStructures.Select(n => n.Id).ToHashSet();
        foreach (var n in next.NewStructures.Where(n => !knownNew.Contains(n.Id)))
        {
            if (n.Kind == "blueprint")
                events.Add(new("NewStructure", $"A new {TypeWord(n.Type)} '{n.Name}' was spawned from a blueprint.", true, Id: "new_structure"));
            else
                events.Add(new("StarterPlaced", $"A starter block for a new {TypeWord(n.Type)} was placed ('{n.Name}').", true, Id: "starter_placed"));
        }

        if (prev.FactoryBlueprint == next.FactoryBlueprint && next.FactoryBlueprint != null
            && prev.FactoryRemainingS is > 0 && next.FactoryRemainingS is <= 0)
            events.Add(new("BlueprintReady", $"The factory finished '{next.FactoryBlueprint}'; it can be spawned now.", true, Id: "blueprint_ready"));

        return events;
    }

    /// <summary>"base", "capital vessel (CV)", … for an entity type BA/CV/SV/HV.</summary>
    public static string TypeWord(string? type) => type switch
    {
        "BA" => "base (BA)",
        "CV" => "capital vessel (CV)",
        "SV" => "small vessel (SV)",
        "HV" => "hover vessel (HV)",
        _ => type ?? "structure",
    };

    /// <summary>
    /// A vital or tank crossing one of its three levels downwards: the most severe enabled level crossed in this poll
    /// fires (empty, then low, then warning); a level re-arms when the value is back above it. Health "empty" is death.
    /// </summary>
    private static void Level(List<GameEvent> events, GameEventConfig config, string resource, string label, string? ship,
        double? pv, double? pmax, double? nv, double? nmax, string? player)
    {
        var before = GameState.Percent(pv, pmax);
        var now = GameState.Percent(nv, nmax);
        if (before is not { } b || now is not { } n) return;
        var t = config.ThresholdsOf(resource);
        foreach (var (level, threshold) in new[] { ("empty", t.Empty), ("low", t.Low), ("warning", t.Warning) })
        {
            var id = $"{resource}_{level}";
            if (!config.Enabled(id) || !(b > threshold && n <= threshold)) continue;
            var what = ship != null ? $"The {label} of '{ship}'" : $"The player's {label}";
            var text = (resource, level) switch
            {
                ("health", "empty") when n <= 0 => $"{player} died.",
                ("ship_oxygen", "empty") => $"The oxygen tank of '{ship}' is empty: the player is breathing from the suit's oxygen now.",
                (_, "empty") => n <= 0 ? $"{what} is empty." : $"Critical: {what} is down to {n:0}%.",
                (_, "low") => $"{what} is low: {n:0}%.",
                _ => $"Warning: {what} is at {n:0}%.",
            };
            events.Add(new(id, text, true, Id: id));
            return;
        }
    }
}

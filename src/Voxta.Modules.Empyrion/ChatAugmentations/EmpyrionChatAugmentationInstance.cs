using System.Text.Json;
using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Abstractions.Services.Agents;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Model.Shared;
using Voxta.Modules.Empyrion.Bridge;

namespace Voxta.Modules.Empyrion.ChatAugmentations;

/// <param name="LiveContext">Keep the game summary in the character's context.</param>
/// <param name="Reactions">Pass game events to the character.</param>
/// <param name="PollInterval">How often the bridge is read.</param>
/// <param name="ReactionCooldown">Minimum time between two spoken reactions.</param>
/// <param name="VcSuffix">Name suffix of the crafting container.</param>
/// <param name="VjSuffix">Name suffix of the container that pays for micro jumps.</param>
public sealed record LiveSettings(bool LiveContext, bool Reactions, TimeSpan PollInterval, TimeSpan ReactionCooldown, string VcSuffix = "VC", string VjSuffix = "VJ");

public sealed class EmpyrionChatAugmentationInstance(
    IChatSessionChatAugmentationApi session,
    BridgeClient client,
    string? steamId,
    ChatStyles[] chatStyles,
    Func<string, bool> isToolSelected,
    LiveSettings live,
    ILogger<EmpyrionChatAugmentationInstance> logger
) : IToolsChatAugmentation
{
    private const string FilterArg = "filter";
    private const string ItemArg = "item";
    private const string AmountArg = "amount";
    private const string ToArg = "to";
    private const string FromArg = "from";
    private const string StructureArg = "structure";
    private const string QueryArg = "query";
    private const string ScopeArg = "scope";
    private const string PoiArg = "poi";
    private const string DetailArg = "detail";
    private const string AllArg = "all";
    private const string SystemArg = "system";
    private const string DistanceArg = "distance_km";
    private const string TargetArg = "target";
    private const string StarsArg = "stars";
    private const string RouteArg = "route";
    private const string KeepArg = "keep";
    private const string StarArg = "star";
    private const string NameArg = "name";
    private const string HudArg = "hud";

    private readonly CancellationTokenSource _stop = new();
    private Task? _poller;

    public ServiceTypes[] GetRequiredServiceTypes() => [];

    public ValueTask<IReadOnlyList<ChatTool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<IReadOnlyList<ChatTool>>(
        [
            ReadTool(EmpyrionConstants.GetLocationTool,
                "Where the player is in Empyrion right now: playfield (planet, moon, space sector), its type, " +
                "solar system, whether PvP is on, the position, and what kinds of entities are around."),
            ReadTool(EmpyrionConstants.GetPlayerStatusTool,
                "The player's own state in Empyrion: health, oxygen, food, credits, inventory size, " +
                "and whether they are on foot or in/piloting a ship."),
            ReadTool(EmpyrionConstants.GetCurrentShipTool,
                "The ship or base the player is in right now: name, type, power, shield, mass, fuel/oxygen/pentaxid " +
                "tanks, every container (incl. docked ships) with its contents, and other own structures in transfer range. " +
                "With 'structure' it lists the containers of that one own structure instead; that also works on foot or inside " +
                "a POI for own ships and bases within transfer range of the player.",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = StructureArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "Name of an own structure in transfer range (as listed by this tool or empyrion_scan_pois) to see its containers.",
                    },
                ]),
            ReadTool(EmpyrionConstants.GetPlayerInventoryTool,
                "What the player carries in Empyrion: toolbar and bag contents with counts, using the game's item names. " +
                "With a filter it also returns what the matching items are (their in-game description).",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = FilterArg,
                        Description = "Only items whose name contains this text, e.g. 'ammo' or 'oxygen'. Leave out for everything.",
                        Type = FunctionArgumentType.String,
                        Required = false,
                    },
                ]),
            ReadTool(EmpyrionConstants.FindItemTool,
                "Where is an item and how much of it: searches the player's bag and toolbar and every container in reach " +
                "(the ship, its carrier and docked ships, own structures in range). Part of a name finds several items.",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = ItemArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "Item name or part of it, e.g. 'Iron Ingot', 'copper' or 'ammo'.",
                    },
                ]),
            ReadTool(EmpyrionConstants.SearchItemsTool,
                "Look up items in the game's item list by name or part of it (not what the player owns; use empyrion_find_item " +
                "for that): their exact game names, whether they can be crafted and in which constructors, which ammo a weapon " +
                "takes and which weapons fire an ammo. Use it to get the exact name before crafting or when unsure.",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = QueryArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "Name or part of it, e.g. '12.7mm', 'minigun' or 'gatling turret'.",
                    },
                ]),
            ReadTool(EmpyrionConstants.WhereToBuyTool,
                "Where can the player buy an item from NPC traders: which traders sell it, in which kinds of places (POI names, " +
                "on which planet/moon types or in which space sectors they spawn, and their faction), plus the sellers the " +
                "player already knows (visited stations or past trades) with the closest first and its distance. Places of " +
                "factions the player is below Neutral with are left out. Stock and prices change in the game; this is what " +
                "the traders can have.",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = ItemArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "Item name or part of it, e.g. 'Reactor Core' or 'pentaxid'.",
                    },
                ]),
            ReadTool(EmpyrionConstants.ScanPoisTool,
                "Points of interest (POIs) on the player's current planet, moon or space sector that the player knows of " +
                "(discovered, or close enough to be seen), closest first, each with its class (trading, major faction base, " +
                "small faction outpost, abandoned dungeon, wreck/salvage, civilian), owning faction and the player's standing " +
                "with it, turrets and traders. Buildings of one faction close together are one complex with summed turrets. " +
                "Turrets are the real danger for ships: a major base or a complex with many turrets can destroy a capital " +
                "vessel; hostile standing (Unfriendly, Hated) means it shoots. Asteroids and decoration are only counted. " +
                "A snapshot of now: call it again after flying somewhere.",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = ScopeArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "'nearby' (default: within 10 km) or 'all' (the whole planet or sector).",
                        Choices = ["nearby", "all"],
                    },
                ]),
            ReadTool(EmpyrionConstants.GetStructureTool,
                "Details of one of the player's (or their faction's) own ships or bases on the current planet or sector, by " +
                "name: type, size, pilot, power, shield, fuel/oxygen/pentaxid tanks, distance, what is docked to it. For the " +
                "ship the player is in, use empyrion_get_current_ship instead.",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = NameArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "Name of the ship or base (or part of it), e.g. as listed by empyrion_scan_pois.",
                    },
                ]),
            MarkTool(EmpyrionConstants.SetActiveStructureTool,
                "Choose which of the player's own ships or bases the container, transfer and crafting tools work from (the " +
                "'active structure'), e.g. the base under the parked ship, or the carrier while the player is inside a POI. " +
                "'auto' goes back to automatic: the one the player is aboard, else the last one they were aboard, else the " +
                "nearest within transfer range. A set one stays until 'auto', it leaves transfer range, or the player changes playfield (always forgotten then). " +
                "Other own structures in range stay reachable for transfers either way.",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = NameArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "Name of the own ship or base (or part of it), or 'auto'.",
                    },
                ]),
            ReadTool(EmpyrionConstants.SystemInfoTool,
                "The star class and the planets, moons and gas giants of a solar system with their types (e.g. Stormy Desert, " +
                "Lava Moon), as the galaxy map shows them: the current system, or any system the player or their faction has " +
                "discovered. 'known' lists all discovered systems. Undiscovered systems only show star class and territory.",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = SystemArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "System or planet name (default: the current system), or 'known' for the list of discovered systems.",
                    },
                ]),
            ReadTool(EmpyrionConstants.PoiTradeTool,
                "What the traders at a trading POI the player knows sell and buy (item names grouped by price against the " +
                "market price), e.g. for 'what can I buy at the Solaris-AMP Factory?'. Everyday goods (food, cheap components, " +
                "building blocks) are only counted unless all is yes. Answer with the few items that matter to the player, not " +
                "the whole list. Exact prices only for places the player has visited; what is in stock is not known.",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = PoiArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "POI name or part of it, e.g. 'Solaris-AMP' or 'Stack-15'.",
                    },
                    new FunctionArgumentDefinition
                    {
                        Name = DetailArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "'yes' adds prices in credits; only when the player asks for them.",
                        Choices = ["yes", "no"],
                    },
                    new FunctionArgumentDefinition
                    {
                        Name = AllArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "'yes' also lists everyday goods (food, ingots, steel plates, building blocks).",
                        Choices = ["yes", "no"],
                    },
                ]),
            MarkTool(EmpyrionConstants.MarkStarsTool,
                "Mark star systems on the player's galaxy map (named markers ending in '(Voxta)'), e.g. the candidate stars from " +
                "empyrion_where_to_buy or a route of several systems in warp order. Replaces earlier Voxta markers unless keep is yes. " +
                "The markers show on the galaxy map right away and on the HUD after the player's next login. Markers of reached " +
                "systems and replaced ones are removed when the player logs off. No need to ask first; tell the player what you marked.",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = StarsArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "Star system names as on the galaxy map, comma separated, in order, e.g. 'Naamumamina, Rubathra, Isomnir'.",
                    },
                    new FunctionArgumentDefinition
                    {
                        Name = RouteArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "'yes' to number the markers 1, 2, 3 … in the given order (a warp route).",
                        Choices = ["yes", "no"],
                    },
                    new FunctionArgumentDefinition
                    {
                        Name = KeepArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "'yes' to keep the Voxta markers that are already on the map.",
                        Choices = ["yes", "no"],
                    },
                ]),
            MarkTool(EmpyrionConstants.ClearMarksTool,
                "Remove all of Voxta's markers from the player's galaxy map (the player's own markers stay). This happens when the " +
                "player logs off, not right away.",
                []),
            MarkTool(EmpyrionConstants.AddMapMarkerTool,
                "Add a marker the player keeps on a star system of the galaxy map, with a name the player chooses (e.g. 'Water Outpost'). " +
                "Unlike empyrion_mark_stars it is never moved or removed by you; the player edits or deletes it in the game. Only when the " +
                "player asks for a lasting marker. It shows on the galaxy map at once and, if shown on the HUD, there after the next login.",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = StarArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "The star system's name as on the galaxy map, e.g. 'Rubathra'.",
                    },
                    new FunctionArgumentDefinition
                    {
                        Name = NameArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "The marker's name, at most 40 characters, e.g. 'Water Outpost'.",
                    },
                    new FunctionArgumentDefinition
                    {
                        Name = HudArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "'no' to show it only on the galaxy map, not on the HUD (default: yes).",
                        Choices = ["yes", "no"],
                    },
                ]),
            ReadTool(EmpyrionConstants.PlanCraftTool,
                $"Plan crafting an item from the materials in the crafting container (the container whose name ends with '{live.VcSuffix}'): " +
                "what it would use, produce and leave over, or what is missing. The container can be on the ship the player is aboard " +
                "or on one of their ships/bases within transfer range (also on foot, like the game's matter transmitter). Changes " +
                "nothing. Tell the player the plan and ask before crafting. For 'make sure I have N of X': check with " +
                "empyrion_find_item, move existing ones with empyrion_transfer_items, plan crafting only for the rest.",
                CraftArgs()),
            new ChatTool(
                EmpyrionConstants.CraftTool,
                $"Craft an item instantly from the materials in the crafting container (name ends with '{live.VcSuffix}'); results and " +
                "leftover intermediates go back into that container. Only after you told the player the plan from " +
                "empyrion_plan_craft and they agreed. Move the results onward with empyrion_transfer_items afterwards if wanted.",
                ChatToolSchema.FromArguments(CraftArgs()),
                EmpyrionConstants.ContextKey)
            {
                AugmentationKey = EmpyrionConstants.AugmentationKey,
                ChatStyles = chatStyles,
                Enabled = isToolSelected(EmpyrionConstants.CraftTool),
                Permission = ToolPermissionClass.Act,
                Display = ToolDisplayMode.Card,
            },
            ReadTool(EmpyrionConstants.PlanMicroJumpTool,
                "Plan a micro jump of the ship the player flies, in space only (not on planets, not docked; needs a Prototype " +
                "Micro Jump Drive on board): either a distance " +
                "straight ahead, or (no distance) to what the ship's nose points at right now: an object, a HUD marker, a planet " +
                "or a moon; the jump stops short of it. Returns the target, jump length and Pentaxid cost, or why it can't jump. " +
                "Changes nothing. Tell the player the plan and ask before jumping.",
                MicroJumpArgs(withTarget: false)),
            new ChatTool(
                EmpyrionConstants.MicroJumpTool,
                "Micro jump the ship the player flies, instantly, as planned with empyrion_plan_microjump. Only after you told the " +
                "player the plan and they agreed. For an aimed jump pass the target name from the plan; the jump is refused if the " +
                $"ship now points elsewhere. Costs Refined Pentaxid from the ship's jump container (name ending in '{live.VjSuffix}'); the drive then cools down for a while.",
                ChatToolSchema.FromArguments(MicroJumpArgs(withTarget: true)),
                EmpyrionConstants.ContextKey)
            {
                AugmentationKey = EmpyrionConstants.AugmentationKey,
                ChatStyles = chatStyles,
                Enabled = isToolSelected(EmpyrionConstants.MicroJumpTool),
                Permission = ToolPermissionClass.Act,
                Display = ToolDisplayMode.Card,
            },
            new ChatTool(
                EmpyrionConstants.TransferItemsTool,
                "Move items between containers in reach (ship, carrier, docked ships, own structures in range) and the player's " +
                "own inventory. Use the container labels from empyrion_get_current_ship; use 'player' (or 'bag' / 'toolbar') for " +
                "the player's inventory. Changes the game, so only do it when the player asks.",
                ChatToolSchema.FromArguments([
                    new FunctionArgumentDefinition
                    {
                        Name = ItemArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "Item name as shown in the containers, e.g. 'Combat Knife' or '45mm Ammo'.",
                    },
                    new FunctionArgumentDefinition
                    {
                        Name = AmountArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "How many, as a number, or 'all'.",
                    },
                    new FunctionArgumentDefinition
                    {
                        Name = ToArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "Target container label as listed by empyrion_get_current_ship, e.g. 'H Ship Ammo @ Test Hauler' " +
                                      "or 'Hold @ Test Corvette', 'player' / 'bag' to give the items to the player, or 'toolbar' to append them to the hotbar " +
                                      "(the game fills it in order). The part before '@' alone works if it is unique. " +
                                      "Must be exactly one container.",
                    },
                    new FunctionArgumentDefinition
                    {
                        Name = FromArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "Source container label, a kind like 'cargo', or 'player' / 'bag' / 'toolbar' for the player's inventory. " +
                                      "Leave out to take from any other container.",
                    },
                ]),
                EmpyrionConstants.ContextKey)
            {
                AugmentationKey = EmpyrionConstants.AugmentationKey,
                ChatStyles = chatStyles,
                Enabled = isToolSelected(EmpyrionConstants.TransferItemsTool),
                Permission = ToolPermissionClass.Act,
                Display = ToolDisplayMode.Card,
            },
        ]);
    }

    private static FunctionArgumentDefinition[] CraftArgs() =>
    [
        new FunctionArgumentDefinition
        {
            Name = ItemArg, Type = FunctionArgumentType.String, Required = true,
            Description = "What to make, by its game name, e.g. '12.7mm Ammo' or 'Steel Plate'.",
        },
        new FunctionArgumentDefinition
        {
            Name = AmountArg, Type = FunctionArgumentType.String, Required = true,
            Description = "How many of the item, as a number.",
        },
        new FunctionArgumentDefinition
        {
            Name = StructureArg, Type = FunctionArgumentType.String, Required = false,
            Description = "Only when several own ships/bases in range have a crafting container: which structure's container to use.",
        },
    ];

    private static FunctionArgumentDefinition[] MicroJumpArgs(bool withTarget) => withTarget
        ?
        [
            new FunctionArgumentDefinition
            {
                Name = DistanceArg, Type = FunctionArgumentType.String, Required = false,
                Description = "Only for a jump straight ahead: how far, in kilometres, e.g. '10'. Leave out for an aimed jump.",
            },
            new FunctionArgumentDefinition
            {
                Name = TargetArg, Type = FunctionArgumentType.String, Required = false,
                Description = "For an aimed jump: the target name from the plan, e.g. 'Akua Moon'.",
            },
        ]
        :
        [
            new FunctionArgumentDefinition
            {
                Name = DistanceArg, Type = FunctionArgumentType.String, Required = false,
                Description = "How far straight ahead, in kilometres, e.g. '10'. Leave out to jump to what the ship points at.",
            },
        ];

    private ChatTool ReadTool(string name, string description, FunctionArgumentDefinition[]? args = null) =>
        new(name, description, ChatToolSchema.FromArguments(args ?? []), EmpyrionConstants.ContextKey)
        {
            AugmentationKey = EmpyrionConstants.AugmentationKey,
            ChatStyles = chatStyles,
            Enabled = isToolSelected(name),
            Permission = ToolPermissionClass.Read,
            Display = ToolDisplayMode.Pill,
        };

    /// <summary>Changes only map markers (no items, no ship): an Act tool that needs no confirmation.</summary>
    private ChatTool MarkTool(string name, string description, FunctionArgumentDefinition[] args) =>
        new(name, description, ChatToolSchema.FromArguments(args), EmpyrionConstants.ContextKey)
        {
            AugmentationKey = EmpyrionConstants.AugmentationKey,
            ChatStyles = chatStyles,
            Enabled = isToolSelected(name),
            Permission = ToolPermissionClass.Act,
            Display = ToolDisplayMode.Pill,
        };

    private static bool IsYes(string? v) => v?.Trim().ToLowerInvariant() is "yes" or "true" or "1" or "y";

    private async ValueTask<ChatToolCallResult> MarksAsync(bool clear, JsonElement? arguments, CancellationToken cancellationToken)
    {
        var stars = GetString(arguments, StarsArg);
        if (!clear && string.IsNullOrWhiteSpace(stars))
            return new ChatToolCallResult($"'{StarsArg}' is required.", IsError: true);
        try
        {
            using var doc = await client.MarksAsync(clear ? "clear" : "add", stars, IsYes(GetString(arguments, RouteArg)),
                !IsYes(GetString(arguments, KeepArg)), steamId, cancellationToken);
            var ok = doc.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            return new ChatToolCallResult(GameView.MarksResult(doc.RootElement), IsError: !ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BridgeException exc)
        {
            return new ChatToolCallResult(exc.Message, IsError: true);
        }
        catch (Exception exc)
        {
            logger.LogWarning(exc, "Empyrion map marks failed");
            return new ChatToolCallResult($"Could not reach the Empyrion game server: {exc.Message}", IsError: true);
        }
    }

    private async ValueTask<ChatToolCallResult> AddMapMarkerAsync(JsonElement? arguments, CancellationToken cancellationToken)
    {
        var star = GetString(arguments, StarArg);
        var markerName = GetString(arguments, NameArg);
        if (string.IsNullOrWhiteSpace(star) || string.IsNullOrWhiteSpace(markerName))
            return new ChatToolCallResult($"'{StarArg}' and '{NameArg}' are required.", IsError: true);
        var hud = GetString(arguments, HudArg) is not { } h || !(h.Trim().ToLowerInvariant() is "no" or "false" or "0" or "n");
        try
        {
            using var doc = await client.AddMapMarkerAsync(star.Trim(), markerName.Trim(), hud, steamId, cancellationToken);
            var ok = doc.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            return new ChatToolCallResult(GameView.MarksResult(doc.RootElement), IsError: !ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BridgeException exc)
        {
            return new ChatToolCallResult(exc.Message, IsError: true);
        }
        catch (Exception exc)
        {
            logger.LogWarning(exc, "Empyrion map marker failed");
            return new ChatToolCallResult($"Could not reach the Empyrion game server: {exc.Message}", IsError: true);
        }
    }

    public async ValueTask<ChatToolCallResult> CallToolAsync(string name, JsonElement? arguments, CancellationToken cancellationToken)
    {
        if (name == EmpyrionConstants.AddMapMarkerTool)
            return await AddMapMarkerAsync(arguments, cancellationToken);
        if (name == EmpyrionConstants.MarkStarsTool || name == EmpyrionConstants.ClearMarksTool)
            return await MarksAsync(clear: name == EmpyrionConstants.ClearMarksTool, arguments, cancellationToken);
        if (name == EmpyrionConstants.TransferItemsTool)
            return await TransferAsync(arguments, cancellationToken);
        if (name == EmpyrionConstants.PlanCraftTool || name == EmpyrionConstants.CraftTool)
            return await CraftAsync(arguments, confirm: name == EmpyrionConstants.CraftTool, cancellationToken);
        if (name == EmpyrionConstants.PlanMicroJumpTool || name == EmpyrionConstants.MicroJumpTool)
            return await MicroJumpAsync(arguments, confirm: name == EmpyrionConstants.MicroJumpTool, cancellationToken);
        if (name == EmpyrionConstants.SearchItemsTool)
            return await SearchItemsAsync(GetString(arguments, QueryArg), cancellationToken);
        if (name == EmpyrionConstants.WhereToBuyTool)
            return await WhereToBuyAsync(GetString(arguments, ItemArg), cancellationToken);
        if (name == EmpyrionConstants.SetActiveStructureTool)
        {
            var target = GetString(arguments, NameArg) is { Length: > 0 } n ? n.Trim() : "auto";
            return await BridgeCallAsync(() => client.SetActiveAsync(target, steamId, cancellationToken), GameView.SetActiveResult);
        }
        if (name == EmpyrionConstants.GetStructureTool)
        {
            if (GetString(arguments, NameArg) is not { Length: > 0 } structureName)
                return new ChatToolCallResult($"'{NameArg}' is required.", IsError: true);
            return await BridgeCallAsync(() => client.StructureInfoAsync(structureName.Trim(), steamId, cancellationToken), GameView.StructureInfoResult);
        }
        if (name == EmpyrionConstants.SystemInfoTool)
            return await BridgeCallAsync(() => client.SystemInfoAsync(GetString(arguments, SystemArg), steamId, cancellationToken), GameView.SystemInfoResult);
        if (name == EmpyrionConstants.PoiTradeTool)
        {
            if (GetString(arguments, PoiArg) is not { Length: > 0 } poi)
                return new ChatToolCallResult($"'{PoiArg}' is required.", IsError: true);
            static bool Yes(string? v) => v?.Trim().ToLowerInvariant() is "yes" or "true" or "1";
            var detail = Yes(GetString(arguments, DetailArg));
            var all = Yes(GetString(arguments, AllArg));
            return await BridgeCallAsync(() => client.PoiTradeAsync(poi.Trim(), detail, all, steamId, cancellationToken), GameView.PoiTradeResult);
        }
        if (name == EmpyrionConstants.ScanPoisTool)
            return await ScanPoisAsync(string.Equals(GetString(arguments, ScopeArg)?.Trim(), "all", StringComparison.OrdinalIgnoreCase), cancellationToken);
        if (name == EmpyrionConstants.FindItemTool)
            return await FindAsync(GetString(arguments, ItemArg), cancellationToken);
        if (name == EmpyrionConstants.GetCurrentShipTool && GetString(arguments, StructureArg) is { Length: > 0 } structure)
            return await StructureContainersAsync(structure.Trim(), cancellationToken);

        Func<GameView, string>? render = name switch
        {
            EmpyrionConstants.GetLocationTool => v => v.Location(),
            EmpyrionConstants.GetPlayerStatusTool => v => v.PlayerStatus(),
            EmpyrionConstants.GetCurrentShipTool => v => v.CurrentShip(),
            EmpyrionConstants.GetPlayerInventoryTool => v => v.PlayerInventory(GetString(arguments, FilterArg)),
            _ => null,
        };
        if (render == null)
            return new ChatToolCallResult($"There is no tool named '{name}' here.", IsError: true);

        try
        {
            using var doc = await client.GetStatusAsync(cancellationToken);
            var view = GameView.Create(doc.RootElement, steamId, out var error);
            if (view == null) return new ChatToolCallResult(error ?? "No player found.", IsError: true);
            return new ChatToolCallResult(render(view));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BridgeException exc)
        {
            return new ChatToolCallResult(exc.Message, IsError: true);
        }
        catch (Exception exc)
        {
            logger.LogWarning(exc, "Empyrion tool {Tool} failed", name);
            return new ChatToolCallResult($"Could not reach the Empyrion game server: {exc.Message}", IsError: true);
        }
    }

    private async ValueTask<ChatToolCallResult> TransferAsync(JsonElement? arguments, CancellationToken cancellationToken)
    {
        var item = GetString(arguments, ItemArg);
        var to = GetString(arguments, ToArg);
        var amount = GetString(arguments, AmountArg) ?? GetNumber(arguments, AmountArg);
        if (string.IsNullOrWhiteSpace(item) || string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(amount))
            return new ChatToolCallResult($"'{ItemArg}', '{AmountArg}' and '{ToArg}' are required.", IsError: true);

        JsonDocument doc;
        try
        {
            doc = await client.TransferAsync(
                new TransferRequest(item.Trim(), amount.Trim(), to.Trim(), GetString(arguments, FromArg), steamId), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BridgeException exc)
        {
            return new ChatToolCallResult(exc.Message, IsError: true);
        }
        catch (Exception exc)
        {
            logger.LogWarning(exc, "Empyrion transfer failed");
            return new ChatToolCallResult($"Could not reach the Empyrion game server: {exc.Message}. The transfer may not have happened; " +
                                          "check the containers/inventory before trying again.", IsError: true);
        }

        // The game side answered: from here on, never claim it "may not have happened" (a retry would move twice).
        using (doc)
        try
        {
            var text = WithWorkingFrom(BridgeTransfer.Describe(doc.RootElement), doc.RootElement);
            var ok = doc.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            logger.LogInformation("Empyrion transfer: {Result}", text);
            return new ChatToolCallResult(text, IsError: !ok);
        }
        catch (Exception exc)
        {
            logger.LogWarning(exc, "Empyrion transfer result unreadable: {Raw}", doc.RootElement.GetRawText());
            return new ChatToolCallResult("The game server handled the transfer, but its answer could not be read. " +
                                          "Do NOT repeat it; check the containers/inventory first. Raw answer: " + doc.RootElement.GetRawText());
        }
    }

    private async ValueTask<ChatToolCallResult> CraftAsync(JsonElement? arguments, bool confirm, CancellationToken cancellationToken)
    {
        var item = GetString(arguments, ItemArg);
        var amount = GetString(arguments, AmountArg) ?? GetNumber(arguments, AmountArg);
        if (string.IsNullOrWhiteSpace(item) || string.IsNullOrWhiteSpace(amount))
            return new ChatToolCallResult($"'{ItemArg}' and '{AmountArg}' are required.", IsError: true);
        try
        {
            using var doc = await client.CraftAsync(item.Trim(), amount.Trim(), live.VcSuffix, confirm, steamId, cancellationToken,
                GetString(arguments, StructureArg));
            var ok = doc.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            return new ChatToolCallResult(WithWorkingFrom(GameView.CraftResult(doc.RootElement), doc.RootElement), IsError: !ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BridgeException exc)
        {
            return new ChatToolCallResult(exc.Message, IsError: true);
        }
        catch (Exception exc)
        {
            logger.LogWarning(exc, "Empyrion craft failed");
            return new ChatToolCallResult(
                confirm ? $"Could not reach the Empyrion game server; check the crafting container before trying again: {exc.Message}"
                        : $"Could not reach the Empyrion game server: {exc.Message}", IsError: true);
        }
    }

    private async ValueTask<ChatToolCallResult> MicroJumpAsync(JsonElement? arguments, bool confirm, CancellationToken cancellationToken)
    {
        string? meters = null;
        var km = GetString(arguments, DistanceArg) ?? GetNumber(arguments, DistanceArg);
        if (!string.IsNullOrWhiteSpace(km))
        {
            if (!double.TryParse(km.Trim().Replace("km", "").Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var k) || k <= 0)
                return new ChatToolCallResult($"'{DistanceArg}' must be a number of kilometres, e.g. '10'.", IsError: true);
            meters = Math.Round(k * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        try
        {
            using var doc = await client.MicroJumpAsync(meters, confirm ? GetString(arguments, TargetArg) : null, live.VjSuffix, confirm, steamId, cancellationToken);
            var ok = doc.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            return new ChatToolCallResult(GameView.MicroJumpResult(doc.RootElement), IsError: !ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BridgeException exc)
        {
            return new ChatToolCallResult(exc.Message, IsError: true);
        }
        catch (Exception exc)
        {
            logger.LogWarning(exc, "Empyrion micro jump failed");
            return new ChatToolCallResult(
                confirm ? $"Could not reach the Empyrion game server; ask the player whether the ship moved before trying again: {exc.Message}"
                        : $"Could not reach the Empyrion game server: {exc.Message}", IsError: true);
        }
    }

    private async ValueTask<ChatToolCallResult> SearchItemsAsync(string? query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new ChatToolCallResult($"'{QueryArg}' is required.", IsError: true);
        try
        {
            using var doc = await client.SearchItemsAsync(query.Trim(), cancellationToken);
            var ok = doc.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            return new ChatToolCallResult(GameView.ItemsResult(doc.RootElement), IsError: !ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BridgeException exc)
        {
            return new ChatToolCallResult(exc.Message, IsError: true);
        }
        catch (Exception exc)
        {
            logger.LogWarning(exc, "Empyrion item search failed");
            return new ChatToolCallResult($"Could not reach the Empyrion game server: {exc.Message}", IsError: true);
        }
    }

    /// <summary>Appends the bridge's "Working from: …" line (the structure an item feature acted on) so the model can troubleshoot.</summary>
    private static string WithWorkingFrom(string text, JsonElement root) =>
        GameView.WorkingFrom(root) is { } w ? $"{text}\n{w}" : text;

    /// <summary>A read call to the bridge rendered by <paramref name="render"/>; ok=false answers are errors for the model.</summary>
    private static async ValueTask<ChatToolCallResult> BridgeCallAsync(Func<Task<JsonDocument>> call, Func<JsonElement, string> render)
    {
        try
        {
            using var doc = await call();
            var ok = doc.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            return new ChatToolCallResult(render(doc.RootElement), IsError: !ok);
        }
        catch (BridgeException exc)
        {
            return new ChatToolCallResult(exc.Message, IsError: true);
        }
    }

    private async ValueTask<ChatToolCallResult> ScanPoisAsync(bool all, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await client.ScanPoisAsync(all, steamId, cancellationToken);
            var ok = doc.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            return new ChatToolCallResult(GameView.PoiScanResult(doc.RootElement), IsError: !ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BridgeException exc)
        {
            return new ChatToolCallResult(exc.Message, IsError: true);
        }
    }

    private async ValueTask<ChatToolCallResult> WhereToBuyAsync(string? item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item))
            return new ChatToolCallResult($"'{ItemArg}' is required.", IsError: true);
        try
        {
            using var doc = await client.WhereToBuyAsync(item.Trim(), steamId, cancellationToken);
            var ok = doc.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            return new ChatToolCallResult(GameView.WhereToBuyResult(doc.RootElement), IsError: !ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BridgeException exc)
        {
            return new ChatToolCallResult(exc.Message, IsError: true);
        }
        catch (Exception exc)
        {
            logger.LogWarning(exc, "Empyrion where-to-buy failed");
            return new ChatToolCallResult($"Could not reach the Empyrion game server: {exc.Message}", IsError: true);
        }
    }

    private async ValueTask<ChatToolCallResult> FindAsync(string? item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item))
            return new ChatToolCallResult($"'{ItemArg}' is required.", IsError: true);
        try
        {
            using var doc = await client.FindAsync(item.Trim(), steamId, cancellationToken);
            var ok = doc.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            return new ChatToolCallResult(WithWorkingFrom(GameView.FindResult(doc.RootElement), doc.RootElement), IsError: !ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BridgeException exc)
        {
            return new ChatToolCallResult(exc.Message, IsError: true);
        }
        catch (Exception exc)
        {
            logger.LogWarning(exc, "Empyrion find failed");
            return new ChatToolCallResult($"Could not reach the Empyrion game server: {exc.Message}", IsError: true);
        }
    }

    private async ValueTask<ChatToolCallResult> StructureContainersAsync(string structure, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await client.ContainersAsync(structure, steamId, cancellationToken);
            var ok = doc.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            return new ChatToolCallResult(WithWorkingFrom(GameView.StructureContainers(doc.RootElement), doc.RootElement), IsError: !ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BridgeException exc)
        {
            return new ChatToolCallResult(exc.Message, IsError: true);
        }
        catch (Exception exc)
        {
            logger.LogWarning(exc, "Empyrion structure containers failed");
            return new ChatToolCallResult($"Could not reach the Empyrion game server: {exc.Message}", IsError: true);
        }
    }

    private static string? GetNumber(JsonElement? arguments, string name) =>
        arguments is { ValueKind: JsonValueKind.Object } args && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetRawText()
            : null;

    // ----- live context + events -------------------------------------------------------------

    /// <summary>
    /// The nearby scan costs ~0.1-0.15 s per call, nearly all of it the round trip to the playfield server (measured
    /// 2026-11-19; the dedi's own work is a few ms), so it runs on every poll, at most every 5 s.
    /// </summary>
    private static readonly TimeSpan NearbyRefresh = TimeSpan.FromSeconds(5);

    public void Start()
    {
        if (!live.LiveContext && !live.Reactions) return;
        _poller = Task.Run(() => PollLoopAsync(_stop.Token));
    }

    private async Task PollLoopAsync(CancellationToken stop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, session.Abort);
        var ct = linked.Token;

        GameState? previous = null;
        var firstPoll = true;
        var bridgeDown = false;
        string? lastContext = null;
        var lastSpoken = DateTimeOffset.MinValue;
        string? nearby = null, nearbyPlayfield = null;
        var nearbyAt = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                GameState? state;
                string context;
                using (var doc = await client.GetStatusAsync(ct))
                {
                    var view = GameView.Create(doc.RootElement, steamId, out var error);
                    state = view?.State();
                    context = state?.ToContext() ?? $"[ EMPYRION ] {error}";
                }

                // nearby places: rescanned every poll (at most every 5 s) and on a playfield change (one bridge call, read-only)
                if (live.LiveContext && state?.Playfield is { } pf &&
                    (pf != nearbyPlayfield || DateTimeOffset.UtcNow - nearbyAt >= NearbyRefresh - TimeSpan.FromMilliseconds(250)))
                {
                    nearbyAt = DateTimeOffset.UtcNow;
                    nearbyPlayfield = pf;
                    try
                    {
                        using var scan = await client.ScanPoisAsync(false, steamId, ct);
                        nearby = GameView.NearbyLine(scan.RootElement);
                    }
                    catch (Exception exc) when (exc is not OperationCanceledException)
                    {
                        logger.LogDebug("Empyrion nearby scan failed: {Message}", exc.Message);
                    }
                }
                if (state == null) nearby = null;
                if (nearby != null) context += "\n" + nearby;

                if (bridgeDown)
                {
                    logger.LogInformation("Empyrion bridge reachable again");
                    bridgeDown = false;
                }

                if (live.LiveContext && context != lastContext)
                {
                    await session.SetContexts(EmpyrionConstants.AugmentationKey, [ContextDefinition.Single(context)], ct);
                    lastContext = context;
                }

                if (live.Reactions)
                {
                    var events = GameEvents.Diff(previous, state, firstPoll);
                    if (events.Count > 0) lastSpoken = await DeliverAsync(events, lastSpoken, ct);
                }

                previous = state;
                firstPoll = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exc)
            {
                if (!bridgeDown)
                {
                    logger.LogWarning("Empyrion bridge poll failed: {Message}", exc.Message);
                    bridgeDown = true;
                    if (live.LiveContext)
                    {
                        const string down = "[ EMPYRION ] The game server is not reachable right now; game state unknown.";
                        try
                        {
                            await session.SetContexts(EmpyrionConstants.AugmentationKey, [ContextDefinition.Single(down)], ct);
                            lastContext = down;
                        }
                        catch (Exception) { /* session may be closing */ }
                    }
                }
                // Keep the last known state: a short outage must not look like "player left + joined".
            }

            try { await Task.Delay(live.PollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Important events (at most one spoken reaction per cooldown) go in as a secret and trigger a reply;
    /// the rest go in as a note the character sees but does not have to answer.
    /// </summary>
    private async Task<DateTimeOffset> DeliverAsync(List<GameEvent> events, DateTimeOffset lastSpoken, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var important = events.Where(e => e.Important).ToList();
        var speak = important.Count > 0 && now - lastSpoken >= live.ReactionCooldown;

        foreach (var e in events.Where(e => !speak || !e.Important))
        {
            logger.LogInformation("Empyrion event (silent): {Event}", e.Name);
            await session.SendNoteAsync($"[Empyrion: {e.Name}] {e.Description}", cancellationToken: ct);
        }

        if (!speak) return lastSpoken;

        var text = string.Join(" ", important.Select(e => e.Description));
        logger.LogInformation("Empyrion event (spoken): {Events}", string.Join(", ", important.Select(e => e.Name)));
        await session.SendSecretAsync($"[GAME EVENT in Empyrion] {text} React to this briefly, in character.", cancellationToken: ct);
        await session.TriggerReplyAsync(ct);
        return now;
    }

    private static string? GetString(JsonElement? arguments, string name)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } args) return null;
        if (!args.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_poller != null)
        {
            try { await _poller; }
            catch (Exception) { /* already logged */ }
        }
        _stop.Dispose();
    }
}

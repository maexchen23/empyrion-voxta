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
public sealed record LiveSettings(bool LiveContext, bool Reactions, TimeSpan PollInterval, TimeSpan ReactionCooldown, string VcSuffix = "VC",
    GameEventConfig? Events = null, bool GameChat = true, bool GameChatMirror = false);

public sealed class EmpyrionChatAugmentationInstance(
    IChatSessionChatAugmentationApi session,
    BridgeClient client,
    string? steamId,
    ChatStyles[] chatStyles,
    Func<string, bool> isToolSelected,
    LiveSettings live,
    WatchStore watches,
    ILogger<EmpyrionChatAugmentationInstance> logger
) : IToolsChatAugmentation, IChatPostProcessAugmentation
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
    private const string StatusArg = "status";
    private const string TextArg = "text";
    private const string NearArg = "near";
    private const string ChangesArg = "changes";
    private const string ActionArg = "action";
    private const string ConditionsArg = "conditions";
    private const string NoteArg = "note";
    private const string IdArg = "id";

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
                "discovered, with each body's difficulty. With a planet or moon name: its description as the game shows it (lore, " +
                "special effects such as reduced detection), gravity, atmosphere, oxygen, temperatures, radiation and resources. " +
                "'known' lists all discovered systems. Undiscovered systems only show star class and territory.",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = SystemArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "System name, or a planet/moon name for its details (default: the current system), or 'known' for the list of discovered systems.",
                    },
                ]),
            ReadTool(EmpyrionConstants.PoiTerminalTool,
                "Read the info terminal of a POI (abandoned bunkers, reactors, launch controllers, research stations): its screens " +
                "with lore, warnings, codes and hints, as the game shows them. Only for a POI the player is at (within 2 km) or " +
                "has visited. Use it when the scan shows an info terminal or the player asks what a place is about; retell what " +
                "matters, don't read everything out.",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = PoiArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "POI name or part of it, e.g. 'Abandoned Bunker'.",
                    },
                ]),
            ReadTool(EmpyrionConstants.ListQuestsTool,
                "The player's trader missions: specialised factories (and some shops) that want items before they trade, or " +
                "offer paid jobs. Read from the game's own save, so it covers every trader the player has talked to. The game " +
                "doesn't track these missions anywhere the player can see, so this is where the character remembers them. " +
                "Gives what each wants, the reward, the work-order alternative and the distance, closest first, at most 5.",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = StatusArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "'open' (default: missions waiting, trades still locked), 'unlocked' (factories that trade now) or 'all'.",
                    },
                    new FunctionArgumentDefinition
                    {
                        Name = NearArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "Only near the player: 'playfield', 'system', or a distance in light years, e.g. '20'.",
                    },
                    new FunctionArgumentDefinition
                    {
                        Name = ItemArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "Only missions that want this item (name or part of it), e.g. 'Graphite'.",
                    },
                ]),
            MarkTool(EmpyrionConstants.PursueQuestTool,
                "Pursue one trader mission when the player asks to work on it: its line ('Pursuing: …') stays in the live game " +
                "state until it is done or cleared, and the character hears when it's done. One at a time; a new one replaces the " +
                "old. 'none' clears it.",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = NameArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "Factory name (or part of it, or its playfield) as empyrion_list_quests gives it, or 'none'.",
                    },
                ]),
            MarkTool(EmpyrionConstants.SendToChatTool,
                "Write a message into the player's game chat (a private message from you). For details that are tedious to " +
                "listen to and handy to read later: item counts, lists, coordinates, a shopping list. Say only the short " +
                "version out loud and mention that the details are in their chat.",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = TextArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "The text for the chat; plain text, line breaks for lists. Long texts are split into several messages.",
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
                "ship now points elsewhere. Costs Refined Pentaxid from the ship's pentaxid tank; the drive then cools down for a while.",
                ChatToolSchema.FromArguments(MicroJumpArgs(withTarget: true)),
                EmpyrionConstants.ContextKey)
            {
                AugmentationKey = EmpyrionConstants.AugmentationKey,
                ChatStyles = chatStyles,
                Enabled = isToolSelected(EmpyrionConstants.MicroJumpTool),
                Permission = ToolPermissionClass.Act,
                Display = ToolDisplayMode.Card,
            },
            ReadTool(EmpyrionConstants.FindShipControlsTool,
                "Search the switchable controls of the active structure (the own ship or base the item tools work from) and " +
                "what is docked to it, or of a named own ship or base in reach: signals and named devices or device groups whose name or block type contains the query, plus main power " +
                "and shield, each with its current state. Never lists everything; search for what the player talks about " +
                "('turret', 'light', 'production', a signal name). Changes nothing.",
                [
                    new FunctionArgumentDefinition
                    {
                        Name = QueryArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "Part of a name or block type, e.g. 'turret', 'production', 'light', 'gear'.",
                    },
                    new FunctionArgumentDefinition
                    {
                        Name = StructureArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "Name of one own ship or base in reach (docking group, transfer range). Leave out for the active structure and its docking group.",
                    },
                ]),
            new ChatTool(
                EmpyrionConstants.ShipControlTool,
                "Switch things on one of the player's own ships or bases: main power, the shield, signals (from the control " +
                "panel or switches) and named devices or device groups, on, off or toggle. Several at once in one call (e.g. a " +
                "parking routine). Only when the player asks or agreed. Unknown names come back with close matches; use " +
                "empyrion_find_ship_controls when unsure. Devices without a custom name or group can't be switched.",
                ChatToolSchema.FromArguments([
                    new FunctionArgumentDefinition
                    {
                        Name = ChangesArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "'target=state' items separated by ';', state on, off or toggle. Target: power, shield, a " +
                                      "signal name or a device/group name, e.g. 'POWERSAVER=on; Production=off' or 'shield=off'. " +
                                      "Prefix signal: or device: if a name is both.",
                    },
                    new FunctionArgumentDefinition
                    {
                        Name = StructureArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "Name of an own ship or base in reach (e.g. a docked miner). Leave out for the active structure.",
                    },
                ]),
                EmpyrionConstants.ContextKey)
            {
                AugmentationKey = EmpyrionConstants.AugmentationKey,
                ChatStyles = chatStyles,
                Enabled = isToolSelected(EmpyrionConstants.ShipControlTool),
                Permission = ToolPermissionClass.Act,
                Display = ToolDisplayMode.Card,
            },
            new ChatTool(
                EmpyrionConstants.WatchTool,
                "Watches ('tell me when …'): be notified when a simple game state changes, then decide what to do (e.g. " +
                "power a docked miner down when the player leaves it). A watch is a list of field=value conditions that all " +
                "must match an event, plus a note why. Actions: add, list, remove. Skill empyrion-watches has the fields.",
                ChatToolSchema.FromArguments([
                    new FunctionArgumentDefinition
                    {
                        Name = ActionArg, Type = FunctionArgumentType.String, Required = true,
                        Description = "add, list or remove.", Choices = ["add", "list", "remove"],
                    },
                    new FunctionArgumentDefinition
                    {
                        Name = ConditionsArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "For add: 'field=value' pairs separated by ';', all must match, e.g. 'event=left; structure=Miner; docked=true'. " +
                                      "event: seat_taken, seat_left, entered, left, docked, undocked, landed, lifted_off, signal, device, " +
                                      "new_structure (spawned from a blueprint), starter_placed. " +
                                      "Other fields: structure (ship or base name), type (ba/cv/sv/hv), seat (pilot/aboard/on_foot), where (space/planet), playfield, landed, docked (true/false), " +
                                      "docked_to, other_structure, name (signal/device name), state (on/off).",
                    },
                    new FunctionArgumentDefinition
                    {
                        Name = NoteArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "For add: why, and what to do when it fires, e.g. 'power the miner down'. Comes back with the notification.",
                    },
                    new FunctionArgumentDefinition
                    {
                        Name = IdArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "For remove: the watch id from list, or 'all'.",
                    },
                ]),
                EmpyrionConstants.ContextKey)
            {
                AugmentationKey = EmpyrionConstants.AugmentationKey,
                ChatStyles = chatStyles,
                Enabled = isToolSelected(EmpyrionConstants.WatchTool),
                Permission = ToolPermissionClass.InChat,
                Display = ToolDisplayMode.Pill,
            },
            new ChatTool(
                EmpyrionConstants.TransferItemsTool,
                "Move items between containers in reach (ship, carrier, docked ships, own structures in range) and the player's " +
                "own inventory. Use the container labels from empyrion_get_current_ship; use 'player' (or 'bag' / 'toolbar') for " +
                "the player's inventory. A ship's 'Fuel Tank', 'Oxygen Tank' or 'Pentaxid Tank' can be the target (fill it from containers) or " +
                "the source (take fuel cells, oxygen bottles or Refined Pentaxid out). Changes the game, so only do it when the player asks.",
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
                                      "(the game fills it in order), or a tank: 'Fuel Tank', 'Oxygen Tank', 'Pentaxid Tank' (filled from containers, not from the player). " +
                                      "The part before '@' alone works if it is unique. " +
                                      "Must be exactly one container.",
                    },
                    new FunctionArgumentDefinition
                    {
                        Name = FromArg, Type = FunctionArgumentType.String, Required = false,
                        Description = "Source container label, a kind like 'cargo', 'player' / 'bag' / 'toolbar' for the player's inventory, " +
                                      "or a tank ('Fuel Tank' gives Fuel Cells, 'Oxygen Tank' Oxygen Bottles, 'Pentaxid Tank' Refined Pentaxid; only into containers). " +
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
        if (name == EmpyrionConstants.WatchTool)
            return WatchTool(arguments);
        if (name == EmpyrionConstants.FindShipControlsTool)
        {
            if (GetString(arguments, QueryArg) is not { Length: > 0 } controlQuery)
                return new ChatToolCallResult($"'{QueryArg}' is required.", IsError: true);
            return await BridgeCallAsync(() => client.FindShipControlsAsync(controlQuery.Trim(), GetString(arguments, StructureArg), steamId, cancellationToken),
                r => WithWorkingFrom(GameView.FindShipControlsResult(r), r));
        }
        if (name == EmpyrionConstants.ShipControlTool)
        {
            if (GetString(arguments, ChangesArg) is not { Length: > 0 } changes)
                return new ChatToolCallResult($"'{ChangesArg}' is required, e.g. 'POWERSAVER=on; Production=off'.", IsError: true);
            return await BridgeCallAsync(() => client.ShipControlAsync(changes.Trim(), GetString(arguments, StructureArg), steamId, cancellationToken),
                r => WithWorkingFrom(GameView.ShipControlResult(r), r));
        }
        if (name == EmpyrionConstants.GetStructureTool)
        {
            if (GetString(arguments, NameArg) is not { Length: > 0 } structureName)
                return new ChatToolCallResult($"'{NameArg}' is required.", IsError: true);
            return await BridgeCallAsync(() => client.StructureInfoAsync(structureName.Trim(), steamId, cancellationToken), GameView.StructureInfoResult);
        }
        if (name == EmpyrionConstants.SystemInfoTool)
            return await BridgeCallAsync(() => client.SystemInfoAsync(GetString(arguments, SystemArg), steamId, cancellationToken), GameView.SystemInfoResult);
        if (name == EmpyrionConstants.PoiTerminalTool)
        {
            if (GetString(arguments, PoiArg) is not { Length: > 0 } terminalPoi)
                return new ChatToolCallResult($"'{PoiArg}' is required.", IsError: true);
            return await BridgeCallAsync(() => client.PoiTerminalAsync(terminalPoi.Trim(), steamId, cancellationToken), GameView.PoiTerminalResult);
        }
        if (name == EmpyrionConstants.ListQuestsTool)
            return await BridgeCallAsync(() => client.QuestsAsync(GetString(arguments, StatusArg), GetString(arguments, NearArg), GetString(arguments, ItemArg), steamId, cancellationToken), GameView.QuestsResult);
        if (name == EmpyrionConstants.PursueQuestTool)
        {
            var quest = GetString(arguments, NameArg) is { Length: > 0 } qn ? qn.Trim() : "none";
            try
            {
                using var doc = await client.PursueQuestAsync(quest, steamId, cancellationToken);
                var root = doc.RootElement;
                var ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                var text = GameView.PursueQuestResult(root);
                if (ok && root.TryGetProperty("quest", out var q) && GameView.Needs(q) is { Count: > 0 } needs)
                    text += " " + await ReachTextAsync(needs, cancellationToken);
                return new ChatToolCallResult(text, IsError: !ok);
            }
            catch (BridgeException exc) { return new ChatToolCallResult(exc.Message, IsError: true); }
        }
        if (name == EmpyrionConstants.SendToChatTool)
        {
            if (GetString(arguments, TextArg) is not { Length: > 0 } chatText)
                return new ChatToolCallResult($"'{TextArg}' is required.", IsError: true);
            return await BridgeCallAsync(() => client.ChatSayAsync(chatText.Trim(), CharacterName(), steamId, cancellationToken),
                r => Str(r, "error") ?? "Sent to the player's game chat.");
        }
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
            using var doc = await client.MicroJumpAsync(meters, confirm ? GetString(arguments, TargetArg) : null, confirm, steamId, cancellationToken);
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
    private ChatToolCallResult WatchTool(JsonElement? arguments)
    {
        var action = (GetString(arguments, ActionArg) ?? "list").Trim().ToLowerInvariant();
        if (action == "add")
        {
            if (Watches.Parse(GetString(arguments, ConditionsArg), out var error) is not { } conditions)
                return new ChatToolCallResult(error ?? "Bad conditions.", IsError: true);
            var note = (GetString(arguments, NoteArg) ?? "").Trim();
            if (note.Length == 0) return new ChatToolCallResult($"'{NoteArg}' is required: why the watch, and what to do when it fires.", IsError: true);
            if (note.Length > 300) note = note[..300];
            if (watches.Add(conditions, note, out var addError) is not { } w) return new ChatToolCallResult(addError ?? "Could not add the watch.", IsError: true);
            logger.LogInformation("Empyrion watch #{Id} added: {Conditions}", w.Id, w.ConditionText);
            return new ChatToolCallResult($"Watch #{w.Id} added: {w.ConditionText} (note: {w.Note}). It fires when an event matches all conditions.");
        }
        if (action == "remove")
        {
            var idText = (GetString(arguments, IdArg) ?? "").Trim();
            if (idText.Equals("all", StringComparison.OrdinalIgnoreCase))
                return new ChatToolCallResult($"Removed all watches ({watches.Remove(null)}).");
            if (!int.TryParse(idText.TrimStart('#'), out var id)) return new ChatToolCallResult($"'{IdArg}' must be a watch id from list, or 'all'.", IsError: true);
            return watches.Remove(id) > 0 ? new ChatToolCallResult($"Watch #{id} removed.") : new ChatToolCallResult($"There is no watch #{id}.", IsError: true);
        }
        var all = watches.All;
        if (all.Count == 0) return new ChatToolCallResult("No watches set.");
        return new ChatToolCallResult(string.Join("\n", all.Select(w => $"#{w.Id}: {w.ConditionText} (note: {w.Note})")));
    }

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
        var warpSmoother = new WarpSmoother();
        var gameChat = new GameChatFilter(null);
        var nearbyAt = DateTimeOffset.MinValue;
        var watchStates = new Dictionary<string, (string kind, string state, string ship)>();
        var watchFired = new Dictionary<int, DateTimeOffset>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                GameState? state;
                string context;
                using (var doc = await client.GetStatusAsync(ct))
                {
                    var view = GameView.Create(doc.RootElement, steamId, out var error);
                    state = warpSmoother.Apply(view?.State(), DateTimeOffset.UtcNow);
                    context = state?.ToContext() ?? $"[ EMPYRION ] {error}";
                    if (live.GameChat && view?.SteamId is { } chatPlayer)
                    {
                        var chatTexts = gameChat.Take(GameView.ChatLines(doc.RootElement, chatPlayer));
                        foreach (var text in chatTexts)
                        {
                            logger.LogInformation("Empyrion game chat -> character ({Length} chars)", text.Length);
                            await session.SendUserMessageAsync(text, ct);
                        }
                        // SendUserMessageAsync only adds the message (seen with Voxta 1.13): ask for the reply explicitly
                        if (chatTexts.Count > 0) await session.TriggerReplyAsync(ct);
                    }
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
                    // where the player is (location + aboard/on foot): logged when it changes, to compare with what the character says
                    var where = WhereLines(context);
                    if (where != WhereLines(lastContext)) logger.LogInformation("Empyrion context now: {Where}", where);
                    lastContext = context;
                }

                if (live.Reactions)
                {
                    var events = GameEvents.Diff(previous, state, firstPoll, live.Events);
                    // a new mission: say what of it is already in reach (only for the ones spoken about)
                    for (var i = 0; i < events.Count; i++)
                        if (events[i] is { MissionKey: { } key, Important: true } ev &&
                            state?.OpenMissions?.FirstOrDefault(m => m.Key == key)?.Needs is { Count: > 0 } needs)
                            events[i] = ev with { Description = ev.Description + " " + await ReachTextAsync(needs, ct) };
                    if (events.Count > 0) lastSpoken = await DeliverAsync(events, lastSpoken, ct);
                }

                if (isToolSelected(EmpyrionConstants.WatchTool) && watches.All is { Count: > 0 } active)
                    watchStates = await CheckWatchesAsync(active, previous, state, firstPoll, watchStates, watchFired, ct);

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
    /// Builds this poll's watch events (ship, seat, docking, landing, and the watched signals/devices, asked from the
    /// bridge only when such watches exist), matches them and delivers the fired watches: always (they were set on
    /// purpose, no reaction cooldown), but each watch at most once per <see cref="Watches.Cooldown"/>. Returns the
    /// signal/device states for the next poll.
    /// </summary>
    private async Task<Dictionary<string, (string kind, string state, string ship)>> CheckWatchesAsync(IReadOnlyList<Watch> active,
        GameState? previous, GameState? state, bool firstPoll,
        Dictionary<string, (string kind, string state, string ship)> prevStates, Dictionary<int, DateTimeOffset> fired, CancellationToken ct)
    {
        var states = new Dictionary<string, (string kind, string state, string ship)>(StringComparer.OrdinalIgnoreCase);
        if (state == null) return states;
        foreach (var (ship, names) in Watches.WatchedNames(active))
        {
            try
            {
                using var doc = await client.DeviceStatesAsync(names, ship.Length > 0 ? ship : null, steamId, ct);
                var r = doc.RootElement;
                if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)) continue;
                var structure = r.TryGetProperty("structure", out var st) ? st.GetString() ?? ship : ship;
                foreach (var x in r.GetProperty("states").EnumerateArray())
                {
                    var name = x.GetProperty("name").GetString() ?? "";
                    var kind = x.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "";
                    states[$"{structure}|{name}"] = (kind, x.GetProperty("state").GetString() ?? "", structure);
                }
            }
            catch (Exception exc) when (exc is not OperationCanceledException)
            {
                logger.LogDebug("Empyrion watch states failed: {Message}", exc.Message);
            }
        }
        if (firstPoll) return states;

        var events = Watches.Diff(previous, state).Concat(Watches.StateEvents(state, prevStates, states)).ToList();
        var now = DateTimeOffset.UtcNow;
        var texts = new List<string>();
        foreach (var w in active)
        {
            var hit = events.FirstOrDefault(e => Watches.Matches(w, e));
            if (hit == null) continue;
            if (fired.TryGetValue(w.Id, out var last) && now - last < Watches.Cooldown) continue;
            fired[w.Id] = now;
            logger.LogInformation("Empyrion watch #{Id} fired: {Event}", w.Id, string.Join(", ", hit.Select(kv => $"{kv.Key}={kv.Value}")));
            texts.Add(Watches.Notification(w, hit));
        }
        if (texts.Count > 0)
        {
            await session.SendSecretAsync(string.Join("\n", texts), cancellationToken: ct);
            await session.TriggerReplyAsync(ct);
        }
        return states;
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

    /// <summary>
    /// "Already in reach: Communication Equipment 8 of 12 (5 in Ores @ Test Hauler, 3 in the player's inventory)." from one
    /// find per wanted item (same scope as empyrion_find_item: inventory, active structure group, own structures in range).
    /// </summary>
    private async Task<string> ReachTextAsync(IReadOnlyList<MissionNeed> needs, CancellationToken ct)
    {
        var parts = new List<string>();
        foreach (var need in needs.Where(n => n.TokenId == null).Take(6))
        {
            try
            {
                using var doc = await client.FindAsync(need.Name, steamId, ct);
                var item = doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                    ? items.EnumerateArray().FirstOrDefault(i => i.TryGetProperty("name", out var nm) && string.Equals(nm.GetString(), need.Name, StringComparison.OrdinalIgnoreCase))
                    : default;
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("total", out var tot) || tot.GetInt32() == 0)
                {
                    parts.Add($"{need.Name}: none");
                    continue;
                }
                var where = item.TryGetProperty("locations", out var locs) && locs.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", locs.EnumerateArray().Take(3).Select(l =>
                    {
                        var w = l.GetProperty("where").GetString() ?? "";
                        w = w.StartsWith("player ") ? "the player's inventory" : w;
                        return $"{l.GetProperty("count").GetInt32()} in {w}";
                    }))
                    : "";
                parts.Add($"{need.Name}: {Math.Min(tot.GetInt32(), need.Count)} of {need.Count}{(where.Length > 0 ? $" ({where})" : "")}");
            }
            catch (Exception exc) when (exc is BridgeException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                parts.Add($"{need.Name}: unknown");
            }
        }
        return parts.Count == 0 ? "" : "Already in reach (inventory, ship and own structures nearby): " + string.Join("; ", parts) + ".";
    }

    // ----- replies mirrored to the game chat ----------------------------------------------------

    private readonly object _mirrorLock = new();
    private readonly System.Text.StringBuilder _mirror = new();
    private CancellationTokenSource? _mirrorFlush;
    private static readonly TimeSpan MirrorQuiet = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Called for each sentence of a reply while it streams (unchanged here). With mirroring on, the sentences are
    /// collected and sent to the game chat as one message once no sentence came for <see cref="MirrorQuiet"/>.
    /// </summary>
    public ValueTask<string> PostProcessTextAsync(ChatMessageRole role, string text, CancellationToken cancellationToken)
    {
        if (live.GameChatMirror && role == ChatMessageRole.Assistant && !string.IsNullOrWhiteSpace(text))
        {
            CancellationTokenSource cts;
            lock (_mirrorLock)
            {
                if (_mirror.Length > 0) _mirror.Append(' ');
                _mirror.Append(text.Trim());
                _mirrorFlush?.Cancel();
                _mirrorFlush = cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            }
            _ = FlushMirrorAsync(cts.Token);
        }
        return ValueTask.FromResult(text);
    }

    private async Task FlushMirrorAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(MirrorQuiet, ct);
            string text;
            lock (_mirrorLock)
            {
                text = _mirror.ToString();
                _mirror.Clear();
            }
            if (text.Length == 0) return;
            using var _ = await client.ChatSayAsync(text, CharacterName(), steamId, ct);
        }
        catch (OperationCanceledException) { /* a newer sentence came, or the chat ended */ }
        catch (Exception exc) { logger.LogWarning("Empyrion: mirroring a reply to the game chat failed: {Message}", exc.Message); }
    }

    /// <summary>The character's name for the game chat ("IDA"), from Voxta's template.</summary>
    private string CharacterName()
    {
        try { return session.ProcessSimpleTemplate("{{ char }}") is { Length: > 0 } n && !n.Contains("{{") ? n : "AI"; }
        catch (Exception) { return "AI"; }
    }

    private static string? Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string WhereLines(string? context) =>
        context == null ? "" : string.Join(" | ", context.Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith("Location:") || l.StartsWith("On foot") || l.StartsWith("Aboard") || l.StartsWith("Piloting")));

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

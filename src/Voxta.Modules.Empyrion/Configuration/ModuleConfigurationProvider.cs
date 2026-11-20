using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Abstractions.Services.Agents;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Model.Shared;
using Voxta.Model.Shared.Forms;

namespace Voxta.Modules.Empyrion.Configuration;

public class ModuleConfigurationProvider : ModuleConfigurationProviderBase, IModuleConfigurationProvider
{
    public static string[] FieldsRequiringReload => [];

    /// <summary>
    /// Which chats the game tools start in by themselves (the Augmentations page's "Enabled for"; Voxta 1.13+):
    /// characters by default, since these are game-companion tools. A character can still add the key by name.
    /// </summary>
    public static readonly ModuleWideAugmentationAutoEnabledSetting AutoEnabledFor =
        ChatToolSettingsContributions.AutoEnabledFor(AugmentationAutoEnabledFor.Characters);

    AugmentationAutoEnabledSetting? IModuleConfigurationProvider.AugmentationsAutoEnabledFor => AutoEnabledFor;

    public static readonly IReadOnlyList<ChatToolChoice> Tools =
    [
        new(EmpyrionConstants.GetLocationTool, "Get Location"),
        new(EmpyrionConstants.GetPlayerStatusTool, "Get Player Status"),
        new(EmpyrionConstants.GetCurrentShipTool, "Get Current Ship"),
        new(EmpyrionConstants.GetPlayerInventoryTool, "Get Player Inventory"),
        new(EmpyrionConstants.FindItemTool, "Find Item"),
        new(EmpyrionConstants.SearchItemsTool, "Search Items"),
        new(EmpyrionConstants.WhereToBuyTool, "Where To Buy"),
        new(EmpyrionConstants.ScanPoisTool, "Scan Points Of Interest"),
        new(EmpyrionConstants.GetStructureTool, "Own Ship/Base Details"),
        new(EmpyrionConstants.SetActiveStructureTool, "Set Active Ship/Base"),
        new(EmpyrionConstants.SystemInfoTool, "Planets In The Current System"),
        new(EmpyrionConstants.PoiTradeTool, "What A POI Sells And Buys"),
        new(EmpyrionConstants.PoiTerminalTool, "Read A POI's Info Terminal"),
        new(EmpyrionConstants.ListQuestsTool, "List Trader Missions"),
        new(EmpyrionConstants.PursueQuestTool, "Pursue A Trader Mission"),
        new(EmpyrionConstants.SendToChatTool, "Write To The Game Chat"),
        new(EmpyrionConstants.MarkStarsTool, "Mark Stars On The Galaxy Map"),
        new(EmpyrionConstants.ClearMarksTool, "Clear Galaxy Map Marks"),
        new(EmpyrionConstants.AddMapMarkerTool, "Add Permanent Map Marker"),
        new(EmpyrionConstants.PlanCraftTool, "Plan Crafting"),
        new(EmpyrionConstants.CraftTool, "Craft (VC container)"),
        new(EmpyrionConstants.PlanMicroJumpTool, "Plan Micro Jump"),
        new(EmpyrionConstants.MicroJumpTool, "Micro Jump (moves the ship)"),
        new(EmpyrionConstants.TransferItemsTool, "Transfer Items (changes the game)"),
        new(EmpyrionConstants.FindShipControlsTool, "Find Ship Controls"),
        new(EmpyrionConstants.ShipControlTool, "Ship Control: Power, Shield, Signals, Devices (changes the game)"),
    ];

    public static readonly FormTextField BridgeUrl = new()
    {
        Name = "BridgeUrl",
        Label = "Bridge URL",
        Text = "Address of the VoxtaBridge mod on the game server, e.g. `http://my-server:30016`.",
        DefaultValue = "http://127.0.0.1:30016",
        Required = true,
        ValidationRegex = FormTextField.UrlRegex,
    };

    public static readonly FormPasswordField Token = new()
    {
        Name = "Token",
        Label = "Bridge Token",
        Text = "The `Token` value from `Content/Mods/VoxtaBridge/VoxtaBridge.cfg` on the game server. Use **Test** on the module to check it.",
        Required = true,
    };

    public static readonly FormTextField SteamId = new()
    {
        Name = "SteamId",
        Label = "Player SteamID64",
        Text = "Which player this character talks to. Empty: the only player online.",
        DefaultValue = "",
    };

    public static readonly FormBooleanField LiveContext = new()
    {
        Name = "LiveContext",
        Label = "Live Game Context",
        Text = "Keep a short summary of the player's location, vitals and ship in the character's context.",
        DefaultValue = true,
    };

    public static readonly FormBooleanField Reactions = new()
    {
        Name = "Reactions",
        Label = "React to Game Events",
        Text = "Let the character speak up on important events (low oxygen, arriving somewhere, empty fuel). " +
               "Minor events (boarding a ship) are only passed on silently.",
        DefaultValue = true,
    };

    /// <summary>Folded section (Advanced on the title folds all of it): grows with every tracked quest type.</summary>
    public static readonly FormTitleField AnnouncementsTitle = Folded(FormTitleField.Create(
        "Mission Announcements",
        text: "Which trader missions the character speaks up about (needs 'React to Game Events'); the others are only passed on silently."));

    private static FormTitleField Folded(FormTitleField title) { title.Advanced = true; return title; }

    public static readonly FormBooleanField AnnounceFactoryMissions = new()
    {
        Name = "AnnounceFactoryMissions",
        Label = "Announce New Factory Missions",
        Text = "The character speaks up when the player gets a new mission from a factory whose trades wait for it " +
               "(or a follow-up there). Off: the character is only told silently.",
        DefaultValue = true,
    };

    public static readonly FormBooleanField AnnounceTraderMissions = new()
    {
        Name = "AnnounceTraderMissions",
        Label = "Announce New Trader Missions",
        Text = "The character speaks up when the player gets a job from a shop that trades anyway (e.g. while checking " +
               "every trader on a station). Off: the character is only told silently.",
        DefaultValue = false,
    };

    public static readonly FormBooleanField AnnounceDoneFactoryMissions = new()
    {
        Name = "AnnounceDoneFactoryMissions",
        Label = "Announce Completed Factory Missions",
        Text = "The character speaks up when the player hands in a factory mission (items or work order). Off: only told silently.",
        DefaultValue = true,
    };

    public static readonly FormBooleanField AnnounceDoneTraderMissions = new()
    {
        Name = "AnnounceDoneTraderMissions",
        Label = "Announce Completed Trader Missions",
        Text = "The character speaks up when the player finishes a job for a shop that trades anyway. Off: only told silently.",
        DefaultValue = false,
    };

    public static readonly FormBooleanField AnnouncePursuedDone = new()
    {
        Name = "AnnouncePursuedDone",
        Label = "Announce Handed-In Pursued Mission",
        Text = "The character speaks up when the mission it was keeping track of (empyrion_pursue_quest) is handed in, " +
               "whatever its kind. Off: only told silently.",
        DefaultValue = true,
    };

    public static readonly FormBooleanField AnnounceItemsReady = new()
    {
        Name = "AnnounceItemsReady",
        Label = "Announce Pursued Mission Items Ready",
        Text = "The character speaks up when the player carries everything the pursued mission wants (personal inventory). Off: only told silently.",
        DefaultValue = true,
    };

    public static readonly FormTextField VcSuffix = new()
    {
        Name = "VcSuffix",
        Label = "Crafting Container Suffix",
        Text = "Crafting uses the one container on the ship whose name ends with this word, e.g. 'Production VC'.",
        DefaultValue = "VC",
    };

    public static readonly FormBooleanField GameChat = new()
    {
        Name = "GameChat",
        Label = "Listen To The Game Chat",
        Text = "Private messages the player sends to themselves in the game's chat (whisper to your own name) go to the " +
               "character, as if typed here. Nobody else sees them.",
        DefaultValue = true,
    };

    public static readonly FormBooleanField GameChatMirror = new()
    {
        Name = "GameChatMirror",
        Label = "Mirror Replies To The Game Chat",
        Text = "Also send each of the character's replies to the player's game chat, as a private message from the character. " +
               "A fallback when there is no voice (text to speech); with voice, leave it off.",
        DefaultValue = false,
    };

    public static readonly FormIntField PollSeconds = new()
    {
        Name = "PollSeconds",
        Label = "Poll Interval (seconds)",
        Text = "How often the game state is read while a chat is open (one small request to the bridge).",
        DefaultValue = 2,
        Min = 2,
        Max = 60,
    };

    public static readonly FormIntField ReactionCooldownSeconds = new()
    {
        Name = "ReactionCooldownSeconds",
        Label = "Reaction Cooldown (seconds)",
        Text = "Minimum time between two spoken reactions; events in between are passed on silently.",
        DefaultValue = 30,
        Min = 0,
        Max = 600,
    };

    public Task<FormField[]> GetModuleConfigurationFieldsAsync(
        IAuthenticationContext auth,
        ISettingsSource settings,
        CancellationToken cancellationToken
    )
    {
        var fields = FormBuilder.Build(
            FormTitleField.Create(
                "Empyrion",
                text: $"Talks to the VoxtaBridge server mod. Augmentation key: `{EmpyrionConstants.AugmentationKey}`"
            ),
            BridgeUrl,
            Token,
            SteamId,
            LiveContext,
            Reactions,
            GameChat,
            GameChatMirror,
            PollSeconds,
            ReactionCooldownSeconds,
            VcSuffix,
            ChatToolSettingsContributions.GetFormFields(Tools, AutoEnabledFor),
            // last: a title starts a section that runs to the next title, and the tool fields have none of their own
            AnnouncementsTitle,
            AnnounceFactoryMissions,
            AnnounceTraderMissions,
            AnnounceDoneFactoryMissions,
            AnnounceDoneTraderMissions,
            AnnouncePursuedDone,
            AnnounceItemsReady
        );
        return Task.FromResult(fields);
    }
}

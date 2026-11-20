using Voxta.Abstractions.Registration;

namespace Voxta.Modules.Empyrion;

public static class EmpyrionConstants
{
    public const string ServiceName = "Empyrion";

    /// <summary>How the tools are grouped in the UI.</summary>
    public const string ContextKey = ServiceName;

    public const string AugmentationKey = "empyrion";

    public const string GetLocationTool = "empyrion_get_location";
    public const string GetPlayerStatusTool = "empyrion_get_player_status";
    public const string GetCurrentShipTool = "empyrion_get_current_ship";
    public const string GetPlayerInventoryTool = "empyrion_get_player_inventory";
    public const string TransferItemsTool = "empyrion_transfer_items";
    public const string FindItemTool = "empyrion_find_item";
    public const string PlanCraftTool = "empyrion_plan_craft";
    public const string SearchItemsTool = "empyrion_search_items";
    public const string WhereToBuyTool = "empyrion_where_to_buy";
    public const string ScanPoisTool = "empyrion_scan_pois";
    public const string GetStructureTool = "empyrion_get_structure";
    public const string SetActiveStructureTool = "empyrion_set_active_structure";
    public const string SystemInfoTool = "empyrion_system_info";
    public const string PoiTradeTool = "empyrion_poi_trade";
    public const string PoiTerminalTool = "empyrion_poi_terminal";
    public const string ListQuestsTool = "empyrion_list_quests";
    public const string PursueQuestTool = "empyrion_pursue_quest";
    public const string SendToChatTool = "empyrion_send_to_chat";
    public const string MarkStarsTool = "empyrion_mark_stars";
    public const string ClearMarksTool = "empyrion_clear_marks";
    public const string AddMapMarkerTool = "empyrion_add_map_marker";
    public const string CraftTool = "empyrion_craft";
    public const string PlanMicroJumpTool = "empyrion_plan_microjump";
    public const string MicroJumpTool = "empyrion_microjump";
    public const string FindShipControlsTool = "empyrion_find_ship_controls";
    public const string ShipControlTool = "empyrion_ship_control";
    public const string WatchTool = "empyrion_watch";

    public static readonly AugmentationKeyDescriptor[] Augmentations =
    [
        new(
            AugmentationKey,
            "Empyrion",
            "Lets the character follow the player's Empyrion game: location, vitals, inventory, current ship and its cargo, and react to events."
        ),
    ];
}

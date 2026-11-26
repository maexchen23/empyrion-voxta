using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Encryption;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Services;
using Voxta.Abstractions.Services.Agents;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Abstractions.Utils;
using Voxta.Modules.Empyrion.Bridge;
using Voxta.Modules.Empyrion.Configuration;

namespace Voxta.Modules.Empyrion.ChatAugmentations;

public class EmpyrionAugmentationsService(
    ILocalEncryptionProvider encryptionProvider,
    ICommonFolders commonFolders,
    ILogger<EmpyrionAugmentationsService> logger,
    ILoggerFactory loggerFactory
) : ServiceBase(logger), IToolsChatAugmentationsService
{
    public Task<AugmentationPlanItem[]> PlanAsync(IChatAugmentationPlanContext context, CancellationToken cancellationToken)
    {
        var chatStyles = ChatToolSettingsContributions.GetChatStyles(ModuleConfigurationProvider.AutoEnabledFor.Get(ModuleConfiguration));
        var augmentation = EmpyrionConstants.Augmentations.Get(EmpyrionConstants.AugmentationKey);

        return Task.FromResult<AugmentationPlanItem[]>([
            new AugmentationPlanItem
            {
                Key = augmentation.Key,
                Label = augmentation.Label,
                Description = augmentation.Description,
                Tools =
                [
                    new AugmentationToolDeclaration(EmpyrionConstants.GetLocationTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.GetPlayerStatusTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.GetCurrentShipTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.GetPlayerInventoryTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.FindItemTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.SearchItemsTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.WhereToBuyTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.ScanPoisTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.GetStructureTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.SetActiveStructureTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.SystemInfoTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.PoiTradeTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.PoiTerminalTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.ListQuestsTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.MiningContractsTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.PursueQuestTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.SendToChatTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.MarkStarsTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.ClearMarksTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.AddMapMarkerTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.PlanCraftTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.CraftTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.PlanMicroJumpTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.MicroJumpTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.TransferItemsTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.FindShipControlsTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.ShipControlTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.WatchTool, EmpyrionConstants.ContextKey),
                ],
                Activation = AugmentationActivation.OptIn,
                Suggested = false,
                IncludeInCatchAll = false,
                ActivateAsync = (session, _) =>
                {
                    var client = new BridgeClient(
                        ModuleConfiguration.GetRequired(ModuleConfigurationProvider.BridgeUrl),
                        BridgeSecrets.RevealToken(encryptionProvider, ModuleConfiguration.GetOptional(ModuleConfigurationProvider.Token)));
                    var steamId = ModuleConfiguration.GetOptional(ModuleConfigurationProvider.SteamId);
                    // watches: one file per game server (bridge URL) and player, in the module's own data folder
                    var watchFolder = commonFolders.GetDataFolder("Voxta.Modules.Empyrion", "watches");
                    Directory.CreateDirectory(watchFolder);
                    var watches = new WatchStore(watchFolder, ModuleConfiguration.GetRequired(ModuleConfigurationProvider.BridgeUrl),
                        string.IsNullOrWhiteSpace(steamId) ? "-" : steamId.Trim(),
                        Math.Max(1, Setting(() => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.MaxWatches), Watches.DefaultMaxWatches)),
                        message => logger.LogWarning("Empyrion {Message}", message));
                    bool Selected(string tool) => ChatToolSettingsContributions.IsToolSelected(
                        ModuleConfiguration, ModuleConfigurationProvider.Tools, tool);

                    var live = new LiveSettings(
                        Setting(() => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.LiveContext), true),
                        Setting(() => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.Reactions), true),
                        TimeSpan.FromSeconds(Math.Clamp(Setting(() => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.PollSeconds), 2), 2, 60)),
                        TimeSpan.FromSeconds(Math.Clamp(Setting(() => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.ReactionCooldownSeconds), 30), 0, 600)),
                        Setting(() => ModuleConfiguration.GetOptional(ModuleConfigurationProvider.VcSuffix), null) is { Length: > 0 } vc ? vc.Trim() : "VC",
                        Setting(() => GameEventConfig.From(
                            id => ModuleConfiguration.GetOptional(GameEventSettings.EventChoiceFields.First(f => f.Name == "Event_" + id)),
                            ModuleConfiguration.GetOptional<Dictionary<string, string>>(GameEventSettings.LevelsField)?
                                .Select(r => (IReadOnlyDictionary<string, string>)r)), GameEventConfig.Default),
                        Setting(() => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.GameChat), true),
                        Setting(() => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.GameChatMirror), false));

                    var instance = new EmpyrionChatAugmentationInstance(
                        session,
                        client,
                        steamId,
                        chatStyles,
                        Selected,
                        live,
                        watches,
                        loggerFactory.CreateLogger<EmpyrionChatAugmentationInstance>());
                    instance.Start();
                    return Task.FromResult<IChatAugmentationServiceInstanceBase?>(instance);
                },
            },
        ]);
    }

    /// <summary>A setting that was added after the module was configured may be missing: use its default.</summary>
    private static T Setting<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception) { return fallback; }
    }
}

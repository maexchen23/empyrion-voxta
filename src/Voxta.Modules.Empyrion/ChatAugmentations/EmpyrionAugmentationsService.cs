using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Encryption;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Services;
using Voxta.Abstractions.Services.Agents;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Modules.Empyrion.Bridge;
using Voxta.Modules.Empyrion.Configuration;

namespace Voxta.Modules.Empyrion.ChatAugmentations;

public class EmpyrionAugmentationsService(
    ILocalEncryptionProvider encryptionProvider,
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
                    new AugmentationToolDeclaration(EmpyrionConstants.MarkStarsTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.ClearMarksTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.AddMapMarkerTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.PlanCraftTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.CraftTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.PlanMicroJumpTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.MicroJumpTool, EmpyrionConstants.ContextKey),
                    new AugmentationToolDeclaration(EmpyrionConstants.TransferItemsTool, EmpyrionConstants.ContextKey),
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
                    bool Selected(string tool) => ChatToolSettingsContributions.IsToolSelected(
                        ModuleConfiguration, ModuleConfigurationProvider.Tools, tool);

                    var live = new LiveSettings(
                        Setting(() => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.LiveContext), true),
                        Setting(() => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.Reactions), true),
                        TimeSpan.FromSeconds(Math.Clamp(Setting(() => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.PollSeconds), 5), 2, 60)),
                        TimeSpan.FromSeconds(Math.Clamp(Setting(() => ModuleConfiguration.GetRequired(ModuleConfigurationProvider.ReactionCooldownSeconds), 30), 0, 600)),
                        Setting(() => ModuleConfiguration.GetOptional(ModuleConfigurationProvider.VcSuffix), null) is { Length: > 0 } vc ? vc.Trim() : "VC",
                        Setting(() => ModuleConfiguration.GetOptional(ModuleConfigurationProvider.VjSuffix), null) is { Length: > 0 } vj ? vj.Trim() : "VJ");

                    var instance = new EmpyrionChatAugmentationInstance(
                        session,
                        client,
                        steamId,
                        chatStyles,
                        Selected,
                        live,
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
